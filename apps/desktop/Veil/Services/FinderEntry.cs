using System.Diagnostics;

namespace Veil.Services;

internal sealed record FinderEntry(
    string Name,
    string Category,
    string SearchToken,
    InstalledApp? App,
    string? IconGlyph)
{
    internal string CategoryLower { get; } = Category.ToLowerInvariant();
    internal string? LaunchUri { get; init; }
    internal string? LaunchCommand { get; init; }
    internal string? LaunchArgs { get; init; }

    internal static FinderEntry FromApp(InstalledApp app) =>
        new(app.Name, "Application", app.SearchToken, app, null);

    internal void Execute()
    {
        if (App is not null)
        {
            InstalledAppService.LaunchOrActivate(App);
            return;
        }

        if (LaunchUri is not null)
        {
            Process.Start(new ProcessStartInfo(LaunchUri) { UseShellExecute = true });
            return;
        }

        if (LaunchCommand is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LaunchCommand,
                Arguments = LaunchArgs ?? string.Empty,
                UseShellExecute = LaunchCommand.EndsWith(".msc", StringComparison.OrdinalIgnoreCase),
                CreateNoWindow = true
            });
        }
    }
}
