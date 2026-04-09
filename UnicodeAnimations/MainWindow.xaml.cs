using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using UnicodeAnimations.Models;
using UnicodeAnimations.ViewModels;
using Windows.Graphics;

namespace UnicodeAnimations;

public sealed partial class MainWindow : Window
{
    private readonly List<SpinnerViewModel> _viewModels = [];

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        LoadSpinners();
    }

    // ── Window setup ─────────────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        // Custom dark title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        // Initial size  
        AppWindow.Resize(new SizeInt32(980, 660));

        // Title bar colours (system caption buttons)
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = AppWindow.TitleBar;
            var transparent = Colors.Transparent;
            var darkBg      = Windows.UI.Color.FromArgb(0xFF, 0x0B, 0x0B, 0x0B);
            var fgColor     = Windows.UI.Color.FromArgb(0xFF, 0x44, 0x44, 0x44);
            var hoverBg     = Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

            tb.BackgroundColor             = darkBg;
            tb.ForegroundColor             = fgColor;
            tb.InactiveBackgroundColor     = darkBg;
            tb.InactiveForegroundColor     = fgColor;
            tb.ButtonBackgroundColor       = transparent;
            tb.ButtonForegroundColor       = fgColor;
            tb.ButtonHoverBackgroundColor  = hoverBg;
            tb.ButtonHoverForegroundColor  = Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x22, 0x22, 0x22);
            tb.ButtonPressedForegroundColor = Colors.White;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonInactiveForegroundColor = fgColor;
        }

        Closed += (_, _) => DisposeViewModels();
    }

    // ── Spinners ─────────────────────────────────────────────────────────────

    private void LoadSpinners()
    {
        var queue = DispatcherQueue;

        foreach (var (name, spinner) in SpinnerRegistry.All)
        {
            _viewModels.Add(new SpinnerViewModel(name, spinner, queue));
        }

        SpinnersHost.ItemsSource = _viewModels;

        // Footer status
        FooterText.Text =
            $"{_viewModels.Count} spinners  ·  " +
            $"{_viewModels.Sum(v => int.Parse(v.IntervalLabel.Replace(" ms", "")) == 0 ? 0 : 1)} active";

        FooterText.Text = $"{_viewModels.Count} spinners active";
    }

    private void DisposeViewModels()
    {
        foreach (var vm in _viewModels)
            vm.Dispose();
        _viewModels.Clear();
    }
}
