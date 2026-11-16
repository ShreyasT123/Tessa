using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Tessy_UI;

internal sealed class StartupRegistrationService
{
    private const string StartupTaskId = "TessellationStartupTask";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Tessellation";
    private const uint ErrorInsufficientBuffer = 122;
    private const uint AppModelErrorNoPackage = 15700;

    public async Task SetEnabledAsync(bool enabled)
    {
        if (IsPackaged())
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (!enabled)
            {
                if (startupTask.State == StartupTaskState.Enabled)
                {
                    startupTask.Disable();
                }

                return;
            }

            var state = startupTask.State == StartupTaskState.Enabled
                ? StartupTaskState.Enabled
                : await startupTask.RequestEnableAsync();
            if (state != StartupTaskState.Enabled)
            {
                throw new InvalidOperationException(
                    $"Windows did not enable Tessellation at sign-in. Startup task state: {state}.");
            }

            return;
        }

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The current user's Windows startup settings could not be opened.");
        if (enabled)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                throw new InvalidOperationException("The Tessellation executable path could not be determined.");
            }

            runKey.SetValue(RunValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static bool IsPackaged()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        if (result == ErrorInsufficientBuffer || result == 0)
        {
            return true;
        }

        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        throw new Win32Exception(
            unchecked((int)result),
            "The Tessellation package identity could not be determined.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
