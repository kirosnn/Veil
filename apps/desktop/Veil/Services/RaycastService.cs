using System.Diagnostics;
using Veil.Diagnostics;

namespace Veil.Services;

internal static class RaycastService
{
    private static readonly string[] KnownPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Raycast", "Raycast.exe"),
    ];

    private static readonly Lazy<string?> _executablePath = new(FindExecutable);

    internal static bool IsInstalled => _executablePath.Value is not null;

    internal static void Activate()
    {
        string? path = _executablePath.Value;
        if (path is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to activate Raycast.", ex);
        }
    }

    private static string? FindExecutable() => KnownPaths.FirstOrDefault(File.Exists);
}
