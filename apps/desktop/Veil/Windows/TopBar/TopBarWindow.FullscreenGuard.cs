using Veil.Interop;
using Veil.Services;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private bool _isHiddenForFullscreen;

    private void CheckFullscreenState()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_settings.HideOnFullscreen)
        {
            if (_isHiddenForFullscreen)
            {
                RestoreFromFullscreen();
            }

            return;
        }

        bool fullscreen = FullscreenDetectionService.IsFullscreenWindowOnMonitor(_screen, _hwnd);

        if (fullscreen && !_isHiddenForFullscreen)
        {
            HideForFullscreen();
        }
        else if (!fullscreen && _isHiddenForFullscreen)
        {
            RestoreFromFullscreen();
        }
    }

    private void HideForFullscreen()
    {
        if (_isHiddenForFullscreen || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _isHiddenForFullscreen = true;

        _finderWindow?.HideWindow();
        _musicControlWindow?.Hide();
        _discordNotificationWindow?.Hide();
        _systemStatsWindow?.Hide();

        _runCatLoadVersion++;
        StopRunCat();

        if (_appBarRegistered)
        {
            WindowHelper.UnregisterAppBar(this);
            _appBarRegistered = false;
        }

        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void RestoreFromFullscreen()
    {
        if (!_isHiddenForFullscreen || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _isHiddenForFullscreen = false;

        ApplyTopBarPlacement(true);
        ShowWindowNative(_hwnd, SW_SHOWNOACTIVATE);
        _ = ApplyRunCatSettingsAsync();
        BoostAppBarMaintenance();
    }
}
