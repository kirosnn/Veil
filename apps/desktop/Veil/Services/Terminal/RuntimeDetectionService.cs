namespace Veil.Services.Terminal;

internal sealed record DetectedRuntime(string Name, string Version, string ExecutablePath);

internal static class RuntimeDetectionService
{
    private static IReadOnlyList<DetectedRuntime>? _cachedRuntimes;

    internal static IReadOnlyList<DetectedRuntime> GetRuntimes()
    {
        if (_cachedRuntimes is not null)
        {
            return _cachedRuntimes;
        }

        var runtimes = new List<DetectedRuntime>();

        TryDetect(runtimes, "Node.js", "node.exe", "--version");
        TryDetect(runtimes, "Bun", "bun.exe", "--version");
        TryDetect(runtimes, "Python", "python.exe", "--version");
        TryDetect(runtimes, "Python 3", "python3.exe", "--version");
        TryDetect(runtimes, "Go", "go.exe", "version");
        TryDetect(runtimes, "Rust (rustc)", "rustc.exe", "--version");
        TryDetect(runtimes, "Java", "java.exe", "-version");
        TryDetect(runtimes, ".NET", "dotnet.exe", "--version");
        TryDetect(runtimes, "Zig", "zig.exe", "version");
        TryDetect(runtimes, "Ruby", "ruby.exe", "--version");
        TryDetect(runtimes, "PHP", "php.exe", "--version");

        _cachedRuntimes = runtimes;
        return _cachedRuntimes;
    }

    internal static void InvalidateCache()
    {
        _cachedRuntimes = null;
    }

    private static void TryDetect(List<DetectedRuntime> runtimes, string name, string exe, string versionArg)
    {
        string? exePath = FindOnPath(exe);
        if (exePath is null)
        {
            return;
        }

        string? version = RunAndCapture(exePath, versionArg);
        runtimes.Add(new DetectedRuntime(name, ParseVersion(version ?? string.Empty), exePath));
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? RunAndCapture(string exe, string args)
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
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(2000);
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch
        {
            return null;
        }
    }

    private static string ParseVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        string trimmed = raw.Trim().Split('\n')[0].Trim();
        return trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }
}
