using System.Text.Json;

namespace VeilTerminal;

internal sealed class TerminalSettings
{
    private static readonly Lazy<TerminalSettings> s_instance = new(() =>
    {
        try { return LoadOrCreate(); }
        catch { return new TerminalSettings(); }
    });

    public static TerminalSettings Current => s_instance.Value;

    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeilTerminal", "settings.json");

    public string  DefaultProfileId { get; set; } = string.Empty;
    public string  FontFamily       { get; set; } = "Cascadia Code, Consolas, monospace";
    public double  FontSize         { get; set; } = 13;
    public string  CursorStyle      { get; set; } = "block";
    public int     Scrollback       { get; set; } = 5000;
    public int     Cols             { get; set; } = 120;
    public int     Rows             { get; set; } = 30;

    private static TerminalSettings LoadOrCreate()
    {
        if (!File.Exists(s_path)) return new TerminalSettings();
        var json = File.ReadAllText(s_path);
        return JsonSerializer.Deserialize<TerminalSettings>(json) ?? new TerminalSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
        File.WriteAllText(s_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
