using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tessellation.Core;
using Tessellation.Platform.Windows;
using Windows.UI;
using WinRT.Interop;

namespace Tessy_UI;

internal sealed class StatusBarWindow : Window
{
    private const uint AppBarNew = 0;
    private const uint AppBarRemove = 1;
    private const uint AppBarQueryPosition = 2;
    private const uint AppBarSetPosition = 3;
    private const uint AppBarEdgeTop = 1;
    private const uint AppBarEdgeBottom = 3;
    private const uint AppBarCallbackMessage = 0x8001;
    private const int WindowStyleIndex = -16;
    private const int ExtendedWindowStyleIndex = -20;
    private const long StyleCaption = 0x00C00000;
    private const long StyleThickFrame = 0x00040000;
    private const long StyleMinimizeBox = 0x00020000;
    private const long StyleMaximizeBox = 0x00010000;
    private const long StyleSystemMenu = 0x00080000;
    private const long ExtendedStyleToolWindow = 0x00000080;
    private const long ExtendedStyleNoActivate = 0x08000000;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);
    private readonly IntPtr _handle;
    private readonly StackPanel _workspacePanel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ScrollViewer _workspaceScrollViewer;
    private readonly TextBlock _focusedTitle = new()
    {
        FontSize = 12,
        Foreground = Brush(225, 230, 240),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button _layoutButton = new()
    {
        FontSize = 11,
        Foreground = Brush(210, 218, 231),
        Background = Brush(35, 43, 57),
        Padding = new Thickness(8, 2, 8, 2),
        IsTabStop = false,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _clock = new()
    {
        FontSize = 12,
        Foreground = Brush(225, 230, 240),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly SubclassProcedure _subclassProcedure;
    private readonly Dictionary<int, Button> _workspaceButtons = [];
    private readonly uint _taskbarCreatedMessage;
    private bool _appBarRegistered;
    private bool _hasPositioned;
    private bool _closed;
    private string _shownTitle = string.Empty;
    private string _shownLayout = string.Empty;
    private string _shownClock = string.Empty;
    private int _shownActiveWorkspace = -1;
    private int[] _shownOccupied = [];
    private string[] _shownNames = [];
    private bool _shownWheelWorkspace;
    private IntPtr _placedMonitor;
    private RectI _placedBounds;
    private uint _placedDpi;
    private uint _placedEdge;
    private int _placedHeight;
    private NativeRect _placedRect;

    public StatusBarWindow()
    {
        Title = "Tessellation status bar";
        _workspaceScrollViewer = new ScrollViewer
        {
            Content = _workspacePanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalAlignment = VerticalAlignment.Center
        };

        var root = new Grid
        {
            Padding = new Thickness(12, 0, 14, 0),
            ColumnSpacing = 18,
            Background = Brush(20, 25, 34)
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _workspaceScrollViewer.Width = 500;
        root.Children.Add(_workspaceScrollViewer);

        var titleContainer = new Grid { VerticalAlignment = VerticalAlignment.Center };
        titleContainer.Children.Add(_focusedTitle);
        Grid.SetColumn(titleContainer, 1);
        root.Children.Add(titleContainer);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center
        };
        right.Children.Add(_layoutButton);
        right.Children.Add(_clock);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);

        Content = new Border
        {
            Background = Brush(20, 25, 34),
            BorderBrush = Brush(49, 59, 76),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = root
        };

        _handle = WindowNative.GetWindowHandle(this);
        ConfigureWindowStyles();
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The shell restart notification could not be registered.");
        }

        _subclassProcedure = WindowProcedure;
        if (!SetWindowSubclass(_handle, _subclassProcedure, new UIntPtr(1), UIntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The status bar could not register for shell notifications.");
        }

        Closed += (_, _) =>
        {
            _closed = true;
            RemoveAppBar();
        };
        _layoutButton.Click += (_, _) => LayoutRequested?.Invoke();
    }

    public event Action<int>? WorkspaceRequested;

    public event Action? LayoutRequested;

    public bool IsClosed => _closed;

    public RectI ScreenBounds => _hasPositioned
        ? new RectI(_placedRect.Left, _placedRect.Top, _placedRect.Right, _placedRect.Bottom)
        : default;

    public bool Update(MonitorStatusSnapshot status)
    {
        var createdOrResized = EnsureAppBar(
            status.Bounds,
            status.Monitor,
            status.StatusBarPosition,
            status.StatusBarHeight);
        UpdateWorkspaces(status);

        var title = string.IsNullOrWhiteSpace(status.FocusedWindowTitle)
            ? "Desktop"
            : status.FocusedWindowTitle;
        if (!string.Equals(title, _shownTitle, StringComparison.Ordinal))
        {
            _shownTitle = title;
            _focusedTitle.Text = title;
        }

        var layout = $"{status.Layout}  ·  {(status.IsTilingEnabled ? "tiled" : "paused")}";
        if (!string.Equals(layout, _shownLayout, StringComparison.Ordinal))
        {
            _shownLayout = layout;
            _layoutButton.Content = layout;
            ToolTipService.SetToolTip(_layoutButton, "Click to cycle the window layout");
        }

        var clock = DateTime.Now.ToString("ddd, MMM d  HH:mm");
        if (!string.Equals(clock, _shownClock, StringComparison.Ordinal))
        {
            _shownClock = clock;
            _clock.Text = clock;
        }

        return createdOrResized;
    }

    public new void Close()
    {
        if (_closed)
        {
            return;
        }

        base.Close();
    }

    private bool EnsureAppBar(RectI monitorBounds, IntPtr monitor, string position, int configuredHeight)
    {
        if (_closed)
        {
            return false;
        }

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The status bar's display scale could not be determined.");
        }

        var barHeight = Math.Clamp((int)Math.Round((double)configuredHeight * dpi / 96d), 1, monitorBounds.Height);
        var edge = position == "bottom" ? AppBarEdgeBottom : AppBarEdgeTop;
        if (_hasPositioned &&
            _appBarRegistered &&
            _placedMonitor == monitor &&
            _placedBounds == monitorBounds &&
            _placedDpi == dpi &&
            _placedEdge == edge &&
            _placedHeight == barHeight)
        {
            // Re-sending ABM_SETPOS on every timer tick makes Windows recompute the
            // work area (and can nudge other windows) even though nothing changed.
            return false;
        }

        var requiresRetile = !_hasPositioned ||
                             _placedMonitor != monitor ||
                             _placedBounds != monitorBounds ||
                             _placedDpi != dpi ||
                             _placedEdge != edge ||
                             _placedHeight != barHeight;

        if (_appBarRegistered && _placedEdge != 0 && _placedEdge != edge)
        {
            RemoveAppBar();
        }

        if (!_appBarRegistered)
        {
            var registration = new AppBarData
            {
                Size = Marshal.SizeOf<AppBarData>(),
                Window = _handle,
                CallbackMessage = AppBarCallbackMessage
            };
            if (SHAppBarMessage(AppBarNew, ref registration) == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Windows rejected status bar registration (ABM_NEW returned false; the window may already be registered).");
            }

            _appBarRegistered = true;
        }

        var data = new AppBarData
        {
            Size = Marshal.SizeOf<AppBarData>(),
            Window = _handle,
            Edge = edge,
            Bounds = new NativeRect
            {
                Left = monitorBounds.Left,
                Right = monitorBounds.Right,
                Top = edge == AppBarEdgeTop ? monitorBounds.Top : monitorBounds.Bottom - barHeight,
                Bottom = edge == AppBarEdgeTop ? monitorBounds.Top + barHeight : monitorBounds.Bottom
            }
        };
        _ = SHAppBarMessage(AppBarQueryPosition, ref data);
        if (edge == AppBarEdgeTop)
        {
            data.Bounds.Bottom = data.Bounds.Top + barHeight;
        }
        else
        {
            data.Bounds.Top = data.Bounds.Bottom - barHeight;
        }

        _ = SHAppBarMessage(AppBarSetPosition, ref data);
        requiresRetile |= !SameRect(_placedRect, data.Bounds);

        var width = data.Bounds.Right - data.Bounds.Left;
        var height = data.Bounds.Bottom - data.Bounds.Top;
        var flags = SetWindowPosNoActivate | SetWindowPosShowWindow;
        if (!_hasPositioned)
        {
            flags |= SetWindowPosFrameChanged;
        }

        if (!SetWindowPos(
                _handle,
                TopmostWindow,
                data.Bounds.Left,
                data.Bounds.Top,
                width,
                height,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The status bar could not be positioned.");
        }

        var widthInDips = width * 96d / dpi;
        _workspaceScrollViewer.Width = Math.Min(500, Math.Max(160, widthInDips / 3d));
        _placedMonitor = monitor;
        _placedBounds = monitorBounds;
        _placedDpi = dpi;
        _placedEdge = edge;
        _placedHeight = barHeight;
        _placedRect = data.Bounds;
        _hasPositioned = true;
        return requiresRetile;
    }

    private void UpdateWorkspaces(MonitorStatusSnapshot status)
    {
        var occupied = status.OccupiedWorkspaces;
        if (_workspaceButtons.Count == status.WorkspaceCount &&
            _shownActiveWorkspace == status.ActiveWorkspace &&
            _shownWheelWorkspace == status.StatusBarWheelWorkspace &&
            _shownOccupied.AsSpan().SequenceEqual(occupied is int[] array ? array : occupied.ToArray()) &&
            _shownNames.AsSpan().SequenceEqual(status.WorkspaceNames.ToArray()))
        {
            return;
        }

        _shownActiveWorkspace = status.ActiveWorkspace;
        _shownWheelWorkspace = status.StatusBarWheelWorkspace;
        _shownOccupied = occupied.ToArray();
        _shownNames = status.WorkspaceNames.ToArray();

        if (_workspaceButtons.Count != status.WorkspaceCount)
        {
            _workspaceButtons.Clear();
            _workspacePanel.Children.Clear();
            for (var workspace = 1; workspace <= status.WorkspaceCount; workspace++)
            {
                var button = new Button
                {
                    Content = workspace.ToString("00"),
                    Tag = workspace,
                    MinWidth = 34,
                    MaxWidth = 120,
                    Height = 30,
                    Padding = new Thickness(5, 0, 5, 0),
                    FontSize = 11,
                    IsTabStop = false,
                    Foreground = Brush(210, 218, 231),
                    Background = Brush(35, 43, 57)
                };
                button.Click += (_, _) => WorkspaceRequested?.Invoke((int)button.Tag);
                _workspaceButtons.Add(workspace, button);
                _workspacePanel.Children.Add(button);
            }
        }

        var occupiedSet = occupied.ToHashSet();
        foreach (var (workspace, button) in _workspaceButtons)
        {
            var isActive = workspace == status.ActiveWorkspace;
            var isOccupied = occupiedSet.Contains(workspace);
            var name = workspace <= status.WorkspaceNames.Count
                ? status.WorkspaceNames[workspace - 1]
                : string.Empty;
            var label = string.IsNullOrWhiteSpace(name) ? workspace.ToString("00") : name;
            button.Content = label;
            button.Background = isActive ? Brush(70, 103, 245) : Brush(35, 43, 57);
            button.Foreground = isActive || isOccupied ? Brush(245, 247, 252) : Brush(143, 154, 174);
            button.BorderBrush = isOccupied ? Brush(123, 149, 255) : Brush(49, 59, 76);
            button.BorderThickness = new Thickness(isOccupied ? 1 : 0);
            ToolTipService.SetToolTip(
                button,
                $"Workspace {workspace}: {label}{(isActive ? " · active" : string.Empty)}{(isOccupied ? " · has windows" : string.Empty)}{(status.StatusBarWheelWorkspace ? " · wheel to cycle" : string.Empty)}");
        }
    }

    private void ConfigureWindowStyles()
    {
        var style = GetWindowLong(_handle, WindowStyleIndex).ToInt64();
        style &= ~(StyleCaption | StyleThickFrame | StyleMinimizeBox | StyleMaximizeBox | StyleSystemMenu);
        _ = SetWindowLong(_handle, WindowStyleIndex, new IntPtr(style));

        var extendedStyle = GetWindowLong(_handle, ExtendedWindowStyleIndex).ToInt64();
        extendedStyle |= ExtendedStyleToolWindow | ExtendedStyleNoActivate;
        _ = SetWindowLong(_handle, ExtendedWindowStyleIndex, new IntPtr(extendedStyle));
    }

    private void RemoveAppBar()
    {
        if (!_appBarRegistered)
        {
            return;
        }

        var data = new AppBarData
        {
            Size = Marshal.SizeOf<AppBarData>(),
            Window = _handle
        };
        if (SHAppBarMessage(AppBarRemove, ref data) == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The status bar could not release monitor work area.");
        }

        _appBarRegistered = false;
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == _taskbarCreatedMessage)
        {
            _appBarRegistered = false;
            _hasPositioned = false;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Color.FromArgb(255, red, green, blue));

    private static bool SameRect(NativeRect left, NativeRect right) =>
        left.Left == right.Left &&
        left.Top == right.Top &&
        left.Right == right.Right &&
        left.Bottom == right.Bottom;

    private static IntPtr GetWindowLong(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLong(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Bounds;
        public IntPtr Parameter;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProcedure callback,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
