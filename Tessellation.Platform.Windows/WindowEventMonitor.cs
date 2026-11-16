using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tessellation.Platform.Windows;

public sealed class WindowEventMonitor
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly List<IntPtr> _hooks = [];
    private readonly WinEventProc _callback;
    private Action? _onWindowChanged;
    private int _processId;

    public WindowEventMonitor()
    {
        _callback = OnWinEvent;
    }

    public bool IsRunning => _hooks.Count > 0;

    public void Start(Action onWindowChanged)
    {
        ArgumentNullException.ThrowIfNull(onWindowChanged);
        if (IsRunning)
        {
            throw new InvalidOperationException("Window event monitoring is already running.");
        }

        _processId = Environment.ProcessId;
        _onWindowChanged = onWindowChanged;
        try
        {
            Hook(EventObjectCreate);
            Hook(EventObjectDestroy);
            Hook(EventObjectShow);
            Hook(EventObjectHide);
            Hook(EventObjectLocationChange);
        }
        catch
        {
            UnhookAll();
            _onWindowChanged = null;
            throw;
        }
    }

    public void Stop()
    {
        var failures = UnhookAll();
        _onWindowChanged = null;
        if (failures.Count > 0)
        {
            throw new AggregateException("One or more window event hooks could not be removed.", failures);
        }
    }

    private void Hook(uint eventType)
    {
        var hook = SetWinEventHook(
            eventType,
            eventType,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext);
        if (hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows event hook 0x{eventType:X} could not be installed.");
        }

        _hooks.Add(hook);
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _ = hook;
        _ = eventThread;
        _ = eventTime;

        if (window == IntPtr.Zero || objectId != ObjectIdWindow || childId != ChildIdSelf)
        {
            return;
        }

        _ = eventType;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == _processId)
        {
            return;
        }

        _onWindowChanged?.Invoke();
    }

    private List<Exception> UnhookAll()
    {
        var failures = new List<Exception>();
        foreach (var hook in _hooks)
        {
            if (!UnhookWinEvent(hook))
            {
                failures.Add(new Win32Exception(Marshal.GetLastWin32Error(), "A Windows event hook could not be removed."));
            }
        }

        _hooks.Clear();
        return failures;
    }

    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
