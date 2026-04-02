using System.Diagnostics;
using System.IO;
using System.Text;
using System.Globalization;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class FinderWindow : Window
{
    private const int DebounceMilliseconds = 60;
    private const int WindowCornerRadius = 12;
    private const int MaxVisibleResults = 200;
    private static readonly CornerRadius InactiveRowCornerRadius = new(0);

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
    private static readonly ImageSource LinuxIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/linux.svg"));
    private static readonly bool IsWslAvailable = File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"));

    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _debounceTimer;
    private FinderEntry[] _allEntries = [];
    private FinderEntry[] _appEntries = [];
    private FinderEntry[] _filteredEntries = [];
    private FinderEntry[] _displayedEntries = [];
    private string[] _fallbackActions = [];
    private bool _isLoadingApps;
    private bool _showRequested;
    private string _pendingQuery = string.Empty;
    private DateTime _openedAtUtc;
    private int _selectedIndex = -1;
    private Button[]? _cachedAppRows;

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
    }

    /// <summary>
    /// Pre-initializes the window off-screen so that acrylic, chrome, and app data
    /// are ready before the user ever triggers the Finder. Call once after construction.
    /// </summary>
    internal void Prewarm()
    {
        Activate();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        ConfigureWindowChrome();
        SetupAcrylic();

        // Position off-screen and hide immediately so the prewarm is invisible
        SetWindowPos(_hwnd, IntPtr.Zero, -10000, -10000, 1, 1,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
        ShowWindowNative(_hwnd, SW_HIDE);

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

        // Restore default results instantly if apps are loaded
        if (_allEntries.Length > 0)
        {
            _filteredEntries = _appEntries;
            RebuildResults();
            UpdateStatusBar();
        }

        ShowWindowNative(_hwnd, SW_SHOW);
        Activate();
        SetForegroundWindow(_hwnd);
        SearchTextBox.Focus(FocusState.Programmatic);

        // Only load if not yet loaded
        if (_allEntries.Length == 0)
        {
            _ = EnsureAppsLoadedAsync();
        }
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

        HideWindow();
    }

    internal void HideWindow()
    {
        _debounceTimer.Stop();
        // Detach cached rows so they can be re-added on next show
        if (ReferenceEquals(_displayedEntries, _appEntries) && _cachedAppRows is not null)
        {
            ResultsPanel.Children.Clear();
            _displayedEntries = [];
        }
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindowNative(_hwnd, SW_HIDE);
        }
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

    internal void Destroy()
    {
        _debounceTimer.Stop();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        Close();
    }

    private async Task EnsureAppsLoadedAsync()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        if (_isLoadingApps || _allEntries.Length > 0)
        {
            return;
        }

        _isLoadingApps = true;
        UpdateStatusBar();

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

            // Pre-build the default rows (all apps, no filter)
            _cachedAppRows = new Button[appEntries.Length];
            for (int i = 0; i < appEntries.Length; i++)
            {
                _cachedAppRows[i] = CreateEntryRow(appEntries[i], i == 0);
            }
        }
        catch
        {
            _allEntries = [];
            _appEntries = [];
        }
        finally
        {
            _isLoadingApps = false;
            double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;
            PerformanceLogger.RecordMilliseconds("Finder.EnsureAppsLoadedAsync", elapsedMs, 25.0);
        }

        ApplyFilter(SearchTextBox.Text);
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
        int selectableCount = ResultsPanel.Children.Count;

        if (e.Key == global::Windows.System.VirtualKey.Enter && _selectedIndex >= 0)
        {
            if (_filteredEntries.Length > 0 && _selectedIndex < _filteredEntries.Length)
            {
                ExecuteEntry(_filteredEntries[_selectedIndex]);
                e.Handled = true;
                return;
            }

            if (_fallbackActions.Length > 0 && _selectedIndex < _fallbackActions.Length)
            {
                ExecuteFallback(_fallbackActions[_selectedIndex]);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == global::Windows.System.VirtualKey.Down && selectableCount > 0)
        {
            SetSelectedIndex(Math.Min(_selectedIndex + 1, selectableCount - 1));
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Up && selectableCount > 0)
        {
            SetSelectedIndex(Math.Max(_selectedIndex - 1, 0));
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Tab && selectableCount > 0)
        {
            if (_filteredEntries.Length > 0 && AutocompleteHint.Visibility == Visibility.Visible && AutocompleteHint.Inlines.Count == 2)
            {
                string full = _filteredEntries[0].Name;
                SearchTextBox.Text = full;
                SearchTextBox.SelectionStart = full.Length;
                AutocompleteHint.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            int next = _selectedIndex + 1;
            SetSelectedIndex(next >= selectableCount ? 0 : next);
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            HideWindow();
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
        using var perfScope = PerformanceLogger.Measure("Finder.ApplyFilter", 1.5);
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
            ResultCountText.Text = _fallbackActions.Length > 0 ? "No app results" : string.Empty;
            HintText.Text = _fallbackActions.Length > 0 ? "\u21b5 Run  \u2191\u2193 Navigate" : string.Empty;
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
        if (ReferenceEquals(_filteredEntries, _displayedEntries) && _filteredEntries.Length > 0)
        {
            // Same data, just reset selection
            SetSelectedIndex(0);
            UpdateStatusBar();
            return;
        }

        _displayedEntries = _filteredEntries;
        ResultsPanel.Children.Clear();
        _fallbackActions = [];

        // Fast path: reuse pre-built rows for the default (all apps) view
        if (ReferenceEquals(_filteredEntries, _appEntries) && _cachedAppRows is not null)
        {
            for (int i = 0; i < _cachedAppRows.Length; i++)
            {
                _cachedAppRows[i].Background = i == 0 ? SelectedBrush : TransparentBrush;
                _cachedAppRows[i].CornerRadius = InactiveRowCornerRadius;
                ResultsPanel.Children.Add(_cachedAppRows[i]);
            }
        }
        else
        {
            for (int i = 0; i < _filteredEntries.Length; i++)
            {
                ResultsPanel.Children.Add(CreateEntryRow(_filteredEntries[i], i == 0));
            }
        }

        bool showFallbacks = _filteredEntries.Length == 0
            && _allEntries.Length > 0
            && !_isLoadingApps
            && !string.IsNullOrWhiteSpace(SearchTextBox.Text);

        if (showFallbacks)
        {
            string query = SearchTextBox.Text.Trim();
            _fallbackActions = IsWslAvailable
                ? ["cmd", "wsl", "web"]
                : ["cmd", "web"];
            ResultsPanel.Children.Add(CreateFallbackRow("\uE756", $"Run \"{query}\"", "PowerShell", "cmd", true));
            if (IsWslAvailable)
            {
                ResultsPanel.Children.Add(CreateFallbackRow("\uE7C4", $"Run \"{query}\"", "WSL", "wsl", false));
            }
            ResultsPanel.Children.Add(CreateFallbackRow("\uE774", $"Search \"{query}\"", "Default browser", "web", false));
        }

        _selectedIndex = ResultsPanel.Children.Count > 0 ? 0 : -1;

        EmptyStatePanel.Visibility = _filteredEntries.Length == 0 && _fallbackActions.Length == 0 && _allEntries.Length > 0
            && !_isLoadingApps
            ? Visibility.Visible
            : Visibility.Collapsed;
        Separator.Visibility = ResultsPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = (_allEntries.Length > 0 || _isLoadingApps) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSelectedIndex(int index)
    {
        if (index == _selectedIndex || index < 0 || index >= ResultsPanel.Children.Count)
        {
            return;
        }

        if (_selectedIndex >= 0 && _selectedIndex < ResultsPanel.Children.Count &&
            ResultsPanel.Children[_selectedIndex] is Button oldBtn)
        {
            oldBtn.Background = TransparentBrush;
            oldBtn.CornerRadius = InactiveRowCornerRadius;
        }

        _selectedIndex = index;

        if (ResultsPanel.Children[_selectedIndex] is Button newBtn)
        {
            newBtn.Background = SelectedBrush;
            newBtn.CornerRadius = InactiveRowCornerRadius;
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
            cornerRadius: InactiveRowCornerRadius,
            tag: entry,
            horizontalAlignment: HorizontalAlignment.Stretch,
            horizontalContentAlignment: HorizontalAlignment.Stretch);
        return button;
    }

    private Button CreateFallbackRow(string icon, string label, string subtitle, string tag, bool selected)
    {
        FrameworkElement iconElement;

        if (tag == "wsl")
        {
            iconElement = new Image
            {
                Source = LinuxIconSource,
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform
            };
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = icon,
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
            Text = label,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CategoryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconElement, nameText, subtitleText }
        };

        var button = PanelButtonFactory.Create(
            content,
            selected ? SelectedBrush : TransparentBrush,
            TextBrush,
            HoverBrush,
            PressedBrush,
            OnFallbackButtonClick,
            height: 36,
            padding: new Thickness(14, 0, 14, 0),
            cornerRadius: InactiveRowCornerRadius,
            tag: tag,
            horizontalAlignment: HorizontalAlignment.Stretch,
            horizontalContentAlignment: HorizontalAlignment.Stretch);
        return button;
    }

    private void OnFallbackButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            ExecuteFallback(tag);
        }
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
        HideWindow();
    }

    private void ExecuteFallback(string action)
    {
        string query = SearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        string workspaceRoot = ResolveWorkspaceRoot();

        try
        {
            switch (action)
            {
                case "cmd":
                    string powerShellPath = GetPreferredPowerShellPath();
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = powerShellPath,
                        Arguments = BuildPowerShellArguments(query),
                        WorkingDirectory = workspaceRoot,
                        UseShellExecute = false,
                        CreateNoWindow = false
                    });
                    break;
                case "wsl":
                    if (!IsWslAvailable)
                    {
                        break;
                    }

                    string wslPowerShellPath = GetPreferredPowerShellPath();
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = wslPowerShellPath,
                        Arguments = BuildPowerShellArguments($"wsl.exe {BuildWslCommand(query)}"),
                        WorkingDirectory = workspaceRoot,
                        UseShellExecute = false,
                        CreateNoWindow = false
                    });
                    break;
                case "web":
                    string encoded = Uri.EscapeDataString(query);
                    Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={encoded}")
                    {
                        UseShellExecute = true
                    });
                    break;
            }
        }
        catch
        {
        }

        HideWindow();
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            bool hasGit = Directory.Exists(Path.Combine(current.FullName, ".git"));
            bool hasSolution = File.Exists(Path.Combine(current.FullName, "Veil.sln"));
            if (hasGit || hasSolution)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private static string GetPreferredPowerShellPath()
    {
        string powerShell7Path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");

        if (File.Exists(powerShell7Path))
        {
            return powerShell7Path;
        }

        return "powershell.exe";
    }

    private static string BuildPowerShellArguments(string query)
    {
        string escapedQuery = query.Replace("`", "``").Replace("\"", "`\"");
        return $"-NoExit -Command \"& {{ {escapedQuery} }}\"";
    }

    private static string BuildWslCommand(string query)
    {
        string escapedQuery = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-e bash -lc \"{escapedQuery}\"";
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
