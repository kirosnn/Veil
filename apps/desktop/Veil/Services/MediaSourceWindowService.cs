using System.Diagnostics;
using System.Globalization;
using System.Text;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class MediaSourceWindowService
{
    private IntPtr _hiddenWindow;
    private string? _hiddenSourceAppId;

    internal bool IsSourceWindowHidden => _hiddenWindow != IntPtr.Zero;

    internal void SyncSource(string? sourceAppId)
    {
        if (string.Equals(_hiddenSourceAppId, sourceAppId, StringComparison.OrdinalIgnoreCase))
        {
            if (_hiddenWindow != IntPtr.Zero && !IsWindow(_hiddenWindow))
            {
                _hiddenWindow = IntPtr.Zero;
                _hiddenSourceAppId = null;
            }

            return;
        }

        _hiddenWindow = IntPtr.Zero;
        _hiddenSourceAppId = null;
    }

    internal bool ToggleVisibility(string? sourceAppId, string? sourceLabel)
    {
        if (_hiddenWindow != IntPtr.Zero)
        {
            IntPtr hiddenWindow = _hiddenWindow;
            _hiddenWindow = IntPtr.Zero;
            _hiddenSourceAppId = null;

            if (!IsWindow(hiddenWindow))
            {
                return false;
            }

            ShowWindowNative(hiddenWindow, SW_RESTORE);
            SetForegroundWindow(hiddenWindow);
            return true;
        }

        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return false;
        }

        IntPtr sourceWindow = FindBestWindow(sourceAppId, sourceLabel);
        if (sourceWindow == IntPtr.Zero)
        {
            return false;
        }

        ShowWindowNative(sourceWindow, SW_HIDE);
        _hiddenWindow = sourceWindow;
        _hiddenSourceAppId = sourceAppId;
        return true;
    }

    internal string GetSourceLabel(string? sourceAppId, string? sourceLabel)
    {
        if (!string.IsNullOrWhiteSpace(sourceLabel))
        {
            return sourceLabel;
        }

        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return "App";
        }

        if (sourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (sourceAppId.Contains("deezer", StringComparison.OrdinalIgnoreCase))
        {
            return "Deezer";
        }

        if (sourceAppId.Contains("applemusic", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("apple music", StringComparison.OrdinalIgnoreCase))
        {
            return "Apple Music";
        }

        if (sourceAppId.Contains("musicwin", StringComparison.OrdinalIgnoreCase))
        {
            return "Media Player";
        }

        string[] tokens = BuildMatchTokens(sourceAppId, null);
        return tokens.FirstOrDefault(token => token.Length > 2) is { } token
            ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(token)
            : "App";
    }

    private static IntPtr FindBestWindow(string sourceAppId, string? sourceLabel)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            int score = ScoreWindow(hwnd, sourceAppId, sourceLabel);
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    private static int ScoreWindow(IntPtr hwnd, string sourceAppId, string? sourceLabel)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return 0;
        }

        string[] matchTokens = BuildMatchTokens(sourceAppId, sourceLabel);
        if (matchTokens.Length == 0)
        {
            return 0;
        }

        string windowTitle = GetWindowTitle(hwnd);
        int score = 0;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = Normalize(process.ProcessName);
            string fileDescription = Normalize(process.MainModule?.FileVersionInfo?.FileDescription);

            foreach (string token in matchTokens)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (processName == token)
                {
                    score += 180;
                }
                else if (processName.Contains(token, StringComparison.Ordinal) || token.Contains(processName, StringComparison.Ordinal))
                {
                    score += 120;
                }

                if (!string.IsNullOrWhiteSpace(fileDescription) &&
                    (fileDescription.Contains(token, StringComparison.Ordinal) || token.Contains(fileDescription, StringComparison.Ordinal)))
                {
                    score += 90;
                }

                if (!string.IsNullOrWhiteSpace(windowTitle) && windowTitle.Contains(token, StringComparison.Ordinal))
                {
                    score += 70;
                }
            }

            if (!string.IsNullOrWhiteSpace(windowTitle) && matchTokens.Any(windowTitle.Contains))
            {
                score += 20;
            }
        }
        catch
        {
            return 0;
        }

        return score;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[512];
        int length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length <= 0
            ? string.Empty
            : Normalize(new string(buffer, 0, length));
    }

    private static string[] BuildMatchTokens(string sourceAppId, string? sourceLabel)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        void AddToken(string? value)
        {
            string normalized = Normalize(value);
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
        AddToken(sourceLabel);

        string packageSegment = baseId;
        int underscoreIndex = packageSegment.IndexOf('_');
        if (underscoreIndex >= 0)
        {
            packageSegment = packageSegment[..underscoreIndex];
        }

        foreach (string part in packageSegment.Split(['.', '\\', '/', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddToken(part);
            if (part.EndsWith("music", StringComparison.OrdinalIgnoreCase))
            {
                AddToken(part[..^"music".Length]);
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceLabel))
        {
            foreach (string part in sourceLabel.Split([' ', '.', '-', '_', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddToken(part);
            }
        }

        return tokens.OrderByDescending(token => token.Length).ToArray();
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
            else if (char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
