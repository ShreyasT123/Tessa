using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Tessellation.Configuration;
using Tessellation.Core;

namespace Tessellation.Platform.Windows;

public sealed class WindowTilingService
{
    private const int ExToolWindow = 0x00000080;
    private const int DwmAttributeCloaked = 14;
    private const int ShowHide = 0;
    private const int ShowNoActivate = 4;
    private const uint MessageClose = 0x0010;
    private const uint SetWindowPosNoSizeOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPosAsync = 0x4000;
    private const double AnimationFrameMilliseconds = 1000d / 60d;
    private readonly object _animationGate = new();
    private readonly WindowRestoreJournal _restoreJournal = new();
    private readonly Dictionary<IntPtr, NativeRect> _originalBounds = [];
    private readonly Dictionary<(IntPtr Monitor, int Workspace), int> _scrollIndices = [];
    private readonly Dictionary<(IntPtr Monitor, int Workspace), int> _lastWindowCounts = [];
    private readonly Dictionary<IntPtr, IntPtr> _windowMonitorAssignments = [];
    private readonly Dictionary<IntPtr, int> _windowWorkspaces = [];
    private readonly Dictionary<IntPtr, long> _focusOrder = [];
    private readonly HashSet<IntPtr> _hiddenByWorkspace = [];
    private readonly HashSet<IntPtr> _floatingWindows = [];
    private readonly HashSet<IntPtr> _pendingMonitorMoves = [];
    private CancellationTokenSource? _animationCancellation;
    private Task _animationTask = Task.CompletedTask;
    private TessellationPreferences? _preferences;
    private volatile bool _isArranging;
    private long _lastPlacementAppliedAt;
    private bool _isRunning;
    private bool _tilingEnabled = true;
    private int _activeWorkspace = 1;
    private long _nextFocusOrder;

    public bool IsRunning => _isRunning;

    public bool IsTilingEnabled => _tilingEnabled;

    public int ActiveWorkspace => _activeWorkspace;

    /// <summary>
    /// Gets a value indicating whether the service is currently moving windows into their tiles.
    /// </summary>
    public bool IsArranging => _isArranging;

    /// <summary>
    /// Gets how long ago the last tile position was applied to a window. Used to ignore the
    /// geometry notifications that our own layout changes generate.
    /// </summary>
    public long MillisecondsSinceLastPlacement => Environment.TickCount64 - Interlocked.Read(ref _lastPlacementAppliedAt);

    public event Action<Exception>? AnimationFailed;

    /// <summary>
    /// Raised after the service has finished moving windows into their tiles.
    /// </summary>
    public event Action? LayoutSettled;

    public IReadOnlyList<MonitorStatusSnapshot> GetMonitorStatus()
    {
        if (!_isRunning || _preferences is null)
        {
            return [];
        }

        var occupiedByMonitor = _windowWorkspaces
            .Where(entry => IsWindow(entry.Key))
            .Select(entry => (
                Monitor: _windowMonitorAssignments.GetValueOrDefault(entry.Key, MonitorFromWindow(entry.Key, MonitorDefaultToNearest)),
                Workspace: entry.Value))
            .GroupBy(entry => entry.Monitor)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Workspace).Distinct().Order().ToArray());
        var foreground = GetForegroundWindow();
        var title = GetWindowTitle(foreground);

        return EnumerateMonitors()
            .Select(monitor => new MonitorStatusSnapshot(
                monitor.Handle,
                monitor.Bounds,
                _activeWorkspace,
                _preferences.WorkspaceCount,
                _preferences.WorkspaceNames.ToArray(),
                occupiedByMonitor.GetValueOrDefault(monitor.Handle, []),
                _preferences.Layout,
                _tilingEnabled,
                title,
                _preferences.StatusBarPosition,
                _preferences.StatusBarHeight,
                _preferences.StatusBarWheelWorkspace))
            .ToArray();
    }

    public bool IsEligibleWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == GetShellWindow() ||
            !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle) ||
            GetWindow(handle, 4) != IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(handle, out var processId);
        return processId != Environment.ProcessId &&
               (GetWindowLong(handle, -20) & ExToolWindow) == IntPtr.Zero;
    }

    public int Start(TessellationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        PreferenceStore.Validate(preferences);
        if (_isRunning)
        {
            throw new InvalidOperationException("The window manager is already running.");
        }

        _restoreJournal.RestorePending();
        _preferences = Copy(preferences);
        _activeWorkspace = 1;
        _tilingEnabled = true;
        _focusOrder.Clear();
        _nextFocusOrder = 0;
        _isRunning = true;
        try
        {
            return TileNow();
        }
        catch (Exception startupException) when (
            startupException is Win32Exception or InvalidOperationException or OverflowException or IOException or UnauthorizedAccessException)
        {
            try
            {
                RestoreOriginalBounds();
                _restoreJournal.Clear();
            }
            catch (Exception restoreException) when (
                restoreException is Win32Exception or IOException or UnauthorizedAccessException)
            {
                _isRunning = false;
                _preferences = null;
                throw new AggregateException("Window tiling failed and some original positions could not be restored.", startupException, restoreException);
            }

            _isRunning = false;
            _preferences = null;
            throw;
        }
    }

    public int TileNow()
    {
        if (!_isRunning || _preferences is null)
        {
            throw new InvalidOperationException("Start the window manager before tiling windows.");
        }

        if (!_tilingEnabled)
        {
            return 0;
        }

        CancelAnimation();
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report any monitors.");
        }

        var windows = EnumerateWindows();
        var removedWindow = false;
        foreach (var handle in _windowWorkspaces.Keys.ToArray())
        {
            if (IsWindow(handle))
            {
                continue;
            }

            _windowWorkspaces.Remove(handle);
            _hiddenByWorkspace.Remove(handle);
            _floatingWindows.Remove(handle);
            _windowMonitorAssignments.Remove(handle);
            _pendingMonitorMoves.Remove(handle);
            _originalBounds.Remove(handle);
            _focusOrder.Remove(handle);
            _restoreJournal.Remove(handle);
            removedWindow = true;
        }

        foreach (var window in windows.ToArray())
        {
            _windowWorkspaces.TryAdd(window.Handle, _activeWorkspace);
            if (_windowWorkspaces[window.Handle] != _activeWorkspace)
            {
                _ = ShowWindowAsync(window.Handle, ShowHide);
                _hiddenByWorkspace.Add(window.Handle);
                windows.Remove(window);
            }
            else if (_floatingWindows.Contains(window.Handle))
            {
                windows.Remove(window);
            }
        }

        foreach (var window in windows)
        {
            if (!_focusOrder.ContainsKey(window.Handle))
            {
                _focusOrder.Add(window.Handle, _nextFocusOrder++);
            }
        }

        var monitorByHandle = monitors.ToDictionary(monitor => monitor.Handle);
        foreach (var window in windows)
        {
            var bounds = ToRect(window.Bounds);
            var isOnScreen = monitors.Any(monitor => bounds.Intersects(monitor.Bounds));
            var preservePendingMove = false;
            if (_pendingMonitorMoves.Contains(window.Handle) &&
                _windowMonitorAssignments.TryGetValue(window.Handle, out var pendingMonitor) &&
                monitorByHandle.TryGetValue(pendingMonitor, out var pendingTarget))
            {
                if (bounds.Intersects(pendingTarget.Bounds))
                {
                    _pendingMonitorMoves.Remove(window.Handle);
                }
                else
                {
                    preservePendingMove = true;
                }
            }
            else
            {
                _pendingMonitorMoves.Remove(window.Handle);
            }

            if (!preservePendingMove && (isOnScreen || !_windowMonitorAssignments.ContainsKey(window.Handle)))
            {
                _windowMonitorAssignments[window.Handle] = window.Monitor;
            }

            if (_windowMonitorAssignments.TryGetValue(window.Handle, out var assignedMonitor) &&
                monitorByHandle.ContainsKey(assignedMonitor))
            {
                window.Monitor = assignedMonitor;
            }
        }

        var windowsByMonitor = windows
            .GroupBy(window => window.Monitor)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(window => _focusOrder[window.Handle]).ToList());
        var layout = Enum.Parse<WindowLayout>(_preferences.Layout, ignoreCase: true);
        var arranged = 0;
        var placements = new List<Placement>();

        foreach (var monitor in monitors)
        {
            var scrollKey = (monitor.Handle, _activeWorkspace);
            if (!windowsByMonitor.TryGetValue(monitor.Handle, out var monitorWindows))
            {
                _lastWindowCounts[scrollKey] = 0;
                continue;
            }

            _lastWindowCounts[scrollKey] = monitorWindows.Count;
            var scrollIndex = _scrollIndices.GetValueOrDefault(scrollKey);
            var targets = LayoutEngine.Calculate(
                layout,
                monitor.WorkArea,
                monitorWindows.Count,
                _preferences.OuterGap,
                _preferences.InnerGap,
                scrollIndex,
                _preferences.MasterRatio,
                _preferences.ScrollColumnWidth);
            if (targets.Count != monitorWindows.Count)
            {
                throw new InvalidOperationException($"The {layout} layout could not place every window on a monitor.");
            }

            for (var index = 0; index < monitorWindows.Count; index++)
            {
                var window = monitorWindows[index];
                var target = targets[index];
                if (layout == WindowLayout.Scrolling && !target.Intersects(monitor.WorkArea))
                {
                    target = ParkedRect(window.Bounds, index, monitors);
                }
                else if (layout == WindowLayout.Scrolling && CrossesAnotherMonitor(target, monitor, monitors))
                {
                    target = ClampTo(target, monitor.WorkArea);
                }

                if (!_originalBounds.ContainsKey(window.Handle))
                {
                    _originalBounds.Add(window.Handle, window.Bounds);
                    _restoreJournal.Record(window.Handle, ToRect(window.Bounds));
                }

                var current = ToRect(window.Bounds);
                if (current != target)
                {
                    placements.Add(new Placement(window.Handle, current, target));
                }

                arranged++;
            }
        }

        if (removedWindow || _originalBounds.Count > 0)
        {
            _restoreJournal.Save();
        }

        StartAnimation(placements, _preferences.AnimationDurationMs);
        return arranged;
    }

    public int Scroll(int direction)
    {
        if (!_isRunning || _preferences is null)
        {
            throw new InvalidOperationException("Start the window manager before scrolling.");
        }

        if (!string.Equals(_preferences.Layout, "scrolling", StringComparison.Ordinal))
        {
            return 0;
        }

        foreach (var (key, count) in _lastWindowCounts)
        {
            if (key.Workspace != _activeWorkspace)
            {
                continue;
            }

            var monitor = EnumerateMonitors().FirstOrDefault(candidate => candidate.Handle == key.Monitor);
            var visibleColumns = monitor is null
                ? 1
                : GetVisibleScrollingColumns(monitor.WorkArea, _preferences);
            var maxIndex = Math.Max(0, count - visibleColumns);
            _scrollIndices[key] = Math.Clamp(_scrollIndices.GetValueOrDefault(key) + Math.Sign(direction), 0, maxIndex);
        }

        return TileNow();
    }

    public int SwitchWorkspace(int workspace)
    {
        if (!_isRunning || _preferences is null)
        {
            throw new InvalidOperationException("Start the window manager before switching workspaces.");
        }

        if (workspace < 1 || workspace > _preferences.WorkspaceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workspace), "The workspace is outside the configured range.");
        }

        if (workspace == _activeWorkspace)
        {
            return TileNow();
        }

        CancelAnimation();
        foreach (var (handle, assignedWorkspace) in _windowWorkspaces)
        {
            if (assignedWorkspace == _activeWorkspace && IsWindowVisible(handle))
            {
                _ = ShowWindowAsync(handle, ShowHide);
                _hiddenByWorkspace.Add(handle);
            }
        }

        _activeWorkspace = workspace;
        foreach (var handle in _hiddenByWorkspace.ToArray())
        {
            if (!_windowWorkspaces.TryGetValue(handle, out var assignedWorkspace) || assignedWorkspace != workspace)
            {
                continue;
            }

            if (IsWindow(handle))
            {
                _ = ShowWindowAsync(handle, ShowNoActivate);
            }

            _hiddenByWorkspace.Remove(handle);
        }

        return TileNow();
    }

    public void MarkWindowFloating(IntPtr handle)
    {
        if (!_isRunning || !IsEligibleWindow(handle))
        {
            throw new InvalidOperationException("The selected window cannot be floated.");
        }

        CancelAnimation();
        _windowWorkspaces.TryAdd(handle, _activeWorkspace);
        if (!_originalBounds.ContainsKey(handle))
        {
            if (!GetWindowRect(handle, out var bounds))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The window's original position could not be read.");
            }

            _originalBounds.Add(handle, bounds);
            _restoreJournal.Record(handle, ToRect(bounds));
            _restoreJournal.Save();
        }

        _floatingWindows.Add(handle);
    }

    public int RetileAll()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before retiling windows.");
        }

        _tilingEnabled = true;
        _floatingWindows.Clear();
        return TileNow();
    }

    public (bool IsEnabled, int ArrangedWindowCount) ToggleTiling()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before toggling tiling.");
        }

        CancelAnimation();
        _tilingEnabled = !_tilingEnabled;
        return (_tilingEnabled, _tilingEnabled ? TileNow() : 0);
    }

    public void FocusRelative(int direction)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before changing focus.");
        }

        if (direction == 0)
        {
            return;
        }

        var activeWindows = EnumerateWindows()
            .Where(window => _windowWorkspaces.GetValueOrDefault(window.Handle, _activeWorkspace) == _activeWorkspace)
            .Select(window => window.Handle)
            .ToArray();
        if (activeWindows.Length == 0)
        {
            return;
        }

        foreach (var handle in activeWindows)
        {
            if (!_focusOrder.ContainsKey(handle))
            {
                _focusOrder.Add(handle, _nextFocusOrder++);
            }
        }

        var windows = activeWindows.OrderBy(handle => _focusOrder[handle]).ToArray();
        var foreground = GetForegroundWindow();
        var currentIndex = Array.IndexOf(windows, foreground);
        var step = Math.Sign(direction);
        var targetIndex = currentIndex < 0
            ? (step > 0 ? 0 : windows.Length - 1)
            : (currentIndex + step + windows.Length) % windows.Length;
        if (windows[targetIndex] != foreground && !SetForegroundWindow(windows[targetIndex]))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected window could not be focused.");
        }
    }

    public void FocusDirection(CardinalDirection direction)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before changing focus.");
        }

        if (string.Equals(_preferences?.Layout, "scrolling", StringComparison.Ordinal) &&
            direction is CardinalDirection.Left or CardinalDirection.Right)
        {
            FocusScrollingColumn(direction == CardinalDirection.Right ? 1 : -1);
            return;
        }

        var foreground = GetForegroundWindow();
        var windows = EnumerateWindows()
            .Where(window => _windowWorkspaces.GetValueOrDefault(window.Handle, _activeWorkspace) == _activeWorkspace)
            .ToArray();
        var current = windows.FirstOrDefault(window => window.Handle == foreground);
        if (current is null)
        {
            return;
        }

        var nearest = FindDirectionalNeighbor(current, windows, direction);

        if (nearest is not null &&
            !SetForegroundWindow(nearest.Handle) &&
            GetForegroundWindow() != nearest.Handle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The window in the requested direction could not be focused.");
        }
    }

    private void FocusScrollingColumn(int direction)
    {
        var foreground = GetForegroundWindow();
        var windows = EnumerateWindows()
            .Where(window =>
                !_floatingWindows.Contains(window.Handle) &&
                _windowWorkspaces.GetValueOrDefault(window.Handle, _activeWorkspace) == _activeWorkspace)
            .ToArray();
        var current = windows.FirstOrDefault(window => window.Handle == foreground);
        if (current is null)
        {
            return;
        }

        var monitorHandle = _windowMonitorAssignments.GetValueOrDefault(current.Handle, current.Monitor);
        var columns = windows
            .Where(window => _windowMonitorAssignments.GetValueOrDefault(window.Handle, window.Monitor) == monitorHandle)
            .OrderBy(window => _focusOrder.GetValueOrDefault(window.Handle, long.MaxValue))
            .ToArray();
        var currentIndex = Array.FindIndex(columns, window => window.Handle == foreground);
        var targetIndex = currentIndex + Math.Sign(direction);
        if (targetIndex < 0 || targetIndex >= columns.Length)
        {
            return;
        }

        if (!SetForegroundWindow(columns[targetIndex].Handle) &&
            GetForegroundWindow() != columns[targetIndex].Handle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The adjacent scrolling column could not be focused.");
        }

        var monitor = EnumerateMonitors().FirstOrDefault(candidate => candidate.Handle == monitorHandle);
        if (monitor is not null)
        {
            var visibleColumns = GetVisibleScrollingColumns(monitor.WorkArea, _preferences!);
            var key = (monitorHandle, _activeWorkspace);
            var scrollIndex = _scrollIndices.GetValueOrDefault(key);
            if (targetIndex < scrollIndex)
            {
                scrollIndex = targetIndex;
            }
            else if (targetIndex >= scrollIndex + visibleColumns)
            {
                scrollIndex = targetIndex - visibleColumns + 1;
            }

            _scrollIndices[key] = Math.Clamp(scrollIndex, 0, Math.Max(0, columns.Length - visibleColumns));
        }

        _ = TileNow();
    }

    private static int GetVisibleScrollingColumns(RectI workArea, TessellationPreferences preferences)
    {
        var insetWidth = Math.Max(1, workArea.Width - preferences.OuterGap * 2);
        var columnWidth = Math.Clamp(
            (int)Math.Round(insetWidth * preferences.ScrollColumnWidth),
            1,
            insetWidth);
        return Math.Max(1, (insetWidth + preferences.InnerGap) / (columnWidth + preferences.InnerGap));
    }

    public int MoveWindowDirection(CardinalDirection direction)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before moving a window directionally.");
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        CancelAnimation();
        var foreground = GetForegroundWindow();
        var windows = EnumerateWindows()
            .Where(window =>
                !_floatingWindows.Contains(window.Handle) &&
                _windowWorkspaces.GetValueOrDefault(window.Handle, _activeWorkspace) == _activeWorkspace)
            .ToArray();
        var current = windows.FirstOrDefault(window => window.Handle == foreground);
        if (current is null)
        {
            return 0;
        }

        var candidates = windows.Where(window => window.Handle != foreground).ToArray();
        var neighbor = FindDirectionalNeighbor(current, candidates, direction);
        if (neighbor is null)
        {
            return 0;
        }

        if (!_focusOrder.ContainsKey(current.Handle))
        {
            _focusOrder.Add(current.Handle, _nextFocusOrder++);
        }

        if (!_focusOrder.ContainsKey(neighbor.Handle))
        {
            _focusOrder.Add(neighbor.Handle, _nextFocusOrder++);
        }

        (_focusOrder[current.Handle], _focusOrder[neighbor.Handle]) =
            (_focusOrder[neighbor.Handle], _focusOrder[current.Handle]);

        if (current.Monitor != neighbor.Monitor)
        {
            _windowMonitorAssignments[current.Handle] = neighbor.Monitor;
            _pendingMonitorMoves.Add(current.Handle);
        }

        return TileNow();
    }

    public int SwapWindowOrder(int direction)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before swapping windows.");
        }

        if (direction == 0)
        {
            return 0;
        }

        var foreground = GetForegroundWindow();
        var windows = EnumerateWindows()
            .Where(window =>
                !_floatingWindows.Contains(window.Handle) &&
                _windowWorkspaces.GetValueOrDefault(window.Handle, _activeWorkspace) == _activeWorkspace &&
                window.Monitor == MonitorFromWindow(foreground, MonitorDefaultToNearest))
            .Select(window => window.Handle)
            .OrderBy(handle => _focusOrder.GetValueOrDefault(handle, long.MaxValue))
            .ToArray();
        var currentIndex = Array.IndexOf(windows, foreground);
        var neighborIndex = currentIndex + Math.Sign(direction);
        if (currentIndex < 0 || neighborIndex < 0 || neighborIndex >= windows.Length)
        {
            return 0;
        }

        (_focusOrder[windows[currentIndex]], _focusOrder[windows[neighborIndex]]) =
            (_focusOrder[windows[neighborIndex]], _focusOrder[windows[currentIndex]]);
        return TileNow();
    }

    public int PromoteForegroundWindowToMaster()
    {
        if (!_isRunning || _preferences is null)
        {
            throw new InvalidOperationException("Start the window manager before promoting a window.");
        }

        if (!string.Equals(_preferences.Layout, "master", StringComparison.Ordinal))
        {
            return 0;
        }

        var foreground = GetForegroundWindow();
        var window = EnumerateWindows().FirstOrDefault(candidate =>
            candidate.Handle == foreground &&
            !_floatingWindows.Contains(candidate.Handle) &&
            _windowWorkspaces.GetValueOrDefault(candidate.Handle, _activeWorkspace) == _activeWorkspace);
        if (window is null)
        {
            return 0;
        }

        var monitor = _windowMonitorAssignments.GetValueOrDefault(window.Handle, window.Monitor);
        var monitorWindows = EnumerateWindows()
            .Where(candidate =>
                !_floatingWindows.Contains(candidate.Handle) &&
                _windowWorkspaces.GetValueOrDefault(candidate.Handle, _activeWorkspace) == _activeWorkspace &&
                _windowMonitorAssignments.GetValueOrDefault(candidate.Handle, candidate.Monitor) == monitor)
            .Select(candidate => candidate.Handle)
            .OrderBy(handle => _focusOrder.GetValueOrDefault(handle, long.MaxValue))
            .ToArray();
        var currentIndex = Array.IndexOf(monitorWindows, foreground);
        if (currentIndex <= 0)
        {
            return 0;
        }

        if (!_focusOrder.ContainsKey(monitorWindows[0]))
        {
            _focusOrder[monitorWindows[0]] = _nextFocusOrder++;
        }

        if (!_focusOrder.ContainsKey(foreground))
        {
            _focusOrder[foreground] = _nextFocusOrder++;
        }

        (_focusOrder[monitorWindows[0]], _focusOrder[foreground]) =
            (_focusOrder[foreground], _focusOrder[monitorWindows[0]]);
        return TileNow();
    }

    public (bool IsFloating, int ArrangedWindowCount) ToggleForegroundWindowFloating()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before toggling floating mode.");
        }

        var foreground = GetForegroundWindow();
        var window = EnumerateWindows().FirstOrDefault(candidate => candidate.Handle == foreground);
        if (window is null)
        {
            throw new InvalidOperationException("The foreground window is not an eligible managed window.");
        }

        CancelAnimation();
        _windowWorkspaces.TryAdd(window.Handle, _activeWorkspace);
        if (_floatingWindows.Remove(window.Handle))
        {
            return (false, TileNow());
        }

        if (!_originalBounds.ContainsKey(window.Handle))
        {
            _originalBounds.Add(window.Handle, window.Bounds);
            _restoreJournal.Record(window.Handle, ToRect(window.Bounds));
            _restoreJournal.Save();
        }

        _floatingWindows.Add(window.Handle);
        return (true, TileNow());
    }

    public bool CloseForegroundWindow()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Start the window manager before closing a managed window.");
        }

        var foreground = GetForegroundWindow();
        if (!EnumerateWindows().Any(window => window.Handle == foreground))
        {
            return false;
        }

        if (!PostMessage(foreground, MessageClose, UIntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The focused window could not be asked to close.");
        }

        return true;
    }

    public int MoveForegroundWindowToWorkspace(int workspace)
    {
        if (!_isRunning || _preferences is null)
        {
            throw new InvalidOperationException("Start the window manager before moving windows between workspaces.");
        }

        if (workspace < 1 || workspace > _preferences.WorkspaceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(workspace), "The workspace is outside the configured range.");
        }

        var foreground = GetForegroundWindow();
        var window = EnumerateWindows().FirstOrDefault(candidate => candidate.Handle == foreground);
        if (window is null)
        {
            throw new InvalidOperationException("The foreground window is not an eligible managed window.");
        }

        _windowWorkspaces.TryAdd(window.Handle, _activeWorkspace);
        if (_windowWorkspaces[window.Handle] == workspace)
        {
            return TileNow();
        }

        if (!_originalBounds.ContainsKey(window.Handle))
        {
            _originalBounds.Add(window.Handle, window.Bounds);
            _restoreJournal.Record(window.Handle, ToRect(window.Bounds));
            _restoreJournal.Save();
        }

        _windowWorkspaces[window.Handle] = workspace;
        if (workspace != _activeWorkspace)
        {
            _ = ShowWindowAsync(window.Handle, ShowHide);
            _hiddenByWorkspace.Add(window.Handle);
        }

        return TileNow();
    }

    public int UpdatePreferences(TessellationPreferences preferences)
    {
        if (!_isRunning)
        {
            return 0;
        }

        PreferenceStore.Validate(preferences);
        _preferences = Copy(preferences);
        var activeWorkspace = Math.Min(_activeWorkspace, _preferences.WorkspaceCount);
        if (activeWorkspace != _activeWorkspace)
        {
            _ = SwitchWorkspace(activeWorkspace);
        }

        foreach (var handle in _windowWorkspaces.Keys.ToArray())
        {
            if (_windowWorkspaces[handle] > _preferences.WorkspaceCount)
            {
                _windowWorkspaces[handle] = _preferences.WorkspaceCount;
            }
        }

        foreach (var handle in _hiddenByWorkspace.ToArray())
        {
            if (_windowWorkspaces.GetValueOrDefault(handle) != _activeWorkspace)
            {
                continue;
            }

            if (IsWindow(handle))
            {
                _ = ShowWindowAsync(handle, ShowNoActivate);
            }

            _hiddenByWorkspace.Remove(handle);
        }

        return TileNow();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        CancelAnimation();
        _isArranging = false;
        RestoreOriginalBounds();
        foreach (var handle in _hiddenByWorkspace.ToArray())
        {
            if (IsWindow(handle))
            {
                _ = ShowWindowAsync(handle, ShowNoActivate);
            }
        }

        _restoreJournal.RestoreVisibility();
        _restoreJournal.Clear();
        _isRunning = false;
        _preferences = null;
        _scrollIndices.Clear();
        _lastWindowCounts.Clear();
        _windowMonitorAssignments.Clear();
        _windowWorkspaces.Clear();
        _focusOrder.Clear();
        _nextFocusOrder = 0;
        _pendingMonitorMoves.Clear();
        _hiddenByWorkspace.Clear();
        _floatingWindows.Clear();
        _tilingEnabled = true;
        _activeWorkspace = 1;
    }

    private void RestoreOriginalBounds()
    {
        foreach (var (handle, bounds) in _originalBounds.ToArray())
        {
            if (!IsWindow(handle))
            {
                _originalBounds.Remove(handle);
                continue;
            }

            if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                SetWindowPosNoSizeOrder | SetWindowPosNoActivate))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "A window's original position could not be restored.");
            }

            _originalBounds.Remove(handle);
        }

        _originalBounds.Clear();
    }

    private static TessellationPreferences Copy(TessellationPreferences preferences) =>
        new()
        {
            Layout = preferences.Layout,
            MainModifier = preferences.MainModifier,
            WorkspaceCount = preferences.WorkspaceCount,
            WorkspaceNames = [.. preferences.WorkspaceNames],
            OuterGap = preferences.OuterGap,
            InnerGap = preferences.InnerGap,
            FocusFollowsMouse = preferences.FocusFollowsMouse,
            AltDrag = preferences.AltDrag,
            MasterRatio = preferences.MasterRatio,
            ScrollColumnWidth = preferences.ScrollColumnWidth,
            StatusBarPosition = preferences.StatusBarPosition,
            StatusBarHeight = preferences.StatusBarHeight,
            StatusBarWheelWorkspace = preferences.StatusBarWheelWorkspace,
            AnimationDurationMs = preferences.AnimationDurationMs
        };

    private void StartAnimation(IReadOnlyList<Placement> placements, int durationMs)
    {
        if (placements.Count == 0)
        {
            return;
        }

        if (durationMs <= 0)
        {
            _isArranging = true;
            try
            {
                ApplyPlacements(placements, SetWindowPosNoSizeOrder | SetWindowPosNoActivate);
                Interlocked.Exchange(ref _lastPlacementAppliedAt, Environment.TickCount64);
            }
            finally
            {
                _isArranging = false;
            }

            LayoutSettled?.Invoke();
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (_animationGate)
        {
            _isArranging = true;
            _animationCancellation = cancellation;
            _animationTask = Task.Run(() => AnimatePlacements(placements, durationMs, cancellation.Token));
        }

        var animation = _animationTask;
        _ = animation.ContinueWith(
            task =>
            {
                if (task.IsFaulted && task.Exception is { } exception)
                {
                    AnimationFailed?.Invoke(exception.GetBaseException());
                }

                var settled = false;
                lock (_animationGate)
                {
                    if (ReferenceEquals(_animationCancellation, cancellation))
                    {
                        _animationCancellation = null;
                        _animationTask = Task.CompletedTask;
                        _isArranging = false;
                        if (!task.IsCanceled)
                        {
                            Interlocked.Exchange(ref _lastPlacementAppliedAt, Environment.TickCount64);
                            settled = true;
                        }
                    }
                }

                cancellation.Dispose();
                if (settled)
                {
                    LayoutSettled?.Invoke();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelAnimation()
    {
        Task animation;
        lock (_animationGate)
        {
            _animationCancellation?.Cancel();
            animation = _animationTask;
        }

        try
        {
            animation.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The animation task already reported the failure through AnimationFailed;
            // cancelling a layout must not surface it again to the caller.
        }
    }

    private async Task AnimatePlacements(
        IReadOnlyList<Placement> placements,
        int durationMs,
        CancellationToken cancellationToken)
    {
        // Task.Delay is only as accurate as the system timer, which defaults to ~15.6 ms.
        // Raising the resolution to 1 ms while animating keeps the frame cadence steady
        // instead of producing uneven 16/32 ms jumps.
        using var timerResolution = new TimerResolutionScope();
        var stopwatch = Stopwatch.StartNew();
        var frame = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            var eased = EaseOutCubic(progress);
            foreach (var placement in placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Interpolate(placement.Start, placement.Target, eased);
                if (!SetWindowPos(
                    placement.Window,
                    IntPtr.Zero,
                    target.Left,
                    target.Top,
                    target.Width,
                    target.Height,
                    SetWindowPosNoSizeOrder | SetWindowPosNoActivate | SetWindowPosAsync))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "A window could not be animated to its tile.");
                }
            }

            Interlocked.Exchange(ref _lastPlacementAppliedAt, Environment.TickCount64);
            if (progress >= 1)
            {
                return;
            }

            frame++;
            var wait = frame * AnimationFrameMilliseconds - stopwatch.Elapsed.TotalMilliseconds;
            if (wait > 0.5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static double EaseOutCubic(double progress)
    {
        var remaining = 1 - Math.Clamp(progress, 0, 1);
        return 1 - remaining * remaining * remaining;
    }

    private sealed class TimerResolutionScope : IDisposable
    {
        public TimerResolutionScope()
        {
            _ = timeBeginPeriod(1);
        }

        public void Dispose()
        {
            _ = timeEndPeriod(1);
        }
    }

    private static void ApplyPlacements(IReadOnlyList<Placement> placements, uint flags)
    {
        foreach (var placement in placements)
        {
            if (!SetWindowPos(
                placement.Window,
                IntPtr.Zero,
                placement.Target.Left,
                placement.Target.Top,
                placement.Target.Width,
                placement.Target.Height,
                flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "A window could not be moved to its tile.");
            }
        }
    }

    private static RectI Interpolate(RectI start, RectI target, double progress) =>
        new(
            Interpolate(start.Left, target.Left, progress),
            Interpolate(start.Top, target.Top, progress),
            Interpolate(start.Right, target.Right, progress),
            Interpolate(start.Bottom, target.Bottom, progress));

    private static int Interpolate(int start, int target, double progress) =>
        (int)Math.Round(start + (target - (double)start) * progress);

    private readonly record struct Placement(IntPtr Window, RectI Start, RectI Target);

    private static List<Monitor> EnumerateMonitors()
    {
        var monitors = new List<Monitor>();
        MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(handle, ref info))
            {
                return false;
            }

            monitors.Add(new Monitor(handle, ToRect(info.WorkArea), ToRect(info.Bounds)));
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Monitor enumeration failed.");
        }

        return monitors;
    }

    private static List<TileWindow> EnumerateWindows()
    {
        var windows = new List<TileWindow>();
        var currentProcessId = Environment.ProcessId;
        var shellWindow = GetShellWindow();
        EnumWindowsProc callback = (handle, _) =>
        {
            if (handle == shellWindow || !IsWindowVisible(handle) || IsIconic(handle) || GetWindow(handle, 4) != IntPtr.Zero)
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == currentProcessId || (GetWindowLong(handle, -20) & ExToolWindow) != 0)
            {
                return true;
            }

            var titleLength = GetWindowTextLength(handle);
            if (titleLength == 0 || IsCloaked(handle))
            {
                return true;
            }

            if (!GetWindowRect(handle, out var bounds))
            {
                return true;
            }

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                windows.Add(new TileWindow(handle, monitor, bounds));
            }

            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Window enumeration failed.");
        }

        return windows;
    }

    private static bool IsCloaked(IntPtr handle)
    {
        var result = DwmGetWindowAttribute(handle, DwmAttributeCloaked, out var cloaked, sizeof(int));
        return result >= 0 && cloaked != 0;
    }

    private static bool CrossesAnotherMonitor(RectI target, Monitor own, IReadOnlyList<Monitor> monitors) =>
        monitors.Any(monitor => monitor.Handle != own.Handle && target.Intersects(monitor.Bounds));

    private static TileWindow? FindDirectionalNeighbor(
        TileWindow current,
        IReadOnlyList<TileWindow> candidates,
        CardinalDirection direction)
    {
        var source = ToRect(current.Bounds);
        var sourceCenterX = (long)source.Left + source.Right;
        var sourceCenterY = (long)source.Top + source.Bottom;
        TileWindow? nearest = null;
        long bestScore = long.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Handle == current.Handle)
            {
                continue;
            }

            var bounds = ToRect(candidate.Bounds);
            var centerX = (long)bounds.Left + bounds.Right;
            var centerY = (long)bounds.Top + bounds.Bottom;
            var isInDirection = direction switch
            {
                CardinalDirection.Left => centerX < sourceCenterX,
                CardinalDirection.Right => centerX > sourceCenterX,
                CardinalDirection.Up => centerY < sourceCenterY,
                CardinalDirection.Down => centerY > sourceCenterY,
                _ => false
            };
            if (!isInDirection)
            {
                continue;
            }

            var primaryDistance = direction switch
            {
                CardinalDirection.Left => Math.Max(0L, (long)source.Left - bounds.Right),
                CardinalDirection.Right => Math.Max(0L, (long)bounds.Left - source.Right),
                CardinalDirection.Up => Math.Max(0L, (long)source.Top - bounds.Bottom),
                CardinalDirection.Down => Math.Max(0L, (long)bounds.Top - source.Bottom),
                _ => long.MaxValue
            };
            var perpendicularDistance = direction is CardinalDirection.Left or CardinalDirection.Right
                ? Math.Abs(centerY - sourceCenterY)
                : Math.Abs(centerX - sourceCenterX);
            var score = primaryDistance * 2 + perpendicularDistance;
            if (score < bestScore)
            {
                bestScore = score;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static RectI ClampTo(RectI target, RectI area)
    {
        var width = Math.Min(target.Width, area.Width);
        var height = Math.Min(target.Height, area.Height);
        var left = Math.Clamp(target.Left, area.Left, area.Right - width);
        var top = Math.Clamp(target.Top, area.Top, area.Bottom - height);
        return new RectI(left, top, left + width, top + height);
    }

    private static RectI ParkedRect(NativeRect bounds, int index, IReadOnlyList<Monitor> monitors)
    {
        var bottom = monitors.Max(monitor => monitor.Bounds.Bottom);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var top = checked(bottom + 200 + index * (height + 16));
        return new RectI(bounds.Left, top, bounds.Left + Math.Max(1, bounds.Right - bounds.Left), top + height);
    }

    private static RectI ToRect(NativeRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static IntPtr GetWindowLong(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    private static string GetWindowTitle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        _ = GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    private sealed record Monitor(IntPtr Handle, RectI WorkArea, RectI Bounds);

    private sealed class TileWindow(IntPtr handle, IntPtr monitor, NativeRect bounds)
    {
        public IntPtr Handle { get; } = handle;

        public IntPtr Monitor { get; set; } = monitor;

        public NativeRect Bounds { get; } = bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds;
        public NativeRect WorkArea;
        public int Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint period);
}

public sealed record MonitorStatusSnapshot(
    IntPtr Monitor,
    RectI Bounds,
    int ActiveWorkspace,
    int WorkspaceCount,
    IReadOnlyList<string> WorkspaceNames,
    IReadOnlyList<int> OccupiedWorkspaces,
    string Layout,
    bool IsTilingEnabled,
    string FocusedWindowTitle,
    string StatusBarPosition,
    int StatusBarHeight,
    bool StatusBarWheelWorkspace);
