using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Services;

namespace OBDim.Monitoring.Services;

/// <summary>
/// Loads and saves monitoring.json — independent from the original config.json (spec §8(2)):
/// the original settings form never rewrites it, and a corrupt monitoring file is backed up
/// and reset WITHOUT touching the screen-timeout configuration. Writes are atomic
/// (temp + File.Replace), matching ConfigService's pattern.
/// </summary>
public sealed class MonitoringSettingsService
{
    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    /// <summary>True when the last Load found an unreadable file (backed up, defaults returned).</summary>
    public bool LastLoadWasRecovered { get; private set; }

    public MonitoringSettingsService(string filePath) => FilePath = filePath;

    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTimeoutToggle",
            "monitoring.json");

    public static MonitoringSettingsService CreateDefault() => new(DefaultFilePath);

    public MonitoringSettings Load()
    {
        LastLoadWasRecovered = false;
        if (!File.Exists(FilePath))
        {
            var created = MonitoringSettings.CreateDefault();
            Save(created);
            return created;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<MonitoringSettings>(json, JsonOptions);
            if (loaded is null) return MonitoringSettings.CreateDefault();
            return Normalize(loaded);
        }
        catch (JsonException ex)
        {
            // Corrupt monitoring file: back it up separately and return defaults. The
            // original config.json must stay untouched (spec §8(6)).
            LogService.Error($"monitoring.json backed up due to parse error: {ex.Message}");
            LastLoadWasRecovered = true;
            try
            {
                var dir = Path.GetDirectoryName(FilePath) ?? ".";
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(FilePath, Path.Combine(dir, $"monitoring.json.bak.{stamp}"), overwrite: true);
            }
            catch (IOException)
            {
                // Best effort — defaults still apply in memory.
            }
            var defaults = MonitoringSettings.CreateDefault();
            Save(defaults);
            return defaults;
        }
    }

    /// <summary>
    /// Persists the settings. Returns false when the write failed — the caller must treat
    /// the change as in-memory only (a failed save is never reported as saved, spec §8(6)).
    /// </summary>
    public bool Save(MonitoringSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var versioned = Normalize(settings);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(versioned, JsonOptions));
            try
            {
                File.Replace(tmp, FilePath, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                File.Move(tmp, FilePath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("Failed to write monitoring.json", ex);
            return false;
        }
    }

    public static bool IsSupportedInterval(int minutes) => minutes is 0 or 1 or 5 or 15 or 30;

    /// <summary>Missing provider rows get defaults (upgraded configs keep working).</summary>
    public static MonitoringSettings Normalize(MonitoringSettings loaded)
    {
        var existing = (loaded.Providers ?? []).Where(p => p is not null && Enum.IsDefined(p.Id))
            .DistinctBy(p => p.Id).Select(p => p with
            {
                RefreshIntervalMinutes = p.RefreshIntervalMinutes is { } value && IsSupportedInterval(value) ? value : null,
            }).ToList();
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (!existing.Any(p => p.Id == id))
            {
                existing.Add(new ProviderSettings { Id = id, Enabled = false });
            }
        }
        return loaded with
        {
            SchemaVersion = CurrentSchemaVersion,
            RefreshIntervalMinutes = IsSupportedInterval(loaded.RefreshIntervalMinutes) ? loaded.RefreshIntervalMinutes : 5,
            Providers = existing.OrderBy(p => p.Id).ToList(),
        };
    }
}
