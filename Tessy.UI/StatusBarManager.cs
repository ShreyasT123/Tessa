using Tessellation.Core;
using Tessellation.Platform.Windows;

namespace Tessy_UI;

internal sealed class StatusBarManager(Action<int> switchWorkspace, Action cycleLayout)
{
    private readonly Dictionary<IntPtr, StatusBarWindow> _windows = [];

    public IReadOnlyList<RectI> GetStatusBarBounds() =>
        _windows.Values
            .Select(window => window.ScreenBounds)
            .Where(bounds => bounds.IsValid)
            .ToArray();

    public bool Update(IReadOnlyList<MonitorStatusSnapshot> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var activeMonitors = statuses.Select(status => status.Monitor).ToHashSet();
        var shouldRetile = false;

        foreach (var monitor in _windows.Keys.Where(monitor => !activeMonitors.Contains(monitor)).ToArray())
        {
            _windows[monitor].Close();
            _windows.Remove(monitor);
            shouldRetile = true;
        }

        foreach (var status in statuses)
        {
            if (!_windows.TryGetValue(status.Monitor, out var existingWindow) ||
                existingWindow is null ||
                existingWindow.IsClosed)
            {
                var window = new StatusBarWindow();
                window.WorkspaceRequested += switchWorkspace;
                window.LayoutRequested += cycleLayout;
                try
                {
                    shouldRetile |= window.Update(status);
                    _windows[status.Monitor] = window;
                }
                catch
                {
                    window.Close();
                    throw;
                }

                continue;
            }

            shouldRetile |= existingWindow.Update(status);
        }

        return shouldRetile;
    }

    public void Stop()
    {
        var windows = _windows.Values.ToArray();
        _windows.Clear();
        List<Exception>? failures = null;
        foreach (var window in windows)
        {
            try
            {
                window.Close();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more status bars could not be closed cleanly.", failures);
        }
    }
}
