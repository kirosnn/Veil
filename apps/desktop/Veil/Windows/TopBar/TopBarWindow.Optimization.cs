using Microsoft.UI.Xaml;
using Veil.Diagnostics;
using Veil.Services;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private void OnBackgroundMaintenanceTick(object? sender, object e)
    {
        try
        {
            UpdateBackgroundMaintenanceState();
            QueueBackgroundMaintenance();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"TopBarWindow background maintenance tick failed for {_monitorId}.", ex);
        }
    }

    private void UpdateBackgroundMaintenanceState(bool boost = false)
    {
        if (boost)
        {
            _lastBackgroundMaintenanceBoostUtc = DateTime.UtcNow;
        }

        _backgroundMaintenanceModule.Update(EvaluateBackgroundMaintenanceDemand());
    }

    private void QueueBackgroundMaintenance()
    {
        if (!_settings.BackgroundOptimizationEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _backgroundMaintenanceInFlight, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (_settings.BackgroundOptimizationEnabled)
                {
                    _veilOptimizationService.ApplyBackgroundOptimizations();
                }
                else
                {
                    _veilOptimizationService.RestoreNormalOptimizations();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundMaintenanceInFlight, 0);
            }
        });
    }

    private ModuleDemand EvaluateBackgroundMaintenanceDemand()
    {
        if (!_settings.BackgroundOptimizationEnabled)
        {
            return ModuleDemand.Cold("optimization-disabled");
        }

        if (DateTime.UtcNow - _lastBackgroundMaintenanceBoostUtc <= BackgroundMaintenanceHotHoldDuration)
        {
            return ModuleDemand.Hot("optimization-boost");
        }

        return ModuleDemand.Warm("optimization-idle");
    }

    private void OnBackgroundMaintenanceTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        if (nextTemperature == ModuleTemperature.Cold)
        {
            _backgroundMaintenanceTimer.Stop();

            if (Interlocked.CompareExchange(ref _backgroundMaintenanceInFlight, 0, 0) == 0)
            {
                _ = Task.Run(_veilOptimizationService.RestoreNormalOptimizations);
            }

            return;
        }

        _backgroundMaintenanceTimer.Interval = nextTemperature == ModuleTemperature.Hot
            ? BackgroundMaintenanceInterval
            : BackgroundMaintenanceWarmInterval;

        if (!_backgroundMaintenanceTimer.IsEnabled)
        {
            _backgroundMaintenanceTimer.Start();
        }

        if (nextTemperature == ModuleTemperature.Hot || previousTemperature == ModuleTemperature.Cold)
        {
            QueueBackgroundMaintenance();
        }
    }
}
