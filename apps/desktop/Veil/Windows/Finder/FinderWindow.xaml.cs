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
    private const int DebounceMilliseconds = 25;
    private const int WindowCornerRadius = 24;
    private const int MaxVisibleResults = 200;
    private static readonly TimeSpan SessionKeepAliveDuration = TimeSpan.FromMinutes(10);
    private static readonly CornerRadius RowCornerRadius = new(14);

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush SelectedBrush = new(
        global::Windows.UI.Color.FromArgb(30, 255, 255, 255));
    private static readonly SolidColorBrush HoverBrush = new(
        global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush PressedBrush = new(
        global::Windows.UI.Color.FromArgb(12, 255, 255, 255));
    private static readonly SolidColorBrush TextBrush = new(
        global::Windows.UI.Color.FromArgb(236, 255, 255, 255));
    private static readonly SolidColorBrush SubtleBrush = new(
        global::Windows.UI.Color.FromArgb(150, 255, 255, 255));
    private static readonly SolidColorBrush SecondaryTextBrush = new(
        global::Windows.UI.Color.FromArgb(120, 255, 255, 255));
    private static readonly SolidColorBrush CategoryBrush = new(
        global::Windows.UI.Color.FromArgb(170, 255, 255, 255));
    private static readonly SolidColorBrush AutocompleteBrush = new(
        global::Windows.UI.Color.FromArgb(110, 255, 255, 255));
    private static readonly SolidColorBrush SurfaceBrush = new(
        global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
    private static readonly SolidColorBrush SurfaceOutlineBrush = new(
        global::Windows.UI.Color.FromArgb(24, 255, 255, 255));
    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");
    private static readonly ImageSource LinuxIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/linux.svg"));
    private static readonly bool IsWslAvailable = File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"));

    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _sessionKeepAliveTimer;
    private FinderEntry[] _allEntries = [];
    private FinderEntry[] _appEntries = [];
    private FinderEntry[] _filteredEntries = [];
    private FinderEntry[] _displayedEntries = [];
    private string[] _fallbackActions = [];
    private bool _isLoadingApps;
    private bool _showRequested;
    private string _pendingQuery = string.Empty;
    private DateTime _openedAtUtc;
    private DateTime _sessionKeepAliveUntilUtc = DateTime.MinValue;
    private int _selectedIndex = -1;
    private double _retainedVerticalOffset;
    private int _retainedSearchSelectionStart;
    private int _retainedSearchSelectionLength;
    private Button[]? _cachedAppRows;
    private FinderAiWindow? _aiWindow;

    // Pooled scoring buffer to avoid allocations per keystroke
    private (FinderEntry entry, int score)[] _scoreBuffer = [];

    // Sorted names for binary-search autocomplete
    private string[] _sortedNames = [];
    private FinderEntry[] _sortedNameEntries = [];

    // Pooled StringBuilder for NormalizeSearch
    [ThreadStatic]
    private static StringBuilder? t_normalizeBuilder;

    internal event EventHandler? SessionExpired;

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

        _sessionKeepAliveTimer = new DispatcherTimer
        {
            Interval = SessionKeepAliveDuration
        };
        _sessionKeepAliveTimer.Tick += OnSessionKeepAliveElapsed;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
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
        WindowHelper.ApplyAppIcon(this);
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        WindowHelper.PrepareForSystemBackdrop(this);
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(242, 24, 24, 28),
            TintOpacity = 0.34f,
            LuminosityOpacity = 0.12f,
            FallbackColor = global::Windows.UI.Color.FromArgb(212, 20, 20, 24)
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
        bool resumeSession = ShouldResumeSession();
        CancelSessionKeepAlive();
        _openedAtUtc = DateTime.UtcNow;
        ApplyWindowSize();
        if (!resumeSession)
        {
            SearchTextBox.Text = string.Empty;
            UpdatePlaceholder();

            if (_allEntries.Length > 0)
            {
                _filteredEntries = _appEntries;
                RebuildResults();
                UpdateStatusBar();
            }
        }
        else
        {
            UpdatePlaceholder();
            if (_allEntries.Length > 0)
            {
                RebuildResults(preserveSelection: true);
                UpdateStatusBar();
                RestoreRetainedViewport();
            }
        }

        ShowWindowNative(_hwnd, SW_SHOW);
        Activate();
        SetForegroundWindow(_hwnd);
        SearchTextBox.Focus(FocusState.Programmatic);

        // Only load if not yet loaded - await silently so no "Loading..." is visible
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

    internal void HideWindow(bool keepSessionWarm = true)
    {
        _debounceTimer.Stop();
        _retainedVerticalOffset = ResultsScrollViewer.VerticalOffset;
        _retainedSearchSelectionStart = SearchTextBox.SelectionStart;
        _retainedSearchSelectionLength = SearchTextBox.SelectionLength;
        // Detach cached rows so they can be re-added on next show
        if (ReferenceEquals(_displayedEntries, _appEntries) && _cachedAppRows is not null)
        {
            ResultsPanel.Children.Clear();
            _displayedEntries = [];
        }

        if (keepSessionWarm)
        {
            ScheduleSessionKeepAlive();
        }
        else
        {
            CancelSessionKeepAlive();
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

        int width = Math.Clamp((int)(screenWidth * 0.38), 620, 760);
        int height = Math.Clamp((int)(screenHeight * 0.56), 440, 620);

        int x = _screen.Left + ((screenWidth - width) / 2);
        int y = _screen.Top + (int)(screenHeight * 0.16);

        WindowHelper.PositionOnMonitor(this, x, y, width, height);
        WindowHelper.ApplyRoundedRegion(_hwnd, width, height, WindowCornerRadius);
    }

    internal void Destroy()
    {
        CancelSessionKeepAlive();
        _sessionKeepAliveTimer.Stop();
        _debounceTimer.Stop();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        _aiWindow?.Close();
        _aiWindow = null;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _debounceTimer.Stop();
        _sessionKeepAliveTimer.Stop();
        _sessionKeepAliveUntilUtc = DateTime.MinValue;
        _retainedVerticalOffset = 0;
    }

    private async Task EnsureAppsLoadedAsync()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        if (_isLoadingApps || _allEntries.Length > 0)
        {
            return;
        }

        _isLoadingApps = true;

        // Don't show "Loading..." - the preload from App.OnLaunched should already be in flight.
        // If apps arrive quickly the user never sees an empty state.

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

            // Allocate pooled scoring buffer once
            _scoreBuffer = new (FinderEntry, int)[allEntries.Length];

            // Build sorted name index for fast autocomplete
            BuildAutocompleteIndex(allEntries);

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

    private void BuildAutocompleteIndex(FinderEntry[] entries)
    {
        // Sort entries by lowercase name for binary-search prefix matching
        var pairs = new (string lower, FinderEntry entry)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            pairs[i] = (entries[i].Name.ToLowerInvariant(), entries[i]);
        }

        Array.Sort(pairs, static (a, b) =>
        {
            int cmp = string.Compare(a.lower, b.lower, StringComparison.Ordinal);
            return cmp != 0 ? cmp : a.entry.Name.Length.CompareTo(b.entry.Name.Length);
        });

        _sortedNames = new string[pairs.Length];
        _sortedNameEntries = new FinderEntry[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
        {
            _sortedNames[i] = pairs[i].lower;
            _sortedNameEntries[i] = pairs[i].entry;
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
        int selectableCount = ResultsPanel.Children.Count;

        if (e.Key == global::Windows.System.VirtualKey.Enter && _selectedIndex >= 0)
        {
            if (_filteredEntries.Length > 0 && _selectedIndex < _filteredEntries.Length)
            {
                ExecuteEntry(_filteredEntries[_selectedIndex]);
                e.Handled = true;
                return;
            }

            int fallbackIndex = _selectedIndex - _filteredEntries.Length;
            if (_fallbackActions.Length > 0 && fallbackIndex >= 0 && fallbackIndex < _fallbackActions.Length)
            {
                ExecuteFallback(_fallbackActions[fallbackIndex]);
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
            if (AutocompleteHint.Visibility == Visibility.Visible && AutocompleteHint.Inlines.Count == 2)
            {
                string full = string.Concat(AutocompleteHint.Inlines.OfType<Run>().Select(static inline => inline.Text));
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
        string trimmedQuery = raw.Trim();
        string normalizedSearch = NormalizeSearch(raw);

        if (normalizedSearch.Length == 0)
        {
            _filteredEntries = _appEntries;
        }
        else
        {
            // Reuse pooled buffer
            var buffer = _scoreBuffer;
            if (buffer.Length < _allEntries.Length)
            {
                buffer = new (FinderEntry, int)[_allEntries.Length];
                _scoreBuffer = buffer;
            }

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

        if (trimmedQuery.Length > 0)
        {
            _filteredEntries = PrependAskAiEntry(trimmedQuery, _filteredEntries);
        }

        RebuildResults();
        UpdateStatusBar();
    }

    private static int ScoreMatch(FinderEntry entry, string query)
    {
        string token = entry.SearchToken;
        int kindBonus = entry.App is not null ? 2 : 0;

        // Exact prefix match - best score
        if (token.StartsWith(query, StringComparison.Ordinal))
        {
            // Bonus for shorter names (more precise match)
            int lengthBonus = Math.Max(0, 20 - (token.Length - query.Length));
            return 200 + kindBonus + lengthBonus;
        }

        // Word-boundary match: query matches the start of any word
        int wordBoundaryScore = ScoreWordBoundary(token, query);
        if (wordBoundaryScore > 0)
        {
            return 120 + kindBonus + wordBoundaryScore;
        }

        // Substring match
        if (token.Contains(query, StringComparison.Ordinal))
        {
            return 80 + kindBonus;
        }

        // Abbreviation match: first letters of words
        if (query.Length >= 2 && MatchesAbbreviation(token, query))
        {
            return 70 + kindBonus;
        }

        // Category match for non-app entries
        if (entry.App is null && entry.CategoryLower.Contains(query, StringComparison.Ordinal))
        {
            return 30;
        }

        // Fuzzy: all characters present in order
        if (query.Length >= 3 && FuzzyContains(token, query))
        {
            return 15 + kindBonus;
        }

        return 0;
    }

    private static int ScoreWordBoundary(string token, string query)
    {
        // Check if query matches the start of any "word" in the token
        // Words start after non-alphanumeric chars or at uppercase letters in camelCase
        int tokenLen = token.Length;
        int queryLen = query.Length;

        for (int i = 1; i <= tokenLen - queryLen; i++)
        {
            char prev = token[i - 1];
            char curr = token[i];

            // Word boundary: previous char is non-letter or transition to uppercase
            bool isBoundary = !char.IsLetterOrDigit(prev) ||
                              (char.IsLower(prev) && char.IsUpper(curr));

            if (!isBoundary)
            {
                continue;
            }

            if (token.AsSpan(i, queryLen).SequenceEqual(query.AsSpan()))
            {
                return 10;
            }
        }

        return 0;
    }

    private static bool MatchesAbbreviation(string token, string query)
    {
        // Check if query chars match the first letter of consecutive words
        int qi = 0;
        bool atWordStart = true;

        for (int i = 0; i < token.Length && qi < query.Length; i++)
        {
            char c = token[i];

            if (!char.IsLetterOrDigit(c))
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart && c == query[qi])
            {
                qi++;
            }

            atWordStart = false;
        }

        return qi == query.Length;
    }

    private static bool FuzzyContains(string token, string query)
    {
        int qi = 0;
        for (int i = 0; i < token.Length && qi < query.Length; i++)
        {
            if (token[i] == query[qi])
            {
                qi++;
            }
        }

        return qi == query.Length;
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
        int nonAiResultCount = _filteredEntries.Count(static entry => entry.ActionId != FinderActionIds.AiAgent);

        if (_isLoadingApps)
        {
            SectionTitleText.Text = "Finder";
            SectionSubtitleText.Text = "Loading your application library.";
            ResultCountText.Text = string.Empty;
            HintText.Text = string.Empty;
            return;
        }

        if (_fallbackActions.Length > 0)
        {
            SectionTitleText.Text = nonAiResultCount == 0 ? "AI and quick actions" : "Results";
            SectionSubtitleText.Text = nonAiResultCount == 0
                ? "Ask the native AI agent or run the raw query in PowerShell, WSL, or your browser."
                : "Ranked across AI, apps, settings, and system actions.";
            ResultCountText.Text = nonAiResultCount == 0
                ? $"{_fallbackActions.Length + _filteredEntries.Length} quick actions"
                : $"{_filteredEntries.Length + _fallbackActions.Length} results";
            HintText.Text = _selectedIndex >= 0 ? "Enter open  •  ↑↓ navigate" : string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
        {
            SectionTitleText.Text = "Applications";
            SectionSubtitleText.Text = "Your installed apps, ready to open or focus.";
            ResultCountText.Text = $"{_appEntries.Length} applications";
            HintText.Text = _selectedIndex >= 0 ? "Enter open  •  ↑↓ navigate" : "Type to search";
            return;
        }

        if (nonAiResultCount == 0 && _filteredEntries.Length == 0)
        {
            SectionTitleText.Text = "No matches";
            SectionSubtitleText.Text = "Try another name or use the quick actions below.";
            ResultCountText.Text = string.Empty;
            HintText.Text = "Esc close";
        }
        else
        {
            SectionTitleText.Text = _filteredEntries.Length == 1 ? "Best match" : "Results";
            SectionSubtitleText.Text = "Ranked across apps, settings, and system actions.";
            ResultCountText.Text = $"{_filteredEntries.Length} results";
            HintText.Text = _selectedIndex >= 0 ? "Enter open  •  Tab complete" : "Tab complete";
        }
    }

    private void RebuildResults(bool preserveSelection = false)
    {
        if (ReferenceEquals(_filteredEntries, _displayedEntries) && _filteredEntries.Length > 0)
        {
            int target = preserveSelection && _selectedIndex >= 0
                ? Math.Min(_selectedIndex, ResultsPanel.Children.Count - 1)
                : 0;
            SetSelectedIndex(target);
            UpdateStatusBar();
            return;
        }

        int previousSelectedIndex = _selectedIndex;
        _displayedEntries = _filteredEntries;
        ResultsPanel.Children.Clear();
        _fallbackActions = [];

        // Fast path: reuse pre-built rows for the default (all apps) view
        if (ReferenceEquals(_filteredEntries, _appEntries) && _cachedAppRows is not null)
        {
            for (int i = 0; i < _cachedAppRows.Length; i++)
            {
                _cachedAppRows[i].Background = i == 0 ? SelectedBrush : TransparentBrush;
                _cachedAppRows[i].CornerRadius = RowCornerRadius;
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

        bool hasNonAiResults = _filteredEntries.Any(static entry => entry.ActionId != FinderActionIds.AiAgent);
        bool showFallbacks = !hasNonAiResults
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

        _selectedIndex = -1;
        if (ResultsPanel.Children.Count > 0)
        {
            int targetIndex = preserveSelection && previousSelectedIndex >= 0
                ? Math.Min(previousSelectedIndex, ResultsPanel.Children.Count - 1)
                : 0;
            SetSelectedIndex(targetIndex);
        }

        EmptyStatePanel.Visibility = _filteredEntries.Length == 0 && _fallbackActions.Length == 0 && _allEntries.Length > 0
            && !_isLoadingApps
            ? Visibility.Visible
            : Visibility.Collapsed;
        Separator.Visibility = (_allEntries.Length > 0 || _isLoadingApps) ? Visibility.Visible : Visibility.Collapsed;
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
            oldBtn.CornerRadius = RowCornerRadius;
        }

        _selectedIndex = index;

        if (ResultsPanel.Children[_selectedIndex] is Button newBtn)
        {
            newBtn.Background = SelectedBrush;
            newBtn.CornerRadius = RowCornerRadius;
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
                Width = 22,
                Height = 22,
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
                FontSize = 17,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Foreground = SubtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconPlate = CreateIconPlate(iconElement);

        var nameText = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = entry.App is not null ? "Application" : GetEntrySubtitle(entry.Category),
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = SecondaryTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1
        };

        var textStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameText, subtitleText }
        };

        var content = new Grid
        {
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        content.Children.Add(iconPlate);
        Grid.SetColumn(iconPlate, 0);

        content.Children.Add(textStack);
        Grid.SetColumn(textStack, 1);

        var button = PanelButtonFactory.Create(
            content,
            selected ? SelectedBrush : TransparentBrush,
            TextBrush,
            HoverBrush,
            PressedBrush,
            OnEntryButtonClick,
            height: 62,
            padding: new Thickness(12, 10, 12, 10),
            cornerRadius: RowCornerRadius,
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
                Width = 17,
                Height = 17,
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
                FontSize = 17,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Foreground = SubtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconPlate = CreateIconPlate(iconElement);

        var nameText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subtitleText = new TextBlock
        {
            Text = GetFallbackSubtitle(tag, subtitle),
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = SecondaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        var textStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameText, subtitleText }
        };

        var content = new Grid
        {
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        content.Children.Add(iconPlate);
        Grid.SetColumn(iconPlate, 0);

        content.Children.Add(textStack);
        Grid.SetColumn(textStack, 1);

        Border badge = CreateBadge(GetFallbackBadgeText(tag));
        content.Children.Add(badge);
        Grid.SetColumn(badge, 2);

        var button = PanelButtonFactory.Create(
            content,
            selected ? SelectedBrush : TransparentBrush,
            TextBrush,
            HoverBrush,
            PressedBrush,
            OnFallbackButtonClick,
            height: 62,
            padding: new Thickness(12, 10, 12, 10),
            cornerRadius: RowCornerRadius,
            tag: tag,
            horizontalAlignment: HorizontalAlignment.Stretch,
            horizontalContentAlignment: HorizontalAlignment.Stretch);
        return button;
    }

    private static Border CreateIconPlate(UIElement iconElement)
    {
        return new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(12),
            Background = SurfaceBrush,
            BorderBrush = SurfaceOutlineBrush,
            BorderThickness = new Thickness(1),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8),
                Child = iconElement
            }
        };
    }

    private static Border CreateBadge(string text)
    {
        return new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(999),
            Background = SurfaceBrush,
            BorderBrush = SurfaceOutlineBrush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = CategoryBrush,
                CharacterSpacing = 20
            }
        };
    }

    private static string GetEntrySubtitle(string category)
    {
        return category switch
        {
            "Assistant" => "Veil Halo",
            "Settings" => "Windows setting",
            "System" => "System command",
            "Utility" => "Built-in utility",
            _ => category
        };
    }

    private static string GetFallbackSubtitle(string tag, string fallback)
    {
        return tag switch
        {
            "cmd" => "Run this query in PowerShell",
            "wsl" => "Run this query in WSL",
            "web" => "Search this query in your default browser",
            _ => fallback
        };
    }

    private static string GetFallbackBadgeText(string tag)
    {
        return tag switch
        {
            "cmd" => "Shell",
            "wsl" => "WSL",
            "web" => "Web",
            _ => "Action"
        };
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
        if (entry.ActionId == FinderActionIds.AiAgent)
        {
            string prompt = SearchTextBox.Text.Trim();
            OpenAiWindow(prompt, autoSubmit: prompt.Length > 0);
            HideWindow();
            return;
        }

        entry.Execute();
        HideWindow();
    }

    private FinderEntry[] PrependAskAiEntry(string query, FinderEntry[] entries)
    {
        FinderEntry aiEntry = new(
            TrimAskAiLabel(query),
            "Assistant",
            InstalledAppService.NormalizeText(query + " ai agent"),
            null,
            "\uE8A5")
        {
            ActionId = FinderActionIds.AiAgent
        };

        FinderEntry[] filtered = entries
            .Where(static entry => entry.ActionId != FinderActionIds.AiAgent)
            .ToArray();
        var result = new FinderEntry[filtered.Length + 1];
        result[0] = aiEntry;
        filtered.CopyTo(result, 1);
        return result;
    }

    private static string TrimAskAiLabel(string query)
    {
        string compact = query.Trim();
        if (compact.Length > 52)
        {
            compact = compact[..52] + "...";
        }

        return $"Ask AI about \"{compact}\"";
    }

    private void EnsureAiWindowCreated()
    {
        if (_aiWindow is not null)
        {
            return;
        }

        _aiWindow = new FinderAiWindow(_screen);
        _aiWindow.Closed += OnAiWindowClosed;
    }

    private void OnAiWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is FinderAiWindow aiWindow)
        {
            aiWindow.Closed -= OnAiWindowClosed;
            if (ReferenceEquals(_aiWindow, aiWindow))
            {
                _aiWindow = null;
            }
        }
    }

    private void OpenAiWindow(string prompt, bool autoSubmit)
    {
        EnsureAiWindowCreated();
        _aiWindow!.ShowCentered(prompt, autoSubmit);
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

        var sb = t_normalizeBuilder ??= new StringBuilder(64);
        sb.Clear();

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
        if (query.Length == 0 || _sortedNames.Length == 0)
        {
            AutocompleteHint.Inlines.Clear();
            AutocompleteHint.Visibility = Visibility.Collapsed;
            return;
        }

        // Binary search for the first name that starts with the query (case-insensitive)
        string queryLower = query.ToLowerInvariant();
        string? best = FindShortestPrefixMatch(queryLower, query.Length);

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

    private string? FindShortestPrefixMatch(string queryLower, int queryLength)
    {
        // Binary search to find the leftmost name >= queryLower
        int lo = 0;
        int hi = _sortedNames.Length - 1;
        int firstCandidate = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            string name = _sortedNames[mid];

            int cmp = name.Length >= queryLength
                ? string.Compare(name, 0, queryLower, 0, queryLength, StringComparison.Ordinal)
                : string.Compare(name, queryLower, StringComparison.Ordinal);

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else if (cmp > 0)
            {
                hi = mid - 1;
            }
            else
            {
                // Found a prefix match - look for the shortest one
                firstCandidate = mid;
                hi = mid - 1;
            }
        }

        if (firstCandidate < 0)
        {
            return null;
        }

        // The sorted array is ordered by (name, length), so the first match at firstCandidate
        // is already among the shortest. But scan forward to find the absolute shortest.
        string? bestName = null;
        int bestLen = int.MaxValue;

        for (int i = firstCandidate; i < _sortedNames.Length; i++)
        {
            string name = _sortedNames[i];

            if (name.Length < queryLength)
            {
                continue;
            }

            if (!name.StartsWith(queryLower, StringComparison.Ordinal))
            {
                break;
            }

            // Must be strictly longer than query to show a completion
            string originalName = _sortedNameEntries[i].Name;
            if (originalName.Length > queryLength && originalName.Length < bestLen)
            {
                bestLen = originalName.Length;
                bestName = originalName;
            }
        }

        return bestName;
    }

    private void ScheduleSessionKeepAlive()
    {
        _sessionKeepAliveUntilUtc = DateTime.UtcNow + SessionKeepAliveDuration;
        _sessionKeepAliveTimer.Stop();
        _sessionKeepAliveTimer.Interval = SessionKeepAliveDuration;
        _sessionKeepAliveTimer.Start();
    }

    private void CancelSessionKeepAlive()
    {
        _sessionKeepAliveTimer.Stop();
        _sessionKeepAliveUntilUtc = DateTime.MinValue;
    }

    private bool ShouldResumeSession()
    {
        return _sessionKeepAliveUntilUtc > DateTime.UtcNow;
    }

    private void OnSessionKeepAliveElapsed(object? sender, object e)
    {
        _sessionKeepAliveTimer.Stop();
        _sessionKeepAliveUntilUtc = DateTime.MinValue;
        SessionExpired?.Invoke(this, EventArgs.Empty);
        Destroy();
    }

    private void RestoreRetainedViewport()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ResultsScrollViewer.ChangeView(null, _retainedVerticalOffset, null, true);
            int selectionStart = Math.Min(_retainedSearchSelectionStart, SearchTextBox.Text.Length);
            int selectionLength = Math.Min(_retainedSearchSelectionLength, SearchTextBox.Text.Length - selectionStart);
            SearchTextBox.SelectionStart = selectionStart;
            SearchTextBox.SelectionLength = selectionLength;
        });
    }
}
