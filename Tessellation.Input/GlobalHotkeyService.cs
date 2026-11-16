using System.ComponentModel;
using System.Runtime.InteropServices;
using Tessellation.Core;

namespace Tessellation.Input;

public sealed class GlobalHotkeyService
{
    private const int RetileHotkeyId = 1;
    private const int FocusNextHotkeyId = 2;
    private const int FocusPreviousHotkeyId = 3;
    private const int SwapNextHotkeyId = 4;
    private const int SwapPreviousHotkeyId = 5;
    private const int FocusLeftHotkeyId = 6;
    private const int FocusRightHotkeyId = 7;
    private const int FocusUpHotkeyId = 8;
    private const int FocusDownHotkeyId = 9;
    private const int MoveLeftHotkeyId = 30;
    private const int MoveRightHotkeyId = 31;
    private const int MoveUpHotkeyId = 32;
    private const int MoveDownHotkeyId = 33;
    private const int MasterShrinkHotkeyId = 34;
    private const int MasterGrowHotkeyId = 35;
    private const int PromoteMasterHotkeyId = 36;
    private const int ToggleFloatHotkeyId = 37;
    private const int CloseWindowHotkeyId = 38;
    private const int CycleScrollWidthHotkeyId = 39;
    private const int SwitchHotkeyIdBase = 10;
    private const int MoveHotkeyIdBase = 20;
    private const uint MessageHotkey = 0x0312;
    private const uint MessageQuit = 0x0012;
    private const uint PeekNoRemove = 0;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierWin = 0x0008;
    private const uint ModifierNoRepeat = 0x4000;
    private const uint VirtualKeyT = 0x54;
    private const uint VirtualKeyJ = 0x4A;
    private const uint VirtualKeyK = 0x4B;
    private const uint VirtualKeyLeft = 0x25;
    private const uint VirtualKeyUp = 0x26;
    private const uint VirtualKeyRight = 0x27;
    private const uint VirtualKeyDown = 0x28;
    private const uint VirtualKeyH = 0x48;
    private const uint VirtualKeyL = 0x4C;
    private const uint VirtualKeyM = 0x4D;
    private const uint VirtualKeyF = 0x46;
    private const uint VirtualKeyW = 0x57;
    private const uint VirtualKeyR = 0x52;
    private const uint VirtualKey1 = 0x31;
    private const uint WorkspaceHotkeyCount = 9;

    private Thread? _thread;
    private uint _threadId;
    private Action<int>? _onPressed;
    private Action<Exception>? _onFailure;

    public async Task StartAsync(
        string mainModifier,
        Action onToggleTiling,
        Action onFocusNext,
        Action onFocusPrevious,
        Action<int> onSwapWindow,
        Action<CardinalDirection> onFocusDirection,
        Action<CardinalDirection> onMoveWindowDirection,
        Action<double> onAdjustMasterRatio,
        Action onPromoteMaster,
        Action onToggleFloat,
        Action onCloseWindow,
        Action onCycleScrollWidth,
        Action<int> onSwitchWorkspace,
        Action<int> onMoveWindow,
        Action<Exception> onFailure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onToggleTiling);
        ArgumentNullException.ThrowIfNull(onFocusNext);
        ArgumentNullException.ThrowIfNull(onFocusPrevious);
        ArgumentNullException.ThrowIfNull(onSwapWindow);
        ArgumentNullException.ThrowIfNull(onFocusDirection);
        ArgumentNullException.ThrowIfNull(onMoveWindowDirection);
        ArgumentNullException.ThrowIfNull(onAdjustMasterRatio);
        ArgumentNullException.ThrowIfNull(onPromoteMaster);
        ArgumentNullException.ThrowIfNull(onToggleFloat);
        ArgumentNullException.ThrowIfNull(onCloseWindow);
        ArgumentNullException.ThrowIfNull(onCycleScrollWidth);
        ArgumentNullException.ThrowIfNull(onSwitchWorkspace);
        ArgumentNullException.ThrowIfNull(onMoveWindow);
        ArgumentNullException.ThrowIfNull(onFailure);
        var modifiers = GetModifiers(mainModifier);

        if (_thread is { IsAlive: true })
        {
            throw new InvalidOperationException("The global hotkey is already registered.");
        }

        _thread = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new Dictionary<int, Action>
        {
            [RetileHotkeyId] = onToggleTiling,
            [FocusNextHotkeyId] = onFocusNext,
            [FocusPreviousHotkeyId] = onFocusPrevious,
            [SwapNextHotkeyId] = () => onSwapWindow(1),
            [SwapPreviousHotkeyId] = () => onSwapWindow(-1),
            [FocusLeftHotkeyId] = () => onFocusDirection(CardinalDirection.Left),
            [FocusRightHotkeyId] = () => onFocusDirection(CardinalDirection.Right),
            [FocusUpHotkeyId] = () => onFocusDirection(CardinalDirection.Up),
            [FocusDownHotkeyId] = () => onFocusDirection(CardinalDirection.Down),
            [MoveLeftHotkeyId] = () => onMoveWindowDirection(CardinalDirection.Left),
            [MoveRightHotkeyId] = () => onMoveWindowDirection(CardinalDirection.Right),
            [MoveUpHotkeyId] = () => onMoveWindowDirection(CardinalDirection.Up),
            [MoveDownHotkeyId] = () => onMoveWindowDirection(CardinalDirection.Down),
            [MasterShrinkHotkeyId] = () => onAdjustMasterRatio(-0.05),
            [MasterGrowHotkeyId] = () => onAdjustMasterRatio(0.05),
            [PromoteMasterHotkeyId] = onPromoteMaster,
            [ToggleFloatHotkeyId] = onToggleFloat,
            [CloseWindowHotkeyId] = onCloseWindow,
            [CycleScrollWidthHotkeyId] = onCycleScrollWidth
        };
        for (var workspace = 1; workspace <= WorkspaceHotkeyCount; workspace++)
        {
            var workspaceNumber = workspace;
            actions[SwitchHotkeyIdBase + workspaceNumber] = () => onSwitchWorkspace(workspaceNumber);
            actions[MoveHotkeyIdBase + workspaceNumber] = () => onMoveWindow(workspaceNumber);
        }

        _onPressed = id => actions.GetValueOrDefault(id)?.Invoke();
        _onFailure = onFailure;
        _thread = new Thread(() => MessageLoop(modifiers, actions, ready))
        {
            IsBackground = true,
            Name = "Tessellation hotkeys"
        };
        _thread.Start();

        try
        {
            await ready.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (_thread is { IsAlive: false })
            {
                _thread = null;
                _threadId = 0;
                _onPressed = null;
                _onFailure = null;
            }

            throw;
        }
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        if (thread.IsAlive)
        {
            if (!PostThreadMessage(_threadId, MessageQuit, UIntPtr.Zero, IntPtr.Zero) && thread.IsAlive)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The hotkey message loop could not be stopped.");
            }

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The hotkey message loop did not stop in time.");
            }
        }

        _thread = null;
        _threadId = 0;
        _onPressed = null;
        _onFailure = null;
    }

    private void MessageLoop(uint modifiers, IReadOnlyDictionary<int, Action> actions, TaskCompletionSource ready)
    {
        _threadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PeekNoRemove);

        var registeredIds = new List<int>();
        try
        {
            Register(RetileHotkeyId, modifiers, VirtualKeyT, "toggle tiling");
            var registrationFailures = new List<Exception>();
            TryRegister(FocusNextHotkeyId, modifiers, VirtualKeyJ, "focus the next window", registrationFailures);
            TryRegister(FocusPreviousHotkeyId, modifiers, VirtualKeyK, "focus the previous window", registrationFailures);
            TryRegister(SwapNextHotkeyId, modifiers | ModifierShift, VirtualKeyJ, "swap with the next window", registrationFailures);
            TryRegister(SwapPreviousHotkeyId, modifiers | ModifierShift, VirtualKeyK, "swap with the previous window", registrationFailures);
            TryRegister(FocusLeftHotkeyId, modifiers, VirtualKeyLeft, "focus the window to the left", registrationFailures);
            TryRegister(FocusRightHotkeyId, modifiers, VirtualKeyRight, "focus the window to the right", registrationFailures);
            TryRegister(FocusUpHotkeyId, modifiers, VirtualKeyUp, "focus the window above", registrationFailures);
            TryRegister(FocusDownHotkeyId, modifiers, VirtualKeyDown, "focus the window below", registrationFailures);
            TryRegister(MoveLeftHotkeyId, modifiers | ModifierShift, VirtualKeyLeft, "move the window left", registrationFailures);
            TryRegister(MoveRightHotkeyId, modifiers | ModifierShift, VirtualKeyRight, "move the window right", registrationFailures);
            TryRegister(MoveUpHotkeyId, modifiers | ModifierShift, VirtualKeyUp, "move the window up", registrationFailures);
            TryRegister(MoveDownHotkeyId, modifiers | ModifierShift, VirtualKeyDown, "move the window down", registrationFailures);
            TryRegister(MasterShrinkHotkeyId, modifiers, VirtualKeyH, "shrink the master pane", registrationFailures);
            TryRegister(MasterGrowHotkeyId, modifiers, VirtualKeyL, "grow the master pane", registrationFailures);
            TryRegister(PromoteMasterHotkeyId, modifiers, VirtualKeyM, "promote the focused window", registrationFailures);
            TryRegister(ToggleFloatHotkeyId, modifiers, VirtualKeyF, "toggle floating for the focused window", registrationFailures);
            TryRegister(CloseWindowHotkeyId, modifiers, VirtualKeyW, "close the focused window", registrationFailures);
            TryRegister(CycleScrollWidthHotkeyId, modifiers, VirtualKeyR, "cycle scrolling column width", registrationFailures);
            for (var workspace = 1; workspace <= WorkspaceHotkeyCount; workspace++)
            {
                var key = VirtualKey1 + (uint)workspace - 1;
                TryRegister(SwitchHotkeyIdBase + workspace, modifiers, key, $"switch to workspace {workspace}", registrationFailures);
                TryRegister(MoveHotkeyIdBase + workspace, modifiers | ModifierShift, key, $"move a window to workspace {workspace}", registrationFailures);
            }

            ready.TrySetResult();
            if (registrationFailures.Count > 0)
            {
                ReportFailure(new AggregateException("Some optional shortcuts were unavailable.", registrationFailures));
            }

            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), "The hotkey message loop failed."));
                    break;
                }

                if (message.Message == MessageHotkey && actions.ContainsKey(checked((int)message.WParam.ToUInt32())))
                {
                    try
                    {
                        _onPressed?.Invoke(checked((int)message.WParam.ToUInt32()));
                    }
                    catch (Exception exception)
                    {
                        ReportFailure(exception);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!ready.TrySetException(exception))
            {
                ReportFailure(exception);
            }
        }
        finally
        {
            foreach (var id in registeredIds)
            {
                if (!UnregisterHotKey(IntPtr.Zero, id))
                {
                    ReportFailure(new Win32Exception(Marshal.GetLastWin32Error(), $"Global shortcut {id} could not be unregistered."));
                }
            }
        }

        void Register(int id, uint extraModifiers, uint key, string action)
        {
            if (!RegisterHotKey(IntPtr.Zero, id, modifiers | extraModifiers | ModifierNoRepeat, key))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"The global shortcut to {action} could not be registered. It may already be in use.");
            }

            registeredIds.Add(id);
        }

        void TryRegister(
            int id,
            uint extraModifiers,
            uint key,
            string action,
            ICollection<Exception> failures)
        {
            try
            {
                Register(id, extraModifiers, key, action);
            }
            catch (Win32Exception exception)
            {
                failures.Add(exception);
            }
        }
    }

    private void ReportFailure(Exception exception) => _onFailure?.Invoke(exception);

    private static uint GetModifiers(string mainModifier) =>
        mainModifier switch
        {
            "Alt" => ModifierAlt,
            "Win" => ModifierWin,
            "Ctrl+Win" => ModifierControl | ModifierWin,
            "Caps Lock" => throw new NotSupportedException("Caps Lock cannot be used as a global hotkey modifier yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(mainModifier), mainModifier, "Unknown main modifier.")
        };

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
}
