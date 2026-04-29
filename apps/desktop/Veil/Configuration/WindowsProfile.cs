using System.Text.Json;
using System.Text.Json.Serialization;
using Veil.Diagnostics;

namespace Veil.Configuration;

internal enum PowerPlanPreset
{
    Balanced,
    HighPerformance,
    PowerSaver,
    BestEfficiency,
    UltimatePerformance
}

internal sealed class WindowsProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Profile";
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
    public PowerPlanPreset? PowerPlan { get; set; }
    public int? CpuMaxPercent { get; set; }
    public int? RefreshRateHz { get; set; }
    public List<WindowsDisplayProfile> Displays { get; set; } = [];
    public bool? TransparencyEnabled { get; set; }
    public bool? AnimationsEnabled { get; set; }

    public string BuildSummary()
    {
        var parts = new List<string>();
        if (PowerPlan.HasValue)
            parts.Add(PowerPlan.Value switch
            {
                PowerPlanPreset.Balanced => "Équilibré",
                PowerPlanPreset.HighPerformance => "Performance",
                PowerPlanPreset.PowerSaver => "Éco",
                PowerPlanPreset.BestEfficiency => "Efficacité",
                PowerPlanPreset.UltimatePerformance => "Max",
                _ => ""
            });
        if (CpuMaxPercent.HasValue)
            parts.Add($"CPU {CpuMaxPercent}%");
        if (RefreshRateHz.HasValue)
            parts.Add($"{RefreshRateHz} Hz");
        if (TransparencyEnabled.HasValue)
            parts.Add(TransparencyEnabled.Value ? "Transparence On" : "Transparence Off");
        if (AnimationsEnabled.HasValue)
            parts.Add(AnimationsEnabled.Value ? "Animations On" : "Animations Off");
        return parts.Count > 0 ? string.Join(" · ", parts) : "No changes";
    }
}

internal sealed class WindowsDisplayProfile
{
    public string DeviceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRateHz { get; set; }
}

internal sealed class WindowsProfileStore
{
    private static readonly Lazy<WindowsProfileStore> _current = new(Load);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly string _storePath;

    public static WindowsProfileStore Current => _current.Value;

    public List<WindowsProfile> Profiles { get; private set; } = [];
    public string? ActiveProfileId { get; private set; }
    public WindowsProfile? BaseProfile { get; private set; }

    public event Action? Changed;

    private WindowsProfileStore(string storePath)
    {
        _storePath = storePath;
        Profiles = [.. BuiltInProfiles()];
    }

    private static WindowsProfileStore Load()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Veil");
        var path = Path.Combine(dir, "profiles.json");
        var store = new WindowsProfileStore(path);

        if (File.Exists(path))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ProfileStoreDto>(File.ReadAllText(path));
                if (dto != null)
                {
                    store.ActiveProfileId = dto.ActiveProfileId;
                    store.BaseProfile = dto.BaseProfile;
                    if (dto.BuiltInProfiles != null)
                        store.ApplyBuiltInOverrides(dto.BuiltInProfiles);
                    if (dto.UserProfiles != null)
                        store.Profiles.AddRange(dto.UserProfiles);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load windows profiles.", ex);
            }
        }

        store.Save();
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var dto = new ProfileStoreDto
            {
                ActiveProfileId = ActiveProfileId,
                BaseProfile = BaseProfile,
                BuiltInProfiles = Profiles.Where(p => p.IsBuiltIn).Select(CloneProfile).ToList(),
                UserProfiles = Profiles.Where(p => !p.IsBuiltIn).ToList()
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(dto, _jsonOptions));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to save windows profiles.", ex);
        }
    }

    public void AddUserProfile(WindowsProfile profile)
    {
        profile.IsBuiltIn = false;
        Profiles.Add(profile);
        Save();
        Changed?.Invoke();
    }

    public void UpdateProfile(WindowsProfile profile)
    {
        var existing = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing is null)
        {
            return;
        }

        bool isBuiltIn = existing.IsBuiltIn;
        existing.Name = profile.Name;
        existing.PowerPlan = profile.PowerPlan;
        existing.CpuMaxPercent = profile.CpuMaxPercent;
        existing.RefreshRateHz = profile.RefreshRateHz;
        existing.Displays = profile.Displays ?? [];
        existing.TransparencyEnabled = profile.TransparencyEnabled;
        existing.AnimationsEnabled = profile.AnimationsEnabled;
        existing.IsBuiltIn = isBuiltIn;
        Save();
        Changed?.Invoke();
    }

    public void ResetBuiltInProfile(string id)
    {
        var existing = Profiles.FirstOrDefault(p => p.IsBuiltIn && p.Id == id);
        var hardcoded = BuiltInProfiles().FirstOrDefault(p => p.Id == id);
        if (existing is null || hardcoded is null)
        {
            return;
        }

        ApplyProfileValues(existing, hardcoded);
        existing.IsBuiltIn = true;
        Save();
        Changed?.Invoke();
    }

    public void RemoveUserProfile(string id)
    {
        Profiles.RemoveAll(p => !p.IsBuiltIn && p.Id == id);
        if (ActiveProfileId == id) ActiveProfileId = null;
        Save();
        Changed?.Invoke();
    }

    public void SetActiveProfile(string? id)
    {
        ActiveProfileId = id;
        Save();
        Changed?.Invoke();
    }

    public void SaveBaseProfile(WindowsProfile profile)
    {
        profile.Id = "base-windows-profile";
        profile.Name = "Previous Windows State";
        profile.IsBuiltIn = false;
        BaseProfile = profile;
        Save();
        Changed?.Invoke();
    }

    public void ClearBaseProfile()
    {
        BaseProfile = null;
        Save();
        Changed?.Invoke();
    }

    private void ApplyBuiltInOverrides(IEnumerable<WindowsProfile> overrides)
    {
        foreach (var profile in overrides)
        {
            var existing = Profiles.FirstOrDefault(p => p.IsBuiltIn && p.Id == profile.Id);
            if (existing is null)
            {
                continue;
            }

            ApplyProfileValues(existing, profile);
            existing.IsBuiltIn = true;
        }
    }

    private static IEnumerable<WindowsProfile> BuiltInProfiles() =>
    [
        new WindowsProfile
        {
            Id = "builtin-gaming",
            Name = "Gaming",
            IsBuiltIn = true,
            PowerPlan = PowerPlanPreset.HighPerformance,
            CpuMaxPercent = 100,
            RefreshRateHz = 120,
            TransparencyEnabled = false,
            AnimationsEnabled = false
        },
        new WindowsProfile
        {
            Id = "builtin-dev-fluide",
            Name = "Dev Fluide",
            IsBuiltIn = true,
            PowerPlan = PowerPlanPreset.Balanced,
            CpuMaxPercent = 90,
            RefreshRateHz = 120,
            TransparencyEnabled = false,
            AnimationsEnabled = false
        },
        new WindowsProfile
        {
            Id = "builtin-dev-silence",
            Name = "Dev Silence",
            IsBuiltIn = true,
            PowerPlan = PowerPlanPreset.BestEfficiency,
            CpuMaxPercent = 75,
            RefreshRateHz = 60,
            TransparencyEnabled = false,
            AnimationsEnabled = false
        },
        new WindowsProfile
        {
            Id = "builtin-economie",
            Name = "Économie",
            IsBuiltIn = true,
            PowerPlan = PowerPlanPreset.PowerSaver,
            CpuMaxPercent = 60,
            RefreshRateHz = 60,
            TransparencyEnabled = false,
            AnimationsEnabled = false
        },
        new WindowsProfile
        {
            Id = "builtin-normal",
            Name = "Normal",
            IsBuiltIn = true,
            PowerPlan = PowerPlanPreset.Balanced,
            CpuMaxPercent = 100,
            RefreshRateHz = 120,
            TransparencyEnabled = null,
            AnimationsEnabled = true
        }
    ];

    private static WindowsProfile CloneProfile(WindowsProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        IsBuiltIn = profile.IsBuiltIn,
        PowerPlan = profile.PowerPlan,
        CpuMaxPercent = profile.CpuMaxPercent,
        RefreshRateHz = profile.RefreshRateHz,
        Displays = (profile.Displays ?? []).Select(static display => new WindowsDisplayProfile
        {
            DeviceName = display.DeviceName,
            DisplayName = display.DisplayName,
            Width = display.Width,
            Height = display.Height,
            RefreshRateHz = display.RefreshRateHz
        }).ToList(),
        TransparencyEnabled = profile.TransparencyEnabled,
        AnimationsEnabled = profile.AnimationsEnabled
    };

    private static void ApplyProfileValues(WindowsProfile target, WindowsProfile source)
    {
        target.Name = source.Name;
        target.PowerPlan = source.PowerPlan;
        target.CpuMaxPercent = source.CpuMaxPercent;
        target.RefreshRateHz = source.RefreshRateHz;
        target.Displays = (source.Displays ?? []).Select(static display => new WindowsDisplayProfile
        {
            DeviceName = display.DeviceName,
            DisplayName = display.DisplayName,
            Width = display.Width,
            Height = display.Height,
            RefreshRateHz = display.RefreshRateHz
        }).ToList();
        target.TransparencyEnabled = source.TransparencyEnabled;
        target.AnimationsEnabled = source.AnimationsEnabled;
    }

    private sealed class ProfileStoreDto
    {
        public string? ActiveProfileId { get; set; }
        public WindowsProfile? BaseProfile { get; set; }
        public List<WindowsProfile>? BuiltInProfiles { get; set; }
        public List<WindowsProfile>? UserProfiles { get; set; }
    }
}
