using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class FinderAiWindow : Window
{
    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly AiAgentService _aiAgentService = new();
    private readonly AiSecretStore _secretStore = new();
    private readonly FinderAiConversationStore _conversationStore = new();
    private readonly AiModelCatalogService _aiModelCatalogService = new();
    private readonly List<AiAgentTurn> _conversation = [];
    private IReadOnlyList<FinderAiConversationSession> _sessions = [];
    private IReadOnlyList<AiModelCatalogEntry> _modelOptions = [];
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _modelCatalogCancellationSource;
    private IntPtr _hwnd;
    private bool _showRequested;
    private bool _isModelPickerInitializing;
    private string _pendingPrompt = string.Empty;
    private bool _pendingAutoSubmit;
    private bool _isSidebarVisible = true;
    private string? _activeConversationId;
    private string _providerStatusText = "Ready";
    private SolidColorBrush _providerStatusBrush = new(global::Windows.UI.Color.FromArgb(186, 198, 255, 225));

    internal FinderAiWindow(ScreenBounds screen)
    {
        _screen = screen;
        InitializeComponent();
        Title = "Veil Halo";
        Activated += OnFirstActivated;
        Closed += OnClosed;
        UpdateProviderState();
    }

    internal void ShowCentered(string? prompt = null, bool autoSubmit = false)
    {
        _pendingPrompt = prompt?.Trim() ?? string.Empty;
        _pendingAutoSubmit = autoSubmit && _pendingPrompt.Length > 0;

        if (_hwnd == IntPtr.Zero)
        {
            _showRequested = true;
            Activate();
            return;
        }

        ShowCenteredCore();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.ApplyAppIcon(this);
        WindowHelper.RemoveTitleBar(this);
        WindowHelper.PrepareForSystemBackdrop(this);
        SetupAcrylic();
        ApplyWindowSize();
        UpdateSidebarState();

        if (_showRequested)
        {
            DispatcherQueue.TryEnqueue(ShowCenteredCore);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _requestCts?.Cancel();
        _requestCts?.Dispose();
        _requestCts = null;
        _modelCatalogCancellationSource?.Cancel();
        _modelCatalogCancellationSource?.Dispose();
        _modelCatalogCancellationSource = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        _hwnd = IntPtr.Zero;
    }

    private void ShowCenteredCore()
    {
        _showRequested = false;
        UpdateProviderState();
        ApplyWindowSize();
        LoadSessions();
        ApplyModelCatalogPlaceholder();
        _ = LoadModelCatalogAsync();
        UpdateSidebarState();
        Activate();

        if (_hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(_hwnd);
        }

        if (_pendingPrompt.Length > 0)
        {
            StartNewChat(clearComposer: false);
            SetActiveComposerText(_pendingPrompt);
        }
        else if (_conversation.Count == 0 && _sessions.Count > 0 && string.IsNullOrWhiteSpace(_activeConversationId))
        {
            LoadConversation(_sessions[0]);
        }
        else
        {
            UpdateConversationChrome();
        }

        FocusActiveComposer();

        bool shouldAutoSubmit = _pendingAutoSubmit;
        _pendingPrompt = string.Empty;
        _pendingAutoSubmit = false;
        if (shouldAutoSubmit)
        {
            DispatcherQueue.TryEnqueue(async () => await SendPromptAsync(_conversation.Count == 0 ? HomeComposerTextBox : ComposerTextBox));
        }
    }

    private void ApplyWindowSize()
    {
        int screenWidth = _screen.Right - _screen.Left;
        int screenHeight = _screen.Bottom - _screen.Top;
        int width = Math.Clamp((int)(screenWidth * 0.56), 840, 1040);
        int height = Math.Clamp((int)(screenHeight * 0.62), 500, 680);
        int x = _screen.Left + ((screenWidth - width) / 2);
        int y = _screen.Top + (int)(screenHeight * 0.11);
        WindowHelper.PositionOnMonitor(this, x, y, width, height);
        if (_hwnd != IntPtr.Zero)
        {
            WindowHelper.ApplyRoundedRegion(_hwnd, width, height, 22);
        }
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(245, 14, 18, 26),
            TintOpacity = 0.22f,
            LuminosityOpacity = 0.12f,
            FallbackColor = global::Windows.UI.Color.FromArgb(225, 12, 16, 22)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void UpdateProviderState()
    {
        AiProviderValidationResult validation = AiProviderValidationService.Validate(_settings, _secretStore);
        _providerStatusText = validation.Summary;
        _providerStatusBrush = validation.State switch
        {
            AiProviderValidationState.Valid => new SolidColorBrush(global::Windows.UI.Color.FromArgb(186, 198, 255, 225)),
            AiProviderValidationState.Warning => new SolidColorBrush(global::Windows.UI.Color.FromArgb(186, 255, 226, 174)),
            _ => new SolidColorBrush(global::Windows.UI.Color.FromArgb(186, 255, 173, 173))
        };

        StatusTextBlock.Text = string.Empty;
        StatusTextBlock.Visibility = Visibility.Collapsed;
        UpdateConversationChrome();
        UpdateSurfaceState();
    }

    private void LoadSessions()
    {
        _sessions = _conversationStore.LoadSessions();
        RefreshHistoryList();

        if (!string.IsNullOrWhiteSpace(_activeConversationId))
        {
            FinderAiConversationSession? currentSession = _sessions.FirstOrDefault(session =>
                string.Equals(session.Id, _activeConversationId, StringComparison.Ordinal));
            if (currentSession is not null)
            {
                _conversation.Clear();
                _conversation.AddRange(currentSession.Turns);
                RenderConversation();
            }
        }

        UpdateConversationChrome();
    }

    private void StartNewChat(bool clearComposer)
    {
        _requestCts?.Cancel();
        _activeConversationId = null;
        _conversation.Clear();
        MessagesPanel.Children.Clear();

        if (clearComposer)
        {
            HomeComposerTextBox.Text = string.Empty;
            ComposerTextBox.Text = string.Empty;
        }

        UpdateConversationChrome();
        UpdateSurfaceState();
        RefreshHistoryList();
        SetBusyState(false);
    }

    private void LoadConversation(FinderAiConversationSession session)
    {
        _requestCts?.Cancel();
        _activeConversationId = session.Id;
        _conversation.Clear();
        _conversation.AddRange(session.Turns);
        RenderConversation();
        UpdateConversationChrome();
        RefreshHistoryList();
        ScrollToBottom();
    }

    private void RenderConversation()
    {
        MessagesPanel.Children.Clear();
        foreach (AiAgentTurn turn in _conversation)
        {
            AppendBubble(turn.Content, turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase), isMuted: false);
        }

        UpdateSurfaceState();
    }

    private void RefreshHistoryList()
    {
        HistoryListPanel.Children.Clear();

        foreach (FinderAiConversationSession session in _sessions)
        {
            bool isSelected = string.Equals(session.Id, _activeConversationId, StringComparison.Ordinal);
            var button = new Button
            {
                Padding = new Thickness(0),
                Background = new SolidColorBrush(isSelected
                    ? global::Windows.UI.Color.FromArgb(22, 94, 167, 255)
                    : global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(10),
                Tag = session.Id
            };
            button.Click += OnHistoryConversationClick;

            var stack = new StackPanel
            {
                Spacing = 3,
                Margin = new Thickness(10, 8, 10, 8)
            };
            stack.Children.Add(new TextBlock
            {
                Text = session.Title,
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = CreateBrush(240, 255, 255, 255),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{AiProviderKind.ToDisplayName(session.Provider)}  •  {session.Model}",
                FontSize = 9,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = CreateBrush(168, 255, 255, 255),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{BuildConversationPreview(session)}  •  {FormatRelativeTime(session.UpdatedAtUtc)}",
                FontSize = 9,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateBrush(132, 255, 255, 255),
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2
            });

            button.Content = stack;
            HistoryListPanel.Children.Add(button);
        }

        HistoryEmptyText.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteChatButton.IsEnabled = !string.IsNullOrWhiteSpace(_activeConversationId);
    }

    private static string BuildConversationPreview(FinderAiConversationSession session)
    {
        string text = session.Turns.LastOrDefault()?.Content?.Trim() ?? string.Empty;
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 68 ? text : $"{text[..68].TrimEnd()}...";
    }

    private void UpdateConversationChrome()
    {
        DeleteChatButton.IsEnabled = !string.IsNullOrWhiteSpace(_activeConversationId);
    }

    private static string BuildConversationTitle(IEnumerable<AiAgentTurn> turns)
    {
        string seed = turns.FirstOrDefault(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content?.Trim()
            ?? "New conversation";
        seed = seed.ReplaceLineEndings(" ");
        return seed.Length <= 44 ? seed : $"{seed[..44].TrimEnd()}...";
    }

    private static string FormatRelativeTime(DateTime updatedAtUtc)
    {
        TimeSpan delta = DateTime.UtcNow - updatedAtUtc;
        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)} min ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)delta.TotalHours)} h ago";
        }

        if (delta.TotalDays < 7)
        {
            return $"{Math.Max(1, (int)delta.TotalDays)} d ago";
        }

        return updatedAtUtc.ToLocalTime().ToString("MMM d");
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync(ComposerTextBox);
    }

    private async void OnHomeSendClick(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync(HomeComposerTextBox);
    }

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        await TrySendFromComposerKeyDownAsync(e, ComposerTextBox);
    }

    private async void OnHomeComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        await TrySendFromComposerKeyDownAsync(e, HomeComposerTextBox);
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        StartNewChat(clearComposer: true);
        FocusActiveComposer();
    }

    private void OnDeleteChatClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeConversationId))
        {
            StartNewChat(clearComposer: true);
            return;
        }

        _conversationStore.DeleteSession(_activeConversationId);
        _activeConversationId = null;
        _sessions = _conversationStore.LoadSessions();

        if (_sessions.Count > 0)
        {
            LoadConversation(_sessions[0]);
            return;
        }

        StartNewChat(clearComposer: true);
    }

    private void OnHistoryConversationClick(object sender, RoutedEventArgs e)
    {
        if (_requestCts is not null)
        {
            _requestCts.Cancel();
        }

        if (sender is not Button { Tag: string sessionId })
        {
            return;
        }

        FinderAiConversationSession? session = _sessions.FirstOrDefault(item =>
            string.Equals(item.Id, sessionId, StringComparison.Ordinal));
        if (session is not null)
        {
            LoadConversation(session);
        }
    }

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        _isSidebarVisible = !_isSidebarVisible;
        UpdateSidebarState();
    }

    private void OnHomeQuickActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt })
        {
            return;
        }

        SetActiveComposerText(prompt);
        FocusActiveComposer();
    }

    private async Task TrySendFromComposerKeyDownAsync(KeyRoutedEventArgs e, TextBox composer)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
        {
            return;
        }

        bool ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrlDown)
        {
            return;
        }

        e.Handled = true;
        await SendPromptAsync(composer);
    }

    private async Task SendPromptAsync(TextBox composer)
    {
        string prompt = composer.Text.Trim();
        if (prompt.Length == 0 || _requestCts is not null)
        {
            return;
        }

        HomeComposerTextBox.Text = string.Empty;
        ComposerTextBox.Text = string.Empty;
        UpdateSurfaceState();

        var userTurn = new AiAgentTurn("user", prompt);
        _conversation.Add(userTurn);
        UpdateSurfaceState();
        AppendBubble(prompt, isUser: true, isMuted: false);
        PersistConversation();

        TextBlock pendingBubble = AppendBubble("Thinking...", isUser: false, isMuted: true);
        ScrollToBottom();

        _requestCts = new CancellationTokenSource();
        SetBusyState(true);

        try
        {
            string reply = await _aiAgentService.GenerateReplyAsync(_conversation.ToArray(), _requestCts.Token);
            pendingBubble.Text = reply;
            pendingBubble.Foreground = CreateBrush(240, 255, 255, 255);
            _conversation.Add(new AiAgentTurn("assistant", reply));
            PersistConversation();
        }
        catch (OperationCanceledException)
        {
            const string cancelledMessage = "Request cancelled.";
            pendingBubble.Text = cancelledMessage;
            pendingBubble.Foreground = CreateBrush(186, 255, 210, 165);
            _conversation.Add(new AiAgentTurn("assistant", cancelledMessage));
            PersistConversation();
        }
        catch (Exception ex)
        {
            string errorMessage = ex.Message.Trim();
            pendingBubble.Text = errorMessage;
            pendingBubble.Foreground = CreateBrush(208, 255, 182, 182);
            _conversation.Add(new AiAgentTurn("assistant", errorMessage));
            PersistConversation();
        }
        finally
        {
            _requestCts?.Dispose();
            _requestCts = null;
            SetBusyState(false);
            ScrollToBottom();
        }
    }

    private void PersistConversation()
    {
        if (_conversation.Count == 0)
        {
            return;
        }

        _activeConversationId ??= Guid.NewGuid().ToString("N");
        var session = new FinderAiConversationSession(
            _activeConversationId,
            BuildConversationTitle(_conversation),
            _settings.AiProvider,
            GetCurrentAiModel(),
            DateTime.UtcNow,
            _conversation.ToArray());
        _conversationStore.UpsertSession(session);
        _sessions = _conversationStore.LoadSessions();
        RefreshHistoryList();
        UpdateConversationChrome();
    }

    private TextBlock AppendBubble(string text, bool isUser, bool isMuted)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = isUser
                ? CreateBrush(244, 244, 248, 255)
                : isMuted
                    ? CreateBrush(172, 255, 255, 255)
                    : CreateBrush(238, 255, 255, 255),
            MaxWidth = 540
        };

        var bubble = new Border
        {
            MaxWidth = 580,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            Background = isUser
                ? CreateBrush(24, 64, 125, 255)
                : CreateBrush(8, 255, 255, 255),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser
                ? CreateBrush(42, 93, 167, 255)
                : CreateBrush(10, 255, 255, 255),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = textBlock
        };

        MessagesPanel.Children.Add(bubble);
        return textBlock;
    }

    private void SetBusyState(bool isBusy)
    {
        SendButton.IsEnabled = !isBusy;
        HomeSendButton.IsEnabled = !isBusy;
        HomeComposerTextBox.IsEnabled = !isBusy;
        ComposerTextBox.IsEnabled = !isBusy;
        ModelComboBox.IsEnabled = !isBusy;
        NewChatButton.IsEnabled = !isBusy;
        DeleteChatButton.IsEnabled = !isBusy && !string.IsNullOrWhiteSpace(_activeConversationId);
        SendButton.Content = isBusy ? "..." : "\uE724";
        HomeSendButton.Content = isBusy ? "..." : "\uE724";
        StatusTextBlock.Text = isBusy ? $"Thinking with {GetProviderDisplayName()}..." : string.Empty;
        StatusTextBlock.Foreground = isBusy ? CreateBrush(186, 196, 228, 255) : _providerStatusBrush;
        StatusTextBlock.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSidebarState()
    {
        SidebarPanel.Visibility = _isSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _isSidebarVisible ? new GridLength(182) : new GridLength(0);
    }

    private void UpdateSurfaceState()
    {
        bool hasConversation = _conversation.Count > 0;
        HomePagePanel.Visibility = hasConversation ? Visibility.Collapsed : Visibility.Visible;
        ConversationPanel.Visibility = hasConversation ? Visibility.Visible : Visibility.Collapsed;
        ConversationComposerPanel.Visibility = hasConversation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetActiveComposerText(string prompt)
    {
        if (_conversation.Count == 0)
        {
            HomeComposerTextBox.Text = prompt;
            HomeComposerTextBox.SelectionStart = HomeComposerTextBox.Text.Length;
            HomeComposerTextBox.SelectionLength = 0;
            return;
        }

        ComposerTextBox.Text = prompt;
        ComposerTextBox.SelectionStart = ComposerTextBox.Text.Length;
        ComposerTextBox.SelectionLength = 0;
    }

    private void FocusActiveComposer()
    {
        if (_conversation.Count == 0)
        {
            HomeComposerTextBox.Focus(FocusState.Programmatic);
            return;
        }

        ComposerTextBox.Focus(FocusState.Programmatic);
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, true);
        });
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(alpha, red, green, blue));
    }

    private void ApplyModelCatalogPlaceholder()
    {
        string currentModel = GetCurrentAiModel();
        _modelOptions = CreateModelOptions([], currentModel);
        _isModelPickerInitializing = true;
        ModelComboBox.ItemsSource = _modelOptions;
        ModelComboBox.SelectedItem = _modelOptions.FirstOrDefault(item =>
            string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
        _isModelPickerInitializing = false;
    }

    private async Task LoadModelCatalogAsync(bool forceRefresh = false)
    {
        _modelCatalogCancellationSource?.Cancel();
        _modelCatalogCancellationSource?.Dispose();
        _modelCatalogCancellationSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _modelCatalogCancellationSource.Token;
        string currentModel = GetCurrentAiModel();

        try
        {
            AiModelCatalogSnapshot snapshot = await _aiModelCatalogService.GetModelsForProviderAsync(
                _settings.AiProvider,
                forceRefresh,
                cancellationToken);

            _modelOptions = CreateModelOptions(snapshot.Models, currentModel);
            _isModelPickerInitializing = true;
            ModelComboBox.ItemsSource = _modelOptions;
            ModelComboBox.SelectedItem = _modelOptions.FirstOrDefault(item =>
                string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
            _isModelPickerInitializing = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _modelOptions = CreateModelOptions([], currentModel);
            _isModelPickerInitializing = true;
            ModelComboBox.ItemsSource = _modelOptions;
            ModelComboBox.SelectedItem = _modelOptions.FirstOrDefault(item =>
                string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
            _isModelPickerInitializing = false;
        }
    }

    private string GetProviderDisplayName()
    {
        return _aiAgentService.ProviderDisplayName;
    }

    private static IReadOnlyList<AiModelCatalogEntry> CreateModelOptions(
        IReadOnlyList<AiModelCatalogEntry> catalog,
        string currentModel)
    {
        var options = new List<AiModelCatalogEntry>();

        if (!string.IsNullOrWhiteSpace(currentModel))
        {
            options.Add(new AiModelCatalogEntry(
                string.Empty,
                string.Empty,
                currentModel,
                currentModel,
                $"{currentModel}  •  current",
                "current"));
        }

        options.AddRange(catalog);
        return options
            .GroupBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(item => string.Equals(item.ModelId, currentModel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isModelPickerInitializing || ModelComboBox.SelectedItem is not AiModelCatalogEntry entry)
        {
            return;
        }

        SetCurrentAiModel(entry.ModelId);
        UpdateProviderState();
        UpdateConversationChrome();
    }

    private string GetCurrentAiModel()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.ChatGptPremium => _settings.ChatGptModel,
            AiProviderKind.OpenAi => _settings.OpenAiModel,
            AiProviderKind.Anthropic => _settings.AnthropicModel,
            AiProviderKind.Mistral => _settings.MistralModel,
            AiProviderKind.Ollama => _settings.OllamaModel,
            AiProviderKind.OllamaCloud => _settings.OllamaCloudModel,
            _ => string.Empty
        };
    }

    private void SetCurrentAiModel(string value)
    {
        switch (_settings.AiProvider)
        {
            case AiProviderKind.ChatGptPremium:
                _settings.ChatGptModel = value;
                break;
            case AiProviderKind.OpenAi:
                _settings.OpenAiModel = value;
                break;
            case AiProviderKind.Anthropic:
                _settings.AnthropicModel = value;
                break;
            case AiProviderKind.Mistral:
                _settings.MistralModel = value;
                break;
            case AiProviderKind.Ollama:
                _settings.OllamaModel = value;
                break;
            case AiProviderKind.OllamaCloud:
                _settings.OllamaCloudModel = value;
                break;
        }
    }
}
