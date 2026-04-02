using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Automation;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal enum DiscordCallAction
{
    ToggleMute,
    ToggleDeafen,
    ToggleCamera,
    ToggleScreenShare,
    Disconnect,
    Answer
}

internal sealed record DiscordCallSnapshot(
    bool IsRunning,
    bool HasVoiceConnection,
    bool HasIncomingCall,
    string StatusTitle,
    string StatusDetail,
    bool CanToggleMute,
    bool CanToggleDeafen,
    bool CanToggleCamera,
    bool CanToggleScreenShare,
    bool CanDisconnect,
    bool CanAnswer)
{
    public static DiscordCallSnapshot Empty { get; } = new(
        false,
        false,
        false,
        "Discord",
        string.Empty,
        false,
        false,
        false,
        false,
        false,
        false);
}

internal static class DiscordAppController
{
    private static readonly string[] DiscordProcessNames = ["Discord", "DiscordCanary", "DiscordPTB"];
    private static readonly string[] BrowserProcessNames = ["chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi"];

    private static IntPtr _cachedWindowHandle;
    private static DateTime _cachedWindowHandleUtc = DateTime.MinValue;
    private static readonly TimeSpan WindowHandleCacheTtl = TimeSpan.FromSeconds(5);

    public static bool IsRunning()
    {
        if (FindBestWindowCached(includeMinimized: true) != IntPtr.Zero)
        {
            return true;
        }

        foreach (string name in DiscordProcessNames)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(name);
                bool found = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                if (found) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    public static DiscordCallSnapshot GetCallSnapshot()
    {
        IntPtr window = FindBestWindowCached(includeMinimized: true);
        if (window == IntPtr.Zero)
        {
            return DiscordCallSnapshot.Empty;
        }

        string title = GetWindowTitle(window);
        if (!TryGetAutomationRoot(window, out AutomationElement? root) || root is null)
        {
            return new DiscordCallSnapshot(
                true,
                false,
                false,
                "Discord",
                CleanWindowTitle(title),
                false,
                false,
                false,
                false,
                false,
                false);
        }

        // Single FindAll call instead of 6 separate traversals
        var (canToggleMute, canToggleDeafen, canToggleCamera, canToggleScreenShare, canDisconnect, canAnswer)
            = FindAllActionElements(root);

        bool hasVoiceConnection = canDisconnect || canToggleMute || canToggleDeafen || canToggleCamera || canToggleScreenShare;
        bool hasIncomingCall = canAnswer;
        string statusTitle = hasIncomingCall
            ? "Incoming call"
            : hasVoiceConnection
                ? "Voice connected"
                : "Discord";

        return new DiscordCallSnapshot(
            true,
            hasVoiceConnection,
            hasIncomingCall,
            statusTitle,
            CleanWindowTitle(title),
            canToggleMute,
            canToggleDeafen,
            canToggleCamera,
            canToggleScreenShare,
            canDisconnect,
            canAnswer);
    }

    private static IntPtr FindBestWindowCached(bool includeMinimized)
    {
        if (DateTime.UtcNow - _cachedWindowHandleUtc < WindowHandleCacheTtl
            && _cachedWindowHandle != IntPtr.Zero
            && IsWindow(_cachedWindowHandle))
        {
            return _cachedWindowHandle;
        }

        IntPtr handle = FindBestWindow(includeMinimized);
        _cachedWindowHandle = handle;
        _cachedWindowHandleUtc = DateTime.UtcNow;
        return handle;
    }

    private static (bool Mute, bool Deafen, bool Camera, bool ScreenShare, bool Disconnect, bool Answer)
        FindAllActionElements(AutomationElement root)
    {
        Condition condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return default;
        }

        bool mute = false, deafen = false, camera = false, screenShare = false, disconnect = false, answer = false;

        foreach (AutomationElement element in elements)
        {
            try
            {
                string name = Normalize(element.Current.Name);
                string automationId = Normalize(element.Current.AutomationId);

                if (!mute && MatchesAction(DiscordCallAction.ToggleMute, name, automationId)) mute = true;
                else if (!deafen && MatchesAction(DiscordCallAction.ToggleDeafen, name, automationId)) deafen = true;
                else if (!camera && MatchesAction(DiscordCallAction.ToggleCamera, name, automationId)) camera = true;
                else if (!screenShare && MatchesAction(DiscordCallAction.ToggleScreenShare, name, automationId)) screenShare = true;
                else if (!disconnect && MatchesAction(DiscordCallAction.Disconnect, name, automationId)) disconnect = true;
                else if (!answer && MatchesAction(DiscordCallAction.Answer, name, automationId)) answer = true;

                if (mute && deafen && camera && screenShare && disconnect && answer) break;
            }
            catch
            {
            }
        }

        return (mute, deafen, camera, screenShare, disconnect, answer);
    }

    public static bool LaunchOrActivate()
    {
        IntPtr window = FindBestWindow(includeMinimized: true);
        if (window != IntPtr.Zero)
        {
            if (IsIconic(window))
            {
                ShowWindowNative(window, SW_RESTORE);
            }

            SetForegroundWindow(window);
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "discord://-/channels/@me",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://discord.com/app",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool MinimizeAllWindows()
    {
        bool minimizedAny = false;

        EnumWindows((hwnd, _) =>
        {
            if (ScoreWindow(hwnd) == 0)
            {
                return true;
            }

            ShowWindowNative(hwnd, SW_MINIMIZE);
            minimizedAny = true;
            return true;
        }, IntPtr.Zero);

        return minimizedAny;
    }

    public static bool TryInvokeCallAction(DiscordCallAction action)
    {
        IntPtr window = FindBestWindow(includeMinimized: true);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindowNative(window, SW_RESTORE);
        }

        if (!TryGetAutomationRoot(window, out AutomationElement? root) || root is null)
        {
            return false;
        }

        AutomationElement? element = FindActionElement(root, action);
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out object? invokePattern))
            {
                ((InvokePattern)invokePattern).Invoke();
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? togglePattern))
            {
                ((TogglePattern)togglePattern).Toggle();
                return true;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selectionPattern))
            {
                ((SelectionItemPattern)selectionPattern).Select();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryGetAutomationRoot(IntPtr hwnd, out AutomationElement? root)
    {
        try
        {
            root = AutomationElement.FromHandle(hwnd);
            return root is not null;
        }
        catch
        {
            root = null;
            return false;
        }
    }

    private static IntPtr FindBestWindow(bool includeMinimized)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            int score = ScoreWindow(hwnd, includeMinimized);
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    private static int ScoreWindow(IntPtr hwnd, bool includeMinimized = false)
    {
        if (!IsWindow(hwnd))
        {
            return 0;
        }

        bool isVisible = IsWindowVisible(hwnd);
        bool isMinimized = IsIconic(hwnd);
        if (!isVisible && !(includeMinimized && isMinimized))
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return 0;
        }

        string title = GetWindowTitle(hwnd);

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName;
            string normalizedTitle = Normalize(title);

            int score = 0;
            if (IsDiscordProcess(processName))
            {
                score += 240;
            }
            else if (IsBrowserProcess(processName) && normalizedTitle.Contains("discord", StringComparison.Ordinal))
            {
                score += 180;
            }

            if (score == 0)
            {
                return 0;
            }

            if (normalizedTitle.Contains("discord", StringComparison.Ordinal))
            {
                score += 40;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                score += 20;
            }

            if (!isMinimized)
            {
                score += 12;
            }

            return score;
        }
        catch
        {
            return 0;
        }
    }

    private static AutomationElement? FindActionElement(AutomationElement root, DiscordCallAction action)
    {
        Condition condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return null;
        }

        foreach (AutomationElement element in elements)
        {
            string name = Normalize(element.Current.Name);
            string automationId = Normalize(element.Current.AutomationId);
            if (MatchesAction(action, name, automationId))
            {
                return element;
            }
        }

        return null;
    }

    private static bool MatchesAction(DiscordCallAction action, string name, string automationId)
    {
        string haystack = string.Concat(name, automationId);
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        return action switch
        {
            DiscordCallAction.ToggleMute => ContainsAny(haystack,
                "mute",
                "unmute",
                "mutemicrophone",
                "unmutemicrophone",
                "couperlemicro",
                "reactiverlemicro",
                "activerlemicro",
                "sourdine"),
            DiscordCallAction.ToggleDeafen => ContainsAny(haystack,
                "deafen",
                "undeafen",
                "mutesound",
                "unmutesound",
                "couperleson",
                "retablirleson"),
            DiscordCallAction.ToggleCamera => ContainsAny(haystack,
                "turnoncamera",
                "turnoffcamera",
                "camera",
                "video",
                "activerlacamera",
                "desactiverlacamera",
                "camera"),
            DiscordCallAction.ToggleScreenShare => ContainsAny(haystack,
                "shareyourscreen",
                "stopscreensharing",
                "stopstreaming",
                "golive",
                "screenshare",
                "partagervotreecran",
                "arreterlepartagedecran",
                "partagerlecran"),
            DiscordCallAction.Disconnect => ContainsAny(haystack,
                "disconnect",
                "leavecall",
                "endcall",
                "hangup",
                "quitterlappel",
                "quitterlappelaudio",
                "raccrocher"),
            DiscordCallAction.Answer => ContainsAny(haystack,
                "answer",
                "accept",
                "joincall",
                "joinvoice",
                "repondre",
                "accepter",
                "rejoindrelappel",
                "rejoindrelappelaudio"),
            _ => false
        };
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDiscordProcess(string processName)
    {
        return DiscordProcessNames.Any(name => processName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBrowserProcess(string processName)
    {
        return BrowserProcessNames.Any(name => processName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        char[] buffer = new char[512];
        int length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string CleanWindowTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string trimmed = title.Trim();
        string[] suffixes =
        [
            " - Discord",
            " | Discord",
            " — Discord"
        ];

        foreach (string suffix in suffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
        }

        return string.Equals(trimmed, "Discord", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static string Normalize(string? value)
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
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
