using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Veil.Diagnostics;

namespace Veil.Services;

internal static partial class RaycastService
{
    private const uint VK_MENU = 0x12;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_SHIFT = 0x10;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_SPACE = 0x20;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int INPUT_KEYBOARD = 1;

    private static readonly string[] KnownPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Programs", "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Raycast", "Raycast.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Raycast", "Raycast.exe"),
    ];

    private static readonly string[] ConfigSearchRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raycast"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raycast"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Raycast"),
    ];

    private static readonly Lazy<string?> _executablePath = new(FindExecutable);
    private static readonly Lazy<RaycastHotkey> _detectedHotkey = new(DetectHotkey);
    private static readonly RaycastHotkey DefaultHotkey = new([VK_MENU], VK_SPACE, "Alt+Space");
    private static int _detectStarted;

    internal static bool IsInstalled
    {
        get
        {
            bool installed = _executablePath.Value is not null;
            if (installed)
            {
                StartHotkeyDetection();
            }

            return installed;
        }
    }

    internal static RaycastHotkey DetectedHotkey => _detectedHotkey.Value;

    internal static void Activate(string? hotkeyOverride = null)
    {
        var hotkey = string.IsNullOrWhiteSpace(hotkeyOverride)
            ? GetFastHotkey()
            : ParseHotkeyString(hotkeyOverride) ?? DefaultHotkey;

        if (!SimulateHotkey(hotkey))
        {
            StartRaycast();
        }
    }

    private static RaycastHotkey GetFastHotkey()
    {
        StartHotkeyDetection();
        return _detectedHotkey.IsValueCreated ? _detectedHotkey.Value : DefaultHotkey;
    }

    private static void StartHotkeyDetection()
    {
        if (Interlocked.Exchange(ref _detectStarted, 1) == 1)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(static _ =>
        {
            _ = _detectedHotkey.Value;
        });
    }

    private static RaycastHotkey DetectHotkey()
    {
        try
        {
            foreach (string root in ConfigSearchRoots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        if (!json.Contains("hotkey", StringComparison.OrdinalIgnoreCase) &&
                            !json.Contains("shortcut", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var hotkey = TryParseHotkeyFromJson(json);
                        if (hotkey is not null)
                        {
                            AppLogger.Info($"Raycast hotkey detected from {file}: {hotkey.DisplayString}");
                            return hotkey;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to detect Raycast hotkey.", ex);
        }

        AppLogger.Info($"Raycast hotkey defaulting to {DefaultHotkey.DisplayString}");
        return DefaultHotkey;
    }

    private static RaycastHotkey? TryParseHotkeyFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return SearchForHotkey(doc.RootElement);
        }
        catch { return null; }
    }

    private static RaycastHotkey? SearchForHotkey(JsonElement element, int depth = 0)
    {
        if (depth > 5)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (IsHotkeyKey(prop.Name))
                {
                    var hotkey = TryExtractHotkey(prop.Value);
                    if (hotkey is not null)
                    {
                        return hotkey;
                    }
                }

                var nested = SearchForHotkey(prop.Value, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = SearchForHotkey(item, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsHotkeyKey(string name)
    {
        return name.Equals("hotkey", StringComparison.OrdinalIgnoreCase)
            || name.Equals("globalHotkey", StringComparison.OrdinalIgnoreCase)
            || name.Equals("launcherHotkey", StringComparison.OrdinalIgnoreCase)
            || name.Equals("shortcut", StringComparison.OrdinalIgnoreCase)
            || name.Equals("keyboardShortcut", StringComparison.OrdinalIgnoreCase);
    }

    private static RaycastHotkey? TryExtractHotkey(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return ParseHotkeyString(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            string? key = null;
            List<string> mods = [];

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals("key", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                {
                    key = prop.Value.GetString();
                }
                else if ((prop.Name.Equals("modifiers", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("modifier", StringComparison.OrdinalIgnoreCase))
                    && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mod in prop.Value.EnumerateArray())
                    {
                        if (mod.ValueKind == JsonValueKind.String && mod.GetString() is string s)
                        {
                            mods.Add(s);
                        }
                    }
                }
            }

            if (key is not null)
            {
                return BuildHotkey(mods, key);
            }
        }

        return null;
    }

    internal static RaycastHotkey? ParseHotkeyString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string normalized = raw
            .Replace("⌥", "Alt")
            .Replace("⌃", "Ctrl")
            .Replace("⇧", "Shift")
            .Replace("⌘", "Win");

        var parts = normalized
            .Split(['+', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => p.Trim())
            .ToList();

        if (parts.Count < 2)
        {
            return null;
        }

        string keyPart = parts[^1];
        var modParts = parts.Take(parts.Count - 1).ToList();

        return BuildHotkey(modParts, keyPart);
    }

    private static RaycastHotkey? BuildHotkey(List<string> modifiers, string key)
    {
        List<uint> modVks = [];
        List<string> modLabels = [];

        foreach (string mod in modifiers)
        {
            switch (mod.ToLowerInvariant())
            {
                case "alt" or "option" or "opt":
                    modVks.Add(VK_MENU);
                    modLabels.Add("Alt");
                    break;
                case "ctrl" or "control":
                    modVks.Add(VK_CONTROL);
                    modLabels.Add("Ctrl");
                    break;
                case "shift":
                    modVks.Add(VK_SHIFT);
                    modLabels.Add("Shift");
                    break;
                case "win" or "cmd" or "command" or "super":
                    modVks.Add(VK_LWIN);
                    modLabels.Add("Win");
                    break;
            }
        }

        if (modVks.Count == 0)
        {
            return null;
        }

        uint? vk = MapKeyName(key);
        if (vk is null)
        {
            return null;
        }

        string display = string.Join("+", modLabels) + "+" + NormalizeKeyLabel(key);
        return new RaycastHotkey([.. modVks], vk.Value, display);
    }

    private static uint? MapKeyName(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "space" or " " => VK_SPACE,
            "a" => 0x41, "b" => 0x42, "c" => 0x43, "d" => 0x44, "e" => 0x45,
            "f" => 0x46, "g" => 0x47, "h" => 0x48, "i" => 0x49, "j" => 0x4A,
            "k" => 0x4B, "l" => 0x4C, "m" => 0x4D, "n" => 0x4E, "o" => 0x4F,
            "p" => 0x50, "q" => 0x51, "r" => 0x52, "s" => 0x53, "t" => 0x54,
            "u" => 0x55, "v" => 0x56, "w" => 0x57, "x" => 0x58, "y" => 0x59,
            "z" => 0x5A,
            "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34,
            "5" => 0x35, "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74,
            "f6" => 0x75, "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79,
            "f11" => 0x7A, "f12" => 0x7B,
            "return" or "enter" => 0x0D,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "backspace" or "back" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" or "page_up" => 0x21,
            "pagedown" or "pgdn" or "page_down" => 0x22,
            "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            "comma" or "," => 0xBC,
            "period" or "." => 0xBE,
            "slash" or "/" => 0xBF,
            "backslash" or "\\" => 0xDC,
            "semicolon" or ";" => 0xBA,
            "quote" or "'" => 0xDE,
            "bracketleft" or "[" => 0xDB,
            "bracketright" or "]" => 0xDD,
            "minus" or "-" => 0xBD,
            "equal" or "=" => 0xBB,
            "grave" or "`" => 0xC0,
            _ when key.Length == 1 => (uint?)MapSingleChar(key[0]),
            _ => null
        };
    }

    private static uint? MapSingleChar(char c)
    {
        if (c >= 'A' && c <= 'Z') return (uint)c;
        if (c >= 'a' && c <= 'z') return (uint)(c - 32);
        if (c >= '0' && c <= '9') return (uint)c;
        return null;
    }

    private static string NormalizeKeyLabel(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "space" or " " => "Space",
            _ when key.Length == 1 => key.ToUpperInvariant(),
            _ => char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant()
        };
    }

    private static bool SimulateHotkey(RaycastHotkey hotkey)
    {
        var inputs = new List<INPUT>();

        foreach (uint mod in hotkey.ModifierKeys)
        {
            inputs.Add(MakeKeyInput(mod, false));
        }

        inputs.Add(MakeKeyInput(hotkey.VirtualKey, false));
        inputs.Add(MakeKeyInput(hotkey.VirtualKey, true));

        foreach (uint mod in hotkey.ModifierKeys.Reverse())
        {
            inputs.Add(MakeKeyInput(mod, true));
        }

        var arr = inputs.ToArray();
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
        {
            AppLogger.Error($"Failed to send Raycast hotkey {hotkey.DisplayString}. Sent {sent}/{arr.Length} inputs.", null);
            return false;
        }

        return true;
    }

    private static INPUT MakeKeyInput(uint vk, bool keyUp)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (vk == VK_LWIN || vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x21 or 0x22 or 0x23 or 0x24)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = (ushort)vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
    }

    private static string? FindExecutable()
    {
        string? fromPath = KnownPaths.FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;

        string? fromRegistry = FindInRegistry();
        if (fromRegistry is not null && File.Exists(fromRegistry)) return fromRegistry;

        string? fromProcess = FindRunningProcess();
        if (fromProcess is not null && File.Exists(fromProcess)) return fromProcess;

        return null;
    }

    private static string? FindInRegistry()
    {
        string[] uninstallKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (string keyPath in uninstallKeys)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var uninstall = baseKey.OpenSubKey(keyPath);
                    if (uninstall is null) continue;

                    foreach (string subKeyName in uninstall.GetSubKeyNames())
                    {
                        if (!subKeyName.Contains("Raycast", StringComparison.OrdinalIgnoreCase)) continue;

                        using var appKey = uninstall.OpenSubKey(subKeyName);
                        if (appKey is null) continue;

                        string? installLocation = appKey.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(installLocation)) continue;

                        string candidate = Path.Combine(installLocation, "Raycast.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static string? FindRunningProcess()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("Raycast"))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static void StartRaycast()
    {
        string? path = _executablePath.Value;
        if (path is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to start Raycast.", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}

internal sealed record RaycastHotkey(uint[] ModifierKeys, uint VirtualKey, string DisplayString);
