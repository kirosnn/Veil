using Veil.Diagnostics;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Veil.Services;

internal sealed class DiscordNotificationService : IDisposable
{
    private static readonly TimeSpan HotPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarmPollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ColdPollInterval = TimeSpan.FromSeconds(30);
    private UserNotificationListener? _listener;
    private readonly System.Threading.Timer _pollTimer;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, ModuleTemperature> _demandByOwner = [];
    private uint _lastNotificationId;
    private bool _disposed;
    private bool _initialized;
    private bool _notificationsAccessAllowed;
    private bool _discordCallInspectionDisabled;
    private Task? _initializationTask;
    private TimeSpan _currentPollInterval = ColdPollInterval;

    public event Action? NotificationsChanged;

    public int UnreadCount { get; private set; }
    public bool HasActiveCall { get; private set; }
    public bool HasIncomingCall { get; private set; }
    public bool HasVoiceConnection { get; private set; }
    public bool IsDiscordRunning { get; private set; }
    public DiscordCallSnapshot CallSnapshot { get; private set; } = DiscordCallSnapshot.Empty;
    public IReadOnlyList<DiscordNotification> Notifications { get; private set; } = [];

    public DiscordNotificationService()
    {
        _pollTimer = new System.Threading.Timer(OnPoll, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public Task EnsureInitializedAsync()
    {
        lock (_sync)
        {
            if (_initializationTask is not null)
            {
                return _initializationTask;
            }

            _initializationTask = InitializeCoreAsync();
            return _initializationTask;
        }
    }

    public void SetDemand(string ownerId, ModuleTemperature temperature)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        lock (_sync)
        {
            _demandByOwner[ownerId] = temperature;
            UpdatePollCadenceLocked();
        }
    }

    public void ReleaseDemand(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        lock (_sync)
        {
            _demandByOwner.Remove(ownerId);
            UpdatePollCadenceLocked();
        }
    }

    private void OnPoll(object? state)
    {
        if (_disposed) return;
        try
        {
            PollNotificationsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to poll notifications.", ex);
        }
    }

    private async Task PollNotificationsAsync()
    {
        DiscordCallSnapshot callSnapshot;
        if (_discordCallInspectionDisabled)
        {
            callSnapshot = DiscordCallSnapshot.Empty with
            {
                IsRunning = DiscordAppController.IsRunning()
            };
        }
        else
        {
            try
            {
                callSnapshot = DiscordAppController.GetCallSnapshot();
            }
            catch (BadImageFormatException ex)
            {
                _discordCallInspectionDisabled = true;
                AppLogger.Error("Discord call inspection disabled because UI Automation could not be loaded.", ex);
                callSnapshot = DiscordCallSnapshot.Empty with
                {
                    IsRunning = DiscordAppController.IsRunning()
                };
            }
            catch (FileLoadException ex)
            {
                _discordCallInspectionDisabled = true;
                AppLogger.Error("Discord call inspection disabled because UI Automation could not be loaded.", ex);
                callSnapshot = DiscordCallSnapshot.Empty with
                {
                    IsRunning = DiscordAppController.IsRunning()
                };
            }
        }

        bool isRunning = callSnapshot.IsRunning;
        var discordNotifs = new List<DiscordNotification>();
        bool hasIncomingCall = false;

        if (_listener is not null && _notificationsAccessAllowed)
        {
            IReadOnlyList<UserNotification> userNotifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notif in userNotifications)
            {
                string appId = notif.AppInfo?.AppUserModelId ?? "";
                if (!IsDiscordApp(appId)) continue;

                var binding = notif.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding is null) continue;

                var texts = binding.GetTextElements().ToList();
                string title = texts.Count > 0 ? texts[0].Text : "";
                string body = texts.Count > 1 ? texts[1].Text : "";

                if (IsCallNotification(title, body))
                {
                    hasIncomingCall = true;
                }

                discordNotifs.Add(new DiscordNotification(
                    notif.Id,
                    title,
                    body,
                    notif.CreationTime.LocalDateTime));
            }
        }

        hasIncomingCall |= callSnapshot.HasIncomingCall;
        bool hasVoiceConnection = callSnapshot.HasVoiceConnection;
        bool hasActiveCall = hasIncomingCall || hasVoiceConnection;

        DiscordCallSnapshot mergedCallSnapshot = callSnapshot with
        {
            HasIncomingCall = hasIncomingCall,
            StatusTitle = hasIncomingCall ? "Incoming call" : callSnapshot.StatusTitle
        };

        bool changed = IsDiscordRunning != isRunning
            || HasActiveCall != hasActiveCall
            || HasIncomingCall != hasIncomingCall
            || HasVoiceConnection != hasVoiceConnection
            || UnreadCount != discordNotifs.Count
            || !Equals(CallSnapshot, mergedCallSnapshot)
            || (discordNotifs.Count > 0 && discordNotifs[^1].Id != _lastNotificationId);

        IsDiscordRunning = isRunning;
        HasActiveCall = hasActiveCall;
        HasIncomingCall = hasIncomingCall;
        HasVoiceConnection = hasVoiceConnection;
        CallSnapshot = mergedCallSnapshot;
        UnreadCount = discordNotifs.Count;
        Notifications = discordNotifs;
        if (discordNotifs.Count > 0)
        {
            _lastNotificationId = discordNotifs[^1].Id;
        }

        if (changed)
        {
            NotificationsChanged?.Invoke();
        }

        lock (_sync)
        {
            UpdatePollCadenceLocked();
        }
    }

    private static bool IsDiscordApp(string appId)
    {
        return appId.Contains("Discord", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCallNotification(string title, string body)
    {
        string combined = $"{title} {body}";
        return combined.Contains("incoming call", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("appel entrant", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("is calling", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("vous appelle", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ClearNotificationsAsync()
    {
        if (_listener is null || Notifications.Count == 0)
        {
            return;
        }

        foreach (DiscordNotification notification in Notifications.ToArray())
        {
            _listener.RemoveNotification(notification.Id);
        }

        await PollNotificationsAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            _notificationsAccessAllowed = access == UserNotificationListenerAccessStatus.Allowed;

            if (!_notificationsAccessAllowed)
            {
                AppLogger.Error("Notification access denied.", null);
            }

            lock (_sync)
            {
                _initialized = true;
                UpdatePollCadenceLocked(runImmediately: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to initialize notification listener.", ex);
        }
    }

    private void UpdatePollCadenceLocked(bool runImmediately = false)
    {
        if (_disposed || !_initialized)
        {
            return;
        }

        ModuleTemperature requestedTemperature = ResolveRequestedTemperatureLocked();
        ModuleTemperature effectiveTemperature = ResolveEffectiveTemperature(requestedTemperature);
        TimeSpan nextInterval = effectiveTemperature switch
        {
            ModuleTemperature.Hot => HotPollInterval,
            ModuleTemperature.Warm => WarmPollInterval,
            _ => ColdPollInterval
        };

        if (_currentPollInterval == nextInterval && !runImmediately)
        {
            return;
        }

        _currentPollInterval = nextInterval;
        _pollTimer.Change(runImmediately ? TimeSpan.Zero : nextInterval, nextInterval);
    }

    private ModuleTemperature ResolveRequestedTemperatureLocked()
    {
        if (_demandByOwner.Count == 0)
        {
            return ModuleTemperature.Cold;
        }

        return _demandByOwner.Values.Max();
    }

    private ModuleTemperature ResolveEffectiveTemperature(ModuleTemperature requestedTemperature)
    {
        if (HasActiveCall || Notifications.Count > 0)
        {
            return ModuleTemperature.Hot;
        }

        return requestedTemperature;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }
}

internal sealed record DiscordNotification(uint Id, string Title, string Body, DateTime Time);
