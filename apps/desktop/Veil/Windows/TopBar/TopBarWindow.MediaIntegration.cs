using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Veil.Diagnostics;
using Veil.Services;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private async Task InitMediaControlAsync()
    {
        try
        {
            await _mediaControlService.InitializeAsync();
            _mediaControlService.StateChanged += OnMediaStateChanged;
            DispatcherQueue.TryEnqueue(UpdateMusicButtonVisibility);
        }
        catch
        {
        }
    }

    private void OnMediaStateChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateMusicButtonVisibility);
    }

    private void UpdateMusicButtonVisibility()
    {
        bool isVisible = _ownsGlobalHotkeys && _settings.MusicButtonEnabled && _mediaControlService.IsMusicApp;
        MusicButton.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isVisible && _musicControlWindow is { IsMenuVisible: true })
        {
            _musicControlWindow.Hide();
        }

        ApplyRightButtonsToggleState();
        _ = UpdateMusicAlbumArtAsync();
    }

    private async Task UpdateMusicAlbumArtAsync()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        int loadVersion = ++_musicAlbumArtLoadVersion;

        try
        {
            if (_mediaControlService.IsYouTubeSource)
            {
                if (loadVersion != _musicAlbumArtLoadVersion)
                {
                    return;
                }

                MusicAlbumArt.Source = UseDarkTopBarForeground(GetTopBarForegroundColor())
                    ? YouTubeDarkIconSource
                    : YouTubeLightIconSource;
                MusicAlbumArt.Visibility = Visibility.Visible;
                MusicIcon.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var imageSource = await MediaArtworkLoader.LoadAsync(
                    _mediaControlService.ThumbnailRef,
                    _mediaControlService.SourceAppId,
                    _mediaControlService.TrackTitle,
                    _mediaControlService.TrackArtist,
                    _mediaControlService.AlbumTitle,
                    _mediaControlService.Subtitle);
                if (loadVersion != _musicAlbumArtLoadVersion)
                {
                    return;
                }

                if (imageSource is not null)
                {
                    MusicAlbumArt.Source = imageSource;
                    MusicAlbumArt.Visibility = Visibility.Visible;
                    MusicIcon.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            catch
            {
            }

            if (loadVersion != _musicAlbumArtLoadVersion)
            {
                return;
            }

            MusicAlbumArt.Source = null;
            MusicAlbumArt.Visibility = Visibility.Collapsed;
            MusicIcon.Visibility = Visibility.Visible;
        }
        finally
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;
            PerformanceLogger.RecordMilliseconds("TopBar.UpdateMusicAlbumArtAsync", elapsedMs, 8.0);
        }
    }

    private async Task InitDiscordNotificationsAsync()
    {
        try
        {
            await _discordNotificationService.EnsureInitializedAsync();
            _discordNotificationService.NotificationsChanged += OnDiscordNotificationsChanged;
            UpdateDiscordDemand(boost: true);
            DispatcherQueue.TryEnqueue(UpdateDiscordButtonVisibility);
        }
        catch
        {
        }
    }

    private void OnDiscordNotificationsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateDiscordButtonVisibility();
            UpdateDiscordDemand(boost: true);
        });
    }

    private void UpdateDiscordButtonVisibility()
    {
        bool isDiscordRunning = _discordNotificationService.IsDiscordRunning;
        bool isVisible = _ownsGlobalHotkeys
            && _settings.DiscordButtonEnabled
            && (isDiscordRunning
                || _discordNotificationService.UnreadCount > 0
                || _discordNotificationService.HasActiveCall);
        DiscordButton.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool hasNotifs = _discordNotificationService.UnreadCount > 0 || _discordNotificationService.HasActiveCall;
        DiscordBadge.Visibility = hasNotifs ? Visibility.Visible : Visibility.Collapsed;
        DiscordBadgeText.Visibility = Visibility.Collapsed;
        DiscordBadge.Padding = new Thickness(0);
        DiscordBadge.MinWidth = 8;
        DiscordBadge.Height = 8;

        if (_discordNotificationService.HasActiveCall)
        {
            DiscordBadge.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 87, 242, 135));
        }
        else
        {
            DiscordBadge.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 237, 66, 69));
            DiscordBadgeText.Text = _discordNotificationService.UnreadCount > 99
                ? "99+"
                : _discordNotificationService.UnreadCount.ToString();
            DiscordBadgeText.Visibility = Visibility.Visible;
            DiscordBadge.Padding = new Thickness(3, 0, 3, 0);
            DiscordBadge.MinWidth = 12;
            DiscordBadge.Height = 12;
        }

        if (!isVisible && _discordNotificationWindow is { IsMenuVisible: true })
        {
            _discordNotificationWindow.Hide();
        }

        ApplyRightButtonsToggleState();
    }

    private void UpdateDiscordDemand(bool boost = false)
    {
        if (boost)
        {
            _lastDiscordBoostUtc = DateTime.UtcNow;
        }

        _discordModule.Update(EvaluateDiscordDemand());
    }

    private ModuleDemand EvaluateDiscordDemand()
    {
        if (!_ownsGlobalHotkeys || !_settings.DiscordButtonEnabled)
        {
            return ModuleDemand.Cold("discord-hidden");
        }

        if (_discordNotificationService.HasActiveCall ||
            _discordNotificationService.UnreadCount > 0 ||
            (_discordNotificationWindow?.IsMenuVisible ?? false) ||
            DateTime.UtcNow - _lastDiscordBoostUtc <= DiscordHotHoldDuration)
        {
            return ModuleDemand.Hot("discord-engaged");
        }

        return ModuleDemand.Warm("discord-visible");
    }

    private void OnDiscordTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        _discordNotificationService.SetDemand(_discordDemandOwnerId, nextTemperature);
    }

    private async Task ApplyRunCatSettingsAsync()
    {
        bool shouldRun = _ownsGlobalHotkeys && _settings.RunCatEnabled;
        string runner = _settings.RunCatRunner;
        int loadVersion = ++_runCatLoadVersion;

        if (!shouldRun)
        {
            if (_runCatService != null)
            {
                _runCatService.FrameChanged -= OnRunCatFrameChanged;
                _runCatService.Stop();
                _runCatService.Dispose();
                _runCatService = null;
                _runCatFrames = null;
            }

            RunCatButton.Visibility = Visibility.Collapsed;
            RunCatImage.Source = null;
            ApplyRightButtonsToggleState();
            return;
        }

        var effectiveTintHex = FormatColorHex(GetTopBarForegroundColor());
        if (_runCatService != null
            && _runCatService.RunnerName == runner
            && _runCatFrames is { Length: > 0 }
            && string.Equals(_lastRunCatTintHex, effectiveTintHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var newService = new RunCatService();
        newService.Start(runner);

        var tintedFrames = new ImageSource[newService.FrameCount];
        var tintColor = GetTopBarForegroundColor();
        for (int i = 0; i < newService.FrameCount; i++)
        {
            tintedFrames[i] = await CreateTintedRunCatFrameAsync(newService.GetFramePath(i), tintColor);
        }

        if (loadVersion != _runCatLoadVersion)
        {
            newService.Stop();
            newService.Dispose();
            return;
        }

        if (_runCatService != null)
        {
            _runCatService.FrameChanged -= OnRunCatFrameChanged;
            _runCatService.Stop();
            _runCatService.Dispose();
        }

        _runCatService = newService;
        _runCatFrames = tintedFrames;
        _lastRunCatTintHex = effectiveTintHex;
        RunCatImage.Source = _runCatFrames[0];
        RunCatButton.Visibility = Visibility.Visible;
        _runCatService.FrameChanged += OnRunCatFrameChanged;
        ApplyRightButtonsToggleState();
    }

    private void OnRunCatFrameChanged(int frame)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_runCatFrames != null && frame < _runCatFrames.Length)
            {
                RunCatImage.Source = _runCatFrames[frame];
            }
        });
    }

    private static async Task<ImageSource> CreateTintedRunCatFrameAsync(string path, global::Windows.UI.Color tintColor)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = pixelData.DetachPixelData();
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            if (alpha == 0)
            {
                continue;
            }

            pixels[index] = (byte)((tintColor.B * alpha) / 255);
            pixels[index + 1] = (byte)((tintColor.G * alpha) / 255);
            pixels[index + 2] = (byte)((tintColor.R * alpha) / 255);
        }

        var bitmap = new WriteableBitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        using var pixelStream = bitmap.PixelBuffer.AsStream();
        await pixelStream.WriteAsync(pixels);
        pixelStream.Seek(0, SeekOrigin.Begin);
        return bitmap;
    }
}
