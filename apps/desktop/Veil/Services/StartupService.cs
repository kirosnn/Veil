using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Veil.Diagnostics;

namespace Veil.Services;

internal static class StartupService
{
    private const string TaskName = "Veil";
    private const string LegacyRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyAppName = "Veil";
    internal const string StartupLaunchArgument = "--startup";

    public static bool IsEnabled()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            string taskXml = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
            if (string.IsNullOrWhiteSpace(taskXml) || !taskXml.Contains("<Task", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(exePath))
            {
                return true;
            }

            return taskXml.Contains(exePath, StringComparison.OrdinalIgnoreCase);
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
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                AppLogger.Error("Cannot determine executable path for startup registration.");
                return;
            }

            TryRemoveLegacyRegistryEntry();

            string userId = WindowsIdentity.GetCurrent().Name;
            string xml = BuildTaskXml(exePath, userId);
            string xmlPath = Path.Combine(Path.GetTempPath(), "VeilStartupTask.xml");
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);

            try
            {
                RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
                AppLogger.Info("Startup task created.");
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { }
            }
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
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            AppLogger.Info("Startup task deleted.");
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
            .Any(arg => string.Equals(arg, StartupLaunchArgument, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryRemoveLegacyRegistryEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, true);
            key?.DeleteValue(LegacyAppName, false);
        }
        catch { }
    }

    private static string RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string BuildTaskXml(string exePath, string userId)
    {
        string escapedExe = System.Security.SecurityElement.Escape(exePath) ?? exePath;
        string escapedUser = System.Security.SecurityElement.Escape(userId) ?? userId;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{escapedUser}</UserId>
                </LogonTrigger>
                <SessionStateChangeTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{escapedUser}</UserId>
                  <StateChange>SessionUnlock</StateChange>
                </SessionStateChangeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{escapedUser}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedExe}</Command>
                  <Arguments>{StartupLaunchArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
