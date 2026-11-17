using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Tessellation.Core;

namespace Tessellation.Input;

public sealed class GlobalMouseDragService
{
    private const int HookMouseLowLevel = 14;
    private const int MessageMouseMove = 0x0200;
    private const int MessageLeftButtonDown = 0x0201;
    private const int MessageLeftButtonUp = 0x0202;
    private const int MessageRightButtonDown = 0x0204;
    private const int MessageRightButtonUp = 0x0205;
    private const int MessageMouseWheel = 0x020A;
    private const int MessageMouseHorizontalWheel = 0x020E;
    private const uint MessageQuit = 0x0012;
    private const uint PeekNoRemove = 0;
    private const uint WindowAncestorRoot = 2;
    private const uint PositionNoZOrder = 0x0004;
    private const uint PositionNoActivate = 0x0010;
    private const uint PositionAsync = 0x4000;
    private const int ModifierAlt = 0x12;
    private const int ModifierControl = 0x11;
    private const int ModifierLeftWindows = 0x5B;
    private const int ModifierRightWindows = 0x5C;
    private const int MinimumWindowWidth = 120;
    private const int MinimumWindowHeight = 80;
    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan FocusRequestInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FocusHoverDelay = TimeSpan.FromMilliseconds(180);

    private readonly LowLevelMouseProc _callback;
    private Channel<DragCommand>? _commands;
    private Channel<NativePoint>? _focusRequests;
    private Thread? _hookThread;
    private Task? _workerTask;
    private Task? _focusWorkerTask;
    private uint _threadId;
    private Action<Exception>? _onFailure;
    private Action<IntPtr>? _onDragStarted;
    private Action<int>? _onStatusBarWheel;
    private Func<IntPtr, bool>? _isEligibleWindow;
    private RectI[] _statusBarBounds = [];
    private string? _mainModifier;
    private DragSession? _activeDrag;
    private bool _allowAltDrag;
    private bool _focusFollowsMouse;
    private volatile bool _isStopping;
    private DateTime _lastFocusRequestAt;
    private int _barWheelRemainder;

    public GlobalMouseDragService()
    {
        _callback = MouseHookCallback;
    }

    public bool IsRunning => _hookThread is { IsAlive: true };

    public void UpdateStatusBarWheel(Action<int>? onStatusBarWheel, IReadOnlyList<RectI> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        Volatile.Write(ref _onStatusBarWheel, onStatusBarWheel);
        Volatile.Write(ref _statusBarBounds, bounds.ToArray());
    }

    public Task StartAsync(
        string mainModifier,
        bool allowAltDrag,
        bool focusFollowsMouse,
        Func<IntPtr, bool> isEligibleWindow,
        Action<IntPtr> onDragStarted,
        Action<Exception> onFailure,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            mainModifier,
            allowAltDrag,
            focusFollowsMouse,
            isEligibleWindow,
            onDragStarted,
            onFailure,
            null,
            cancellationToken);

    public async Task StartAsync(
        string mainModifier,
        bool allowAltDrag,
        bool focusFollowsMouse,
        Func<IntPtr, bool> isEligibleWindow,
        Action<IntPtr> onDragStarted,
        Action<Exception> onFailure,
        Action<int>? onStatusBarWheel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isEligibleWindow);
        ArgumentNullException.ThrowIfNull(onDragStarted);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (allowAltDrag && mainModifier is not ("Alt" or "Win" or "Ctrl+Win"))
        {
            throw new NotSupportedException($"Mouse gestures do not support the {mainModifier} modifier.");
        }

        if (!allowAltDrag && !focusFollowsMouse && onStatusBarWheel is null)
        {
            throw new ArgumentException("At least one mouse feature or status-bar wheel action must be enabled.");
        }

        if (_hookThread is { IsAlive: true })
        {
            throw new InvalidOperationException("The global mouse drag hook is already running.");
        }

        _mainModifier = mainModifier;
        _allowAltDrag = allowAltDrag;
        _focusFollowsMouse = focusFollowsMouse;
        _isEligibleWindow = isEligibleWindow;
        _onDragStarted = onDragStarted;
        _onFailure = onFailure;
        _onStatusBarWheel = onStatusBarWheel;
        _isStopping = false;
        _barWheelRemainder = 0;
        _lastFocusRequestAt = DateTime.MinValue;
        // Only the newest pointer position matters while dragging. Keeping one slot lets
        // a burst of hook messages coalesce instead of replaying stale positions, which
        // looked like lag and jitter in the dragged window.
        _commands = Channel.CreateBounded<DragCommand>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        _focusRequests = Channel.CreateBounded<NativePoint>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workerTask = Task.Run(() => ProcessCommandsAsync(_commands.Reader));
        _focusWorkerTask = Task.Run(() => ProcessFocusRequestsAsync(_focusRequests.Reader));
        _hookThread = new Thread(() => HookMessageLoop(ready))
        {
            IsBackground = true,
            Name = "Tessellation mouse drag"
        };
        _hookThread.Start();

        try
        {
            await ready.Task.WaitAsync(cancellationToken);
        }
        catch (Exception startException)
        {
            try
            {
                await StopAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException("The mouse drag hook failed to start and cleanup was incomplete.", startException, cleanupException);
            }

            throw;
        }
    }

    public async Task StopAsync()
    {
        var hookThread = _hookThread;
        if (hookThread is not null && hookThread.IsAlive)
        {
            _isStopping = true;
            if (!PostThreadMessage(_threadId, MessageQuit, UIntPtr.Zero, IntPtr.Zero) && hookThread.IsAlive)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The mouse hook message loop could not be stopped.");
            }

            if (!hookThread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The mouse hook message loop did not stop in time.");
            }
        }

        _commands?.Writer.TryComplete();
        _focusRequests?.Writer.TryComplete();
        if (_workerTask is not null)
        {
            await _workerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (_focusWorkerTask is not null)
        {
            await _focusWorkerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        _hookThread = null;
        _workerTask = null;
        _focusWorkerTask = null;
        _threadId = 0;
        _activeDrag = null;
        _commands = null;
        _focusRequests = null;
        _isEligibleWindow = null;
        _onDragStarted = null;
        _onStatusBarWheel = null;
        _onFailure = null;
        _mainModifier = null;
        _allowAltDrag = false;
        _focusFollowsMouse = false;
        _barWheelRemainder = 0;
        UpdateStatusBarWheel(null, []);
    }

    private void HookMessageLoop(TaskCompletionSource ready)
    {
        _threadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PeekNoRemove);

        var hook = SetWindowsHookEx(HookMouseLowLevel, _callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(), "The global mouse hook could not be installed."));
            return;
        }

        ready.TrySetResult();
        try
        {
            while (true)
            {
                var result = GetMessage(out _, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), "The mouse hook message loop failed."));
                    break;
                }
            }
        }
        finally
        {
            _activeDrag = null;
            if (!UnhookWindowsHookEx(hook))
            {
                ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), "The global mouse hook could not be removed."));
            }
        }
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _isStopping)
        {
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        try
        {
            var messageId = message.ToInt32();
            var mouse = Marshal.PtrToStructure<MouseHookData>(data);
            var point = mouse.Point;

            if (messageId is MessageLeftButtonDown or MessageRightButtonDown)
            {
                if (_allowAltDrag && _activeDrag is null && MainModifierIsPressed() &&
                    TryBeginDrag(point, messageId == MessageRightButtonDown))
                {
                    return new IntPtr(1);
                }
            }
            else if (messageId is MessageMouseWheel or MessageMouseHorizontalWheel)
            {
                if (TryRouteStatusBarWheel(point, mouse.MouseData))
                {
                    return new IntPtr(1);
                }

                _barWheelRemainder = 0;
            }
            else if (messageId == MessageMouseMove && _activeDrag is { } drag)
            {
                if (DateTime.UtcNow - drag.LastQueuedAt >= MinimumUpdateInterval)
                {
                    drag.LastQueuedAt = DateTime.UtcNow;
                    _commands?.Writer.TryWrite(new DragCommand(drag, point));
                }

                return new IntPtr(1);
            }
            else if (messageId == MessageMouseMove && _focusFollowsMouse &&
                     DateTime.UtcNow - _lastFocusRequestAt >= FocusRequestInterval)
            {
                _lastFocusRequestAt = DateTime.UtcNow;
                _focusRequests?.Writer.TryWrite(point);
            }
            else if (messageId is MessageLeftButtonUp or MessageRightButtonUp &&
                     _activeDrag is { } active &&
                     active.IsResize == (messageId == MessageRightButtonUp))
            {
                _activeDrag = null;
                _commands?.Writer.TryWrite(new DragCommand(active, point));
                return new IntPtr(1);
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private bool TryRouteStatusBarWheel(NativePoint point, uint mouseData)
    {
        var delta = unchecked((short)(mouseData >> 16));
        var onStatusBarWheel = Volatile.Read(ref _onStatusBarWheel);
        if (delta == 0 || onStatusBarWheel is null)
        {
            return false;
        }

        var bounds = Volatile.Read(ref _statusBarBounds);
        if (!bounds.Any(rect =>
                point.X >= rect.Left && point.X < rect.Right &&
                point.Y >= rect.Top && point.Y < rect.Bottom))
        {
            return false;
        }

        _barWheelRemainder += delta;
        var steps = _barWheelRemainder / 120;
        _barWheelRemainder %= 120;
        if (steps != 0)
        {
            onStatusBarWheel(steps);
        }

        return true;
    }

    private bool TryBeginDrag(NativePoint point, bool resize)
    {
        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        window = GetAncestor(window, WindowAncestorRoot);
        if (window == IntPtr.Zero || _isEligibleWindow?.Invoke(window) != true || !GetWindowRect(window, out var bounds))
        {
            return false;
        }

        var session = new DragSession(window, bounds, point, resize);
        _onDragStarted?.Invoke(window);
        _activeDrag = session;
        session.LastQueuedAt = DateTime.UtcNow;
        _commands?.Writer.TryWrite(new DragCommand(session, point));
        return true;
    }

    private async Task ProcessCommandsAsync(ChannelReader<DragCommand> commands)
    {
        await foreach (var command in commands.ReadAllAsync())
        {
            try
            {
                if (!IsWindow(command.Session.Window))
                {
                    continue;
                }

                var target = command.Session.IsResize
                    ? ResizeRect(command.Session, command.Point)
                    : MoveRect(command.Session, command.Point);
                if (!SetWindowPos(
                    command.Session.Window,
                    IntPtr.Zero,
                    target.Left,
                    target.Top,
                    target.Right - target.Left,
                    target.Bottom - target.Top,
                    PositionNoZOrder | PositionNoActivate | PositionAsync))
                {
                    ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), "The dragged window could not be repositioned."));
                }
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private async Task ProcessFocusRequestsAsync(ChannelReader<NativePoint> requests)
    {
        await foreach (var request in requests.ReadAllAsync())
        {
            try
            {
                await Task.Delay(FocusHoverDelay);
                var point = request;
                while (requests.TryRead(out var latest))
                {
                    point = latest;
                }

                if (!GetCursorPos(out var cursor) || cursor.X != point.X || cursor.Y != point.Y)
                {
                    continue;
                }

                var window = WindowFromPoint(point);
                if (window == IntPtr.Zero)
                {
                    continue;
                }

                window = GetAncestor(window, WindowAncestorRoot);
                if (window == IntPtr.Zero || _isEligibleWindow?.Invoke(window) != true ||
                    GetForegroundWindow() == window)
                {
                    continue;
                }

                _ = SetForegroundWindow(window);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    private bool MainModifierIsPressed() =>
        _mainModifier switch
        {
            "Alt" => IsKeyDown(ModifierAlt),
            "Win" => IsKeyDown(ModifierLeftWindows) || IsKeyDown(ModifierRightWindows),
            "Ctrl+Win" => IsKeyDown(ModifierControl) &&
                          (IsKeyDown(ModifierLeftWindows) || IsKeyDown(ModifierRightWindows)),
            _ => false
        };

    private void ReportFailure(Exception exception) => _onFailure?.Invoke(exception);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static NativeRect MoveRect(DragSession session, NativePoint point)
    {
        var deltaX = point.X - session.StartPoint.X;
        var deltaY = point.Y - session.StartPoint.Y;
        return new NativeRect(
            session.StartBounds.Left + deltaX,
            session.StartBounds.Top + deltaY,
            session.StartBounds.Right + deltaX,
            session.StartBounds.Bottom + deltaY);
    }

    private static NativeRect ResizeRect(DragSession session, NativePoint point)
    {
        var start = session.StartBounds;
        var left = session.ResizeFromLeft ? point.X : start.Left;
        var right = session.ResizeFromLeft ? start.Right : point.X;
        var top = session.ResizeFromTop ? point.Y : start.Top;
        var bottom = session.ResizeFromTop ? start.Bottom : point.Y;

        if (right - left < MinimumWindowWidth)
        {
            if (session.ResizeFromLeft)
            {
                left = right - MinimumWindowWidth;
            }
            else
            {
                right = left + MinimumWindowWidth;
            }
        }

        if (bottom - top < MinimumWindowHeight)
        {
            if (session.ResizeFromTop)
            {
                top = bottom - MinimumWindowHeight;
            }
            else
            {
                bottom = top + MinimumWindowHeight;
            }
        }

        return new NativeRect(left, top, right, bottom);
    }

    private sealed class DragSession
    {
        public DragSession(IntPtr window, NativeRect bounds, NativePoint point, bool isResize)
        {
            Window = window;
            StartBounds = bounds;
            StartPoint = point;
            IsResize = isResize;
            ResizeFromLeft = point.X < (bounds.Left + bounds.Right) / 2;
            ResizeFromTop = point.Y < (bounds.Top + bounds.Bottom) / 2;
        }

        public IntPtr Window { get; }

        public NativeRect StartBounds { get; }

        public NativePoint StartPoint { get; }

        public bool IsResize { get; }

        public bool ResizeFromLeft { get; }

        public bool ResizeFromTop { get; }

        public DateTime LastQueuedAt { get; set; }
    }

    private readonly record struct DragCommand(DragSession Session, NativePoint Point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }
}
