using Veil.Configuration;

namespace Veil.Services;

internal sealed record WindowFitSettings(
    bool Enabled,
    int Margin,
    int DelayMilliseconds,
    bool RespectManualResize,
    IReadOnlyList<string> Exclusions,
    IReadOnlySet<string> ExclusionSet)
{
    internal static WindowFitSettings FromAppSettings(AppSettings settings)
    {
        string[] exclusions = settings.SmartWindowFitExclusions.ToArray();
        return new WindowFitSettings(
            settings.SmartWindowFitEnabled,
            settings.SmartWindowFitMargin,
            settings.SmartWindowFitDelayMilliseconds,
            settings.SmartWindowFitRespectManualResize,
            exclusions,
            exclusions.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
