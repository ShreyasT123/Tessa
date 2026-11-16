using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tessy_UI;

internal sealed class TrayIconService : IDisposable
{
    private const uint MessageClose = 0x0010;
    private const uint MessageEndSession = 0x0016;
    private const uint MessageDestroy = 0x0002;
    private const uint MessageNcDestroy = 0x0082;
    private const uint MessageContextMenu = 0x007B;
    private const uint MessageApp = 0x8000;
    private const uint NotifyAdd = 0;
    private const uint NotifyDelete = 2;
    private const uint NotifyIcon = 2;
    private const uint NotifyMessage = 1;
    private const uint NotifyTip = 4;
    private const uint MouseLeftUp = 0x0202;
    private const uint MouseRightUp = 0x0205;
    private const uint MenuString = 0;
    private const uint MenuRightButton = 2;
    private const uint MenuReturnCommand = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const int ShowHide = 0;
    private const int ShowRestore = 9;
    private const uint CommandSettings = 1001;
    private const uint CommandQuit = 1002;

    private readonly IntPtr _window;
    private readonly Action _showSettings;
    private readonly Action _quit;
    private readonly Action _sessionEnding;
    private readonly Action<Exception> _onFailure;
    private readonly SubclassProc _subclassProc;
    private readonly uint _callbackMessage = MessageApp + 32;
    private readonly uint _taskbarCreatedMessage;
    private readonly UIntPtr _subclassId = new(1);
    private readonly IntPtr _icon;
    private bool _allowClose;
    private bool _disposed;

    public TrayIconService(
        IntPtr window,
        string iconPath,
        Action showSettings,
        Action quit,
        Action sessionEnding,
        Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(showSettings);
        ArgumentNullException.ThrowIfNull(quit);
        ArgumentNullException.ThrowIfNull(sessionEnding);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("The application window handle is invalid.", nameof(window));
        }

        _window = window;
        _showSettings = showSettings;
        _quit = quit;
        _sessionEnding = sessionEnding;
        _onFailure = onFailure;
        _subclassProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The taskbar restart notification could not be registered.");
        }

        _icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile);
        if (_icon == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray icon could not be loaded.");
        }

        if (!SetWindowSubclass(_window, _subclassProc, _subclassId, UIntPtr.Zero))
        {
            DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray window handler could not be installed.");
        }

        try
        {
            AddIcon();
        }
        catch
        {
            _ = RemoveWindowSubclass(_window, _subclassProc, _subclassId);
            DestroyIcon(_icon);
            throw;
        }
    }

    public void ShowWindow()
    {
        _ = ShowWindowNative(_window, ShowRestore);
        _ = SetForegroundWindow(_window);
    }

    public void CloseApplication()
    {
        _allowClose = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShellNotifyIcon(NotifyDelete, CreateIconData());
        _ = RemoveWindowSubclass(_window, _subclassProc, _subclassId);
        DestroyIcon(_icon);
    }

    private IntPtr WindowProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        _ = subclassId;
        _ = referenceData;

        try
        {
            if (message == _taskbarCreatedMessage)
            {
                AddIcon();
            }
            else if (message == _callbackMessage)
            {
                var notification = unchecked((uint)lParam.ToInt64());
                if (notification is MouseLeftUp or 0x0400)
                {
                    _showSettings();
                }
                else if (notification is MouseRightUp or MessageContextMenu)
                {
                    ShowContextMenu();
                }

                return IntPtr.Zero;
            }
            else if (message == MessageClose && !_allowClose)
            {
                _ = ShowWindowNative(_window, ShowHide);
                return IntPtr.Zero;
            }
            else if (message == MessageEndSession && wParam != UIntPtr.Zero)
            {
                _sessionEnding();
            }
            else if (message == MessageDestroy)
            {
                _ = ShellNotifyIcon(NotifyDelete, CreateIconData());
            }
            else if (message == MessageNcDestroy)
            {
                _ = RemoveWindowSubclass(_window, _subclassProc, _subclassId);
            }
        }
        catch (Exception exception)
        {
            _onFailure(exception);
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void AddIcon()
    {
        if (!ShellNotifyIcon(NotifyAdd, CreateIconData()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray icon could not be added.");
        }
    }

    private NotifyIconData CreateIconData() =>
        new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _window,
            Id = 1,
            Flags = NotifyMessage | NotifyIcon | NotifyTip,
            CallbackMessage = _callbackMessage,
            Icon = _icon,
            Tip = "Tessellation"
        };

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The tray menu could not be created.");
        }

        try
        {
            if (!AppendMenu(menu, MenuString, CommandSettings, "Settings") ||
                !AppendMenu(menu, MenuString, CommandQuit, "Quit"))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The tray menu items could not be created.");
            }

            if (!GetCursorPos(out var point))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The tray menu position could not be read.");
            }

            _ = SetForegroundWindow(_window);
            var command = TrackPopupMenu(
                menu,
                MenuReturnCommand | MenuRightButton,
                point.X,
                point.Y,
                0,
                _window,
                IntPtr.Zero);
            if (command == CommandSettings)
            {
                _showSettings();
            }
            else if (command == CommandQuit)
            {
                _quit();
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private delegate IntPtr SubclassProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowNative(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

}
