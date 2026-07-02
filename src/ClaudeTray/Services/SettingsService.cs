using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTray.Services;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int AlertThresholdPercent { get; set; } = 90;
    public int WarnThresholdPercent { get; set; } = 60;
    public int HighThresholdPercent { get; set; } = 80;
    public bool NotifyAtThreshold { get; set; } = true;
    public bool NotifyAtExceeded { get; set; } = true;
    public bool NotifyOnReset { get; set; } = false;
    public bool MuteAll { get; set; } = false;
    /// <summary>Bucket keys (LimitEntry.Key) the user muted individually.</summary>
    public HashSet<string> MutedBuckets { get; set; } = new();
    public bool CheckForUpdates { get; set; } = true;

    [JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollIntervalSeconds, 10, 600));
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-tray");

    private static string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOpts));
        Changed?.Invoke();
    }
}
