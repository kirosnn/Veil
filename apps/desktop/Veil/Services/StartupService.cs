using Microsoft.Win32;
using Veil.Diagnostics;

namespace Veil.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Veil";
    internal const string StartupLaunchArgument = "--startup";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key?.GetValue(AppName) is not string startupCommand)
            {
                return false;
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return true;
            }

            return string.Equals(
                startupCommand,
                BuildStartupCommand(exePath),
                StringComparison.OrdinalIgnoreCase);
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

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key.SetValue(AppName, BuildStartupCommand(exePath));
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

    internal static bool IsStartupLaunch(string? launchArguments)
    {
        if (string.IsNullOrWhiteSpace(launchArguments))
        {
            return false;
        }

        return launchArguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument, StartupLaunchArgument, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildStartupCommand(string exePath)
        => $"\"{exePath}\" {StartupLaunchArgument}";
}
