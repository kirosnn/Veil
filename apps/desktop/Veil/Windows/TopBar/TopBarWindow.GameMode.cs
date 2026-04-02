using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private void EnterMinimalMode(int? activeGameProcessId)
    {
        _gamePerformanceService.ApplyGameOptimizations(activeGameProcessId);

        if (_isGameMinimalMode)
        {
            return;
        }

        _isGameMinimalMode = true;
        AppLogger.Info($"TopBarWindow entering minimal mode for {_monitorId}. ActiveGameProcessId={activeGameProcessId?.ToString() ?? "none"}.");
        _clockTimer.Stop();
        UpdateBackgroundMaintenanceState();
        UpdateVisualRefreshState();
        UpdateDiscordDemand();

        _finderWindow?.HideWindow();

        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        _ = ApplyRunCatSettingsAsync();

        if (!_isHidden)
        {
            _isHidden = true;
            if (_appBarRegistered)
            {
                WindowHelper.UnregisterAppBar(this);
                _appBarRegistered = false;
            }

            WindowHelper.HideWindow(this);
        }
    }

    private void ExitMinimalMode()
    {
        _isGameMinimalMode = false;
        AppLogger.Info($"TopBarWindow exiting minimal mode for {_monitorId}.");
        _clockTimer.Start();
        UpdateVisualRefreshState(boost: true);
        UpdateClock();
        _ = ApplyRunCatSettingsAsync();
        UpdateDiscordDemand(boost: true);
        UpdateBackgroundMaintenanceState(boost: true);
    }

    private void OnBackgroundMaintenanceTick(object? sender, object e)
    {
        UpdateBackgroundMaintenanceState();
        QueueBackgroundMaintenance();
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
        if (_isGameMinimalMode || !_settings.BackgroundOptimizationEnabled)
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
                if (_settings.BackgroundOptimizationEnabled && !_isGameMinimalMode)
                {
                    _gamePerformanceService.ApplyBackgroundOptimizations();
                }
                else if (!_isGameMinimalMode)
                {
                    _gamePerformanceService.RestoreNormalOptimizations();
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
        if (_isGameMinimalMode || !_settings.BackgroundOptimizationEnabled)
        {
            return ModuleDemand.Cold("background-disabled");
        }

        if (DateTime.UtcNow - _lastBackgroundMaintenanceBoostUtc <= BackgroundMaintenanceHotHoldDuration)
        {
            return ModuleDemand.Hot("background-boost");
        }

        return ModuleDemand.Warm("background-idle");
    }

    private void OnBackgroundMaintenanceTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        if (nextTemperature == ModuleTemperature.Cold)
        {
            _backgroundMaintenanceTimer.Stop();

            if (Interlocked.CompareExchange(ref _backgroundMaintenanceInFlight, 0, 0) == 0)
            {
                _ = Task.Run(_gamePerformanceService.RestoreNormalOptimizations);
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
