using Microsoft.Win32;

namespace Veil.Services.Terminal;

internal static class ShellProfileService
{
    private static IReadOnlyList<TerminalProfile>? _cachedProfiles;

    internal static IReadOnlyList<TerminalProfile> GetProfiles()
    {
        if (_cachedProfiles is not null)
        {
            return _cachedProfiles;
        }

        var profiles = new List<TerminalProfile>();

        TryAddPowerShell7(profiles);
        TryAddWindowsPowerShell(profiles);
        TryAddCmd(profiles);
        TryAddGitBash(profiles);
        TryAddWslProfiles(profiles);

        _cachedProfiles = profiles.Count > 0
            ? profiles
            : [BuildCmdFallback()];

        return _cachedProfiles;
    }

    internal static TerminalProfile GetDefaultProfile()
    {
        var profiles = GetProfiles();
        return profiles.FirstOrDefault(static p => p.Id == "powershell")
            ?? profiles.FirstOrDefault(static p => p.Id == "pwsh7")
            ?? profiles[0];
    }

    internal static TerminalProfile? GetById(string id)
    {
        return GetProfiles().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    internal static void InvalidateCache()
    {
        _cachedProfiles = null;
    }

    private static void TryAddPowerShell7(List<TerminalProfile> profiles)
    {
        string? path = FindExecutable("pwsh.exe",
        [
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\7-preview\pwsh.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        ]);

        if (path is null)
        {
            return;
        }

        profiles.Add(new TerminalProfile(
            Id: "pwsh7",
            DisplayName: "PowerShell",
            ExecutablePath: path,
            Arguments: "-NoLogo",
            WorkingDirectory: null,
            IconPath: path,
            IsVerified: true));
    }

    private static void TryAddWindowsPowerShell(List<TerminalProfile> profiles)
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(path))
        {
            return;
        }

        profiles.Add(new TerminalProfile(
            Id: "powershell",
            DisplayName: "Windows PowerShell",
            ExecutablePath: path,
            Arguments: "-NoLogo",
            WorkingDirectory: null,
            IconPath: path,
            IsVerified: true));
    }

    private static void TryAddCmd(List<TerminalProfile> profiles)
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string path = Path.Combine(systemRoot, "System32", "cmd.exe");

        if (!File.Exists(path))
        {
            return;
        }

        profiles.Add(new TerminalProfile(
            Id: "cmd",
            DisplayName: "Command Prompt",
            ExecutablePath: path,
            Arguments: string.Empty,
            WorkingDirectory: null,
            IconPath: path,
            IsVerified: true));
    }

    private static void TryAddGitBash(List<TerminalProfile> profiles)
    {
        string? bashPath = FindExecutable("bash.exe",
        [
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\usr\bin\bash.exe",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        ]);

        if (bashPath is null)
        {
            string? regPath = TryFindGitBashFromRegistry();
            if (regPath is not null)
            {
                string usrBash = regPath.Replace(@"\bin\bash.exe", @"\usr\bin\bash.exe", StringComparison.OrdinalIgnoreCase);
                bashPath = File.Exists(usrBash) ? usrBash : regPath;
            }
        }

        if (bashPath is null)
        {
            return;
        }

        profiles.Add(new TerminalProfile(
            Id: "gitbash",
            DisplayName: "Git Bash",
            ExecutablePath: bashPath,
            Arguments: "--login -i",
            WorkingDirectory: null,
            IconPath: bashPath,
            IsVerified: true));
    }

    private static string? TryFindGitBashFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GitForWindows");
            if (key?.GetValue("InstallPath") is string installPath)
            {
                string bash = Path.Combine(installPath, "bin", "bash.exe");
                if (File.Exists(bash))
                {
                    return bash;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void TryAddWslProfiles(List<TerminalProfile> profiles)
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string wslPath = Path.Combine(systemRoot, "System32", "wsl.exe");

        if (!File.Exists(wslPath))
        {
            return;
        }

        try
        {
            var result = RunAndCapture(wslPath, "--list --quiet", timeoutMs: 3000);
            if (result is null)
            {
                // WSL installed but no distros, still add default entry
                profiles.Add(BuildWslProfile("wsl", "WSL", wslPath, string.Empty));
                return;
            }

            var distros = result
                .Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => s.Length > 0 && !s.Equals("Windows Subsystem for Linux Distributions:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (distros.Count == 0)
            {
                profiles.Add(BuildWslProfile("wsl", "WSL", wslPath, string.Empty));
                return;
            }

            foreach (string distro in distros)
            {
                string safeId = $"wsl-{distro.ToLowerInvariant().Replace(' ', '-')}";
                profiles.Add(BuildWslProfile(safeId, $"WSL: {distro}", wslPath, $"-d {QuoteArgument(distro)}"));
            }
        }
        catch
        {
            profiles.Add(BuildWslProfile("wsl", "WSL", wslPath, string.Empty));
        }
    }

    private static TerminalProfile BuildWslProfile(string id, string displayName, string wslPath, string args)
    {
        return new TerminalProfile(
            Id: id,
            DisplayName: displayName,
            ExecutablePath: wslPath,
            Arguments: args,
            WorkingDirectory: null,
            IconPath: null,
            IsVerified: true);
    }

    private static TerminalProfile BuildCmdFallback()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        return new TerminalProfile(
            Id: "cmd",
            DisplayName: "Command Prompt",
            ExecutablePath: Path.Combine(systemRoot, "System32", "cmd.exe"),
            Arguments: string.Empty,
            WorkingDirectory: null,
            IconPath: null,
            IsVerified: false);
    }

    private static string? FindExecutable(string fileName, IEnumerable<string> knownPaths)
    {
        foreach (string known in knownPaths)
        {
            if (File.Exists(known))
            {
                return known;
            }
        }

        // Check PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string trimmedDir = dir.Trim();
            if (trimmedDir.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(trimmedDir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? RunAndCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            bool exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                proc.Kill();
            }

            return output;
        }
        catch
        {
            return null;
        }
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(' ', StringComparison.Ordinal) || value.Contains('\t', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
