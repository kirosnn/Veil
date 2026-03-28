using System.Diagnostics;
using System.Globalization;
using System.Text;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class MediaControlService
{
    private static readonly string[] MusicAppMarkers =
    [
        "spotify",
        "deezer",
        "applemusic",
        "apple music",
        "youtube",
        "youtubemusic",
        "youtube music"
    ];

    private static readonly string[] BrowserAppMarkers =
    [
        "chrome",
        "chromium",
        "comet",
        "msedge",
        "firefox",
        "opera",
        "brave"
    ];

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _sessionSelectionVersion;

    internal string? TrackTitle { get; private set; }
    internal string? TrackArtist { get; private set; }
    internal string? AlbumTitle { get; private set; }
    internal string? Subtitle { get; private set; }
    internal bool IsPlaying { get; private set; }
    internal bool IsMusicApp { get; private set; }
    internal bool IsYouTubeSource { get; private set; }
    internal string? SourceAppId { get; private set; }
    internal string SourceLabel => ResolveSourceLabel(SourceAppId, TrackTitle, TrackArtist, AlbumTitle, Subtitle);
    internal IRandomAccessStreamReference? ThumbnailRef { get; private set; }
    internal bool CanTogglePlayPause { get; private set; }
    internal bool CanSkipNext { get; private set; }
    internal bool CanSkipPrevious { get; private set; }
    internal bool CanToggleShuffle { get; private set; }
    internal bool CanToggleRepeat { get; private set; }

    internal bool? ShuffleActive { get; private set; }
    internal MediaPlaybackAutoRepeatMode? RepeatMode { get; private set; }

    internal TimeSpan Position { get; private set; }
    internal TimeSpan Duration { get; private set; }
    internal DateTime PositionUpdatedAt { get; private set; }

    internal event Action? StateChanged;
    internal event Action? TimelineChanged;

    internal async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        await RefreshActiveSessionAsync();
    }

    internal async Task TogglePlayPauseAsync()
    {
        if (_session is not null && CanTogglePlayPause)
        {
            await _session.TryTogglePlayPauseAsync();
            UpdatePlaybackInfo();
            StateChanged?.Invoke();
        }
    }

    internal async Task NextAsync()
    {
        if (_session is not null && CanSkipNext)
        {
            await _session.TrySkipNextAsync();
            UpdatePlaybackInfo();
            StateChanged?.Invoke();
        }
    }

    internal async Task PreviousAsync()
    {
        if (_session is not null && CanSkipPrevious)
        {
            await _session.TrySkipPreviousAsync();
            UpdatePlaybackInfo();
            StateChanged?.Invoke();
        }
    }

    internal async Task ToggleShuffleAsync()
    {
        if (_session is null || !CanToggleShuffle) return;
        try
        {
            bool current = ShuffleActive ?? false;
            await _session.TryChangeShuffleActiveAsync(!current);
            UpdatePlaybackInfo();
            StateChanged?.Invoke();
        }
        catch { }
    }

    internal async Task CycleRepeatModeAsync()
    {
        if (_session is null || !CanToggleRepeat) return;
        try
        {
            var next = RepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            await _session.TryChangeAutoRepeatModeAsync(next);
            UpdatePlaybackInfo();
            StateChanged?.Invoke();
        }
        catch { }
    }

    internal async Task SeekAsync(TimeSpan position)
    {
        if (_session is null) return;
        try
        {
            await _session.TryChangePlaybackPositionAsync((long)position.TotalMicroseconds * 10); // 100ns ticks
        }
        catch { }
    }

    internal TimeSpan EstimatedPosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var elapsed = DateTime.UtcNow - PositionUpdatedAt;
            var estimated = Position + elapsed;
            return estimated > Duration && Duration > TimeSpan.Zero ? Duration : estimated;
        }
    }

    internal void RefreshTimeline()
    {
        UpdateTimelineInfo();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _ = RefreshActiveSessionAsync();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _ = RefreshActiveSessionAsync();
    }

    private async Task RefreshActiveSessionAsync()
    {
        if (_manager is null)
        {
            return;
        }

        int version = Interlocked.Increment(ref _sessionSelectionVersion);
        var preferredSession = await FindPreferredSessionAsync(_manager);
        if (version != _sessionSelectionVersion)
        {
            return;
        }

        AttachSession(preferredSession ?? _manager.GetCurrentSession());
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> FindPreferredSessionAsync(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        if (sessions.Count == 0)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? currentSession = manager.GetCurrentSession();
        GlobalSystemMediaTransportControlsSession? bestSession = null;
        int bestScore = int.MinValue;

        foreach (GlobalSystemMediaTransportControlsSession session in sessions)
        {
            int score = await ScoreSessionAsync(session, ReferenceEquals(session, currentSession));
            if (score > bestScore)
            {
                bestScore = score;
                bestSession = session;
            }
        }

        return bestScore > 0 ? bestSession : null;
    }

    private static async Task<int> ScoreSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        bool isCurrentSession)
    {
        string? sourceAppId = session.SourceAppUserModelId;
        int score = 0;

        if (IsSupportedMediaSource(sourceAppId, null, null, null, null))
        {
            score += 120;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                score += 60;
            }
        }
        catch
        {
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (IsSupportedMediaSource(sourceAppId, props?.Title, props?.Artist, props?.AlbumTitle, props?.Subtitle))
            {
                score += 240;
            }

            if (IsYouTubeMediaSource(sourceAppId, props?.Title, props?.Artist, props?.AlbumTitle, props?.Subtitle))
            {
                score += 40;
            }

            if (IsBrowserMediaSource(sourceAppId, props?.Title, props?.Artist, props?.AlbumTitle, props?.Subtitle))
            {
                score += 35;
            }

            if (!string.IsNullOrWhiteSpace(props?.Title))
            {
                score += 10;
            }
        }
        catch
        {
        }

        if (isCurrentSession)
        {
            score += 15;
        }

        return score;
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = session;

        if (_session is null)
        {
            TrackTitle = null;
            TrackArtist = null;
            AlbumTitle = null;
            Subtitle = null;
            IsPlaying = false;
            IsMusicApp = false;
            IsYouTubeSource = false;
            SourceAppId = null;
            ThumbnailRef = null;
            CanTogglePlayPause = false;
            CanSkipNext = false;
            CanSkipPrevious = false;
            CanToggleShuffle = false;
            CanToggleRepeat = false;
            ShuffleActive = null;
            RepeatMode = null;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            StateChanged?.Invoke();
            TimelineChanged?.Invoke();
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        SourceAppId = _session.SourceAppUserModelId;
        IsYouTubeSource = IsYouTubeMediaSource(SourceAppId, null, null, null, null);
        IsMusicApp = IsSupportedMediaSource(SourceAppId, null, null, null, null);

        UpdatePlaybackInfo();
        UpdateTimelineInfo();
        _ = UpdateMediaPropertiesAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateMediaPropertiesAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        UpdatePlaybackInfo();
        StateChanged?.Invoke();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimelineInfo();
        TimelineChanged?.Invoke();
    }

    private async Task UpdateMediaPropertiesAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            TrackTitle = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title;
            TrackArtist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist;
            AlbumTitle = string.IsNullOrWhiteSpace(props?.AlbumTitle) ? null : props.AlbumTitle;
            Subtitle = string.IsNullOrWhiteSpace(props?.Subtitle) ? null : props.Subtitle;
            ThumbnailRef = props?.Thumbnail;
            IsYouTubeSource = IsYouTubeMediaSource(SourceAppId, TrackTitle, TrackArtist, AlbumTitle, Subtitle);
            IsMusicApp = IsSupportedMediaSource(SourceAppId, TrackTitle, TrackArtist, AlbumTitle, Subtitle);
        }
        catch
        {
            TrackTitle = null;
            TrackArtist = null;
            AlbumTitle = null;
            Subtitle = null;
            ThumbnailRef = null;
            IsYouTubeSource = false;
            IsMusicApp = IsSupportedMediaSource(SourceAppId, null, null, null, null);
        }

        StateChanged?.Invoke();
    }

    private void UpdatePlaybackInfo()
    {
        if (_session is null)
        {
            IsPlaying = false;
            CanTogglePlayPause = false;
            CanSkipNext = false;
            CanSkipPrevious = false;
            CanToggleShuffle = false;
            CanToggleRepeat = false;
            ShuffleActive = null;
            RepeatMode = null;
            return;
        }

        try
        {
            var info = _session.GetPlaybackInfo();
            var controls = info.Controls;
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CanTogglePlayPause = controls.IsPlayPauseToggleEnabled || (controls.IsPlayEnabled && controls.IsPauseEnabled);
            CanSkipNext = controls.IsNextEnabled;
            CanSkipPrevious = controls.IsPreviousEnabled;
            CanToggleShuffle = controls.IsShuffleEnabled;
            CanToggleRepeat = controls.IsRepeatEnabled;
            ShuffleActive = info.IsShuffleActive;
            RepeatMode = info.AutoRepeatMode;
        }
        catch
        {
            IsPlaying = false;
            CanTogglePlayPause = false;
            CanSkipNext = false;
            CanSkipPrevious = false;
            CanToggleShuffle = false;
            CanToggleRepeat = false;
            ShuffleActive = null;
            RepeatMode = null;
        }
    }

    private void UpdateTimelineInfo()
    {
        if (_session is null)
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            return;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();
            Position = timeline.Position;
            Duration = timeline.EndTime - timeline.StartTime;
            PositionUpdatedAt = DateTime.UtcNow;
        }
        catch
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }
    }

    private static bool IsSupportedMediaSource(
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        if (MusicAppMarkers.Any(marker => sourceAppId.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsYouTubeMediaSource(sourceAppId, trackTitle, trackArtist, albumTitle, subtitle)
            || IsBrowserMediaSource(sourceAppId, trackTitle, trackArtist, albumTitle, subtitle);
    }

    private static bool IsYouTubeMediaSource(
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        if (sourceAppId.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsBrowserSource(sourceAppId))
        {
            return false;
        }

        return ContainsYouTubeMarker(subtitle)
            || ContainsYouTubeMarker(trackTitle)
            || ContainsYouTubeMarker(trackArtist)
            || ContainsYouTubeMarker(albumTitle)
            || HasYouTubeBrowserWindow(sourceAppId);
    }

    private static bool ContainsYouTubeMarker(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Contains("youtube", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserSource(string? sourceAppId)
    {
        return !string.IsNullOrWhiteSpace(sourceAppId) &&
            BrowserAppMarkers.Any(marker => sourceAppId.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBrowserMediaSource(
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        if (!IsBrowserSource(sourceAppId))
        {
            return false;
        }

        if (ContainsMarker(trackTitle, "netflix") ||
            ContainsMarker(trackArtist, "netflix") ||
            ContainsMarker(albumTitle, "netflix") ||
            ContainsMarker(subtitle, "netflix") ||
            ContainsMarker(trackTitle, "disney+") ||
            ContainsMarker(trackArtist, "disney+") ||
            ContainsMarker(albumTitle, "disney+") ||
            ContainsMarker(subtitle, "disney+") ||
            ContainsMarker(trackTitle, "prime video") ||
            ContainsMarker(trackArtist, "prime video") ||
            ContainsMarker(albumTitle, "prime video") ||
            ContainsMarker(subtitle, "prime video"))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(trackTitle)
            || !string.IsNullOrWhiteSpace(trackArtist)
            || !string.IsNullOrWhiteSpace(albumTitle)
            || !string.IsNullOrWhiteSpace(subtitle);
    }

    private static bool HasYouTubeBrowserWindow(string sourceAppId)
    {
        string[] matchTokens = BuildWindowMatchTokens(sourceAppId);
        if (matchTokens.Length == 0)
        {
            return false;
        }

        bool hasMatch = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            string windowTitle = GetWindowTitle(hwnd);
            if (!ContainsYouTubeMarker(windowTitle))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0 || processId == Environment.ProcessId)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                string processName = NormalizeWindowValue(process.ProcessName);
                string fileDescription = NormalizeWindowValue(process.MainModule?.FileVersionInfo?.FileDescription);
                if (matchTokens.Any(token =>
                    processName == token ||
                    processName.Contains(token, StringComparison.Ordinal) ||
                    token.Contains(processName, StringComparison.Ordinal) ||
                    (!string.IsNullOrWhiteSpace(fileDescription) &&
                     (fileDescription.Contains(token, StringComparison.Ordinal) ||
                      token.Contains(fileDescription, StringComparison.Ordinal)))))
                {
                    hasMatch = true;
                    return false;
                }
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);

        return hasMatch;
    }

    private static string[] BuildWindowMatchTokens(string sourceAppId)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        void AddToken(string? value)
        {
            string normalized = NormalizeWindowValue(value);
            if (normalized.Length > 1)
            {
                tokens.Add(normalized);
            }
        }

        string baseId = sourceAppId;
        int bangIndex = baseId.IndexOf('!');
        if (bangIndex >= 0)
        {
            baseId = baseId[..bangIndex];
        }

        AddToken(baseId);
        AddToken(Path.GetFileNameWithoutExtension(baseId));

        string packageSegment = baseId;
        int underscoreIndex = packageSegment.IndexOf('_');
        if (underscoreIndex >= 0)
        {
            packageSegment = packageSegment[..underscoreIndex];
        }

        foreach (string part in packageSegment.Split(['.', '\\', '/', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddToken(part);
        }

        return tokens.OrderByDescending(token => token.Length).ToArray();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[512];
        int length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length <= 0
            ? string.Empty
            : NormalizeWindowValue(new string(buffer, 0, length));
    }

    private static string NormalizeWindowValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ResolveSourceLabel(
        string? sourceAppId,
        string? trackTitle,
        string? trackArtist,
        string? albumTitle,
        string? subtitle)
    {
        if (ContainsMarker(sourceAppId, "spotify"))
        {
            return "Spotify";
        }

        if (ContainsMarker(sourceAppId, "deezer"))
        {
            return "Deezer";
        }

        if (ContainsMarker(sourceAppId, "applemusic") || ContainsMarker(sourceAppId, "apple music"))
        {
            return "Apple Music";
        }

        if (ContainsMarker(sourceAppId, "musicwin"))
        {
            return "Media Player";
        }

        if (ContainsMarker(trackTitle, "youtube music") ||
            ContainsMarker(trackArtist, "youtube music") ||
            ContainsMarker(albumTitle, "youtube music") ||
            ContainsMarker(subtitle, "youtube music"))
        {
            return "YouTube Music";
        }

        if (ContainsMarker(sourceAppId, "youtube") ||
            ContainsMarker(trackTitle, "youtube") ||
            ContainsMarker(trackArtist, "youtube") ||
            ContainsMarker(albumTitle, "youtube") ||
            ContainsMarker(subtitle, "youtube"))
        {
            return "YouTube";
        }

        if (ContainsMarker(trackTitle, "twitch") ||
            ContainsMarker(trackArtist, "twitch") ||
            ContainsMarker(albumTitle, "twitch") ||
            ContainsMarker(subtitle, "twitch"))
        {
            return "Twitch";
        }

        if (ContainsMarker(trackTitle, "netflix") ||
            ContainsMarker(trackArtist, "netflix") ||
            ContainsMarker(albumTitle, "netflix") ||
            ContainsMarker(subtitle, "netflix"))
        {
            return "Netflix";
        }

        if (ContainsMarker(trackTitle, "disney+") ||
            ContainsMarker(trackArtist, "disney+") ||
            ContainsMarker(albumTitle, "disney+") ||
            ContainsMarker(subtitle, "disney+"))
        {
            return "Disney+";
        }

        if (ContainsMarker(sourceAppId, "msedge"))
        {
            return "Edge";
        }

        if (ContainsMarker(sourceAppId, "chrome"))
        {
            return "Chrome";
        }

        if (ContainsMarker(sourceAppId, "comet"))
        {
            return "Comet";
        }

        if (ContainsMarker(sourceAppId, "chromium"))
        {
            return "Chromium";
        }

        if (ContainsMarker(sourceAppId, "firefox"))
        {
            return "Firefox";
        }

        if (ContainsMarker(sourceAppId, "brave"))
        {
            return "Brave";
        }

        if (ContainsMarker(sourceAppId, "opera"))
        {
            return "Opera";
        }

        return "App";
    }

    private static bool ContainsMarker(string? value, string marker)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }
}
