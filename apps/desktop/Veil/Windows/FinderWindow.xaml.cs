using System.Text;
using System.Globalization;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class FinderWindow : Window
{
    private const int DebounceMilliseconds = 100;
    private const int WindowCornerRadius = 12;
    private const int MaxVisibleResults = 200;

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush SelectedBrush = new(
        global::Windows.UI.Color.FromArgb(20, 255, 255, 255));
    private static readonly SolidColorBrush HoverBrush = new(
        global::Windows.UI.Color.FromArgb(12, 255, 255, 255));
    private static readonly SolidColorBrush PressedBrush = new(
        global::Windows.UI.Color.FromArgb(8, 255, 255, 255));
    private static readonly SolidColorBrush TextBrush = new(
        global::Windows.UI.Color.FromArgb(220, 255, 255, 255));
    private static readonly SolidColorBrush SubtleBrush = new(
        global::Windows.UI.Color.FromArgb(100, 255, 255, 255));
    private static readonly SolidColorBrush CategoryBrush = new(
        global::Windows.UI.Color.FromArgb(60, 255, 255, 255));
    private static readonly SolidColorBrush AutocompleteBrush = new(
        global::Windows.UI.Color.FromArgb(80, 255, 255, 255));
    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");

    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _debounceTimer;
    private FinderEntry[] _allEntries = [];
    private FinderEntry[] _appEntries = [];
    private FinderEntry[] _filteredEntries = [];
    private bool _isLoadingApps;
    private bool _showRequested;
    private string _pendingQuery = string.Empty;
    private DateTime _openedAtUtc;
    private int _selectedIndex = -1;

    internal FinderWindow(ScreenBounds screen)
    {
        _screen = screen;
        _settings = AppSettings.Current;

        InitializeComponent();
        Title = "Veil Finder";

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds)
        };
        _debounceTimer.Tick += OnDebounceElapsed;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        ConfigureWindowChrome();
        SetupAcrylic();
        ApplyWindowSize();
        _ = EnsureAppsLoadedAsync();

        if (_showRequested)
        {
            DispatcherQueue.TryEnqueue(ShowCenteredCore);
        }
    }

    private void ConfigureWindowChrome()
    {
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(255, 28, 28, 32),
            TintOpacity = 0.78f,
            LuminosityOpacity = 0.24f,
            FallbackColor = global::Windows.UI.Color.FromArgb(240, 22, 22, 26)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    internal void ShowCentered()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _showRequested = true;
            Activate();
            return;
        }

        ShowCenteredCore();
    }

    private void ShowCenteredCore()
    {
        _showRequested = false;
        _openedAtUtc = DateTime.UtcNow;
        ApplyWindowSize();
        SearchTextBox.Text = string.Empty;
        UpdatePlaceholder();
        Activate();
        SetForegroundWindow(_hwnd);
        SearchTextBox.Focus(FocusState.Programmatic);
        _ = EnsureAppsLoadedAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        Close();
    }

    private void ApplyWindowSize()
    {
        int screenWidth = _screen.Right - _screen.Left;
        int screenHeight = _screen.Bottom - _screen.Top;

        int width = Math.Clamp((int)(screenWidth * 0.32), 480, 680);
        int height = Math.Clamp((int)(screenHeight * 0.48), 380, 520);

        int x = _screen.Left + ((screenWidth - width) / 2);
        int y = _screen.Top + (int)(screenHeight * 0.22);

        WindowHelper.PositionOnMonitor(this, x, y, width, height);
        WindowHelper.ApplyRoundedRegion(_hwnd, width, height, WindowCornerRadius);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _debounceTimer.Stop();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
    }

    private async Task EnsureAppsLoadedAsync()
    {
        if (_isLoadingApps || _allEntries.Length > 0)
        {
            return;
        }

        _isLoadingApps = true;
        UpdateStatusBar();
        RebuildResults();

        try
        {
            var apps = await InstalledAppService.GetAppsAsync();
            if (apps.Count == 0)
            {
                apps = await InstalledAppService.RefreshAppsAsync();
            }

            var actions = WindowsActionService.GetActions();

            var appEntries = new FinderEntry[apps.Count];
            for (int i = 0; i < apps.Count; i++)
            {
                appEntries[i] = FinderEntry.FromApp(apps[i]);
            }

            var allEntries = new FinderEntry[apps.Count + actions.Length];
            appEntries.CopyTo(allEntries, 0);
            actions.CopyTo(allEntries, apps.Count);

            _appEntries = appEntries;
            _allEntries = allEntries;
            ApplyFilter(SearchTextBox.Text);
        }
        catch
        {
            _allEntries = [];
            _appEntries = [];
            ApplyFilter(SearchTextBox.Text);
        }
        finally
        {
            _isLoadingApps = false;
            UpdateStatusBar();
            RebuildResults();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _pendingQuery = SearchTextBox.Text;
        UpdatePlaceholder();
        ApplyAutocomplete(_pendingQuery);
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceElapsed(object? sender, object e)
    {
        _debounceTimer.Stop();
        ApplyFilter(_pendingQuery);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter && _filteredEntries.Length > 0 && _selectedIndex >= 0)
        {
            ExecuteEntry(_filteredEntries[_selectedIndex]);
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Down && _filteredEntries.Length > 0)
        {
            SetSelectedIndex(Math.Min(_selectedIndex + 1, _filteredEntries.Length - 1));
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Up && _filteredEntries.Length > 0)
        {
            SetSelectedIndex(Math.Max(_selectedIndex - 1, 0));
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Tab && _filteredEntries.Length > 0)
        {
            if (AutocompleteHint.Visibility == Visibility.Visible && AutocompleteHint.Inlines.Count == 2)
            {
                string full = _filteredEntries[0].Name;
                SearchTextBox.Text = full;
                SearchTextBox.SelectionStart = full.Length;
                AutocompleteHint.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            int next = _selectedIndex + 1;
            SetSelectedIndex(next >= _filteredEntries.Length ? 0 : next);
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void UpdatePlaceholder()
    {
        bool empty = string.IsNullOrEmpty(SearchTextBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchShortcutHint.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyFilter(string? query)
    {
        string raw = query ?? string.Empty;
        string normalizedSearch = NormalizeSearch(raw);

        if (normalizedSearch.Length == 0)
        {
            _filteredEntries = _appEntries;
        }
        else
        {
            var buffer = new (FinderEntry entry, int score)[_allEntries.Length];
            int count = 0;

            for (int i = 0; i < _allEntries.Length; i++)
            {
                int score = ScoreMatch(_allEntries[i], normalizedSearch);
                if (score > 0)
                {
                    buffer[count++] = (_allEntries[i], score);
                }
            }

            Array.Sort(buffer, 0, count, ScoredEntryComparer.Instance);

            int take = Math.Min(count, MaxVisibleResults);
            var result = new FinderEntry[take];
            for (int i = 0; i < take; i++)
            {
                result[i] = buffer[i].entry;
            }

            _filteredEntries = result;
        }

        RebuildResults();
        UpdateStatusBar();
    }

    private static int ScoreMatch(FinderEntry entry, string query)
    {
        string token = entry.SearchToken;
        int kindBonus = entry.App is not null ? 1 : 0;

        if (token.StartsWith(query, StringComparison.Ordinal))
        {
            return 100 + kindBonus;
        }

        if (token.Contains(query, StringComparison.Ordinal))
        {
            return 50 + kindBonus;
        }

        if (entry.App is null && entry.CategoryLower.Contains(query, StringComparison.Ordinal))
        {
            return 30;
        }

        return 0;
    }

    private sealed class ScoredEntryComparer : IComparer<(FinderEntry entry, int score)>
    {
        internal static readonly ScoredEntryComparer Instance = new();
        public int Compare((FinderEntry entry, int score) x, (FinderEntry entry, int score) y)
        {
            int cmp = y.score.CompareTo(x.score);
            return cmp != 0 ? cmp : x.entry.Name.Length.CompareTo(y.entry.Name.Length);
        }
    }

    private void UpdateStatusBar()
    {
        if (_isLoadingApps)
        {
            ResultCountText.Text = "Loading...";
            HintText.Text = string.Empty;
            return;
        }

        if (_filteredEntries.Length == 0)
        {
            ResultCountText.Text = string.Empty;
            HintText.Text = string.Empty;
        }
        else
        {
            ResultCountText.Text = _filteredEntries == _appEntries
                ? $"{_appEntries.Length} applications"
                : $"{_filteredEntries.Length} results";
            HintText.Text = _selectedIndex >= 0 ? "\u21b5 Open  \u2191\u2193 Navigate" : string.Empty;
        }
    }

    private void RebuildResults()
    {
        ResultsPanel.Children.Clear();
        _selectedIndex = _filteredEntries.Length > 0 ? 0 : -1;

        for (int i = 0; i < _filteredEntries.Length; i++)
        {
            ResultsPanel.Children.Add(CreateEntryRow(_filteredEntries[i], i == 0));
        }

        EmptyStatePanel.Visibility = _filteredEntries.Length == 0 && _allEntries.Length > 0
            && !_isLoadingApps
            ? Visibility.Visible
            : Visibility.Collapsed;
        Separator.Visibility = _filteredEntries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = (_allEntries.Length > 0 || _isLoadingApps) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSelectedIndex(int index)
    {
        if (index == _selectedIndex || index < 0 || index >= _filteredEntries.Length)
        {
            return;
        }

        if (_selectedIndex >= 0 && _selectedIndex < ResultsPanel.Children.Count &&
            ResultsPanel.Children[_selectedIndex] is Button oldBtn)
        {
            oldBtn.Background = TransparentBrush;
        }

        _selectedIndex = index;

        if (ResultsPanel.Children[_selectedIndex] is Button newBtn)
        {
            newBtn.Background = SelectedBrush;
            newBtn.StartBringIntoView();
        }

        UpdateStatusBar();
    }

    private Button CreateEntryRow(FinderEntry entry, bool selected)
    {
        FrameworkElement iconElement;

        if (entry.App is not null)
        {
            var icon = new Image
            {
                Width = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform
            };

            var iconSource = AppIconService.GetIcon(entry.App);
            if (iconSource is not null)
            {
                icon.Source = iconSource;
            }

            iconElement = icon;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = entry.IconGlyph ?? "\uE713",
                FontFamily = GlyphFont,
                FontSize = 16,
                Width = 24,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Foreground = SubtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var nameText = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconElement, nameText }
        };

        if (entry.App is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = entry.Category,
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CategoryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            });
        }

        var button = PanelButtonFactory.Create(
            content,
            selected ? SelectedBrush : TransparentBrush,
            TextBrush,
            HoverBrush,
            PressedBrush,
            OnEntryButtonClick,
            height: 36,
            padding: new Thickness(14, 0, 14, 0),
            cornerRadius: new CornerRadius(0),
            tag: entry,
            horizontalAlignment: HorizontalAlignment.Stretch,
            horizontalContentAlignment: HorizontalAlignment.Stretch);
        return button;
    }

    private void OnEntryButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FinderEntry entry })
        {
            ExecuteEntry(entry);
        }
    }

    private void ExecuteEntry(FinderEntry entry)
    {
        entry.Execute();
        Close();
    }

    private static string NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private void ApplyAutocomplete(string query)
    {
        if (query.Length == 0 || _allEntries.Length == 0)
        {
            AutocompleteHint.Inlines.Clear();
            AutocompleteHint.Visibility = Visibility.Collapsed;
            return;
        }

        string? best = null;
        for (int i = 0; i < _allEntries.Length; i++)
        {
            string name = _allEntries[i].Name;
            if (name.Length > query.Length &&
                name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) &&
                (best is null || name.Length < best.Length))
            {
                best = name;
            }
        }

        if (best is null)
        {
            AutocompleteHint.Inlines.Clear();
            AutocompleteHint.Visibility = Visibility.Collapsed;
            return;
        }

        AutocompleteHint.Inlines.Clear();
        AutocompleteHint.Inlines.Add(new Run
        {
            Text = query,
            Foreground = TransparentBrush
        });
        AutocompleteHint.Inlines.Add(new Run
        {
            Text = best[query.Length..],
            Foreground = AutocompleteBrush
        });
        AutocompleteHint.Visibility = Visibility.Visible;
    }
}
