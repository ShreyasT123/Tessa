using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tessellation.Core;

namespace Tessellation.Platform.Windows;

internal sealed partial class WindowRestoreJournal
{
    private const uint SetWindowPosNoSizeOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const int ShowNoActivate = 4;

    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tessellation",
        "window-restore.json");

    private readonly Dictionary<long, RestorableWindow> _windows = [];
    private bool _isDirty;

    public void RestorePending()
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        JournalSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(
                File.ReadAllText(JournalPath),
                JournalJsonContext.Default.JournalSnapshot)
                ?? throw new InvalidDataException("The window restore journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The window restore journal is invalid.", exception);
        }

        if (snapshot.Windows is null || snapshot.Windows.Any(window =>
                window.Handle == 0 ||
                window.ProcessId == 0 ||
                window.ProcessStartTimeUtcTicks <= 0 ||
                !window.Bounds.IsValid))
        {
            throw new InvalidDataException("The window restore journal contains invalid window data.");
        }

        var failures = new List<Exception>();
        foreach (var window in snapshot.Windows)
        {
            var handle = new IntPtr(window.Handle);
            if (!IsWindow(handle) ||
                GetWindowThreadProcessId(handle, out var processId) == 0 ||
                processId != window.ProcessId ||
                !ProcessMatches(window.ProcessId, window.ProcessStartTimeUtcTicks))
            {
                continue;
            }

            var bounds = window.Bounds;
            if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SetWindowPosNoSizeOrder | SetWindowPosNoActivate))
            {
                failures.Add(new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"A window from process {window.ProcessId} could not be restored."));
                continue;
            }

            if (!RestoreVisibility(handle, window.WasVisible))
            {
                failures.Add(new InvalidOperationException(
                    $"A window from process {window.ProcessId} could not be made visible."));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Some windows from the previous Tessellation session could not be restored.", failures);
        }

        Clear();
    }

    public void Record(IntPtr handle, RectI bounds)
    {
        if (handle == IntPtr.Zero)
        {
            throw new ArgumentException("The window handle is invalid.", nameof(handle));
        }

        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            throw new InvalidOperationException("The window's owning process could not be determined.");
        }

        using var process = Process.GetProcessById((int)processId);
        var startTime = process.StartTime.ToUniversalTime().Ticks;
        var window = new RestorableWindow(
            handle.ToInt64(),
            processId,
            startTime,
            bounds,
            IsWindowVisible(handle));
        if (!_windows.TryGetValue(window.Handle, out var existing) || existing != window)
        {
            _windows[window.Handle] = window;
            _isDirty = true;
        }
    }

    public void Remove(IntPtr handle)
    {
        if (_windows.Remove(handle.ToInt64()))
        {
            _isDirty = true;
        }
    }

    public void RestoreVisibility()
    {
        var failures = _windows.Values
            .Where(window => window.WasVisible)
            .Where(window => !RestoreVisibility(new IntPtr(window.Handle), wasVisible: true))
            .Select(window => new InvalidOperationException(
                $"A window from process {window.ProcessId} could not be made visible."))
            .ToArray();

        if (failures.Length > 0)
        {
            throw new AggregateException("Some windows could not be made visible.", failures);
        }
    }

    public void Save()
    {
        if (!_isDirty)
        {
            return;
        }

        if (_windows.Count == 0)
        {
            Clear();
            return;
        }

        var directory = Path.GetDirectoryName(JournalPath)
            ?? throw new InvalidOperationException("The window restore journal directory could not be determined.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{JournalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new JournalSnapshot(_windows.Values.ToArray()),
                    JournalJsonContext.Default.JournalSnapshot);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, JournalPath, overwrite: true);
            _isDirty = false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Clear()
    {
        if (File.Exists(JournalPath))
        {
            File.Delete(JournalPath);
        }

        _windows.Clear();
        _isDirty = false;
    }

    private static bool ProcessMatches(uint processId, long expectedStartTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool RestoreVisibility(IntPtr handle, bool wasVisible)
    {
        if (!wasVisible || !IsWindow(handle))
        {
            return true;
        }

        if (!IsWindowVisible(handle) && !ShowWindowAsync(handle, ShowNoActivate))
        {
            return false;
        }

        var deadline = Environment.TickCount64 + 1000;
        while (IsWindow(handle) && !IsWindowVisible(handle) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }

        return !IsWindow(handle) || IsWindowVisible(handle);
    }

    private sealed record JournalSnapshot(IReadOnlyList<RestorableWindow> Windows);

    private sealed record RestorableWindow(
        long Handle,
        uint ProcessId,
        long ProcessStartTimeUtcTicks,
        RectI Bounds,
        bool WasVisible);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(JournalSnapshot))]
    private partial class JournalJsonContext : JsonSerializerContext
    {
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
