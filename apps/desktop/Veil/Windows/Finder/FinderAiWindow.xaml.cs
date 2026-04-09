using System.Text.Json;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class FinderAiWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly AiAgentService _aiAgentService = new();
    private readonly AiSecretStore _secretStore = new();
    private readonly FinderAiConversationStore _conversationStore = new();
    private readonly AiModelCatalogService _aiModelCatalogService = new();
    private readonly Action? _returnToFinderAction;
    private readonly List<AiAgentTurn> _conversation = [];
    private IReadOnlyList<FinderAiConversationSession> _sessions = [];
    private IReadOnlyList<AiModelCatalogEntry> _modelOptions = [];
    private WebView2? _aiWebView;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _modelCatalogCancellationSource;
    private Task? _webViewInitializationTask;
    private DispatcherTimer? _bridgeTimer;
    private IntPtr _hwnd;
    private bool _showRequested;
    private bool _isSidebarVisible = true;
    private bool _isBusy;
    private bool _isWebReady;
    private string? _activeConversationId;
    private string _providerStatusText = "Ready";
    private string _providerStatusTone = "valid";
    private string _draftPrompt = string.Empty;
    private int _draftVersion;
    private bool _pendingAutoSubmit;

    internal FinderAiWindow(ScreenBounds screen, Action? returnToFinderAction = null)
    {
        _screen = screen;
        _returnToFinderAction = returnToFinderAction;
        InitializeComponent();
        Title = "Veil Halo";
        Activated += OnFirstActivated;
        Closed += OnClosed;
        UpdateProviderState();
    }

    internal void ShowCentered(string? prompt = null, bool autoSubmit = false)
    {
        _draftPrompt = prompt?.Trim() ?? string.Empty;
        _draftVersion++;
        _pendingAutoSubmit = autoSubmit && _draftPrompt.Length > 0;

        if (_hwnd == IntPtr.Zero)
        {
            _showRequested = true;
            Activate();
            return;
        }

        ShowCenteredCore();
    }

    internal void HideWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindowNative(_hwnd, SW_HIDE);
        }
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.ApplyAppIcon(this);
        WindowHelper.RemoveTitleBar(this);
        WindowHelper.PrepareForSystemBackdrop(this);
        SetupAcrylic();
        ApplyWindowSize();
        UpdateLoadingOverlay(isVisible: true, text: "Loading Finder AI");

        try
        {
            _webViewInitializationTask ??= InitializeWebViewAsync();
            await _webViewInitializationTask;
        }
        catch (Exception ex)
        {
            UpdateLoadingOverlay(isVisible: true, text: ex.Message);
        }

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
        _bridgeTimer?.Stop();
        _bridgeTimer = null;

        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        _hwnd = IntPtr.Zero;
        _isWebReady = false;
    }

    private void ShowCenteredCore()
    {
        _showRequested = false;
        UpdateProviderState();
        ApplyWindowSize();
        LoadSessions();
        ApplyModelCatalogPlaceholder();
        _ = LoadModelCatalogAsync();
        Activate();

        if (_hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(_hwnd);
        }

        if (_draftPrompt.Length > 0)
        {
            StartNewChat(clearDraft: false);
        }
        else if (_conversation.Count == 0 && _sessions.Count > 0 && string.IsNullOrWhiteSpace(_activeConversationId))
        {
            LoadConversation(_sessions[0]);
        }

        _ = PublishStateAsync();
        _ = TryAutoSubmitPendingPromptAsync();
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

    private async Task InitializeWebViewAsync()
    {
        string webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "FinderAi");
        if (!Directory.Exists(webRoot))
        {
            throw new InvalidOperationException($"Finder AI web assets were not found at '{webRoot}'.");
        }

        _aiWebView ??= CreateWebView();
        await _aiWebView.EnsureCoreWebView2Async();
        _aiWebView.Source = new Uri(Path.Combine(webRoot, "index.html") + "?bridge=poll");
        StartBridgeTimer();
    }

    private WebView2 CreateWebView()
    {
        var webView = new WebView2
        {
            DefaultBackgroundColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        WebViewHost.Children.Clear();
        WebViewHost.Children.Add(webView);
        return webView;
    }

    private void StartBridgeTimer()
    {
        _bridgeTimer?.Stop();
        _bridgeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _bridgeTimer.Tick += OnBridgeTimerTick;
        _bridgeTimer.Start();
    }

    private async void OnBridgeTimerTick(object? sender, object e)
    {
        try
        {
            if (_aiWebView is null)
            {
                return;
            }

            if (!_isWebReady)
            {
                string readyResult = await _aiWebView.ExecuteScriptAsync("typeof window.__veilReceiveHostState === 'function'");
                if (string.Equals(readyResult, "true", StringComparison.OrdinalIgnoreCase))
                {
                    _isWebReady = true;
                    UpdateLoadingOverlay(isVisible: false, text: string.Empty);
                    await PublishStateAsync();
                    await TryAutoSubmitPendingPromptAsync();
                }
            }

            if (!_isWebReady)
            {
                return;
            }

            string payload = await _aiWebView.ExecuteScriptAsync("(window.__veilCommandQueue || []).splice(0)");
            if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[]", StringComparison.Ordinal))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(payload);
            foreach (JsonElement command in document.RootElement.EnumerateArray())
            {
                await HandleCommandAsync(command);
            }
        }
        catch
        {
        }
    }

    private async Task HandleCommandAsync(JsonElement root)
    {
        string command = root.TryGetProperty("command", out JsonElement commandElement)
            ? commandElement.GetString() ?? string.Empty
            : string.Empty;

        switch (command)
        {
            case "ready":
                _isWebReady = true;
                UpdateLoadingOverlay(isVisible: false, text: string.Empty);
                await PublishStateAsync();
                await TryAutoSubmitPendingPromptAsync();
                break;
            case "newChat":
                StartNewChat(clearDraft: true);
                await PublishStateAsync();
                break;
            case "deleteChat":
                DeleteCurrentConversation();
                await PublishStateAsync();
                break;
            case "loadConversation":
                if (TryGetString(root, "sessionId", out string? sessionId))
                {
                    LoadConversationById(sessionId!);
                    await PublishStateAsync();
                }

                break;
            case "toggleSidebar":
                _isSidebarVisible = !_isSidebarVisible;
                await PublishStateAsync();
                break;
            case "selectModel":
                if (TryGetString(root, "modelId", out string? modelId) && !string.IsNullOrWhiteSpace(modelId))
                {
                    SetCurrentAiModel(modelId!);
                    UpdateProviderState();
                    await PublishStateAsync();
                }

                break;
            case "refreshModels":
                _ = LoadModelCatalogAsync(forceRefresh: true);
                break;
            case "submitPrompt":
                if (TryGetString(root, "prompt", out string? prompt))
                {
                    await SubmitPromptAsync(prompt ?? string.Empty);
                }

                break;
            case "cancelRequest":
                _requestCts?.Cancel();
                break;
            case "openFinder":
                HideWindow();
                _returnToFinderAction?.Invoke();
                break;
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private void UpdateProviderState()
    {
        AiProviderValidationResult validation = AiProviderValidationService.Validate(_settings, _secretStore);
        _providerStatusText = validation.Summary;
        _providerStatusTone = validation.State switch
        {
            AiProviderValidationState.Valid => "valid",
            AiProviderValidationState.Warning => "warning",
            _ => "invalid"
        };
    }

    private void LoadSessions()
    {
        _sessions = _conversationStore.LoadSessions();

        if (!string.IsNullOrWhiteSpace(_activeConversationId))
        {
            FinderAiConversationSession? currentSession = _sessions.FirstOrDefault(session =>
                string.Equals(session.Id, _activeConversationId, StringComparison.Ordinal));
            if (currentSession is not null)
            {
                _conversation.Clear();
                _conversation.AddRange(currentSession.Turns);
            }
        }
    }

    private void StartNewChat(bool clearDraft)
    {
        _requestCts?.Cancel();
        _activeConversationId = null;
        _conversation.Clear();

        if (clearDraft)
        {
            _draftPrompt = string.Empty;
            _draftVersion++;
        }

        SetBusyState(false);
    }

    private void LoadConversationById(string sessionId)
    {
        FinderAiConversationSession? session = _sessions.FirstOrDefault(item =>
            string.Equals(item.Id, sessionId, StringComparison.Ordinal));
        if (session is not null)
        {
            LoadConversation(session);
        }
    }

    private void LoadConversation(FinderAiConversationSession session)
    {
        _requestCts?.Cancel();
        _activeConversationId = session.Id;
        _conversation.Clear();
        _conversation.AddRange(session.Turns);
        _draftPrompt = string.Empty;
        _draftVersion++;
    }

    private void DeleteCurrentConversation()
    {
        if (string.IsNullOrWhiteSpace(_activeConversationId))
        {
            StartNewChat(clearDraft: true);
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

        StartNewChat(clearDraft: true);
    }

    private async Task SubmitPromptAsync(string prompt)
    {
        string normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length == 0 || _requestCts is not null)
        {
            return;
        }

        _draftPrompt = string.Empty;
        _draftVersion++;
        _pendingAutoSubmit = false;
        _conversation.Add(new AiAgentTurn("user", normalizedPrompt));
        PersistConversation();
        SetBusyState(true);
        await PublishStateAsync();

        _requestCts = new CancellationTokenSource();

        try
        {
            string reply = await _aiAgentService.GenerateReplyAsync(_conversation.ToArray(), _requestCts.Token);
            _conversation.Add(new AiAgentTurn("assistant", reply));
            PersistConversation();
        }
        catch (OperationCanceledException)
        {
            _conversation.Add(new AiAgentTurn("assistant", "Request cancelled."));
            PersistConversation();
        }
        catch (Exception ex)
        {
            string errorMessage = ex.Message.Trim();
            _conversation.Add(new AiAgentTurn("assistant", errorMessage.Length == 0 ? "The request failed." : errorMessage));
            PersistConversation();
        }
        finally
        {
            _requestCts?.Dispose();
            _requestCts = null;
            SetBusyState(false);
            await PublishStateAsync();
        }
    }

    private async Task TryAutoSubmitPendingPromptAsync()
    {
        if (!_isWebReady || !_pendingAutoSubmit || _draftPrompt.Length == 0 || _requestCts is not null)
        {
            return;
        }

        string prompt = _draftPrompt;
        _pendingAutoSubmit = false;
        await SubmitPromptAsync(prompt);
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
    }

    private void SetBusyState(bool isBusy)
    {
        _isBusy = isBusy;
    }

    private void ApplyModelCatalogPlaceholder()
    {
        string currentModel = GetCurrentAiModel();
        _modelOptions = CreateModelOptions([], currentModel);
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
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            _modelOptions = CreateModelOptions([], currentModel);
        }

        await PublishStateAsync();
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

    private async Task PublishStateAsync()
    {
        if (!_isWebReady || _aiWebView is null)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "hostState",
            state = BuildUiState()
        }, JsonOptions);
        await _aiWebView.ExecuteScriptAsync($"window.__veilReceiveHostState({json})");
    }

    private object BuildUiState()
    {
        string currentModel = GetCurrentAiModel();
        return new
        {
            providerLabel = _aiAgentService.ProviderDisplayName,
            providerStatus = _providerStatusText,
            providerStatusTone = _providerStatusTone,
            activeConversationId = _activeConversationId,
            busy = _isBusy,
            canDelete = !string.IsNullOrWhiteSpace(_activeConversationId),
            canCancel = _requestCts is not null,
            sidebarVisible = _isSidebarVisible,
            currentModel,
            draftPrompt = _draftPrompt,
            draftVersion = _draftVersion,
            turns = _conversation.Select(static turn => new
            {
                role = turn.Role,
                content = turn.Content
            }).ToArray(),
            sessions = _sessions.Select(session => new
            {
                id = session.Id,
                title = session.Title,
                provider = AiProviderKind.ToDisplayName(session.Provider),
                model = session.Model,
                preview = BuildConversationPreview(session),
                updatedAtLabel = FormatRelativeTime(session.UpdatedAtUtc),
                selected = string.Equals(session.Id, _activeConversationId, StringComparison.Ordinal)
            }).ToArray(),
            modelOptions = _modelOptions.Select(option => new
            {
                id = option.ModelId,
                label = option.DisplayLabel,
                badge = option.Summary ?? string.Empty,
                selected = string.Equals(option.ModelId, currentModel, StringComparison.OrdinalIgnoreCase)
            }).ToArray()
        };
    }

    private void UpdateLoadingOverlay(bool isVisible, string text)
    {
        LoadingOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0)
        {
            LoadingTextBlock.Text = text;
        }
    }

    private static string BuildConversationPreview(FinderAiConversationSession session)
    {
        string text = session.Turns.LastOrDefault()?.Content?.Trim() ?? string.Empty;
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 68 ? text : $"{text[..68].TrimEnd()}...";
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
