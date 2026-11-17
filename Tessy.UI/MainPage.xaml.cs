using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Tessellation.Configuration;
using Tessellation.Core;
using Tessellation.Input;
using Tessellation.Platform.Windows;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tessy_UI;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    private static readonly TimeSpan StructureRetileDelay = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan GeometryRetileDelay = TimeSpan.FromMilliseconds(450);
    private readonly PreferenceStore _preferenceStore = new();
    private readonly StartupRegistrationService _startupRegistrationService = new();
    private readonly WindowTilingService _windowTilingService = new();
    private readonly WindowEventMonitor _windowEventMonitor = new();
    private readonly GlobalHotkeyService _globalHotkeyService = new();
    private readonly GlobalMouseDragService _globalMouseDragService = new();
    private readonly StatusBarManager _statusBarManager;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _retileTimer;
    private readonly DispatcherQueueTimer _statusBarTimer;
    private TessellationPreferences _preferences = new();
    private string? _registeredModifier;
    private bool _registeredStartWithWindows;
    private bool _isLoading;
    private bool _isUserMovingOrSizing;
    private bool _retilePending;

    public MainPage()
    {
        _isLoading = true;
        InitializeComponent();
        _statusBarManager = new StatusBarManager(SwitchWorkspace, CycleStatusBarLayout);

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += SaveTimer_Tick;
        _retileTimer = DispatcherQueue.CreateTimer();
        _retileTimer.Interval = TimeSpan.FromMilliseconds(500);
        _retileTimer.IsRepeating = false;
        _retileTimer.Tick += RetileTimer_Tick;
        _statusBarTimer = DispatcherQueue.CreateTimer();
        _statusBarTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statusBarTimer.IsRepeating = true;
        _statusBarTimer.Tick += StatusBarTimer_Tick;
        _windowTilingService.AnimationFailed += ReportHotkeyFailure;
        _windowTilingService.LayoutSettled += OnLayoutSettled;
    }

    public event Action<bool>? StartupCompleted;

    public async Task InitializeAsync()
    {
        _isLoading = true;
        var started = false;
        try
        {
            _preferences = await _preferenceStore.LoadAsync();
            if (!_preferences.StartWithWindows)
            {
                await _startupRegistrationService.SetEnabledAsync(false);
            }
            _registeredStartWithWindows = _preferences.StartWithWindows;

            ApplyPreferences();
            SaveStateText.Text = "Up to date";
            await StartEngineAsync();
            started = _windowTilingService.IsRunning;
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Title = "Tessellation could not start";
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
            SaveStateText.Text = "Startup failed";
        }
        finally
        {
            _isLoading = false;
            StartupCompleted?.Invoke(started);
        }
    }

    private void MasterRatio_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading && double.IsFinite(e.NewValue))
        {
            _preferences.MasterRatio = Math.Round(e.NewValue, 2);
            UpdateMasterRatioLabel();
            QueueSave();
        }
    }

    private void ScrollColumnWidth_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading && double.IsFinite(e.NewValue))
        {
            _preferences.ScrollColumnWidth = Math.Round(e.NewValue, 2);
            UpdateScrollColumnWidthLabel();
            QueueSave();
        }
    }

    private void StatusBarHeight_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading && double.IsFinite(e.NewValue))
        {
            _preferences.StatusBarHeight = (int)Math.Round(e.NewValue);
            UpdateStatusBarHeightLabel();
            QueueSave();
        }
    }

    private void StatusBarWheelWorkspace_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.StatusBarWheelWorkspace = StatusBarWheelWorkspaceSwitch.IsOn;
            QueueSave();
        }
    }

    private void WorkspaceNames_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var names = WorkspaceNamesTextBox.Text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(name => name.Trim())
            .ToList();
        if (names.Count > 20 || names.Any(name => name.Length > 24))
        {
            ErrorInfoBar.Title = "Workspace names are too long";
            ErrorInfoBar.Message = "Use no more than 20 lines, with at most 24 characters per name.";
            ErrorInfoBar.IsOpen = true;
            return;
        }

        while (names.Count > 0 && names[^1].Length == 0)
        {
            names.RemoveAt(names.Count - 1);
        }

        _preferences.WorkspaceNames = names;
        RebuildWorkspaceButtons();
        QueueSave();
    }

    private void QueueMoveWindowDirection(CardinalDirection direction)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var count = _windowTilingService.MoveWindowDirection(direction);
                    if (count > 0)
                    {
                        EngineDetailText.Text = $"Moved the focused window {direction.ToString().ToLowerInvariant()}; arranged {count} windows.";
                    }
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to move a window directionally.");
        }
    }

    private void AdjustMasterRatio(double change)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (string.Equals(_preferences.Layout, "scrolling", StringComparison.Ordinal))
                {
                    ScrollColumnWidthSlider.Value = Math.Clamp(_preferences.ScrollColumnWidth + change, 0.1, 1.0);
                }
                else
                {
                    var nextRatio = Math.Clamp(_preferences.MasterRatio + change, 0.25, 0.75);
                    MasterRatioSlider.Value = nextRatio;
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to adjust the layout width.");
        }
    }

    private void CycleScrollColumnWidth()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                var presets = new[] { 1d / 3d, 0.5, 2d / 3d, 1d };
                var next = presets.FirstOrDefault(value => value > _preferences.ScrollColumnWidth + 0.02);
                ScrollColumnWidthSlider.Value = next == 0 ? presets[0] : next;
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to change scrolling column width.");
        }
    }

    private void PromoteFocusedWindowToMaster()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (string.Equals(_preferences.Layout, "scrolling", StringComparison.Ordinal))
                    {
                        ScrollColumnWidthSlider.Value = 1;
                        return;
                    }

                    var count = _windowTilingService.PromoteForegroundWindowToMaster();
                    if (count > 0)
                    {
                        EngineDetailText.Text = $"Promoted the focused window to master; arranged {count} windows.";
                    }
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to promote the focused window.");
        }
    }

    private void ToggleFocusedWindowFloating()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var (isFloating, arrangedCount) = _windowTilingService.ToggleForegroundWindowFloating();
                    EngineDetailText.Text = isFloating
                        ? $"Focused window is floating; arranged {arrangedCount} tiled windows."
                        : $"Focused window returned to tiling; arranged {arrangedCount} windows.";
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to toggle floating mode.");
        }
    }

    private void CloseFocusedWindow()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_windowTilingService.CloseForegroundWindow())
                    {
                        EngineDetailText.Text = "Asked the focused window to close.";
                    }
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to close the focused window.");
        }
    }

    private void AnimationDuration_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.AnimationDurationMs = (int)e.NewValue;
            UpdateAnimationLabel();
            QueueSave();
        }
    }

    private void ApplyPreferences()
    {
        foreach (var radio in new[]
        {
            DwindleRadio, MasterRadio, ColumnsRadio, GridRadio, MonocleRadio, ScrollingRadio
        })
        {
            radio.IsChecked = string.Equals((string)radio.Tag, _preferences.Layout, StringComparison.Ordinal);
        }

        MainModifierCombo.SelectedItem = MainModifierCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals((string)item.Content, _preferences.MainModifier, StringComparison.Ordinal));
        StatusBarPositionCombo.SelectedItem = StatusBarPositionCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals((string)item.Content, _preferences.StatusBarPosition, StringComparison.OrdinalIgnoreCase));
        WorkspaceNamesTextBox.Text = string.Join(Environment.NewLine, _preferences.WorkspaceNames);
        WorkspaceCountBox.Value = _preferences.WorkspaceCount;
        OuterGapSlider.Value = _preferences.OuterGap;
        InnerGapSlider.Value = _preferences.InnerGap;
        MasterRatioSlider.Value = _preferences.MasterRatio;
        ScrollColumnWidthSlider.Value = _preferences.ScrollColumnWidth;
        StatusBarHeightSlider.Value = _preferences.StatusBarHeight;
        StatusBarWheelWorkspaceSwitch.IsOn = _preferences.StatusBarWheelWorkspace;
        AnimationDurationSlider.Value = _preferences.AnimationDurationMs;
        FocusFollowsMouseSwitch.IsOn = _preferences.FocusFollowsMouse;
        AltDragSwitch.IsOn = _preferences.AltDrag;
        StartWithWindowsSwitch.IsOn = _preferences.StartWithWindows;
        UpdateGapLabels();
        UpdateMasterRatioLabel();
        UpdateScrollColumnWidthLabel();
        UpdateStatusBarHeightLabel();
        UpdateAnimationLabel();
        RebuildWorkspaceButtons();
        UpdateEngineControls();
    }

    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isLoading && sender is RadioButton { IsChecked: true, Tag: string layout })
        {
            _preferences.Layout = layout;
            UpdateEngineControls();
            QueueSave();
        }
    }

    private void MainModifier_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && MainModifierCombo.SelectedItem is ComboBoxItem item)
        {
            _preferences.MainModifier = (string)item.Content;
            QueueSave();
        }
    }

    private void StatusBarPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading && StatusBarPositionCombo.SelectedItem is ComboBoxItem item)
        {
            _preferences.StatusBarPosition = ((string)item.Content).ToLowerInvariant();
            QueueSave();
        }
    }

    private void CycleStatusBarLayout()
    {
        var layouts = Enum.GetValues<WindowLayout>();
        var current = Enum.Parse<WindowLayout>(_preferences.Layout, ignoreCase: true);
        var next = layouts[(Array.IndexOf(layouts, current) + 1) % layouts.Length];
        var nextName = next.ToString().ToLowerInvariant();
        var radio = new[]
        {
            DwindleRadio, MasterRadio, ColumnsRadio, GridRadio, MonocleRadio, ScrollingRadio
        }.First(button => string.Equals((string)button.Tag, nextName, StringComparison.Ordinal));
        radio.IsChecked = true;
    }

    private void WorkspaceCount_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoading && double.IsFinite(sender.Value))
        {
            _preferences.WorkspaceCount = (int)Math.Clamp(Math.Round(sender.Value), 1, 20);
            RebuildWorkspaceButtons();
            QueueSave();
        }
    }

    private void OuterGap_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.OuterGap = (int)e.NewValue;
            UpdateGapLabels();
            QueueSave();
        }
    }

    private void InnerGap_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.InnerGap = (int)e.NewValue;
            UpdateGapLabels();
            QueueSave();
        }
    }

    private void FocusFollowsMouse_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.FocusFollowsMouse = FocusFollowsMouseSwitch.IsOn;
            QueueSave();
        }
    }

    private void AltDrag_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.AltDrag = AltDragSwitch.IsOn;
            QueueSave();
        }
    }

    private void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            _preferences.StartWithWindows = StartWithWindowsSwitch.IsOn;
            QueueSave();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _preferences = new TessellationPreferences();
        _isLoading = true;
        ApplyPreferences();
        _isLoading = false;
        QueueSave();
    }

    private void UpdateGapLabels()
    {
        OuterGapText.Text = $"{(int)OuterGapSlider.Value} px";
        InnerGapText.Text = $"{(int)InnerGapSlider.Value} px";
    }

    private void UpdateAnimationLabel()
    {
        AnimationDurationText.Text = _preferences.AnimationDurationMs == 0
            ? "Off"
            : $"{_preferences.AnimationDurationMs} ms";
    }

    private void UpdateMasterRatioLabel() =>
        MasterRatioText.Text = $"{(int)Math.Round(_preferences.MasterRatio * 100)}%";

    private void UpdateScrollColumnWidthLabel() =>
        ScrollColumnWidthText.Text = $"{(int)Math.Round(_preferences.ScrollColumnWidth * 100)}%";

    private void UpdateStatusBarHeightLabel() =>
        StatusBarHeightText.Text = $"{_preferences.StatusBarHeight} px";

    private void QueueSave()
    {
        ErrorInfoBar.IsOpen = false;
        SaveStateText.Text = "Saving...";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _saveTimer.Stop();
        var updatingStartup = false;
        try
        {
            if (_preferences.StartWithWindows != _registeredStartWithWindows)
            {
                updatingStartup = true;
                await _startupRegistrationService.SetEnabledAsync(_preferences.StartWithWindows);
                _registeredStartWithWindows = _preferences.StartWithWindows;
                updatingStartup = false;
            }

            await _preferenceStore.SaveAsync(_preferences);
            SaveStateText.Text = "Up to date";
            if (_windowTilingService.IsRunning)
            {
                try
                {
                    var modifierChanged = !string.Equals(
                        _registeredModifier,
                        _preferences.MainModifier,
                        StringComparison.Ordinal);
                    if (modifierChanged)
                    {
                        _globalHotkeyService.Stop();
                        await _globalHotkeyService.StartAsync(
                            _preferences.MainModifier,
                            QueueToggleTiling,
                            QueueFocusNext,
                            QueueFocusPrevious,
                            QueueSwapWindow,
                            QueueFocusDirection,
                            QueueMoveWindowDirection,
                            AdjustMasterRatio,
                            PromoteFocusedWindowToMaster,
                            ToggleFocusedWindowFloating,
                            CloseFocusedWindow,
                            CycleScrollColumnWidth,
                            QueueSwitchWorkspace,
                            QueueMoveWindow,
                            ReportHotkeyFailure);
                        _registeredModifier = _preferences.MainModifier;
                    }

                    if (modifierChanged && _globalMouseDragService.IsRunning)
                    {
                        await _globalMouseDragService.StopAsync();
                    }

                    var mouseFeaturesEnabled = _preferences.AltDrag ||
                                               _preferences.FocusFollowsMouse ||
                                               (_preferences.StatusBarWheelWorkspace && _preferences.WorkspaceCount > 1);
                    if (mouseFeaturesEnabled && !_globalMouseDragService.IsRunning)
                    {
                        await StartMouseDragAsync();
                    }
                    else if (!mouseFeaturesEnabled && _globalMouseDragService.IsRunning)
                    {
                        await _globalMouseDragService.StopAsync();
                    }

                    var count = _windowTilingService.UpdatePreferences(_preferences);
                    UpdateStatusBarWheelBounds();
                    EngineDetailText.Text = GetRunningStatus(count);
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Title = updatingStartup
                ? "Could not update Windows startup"
                : "Could not save preferences";
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
            SaveStateText.Text = "Save failed";
        }
    }

    private async void StartEngine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartEngineAsync();
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private async Task StartEngineAsync()
    {
        var count = _windowTilingService.Start(_preferences);
        try
        {
            if (_statusBarManager.Update(_windowTilingService.GetMonitorStatus()))
            {
                count = _windowTilingService.TileNow();
            }

            await _globalHotkeyService.StartAsync(
                _preferences.MainModifier,
                QueueToggleTiling,
                QueueFocusNext,
                QueueFocusPrevious,
                QueueSwapWindow,
                QueueFocusDirection,
                QueueMoveWindowDirection,
                AdjustMasterRatio,
                PromoteFocusedWindowToMaster,
                ToggleFocusedWindowFloating,
                CloseFocusedWindow,
                CycleScrollColumnWidth,
                QueueSwitchWorkspace,
                QueueMoveWindow,
                ReportHotkeyFailure);
            _registeredModifier = _preferences.MainModifier;
            _windowEventMonitor.Start(QueueAutoRetile);
            _isUserMovingOrSizing = false;
            _retilePending = false;
            if (_preferences.AltDrag ||
                _preferences.FocusFollowsMouse ||
                (_preferences.StatusBarWheelWorkspace && _preferences.WorkspaceCount > 1))
            {
                await StartMouseDragAsync();
            }

            _statusBarTimer.Start();
        }
        catch (Exception startException)
        {
            var cleanupFailures = new List<Exception>();
            _statusBarTimer.Stop();
            _globalMouseDragService.UpdateStatusBarWheel(null, []);
            try
            {
                _windowEventMonitor.Stop();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                await _globalMouseDragService.StopAsync();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                _globalHotkeyService.Stop();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                _statusBarManager.Stop();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            try
            {
                _windowTilingService.Stop();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            _registeredModifier = null;
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, startException);
                throw new AggregateException("The window manager could not start and rollback was incomplete.", cleanupFailures);
            }

            throw;
        }

        EngineStatusText.Text = "Window manager running";
        EngineDetailText.Text = GetRunningStatus(count);
        RefreshWorkspaceButtons();
        UpdateEngineControls();
    }

    private async void StopEngine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopEngineAsync();
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    public async Task StopEngineAsync()
    {
        var failures = new List<Exception>();
        _retileTimer.Stop();
        _statusBarTimer.Stop();
        _isUserMovingOrSizing = false;
        _retilePending = false;
        _globalMouseDragService.UpdateStatusBarWheel(null, []);

        try
        {
            _statusBarManager.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _globalMouseDragService.StopAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _windowEventMonitor.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _globalHotkeyService.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _windowTilingService.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (!_windowTilingService.IsRunning)
        {
            _registeredModifier = null;
            EngineStatusText.Text = "Window manager stopped";
            EngineDetailText.Text = "Previously arranged windows were restored to their original positions.";
            RefreshWorkspaceButtons();
            UpdateEngineControls();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("The window manager stopped with cleanup errors.", failures);
        }
    }

    public void StopEngineForSessionEnd()
    {
        _retileTimer.Stop();
        _statusBarTimer.Stop();
        _isUserMovingOrSizing = false;
        _retilePending = false;
        _globalMouseDragService.UpdateStatusBarWheel(null, []);
        var failures = new List<Exception>();

        try
        {
            _statusBarManager.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _globalMouseDragService.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _windowEventMonitor.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _globalHotkeyService.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            _windowTilingService.Stop();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("The window manager encountered errors during Windows session shutdown.", failures);
        }
    }

    public void ShowLifecycleError(Exception exception) => ShowEngineError(exception);

    private void TileNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = _windowTilingService.RetileAll();
            EngineDetailText.Text = $"Arranged {count} windows.";
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private void ShowEngineError(Exception exception)
    {
        ErrorInfoBar.Title = "Window manager error";
        ErrorInfoBar.Message = exception.Message;
        ErrorInfoBar.IsOpen = true;
    }

    private void ScrollBack_Click(object sender, RoutedEventArgs e) => Scroll(-1);

    private void ScrollForward_Click(object sender, RoutedEventArgs e) => Scroll(1);

    private void Scroll(int direction)
    {
        try
        {
            var count = _windowTilingService.Scroll(direction);
            EngineDetailText.Text = $"Arranged {count} windows using the scrolling layout.";
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private void UpdateEngineControls()
    {
        var running = _windowTilingService.IsRunning;
        StartEngineButton.IsEnabled = !running;
        StopEngineButton.IsEnabled = running;
        TileNowButton.IsEnabled = running;
        var scrolling = string.Equals(_preferences.Layout, "scrolling", StringComparison.Ordinal);
        ScrollBackButton.IsEnabled = running && scrolling;
        ScrollForwardButton.IsEnabled = running && scrolling;
    }

    private void QueueToggleTiling()
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var (isEnabled, arrangedCount) = _windowTilingService.ToggleTiling();
                    EngineDetailText.Text = isEnabled
                        ? $"Tiling enabled; arranged {arrangedCount} windows."
                        : "Tiling disabled; windows will stay where they are. Press the main modifier+T again to resume.";
                    UpdateEngineControls();
                    RefreshStatusBars();
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to toggle tiling.");
        }
    }

    private void QueueFocusNext() => QueueFocusRelative(1);

    private void QueueFocusPrevious() => QueueFocusRelative(-1);

    private void QueueFocusDirection(CardinalDirection direction)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _windowTilingService.FocusDirection(direction);
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to change directional focus.");
        }
    }

    private void QueueSwapWindow(int direction)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var count = _windowTilingService.SwapWindowOrder(direction);
                    if (count > 0)
                    {
                        EngineDetailText.Text = $"Window order updated; arranged {count} windows.";
                    }
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to swap window order.");
        }
    }

    private void QueueFocusRelative(int direction)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _windowTilingService.FocusRelative(direction);
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to change window focus.");
        }
    }

    private void ReportHotkeyFailure(Exception exception)
    {
        _ = DispatcherQueue.TryEnqueue(() => ShowEngineError(exception));
    }

    private async Task StartMouseDragAsync()
    {
        UpdateStatusBarWheelBounds();
        await _globalMouseDragService.StartAsync(
            _preferences.MainModifier,
            _preferences.AltDrag,
            _preferences.FocusFollowsMouse,
            _windowTilingService.IsEligibleWindow,
            QueueMarkWindowFloating,
            ReportHotkeyFailure,
            _preferences.StatusBarWheelWorkspace && _preferences.WorkspaceCount > 1
                ? QueueCycleWorkspaceFromBar
                : null);
    }

    private void UpdateStatusBarWheelBounds()
    {
        var enabled = _preferences.StatusBarWheelWorkspace && _preferences.WorkspaceCount > 1;
        _globalMouseDragService.UpdateStatusBarWheel(
            enabled ? QueueCycleWorkspaceFromBar : null,
            enabled ? _statusBarManager.GetStatusBarBounds() : []);
    }

    private void QueueCycleWorkspaceFromBar(int wheelSteps)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (!_windowTilingService.IsRunning)
                {
                    return;
                }

                var workspaceCount = _preferences.WorkspaceCount;
                var currentIndex = _windowTilingService.ActiveWorkspace - 1;
                var nextIndex = (currentIndex - wheelSteps % workspaceCount + workspaceCount) % workspaceCount;
                SwitchWorkspace(nextIndex + 1);
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to switch workspaces from the status bar.");
        }
    }

    private void QueueMarkWindowFloating(IntPtr window)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _windowTilingService.MarkWindowFloating(window);
                }
                catch (Exception exception)
                {
                    ShowEngineError(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to start a window drag.");
        }
    }

    private string GetRunningStatus(int windowCount)
    {
        var dragStatus = _preferences.AltDrag
            ? $"{_preferences.MainModifier}+left-drag moves; +right-drag resizes."
            : "Drag gestures are disabled.";
        var focusStatus = _preferences.FocusFollowsMouse
            ? "Focus follows the pointer after a short hover."
            : "Focus follows clicks.";
        var tilingStatus = _windowTilingService.IsTilingEnabled ? "Tiling is on." : "Tiling is off.";
        return $"Arranged {windowCount} windows. {_preferences.MainModifier}+T toggles tiling; +J/K cycles focus; +arrows moves focus; +Shift+arrows moves windows; +Shift+J/K swaps order; +F toggles float; +W closes the focused app; master: +H/L adjusts width, +M promotes; +1-9 switches workspaces; +Shift+1-9 moves a window. {tilingStatus} {dragStatus} {focusStatus}";
    }

    private void QueueSwitchWorkspace(int workspace)
    {
        if (workspace > _preferences.WorkspaceCount)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() => SwitchWorkspace(workspace)))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to switch workspaces.");
        }
    }

    private void QueueMoveWindow(int workspace)
    {
        if (workspace > _preferences.WorkspaceCount)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() => MoveWindowToWorkspace(workspace)))
        {
            throw new InvalidOperationException("The UI dispatcher is not available to move the window.");
        }
    }

    private void SwitchWorkspace(int workspace)
    {
        try
        {
            var count = _windowTilingService.SwitchWorkspace(workspace);
            EngineDetailText.Text = $"Workspace {workspace}: arranged {count} windows.";
            RefreshWorkspaceButtons();
            RefreshStatusBars();
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private void MoveWindowToWorkspace(int workspace)
    {
        try
        {
            var count = _windowTilingService.MoveForegroundWindowToWorkspace(workspace);
            EngineDetailText.Text = $"Moved the focused window to workspace {workspace}. Arranged {count} visible windows.";
            RefreshWorkspaceButtons();
            RefreshStatusBars();
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private void RebuildWorkspaceButtons()
    {
        WorkspaceButtonsPanel.Children.Clear();
        for (var workspace = 1; workspace <= _preferences.WorkspaceCount; workspace++)
        {
            var button = new Button
            {
                Content = GetWorkspaceLabel(workspace),
                Tag = workspace,
                MinWidth = 42,
                IsEnabled = _windowTilingService.IsRunning
            };
            ToolTipService.SetToolTip(button, $"Switch to workspace {workspace}: {GetWorkspaceLabel(workspace)}");
            button.Click += WorkspaceButton_Click;
            WorkspaceButtonsPanel.Children.Add(button);
        }

        RefreshWorkspaceButtons();
    }

    private void WorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int workspace })
        {
            SwitchWorkspace(workspace);
        }
    }

    private void RefreshWorkspaceButtons()
    {
        if (WorkspaceButtonsPanel is null)
        {
            return;
        }

        ActiveWorkspaceText.Text = $"Active: {GetWorkspaceLabel(_windowTilingService.ActiveWorkspace)}";
        for (var index = 0; index < WorkspaceButtonsPanel.Children.Count; index++)
        {
            if (WorkspaceButtonsPanel.Children[index] is Button button)
            {
                button.IsEnabled = _windowTilingService.IsRunning && index + 1 != _windowTilingService.ActiveWorkspace;
                button.Content = index + 1 == _windowTilingService.ActiveWorkspace
                    ? $"[{GetWorkspaceLabel(index + 1)}]"
                    : GetWorkspaceLabel(index + 1);
            }
        }
    }

    private string GetWorkspaceLabel(int workspace)
    {
        if (workspace > 0 &&
            workspace <= _preferences.WorkspaceNames.Count &&
            !string.IsNullOrWhiteSpace(_preferences.WorkspaceNames[workspace - 1]))
        {
            return _preferences.WorkspaceNames[workspace - 1];
        }

        return workspace.ToString("00");
    }

    private void QueueAutoRetile(WindowChangeKind kind)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleWindowChange(kind);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => HandleWindowChange(kind));
    }

    private void HandleWindowChange(WindowChangeKind kind)
    {
        if (!_windowTilingService.IsRunning)
        {
            return;
        }

        if (kind == WindowChangeKind.MoveSizeStarted)
        {
            // The user is dragging or resizing a window with the mouse. Retiling in the
            // middle of that gesture fights the pointer and is the main source of the
            // visible jitter, so wait until the gesture ends.
            _isUserMovingOrSizing = true;
            _retileTimer.Stop();
            return;
        }

        if (kind == WindowChangeKind.MoveSizeEnded)
        {
            _isUserMovingOrSizing = false;
            _retilePending = false;
            ScheduleRetile(StructureRetileDelay);
            return;
        }

        if (_isUserMovingOrSizing)
        {
            // MOVESIZEEND is what resumes tiling. If it were ever lost, the next
            // move/size gesture ends with another MOVESIZEEND, so this cannot latch.
            return;
        }

        if (kind == WindowChangeKind.Geometry &&
            (_windowTilingService.IsArranging || _windowTilingService.MillisecondsSinceLastPlacement < 250))
        {
            // Our own layout animation reports geometry changes for every frame. Ignoring
            // them breaks the retile feedback loop that made the tiles crawl and jitter.
            return;
        }

        if (_windowTilingService.IsArranging)
        {
            _retilePending = true;
            return;
        }

        ScheduleRetile(kind == WindowChangeKind.Geometry ? GeometryRetileDelay : StructureRetileDelay);
    }

    private void ScheduleRetile(TimeSpan delay)
    {
        _retileTimer.Stop();
        _retileTimer.Interval = delay;
        _retileTimer.Start();
    }

    private void OnLayoutSettled()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_retilePending || _isUserMovingOrSizing || !_windowTilingService.IsRunning)
            {
                return;
            }

            _retilePending = false;
            ScheduleRetile(StructureRetileDelay);
        });
    }

    private void RetileTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _retileTimer.Stop();
        if (!_windowTilingService.IsRunning)
        {
            return;
        }

        if (_isUserMovingOrSizing)
        {
            _retilePending = true;
            return;
        }

        if (_windowTilingService.IsArranging)
        {
            _retilePending = true;
            ScheduleRetile(TimeSpan.FromMilliseconds(60));
            return;
        }

        _retilePending = false;
        try
        {
            var count = _windowTilingService.TileNow();
            EngineDetailText.Text = $"Automatically arranged {count} windows. Press {_preferences.MainModifier}+T to retile now.";
            RefreshStatusBars();
        }
        catch (Exception exception)
        {
            ShowEngineError(exception);
        }
    }

    private void StatusBarTimer_Tick(DispatcherQueueTimer sender, object args) =>
        RefreshStatusBars();

    private void RefreshStatusBars()
    {
        if (!_windowTilingService.IsRunning)
        {
            return;
        }

        try
        {
            if (_statusBarManager.Update(_windowTilingService.GetMonitorStatus()))
            {
                _ = _windowTilingService.TileNow();
            }

            UpdateStatusBarWheelBounds();
        }
        catch (Exception exception)
        {
            _statusBarTimer.Stop();
            ShowEngineError(exception);
        }
    }
}
