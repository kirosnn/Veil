using Microsoft.Win32;
using Veil.Diagnostics;

namespace Veil.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Veil";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) is string;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to check startup status.", ex);
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                AppLogger.Error("Cannot determine executable path for startup registration.");
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.SetValue(AppName, $"\"{exePath}\"");
            AppLogger.Info("Startup registration enabled.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to enable startup.", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(AppName, false);
            AppLogger.Info("Startup registration disabled.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to disable startup.", ex);
        }
    }
}
