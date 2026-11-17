using Microsoft.UI.Xaml;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tessy_UI;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _trayIconService;
    private MainPage? _mainPage;
    private Task? _startupTask;
    private bool _isQuitting;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 960));

        var windowHandle = WindowNative.GetWindowHandle(this);
        _trayIconService = new TrayIconService(
            windowHandle,
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ShowSettings,
            Quit,
            SessionEnding,
            ShowTrayError);

        RootFrame.Navigate(typeof(MainPage));
        _mainPage = (MainPage)RootFrame.Content;
        _mainPage.StartupCompleted += StartupCompleted;
        _startupTask = _mainPage.InitializeAsync();
    }

    private void StartupCompleted(bool success)
    {
        if (!success)
        {
            ShowSettings();
        }
    }

    private void ShowSettings() => _trayIconService.ShowWindow();

    private void SessionEnding()
    {
        if (_isQuitting || _startupTask?.IsCompleted != true)
        {
            return;
        }

        _mainPage?.StopEngineForSessionEnd();
    }

    private void ShowTrayError(Exception exception)
    {
        _trayIconService.ShowWindow();
        _mainPage?.ShowLifecycleError(exception);
    }

    private async void Quit()
    {
        if (_isQuitting)
        {
            return;
        }

        _isQuitting = true;
        try
        {
            if (_startupTask is not null)
            {
                await _startupTask;
            }

            if (_mainPage is not null)
            {
                await _mainPage.StopEngineAsync();
            }

            _trayIconService.Dispose();
            _trayIconService.CloseApplication();
            // CloseApplication only marks the window closable; the window still has to be
            // closed for WinUI to shut down, otherwise the process lingers with no tray icon.
            Close();
        }
        catch (Exception exception)
        {
            _isQuitting = false;
            ShowSettings();
            _mainPage?.ShowLifecycleError(exception);
        }
    }
}
