namespace Veil.Services;

internal enum ModuleTemperature
{
    Cold = 0,
    Warm = 1,
    Hot = 2
}

internal readonly record struct ModuleDemand(ModuleTemperature Temperature, string Reason)
{
    internal static ModuleDemand Cold(string reason) => new(ModuleTemperature.Cold, reason);

    internal static ModuleDemand Warm(string reason) => new(ModuleTemperature.Warm, reason);

    internal static ModuleDemand Hot(string reason) => new(ModuleTemperature.Hot, reason);
}

internal sealed class DemandDrivenModule
{
    private readonly TimeSpan _minimumWarmDuration;
    private readonly TimeSpan _minimumHotDuration;
    private readonly Action<ModuleTemperature, ModuleTemperature, string> _onTransition;
    private DateTime _lastTransitionUtc = DateTime.MinValue;

    internal DemandDrivenModule(
        TimeSpan minimumWarmDuration,
        TimeSpan minimumHotDuration,
        Action<ModuleTemperature, ModuleTemperature, string> onTransition)
    {
        _minimumWarmDuration = minimumWarmDuration;
        _minimumHotDuration = minimumHotDuration;
        _onTransition = onTransition;
    }

    internal ModuleTemperature Temperature { get; private set; }

    internal string LastReason { get; private set; } = string.Empty;

    internal void Update(ModuleDemand demand)
    {
        DateTime nowUtc = DateTime.UtcNow;
        ModuleTemperature nextTemperature = ResolveNextTemperature(demand.Temperature, nowUtc);
        LastReason = demand.Reason;

        if (nextTemperature == Temperature)
        {
            return;
        }

        ModuleTemperature previousTemperature = Temperature;
        Temperature = nextTemperature;
        _lastTransitionUtc = nowUtc;
        _onTransition(previousTemperature, nextTemperature, demand.Reason);
    }

    private ModuleTemperature ResolveNextTemperature(ModuleTemperature requestedTemperature, DateTime nowUtc)
    {
        if (requestedTemperature >= Temperature)
        {
            return requestedTemperature;
        }

        if (Temperature == ModuleTemperature.Hot &&
            requestedTemperature < ModuleTemperature.Hot &&
            nowUtc - _lastTransitionUtc < _minimumHotDuration)
        {
            return ModuleTemperature.Hot;
        }

        if (Temperature == ModuleTemperature.Warm &&
            requestedTemperature == ModuleTemperature.Cold &&
            nowUtc - _lastTransitionUtc < _minimumWarmDuration)
        {
            return ModuleTemperature.Warm;
        }

        return requestedTemperature;
    }
}
