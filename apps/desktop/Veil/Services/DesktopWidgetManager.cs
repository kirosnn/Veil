using Veil.Configuration;
using Veil.Interop;
using Veil.Windows;

namespace Veil.Services;

internal sealed class DesktopWidgetManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly WeatherService _weatherService;
    private readonly Dictionary<string, DesktopWidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal DesktopWidgetManager(AppSettings settings)
    {
        _settings = settings;
        _weatherService = new WeatherService();
    }

    internal async Task InitializeAsync()
    {
        try
        {
            await _weatherService.InitializeAsync();
        }
        catch
        {
        }

        _settings.Changed += OnSettingsChanged;
        SyncWindows();
    }

    internal void SyncWindows()
    {
        if (_disposed)
        {
            return;
        }

        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        MonitorInfo2 primaryMonitor = monitors.FirstOrDefault(static monitor => monitor.IsPrimary) ?? monitors[0];

        foreach (string windowId in _windows.Keys.ToArray())
        {
            if (_settings.DesktopWidgets.Any(widget => string.Equals(widget.Id, windowId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _windows[windowId].WidgetMoved -= OnWidgetMoved;
            _windows[windowId].Close();
            _windows.Remove(windowId);
        }

        foreach (DesktopWidgetSetting widget in _settings.DesktopWidgets)
        {
            if (!_windows.TryGetValue(widget.Id, out DesktopWidgetWindow? window))
            {
                window = new DesktopWidgetWindow(widget.Id, _weatherService);
                window.WidgetMoved += OnWidgetMoved;
                _windows[widget.Id] = window;
                window.Initialize();
            }

            window.ApplyWidget(widget with { MonitorId = primaryMonitor.Id }, primaryMonitor);
        }
    }

    private void OnSettingsChanged()
    {
        SyncWindows();
    }

    private void OnWidgetMoved(string widgetId, int x, int y, string monitorId)
    {
        DesktopWidgetSetting? widget = _settings.DesktopWidgets.FirstOrDefault(entry =>
            string.Equals(entry.Id, widgetId, StringComparison.OrdinalIgnoreCase));
        if (widget is null)
        {
            return;
        }

        string primaryMonitorId = MonitorService.GetAllMonitors()
            .FirstOrDefault(static monitor => monitor.IsPrimary)?.Id
            ?? monitorId;

        _settings.UpdateDesktopWidget(widget with
        {
            X = x,
            Y = y,
            MonitorId = primaryMonitorId
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;

        foreach (DesktopWidgetWindow window in _windows.Values)
        {
            window.WidgetMoved -= OnWidgetMoved;
            window.Close();
        }

        _windows.Clear();
    }
}
