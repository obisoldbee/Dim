using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Models;

namespace OBDim.Services;

public class ConfigService
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Configuration directory is kept as "ScreenTimeoutToggle" for backward compatibility
    /// (avoids config loss on upgrade from v1.0.0).
    /// </summary>
    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTimeoutToggle",
            "config.json");

    private readonly string _filePath;

    public ConfigService(string filePath)
    {
        _filePath = filePath;
    }

    public static ConfigService CreateDefault() => new(DefaultFilePath);

    public AppConfig Load()
    {
        if (!File.Exists(_filePath))
        {
            var def = AppConfig.CreateDefault();
            TryWrite(def);
            return def;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            // Use nullable intermediate model to distinguish "field missing" from "explicit 0"
            var nullable = JsonSerializer.Deserialize<NullableAppConfig>(json, JsonOptions);
            if (nullable == null)
                return AppConfig.CreateDefault();

            return MergeWithDefaults(nullable);
        }
        catch (JsonException ex)
        {
            // Backup corrupt file and log
            LogService.Error($"Config file backed up due to JSON error: {ex.Message}");

            var dir = Path.GetDirectoryName(_filePath) ?? ".";
            var fileName = Path.GetFileName(_filePath);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(_filePath, Path.Combine(dir, $"{fileName}.bak.{stamp}"), overwrite: true); }
            catch { /* best effort */ }

            var def = AppConfig.CreateDefault();
            TryWrite(def);
            return def;
        }
    }

    public void Save(AppConfig config)
    {
        TryWrite(config);
    }

    /// <summary>
    /// Merges a nullable deserialized config with defaults.
    /// Missing fields use defaults; explicit values (including 0) are preserved.
    /// </summary>
    private static AppConfig MergeWithDefaults(NullableAppConfig nullable)
    {
        var def = AppConfig.CreateDefault();

        var cfg = new AppConfig
        {
            Version = nullable.Version ?? def.Version,
            Work = MergeTimeout(nullable.Work, def.Work),
            Away = MergeTimeout(nullable.Away, def.Away),
            Hotkey = MergeHotkey(nullable.Hotkey, def.Hotkey),
            AutoStart = nullable.AutoStart ?? def.AutoStart,
            CurrentMode = ResolveCurrentMode(nullable.CurrentMode, def.CurrentMode),
            Language = string.IsNullOrWhiteSpace(nullable.Language) ? def.Language : nullable.Language
        };

        return Migrate(cfg, cfg.Version);
    }

    /// <summary>
    /// Merges a nullable timeout config with defaults.
    /// null fields become defaults; explicit 0 is preserved.
    /// </summary>
    private static TimeoutConfig MergeTimeout(NullableTimeoutConfig? nullable, TimeoutConfig def)
    {
        if (nullable == null) return def;
        return new TimeoutConfig
        {
            AcMinutes = nullable.AcMinutes ?? def.AcMinutes,
            DcMinutes = nullable.DcMinutes ?? def.DcMinutes
        };
    }

    /// <summary>
    /// Merges a nullable hotkey config with defaults.
    /// </summary>
    private static HotkeyConfig MergeHotkey(NullableHotkeyConfig? nullable, HotkeyConfig def)
    {
        if (nullable == null) return def;
        return new HotkeyConfig
        {
            Modifiers = nullable.Modifiers ?? def.Modifiers,
            Key = MigrateHotkeyKey(nullable.Key ?? def.Key, def.Key)
        };
    }

    /// <summary>
    /// v1.0.6 one-off migration: v1.0.3/v1.0.4 stored the Backspace key as "Back"
    /// (that is exactly what <c>Keys.Back.ToString()</c> returns). v1.0.5 removed the
    /// "Back" entry from the hotkey dictionary — it had been silently mapped to the
    /// browser Back key — so those configs now resolve to virtual-key 0 and fail to
    /// register on every launch. Rewrite just that one value to "Backspace".
    /// Intentionally not a general alias mechanism: one rule, one reason.
    /// </summary>
    /// <param name="storedKey">Key string read from the config file.</param>
    /// <param name="defaultKey">Factory default, returned when unmigrating is not needed.</param>
    /// <returns>The migrated key string.</returns>
    private static string MigrateHotkeyKey(string storedKey, string defaultKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey)) return defaultKey;

        if (string.Equals(storedKey.Trim(), "Back", StringComparison.OrdinalIgnoreCase))
        {
            LogService.Info("Config migrated: hotkey key \"Back\" rewritten to \"Backspace\"");
            return "Backspace";
        }

        return storedKey;
    }

    /// <summary>
    /// Resolves the CurrentMode, validating that the enum value is defined.
    /// Invalid values (e.g., 99) fall back to Unknown. Missing values use the default.
    /// </summary>
    private static AppMode ResolveCurrentMode(AppMode? value, AppMode defaultValue)
    {
        if (!value.HasValue) return defaultValue;
        return Enum.IsDefined(typeof(AppMode), value.Value) ? value.Value : AppMode.Unknown;
    }

    /// <summary>
    /// Migration hook for future config format upgrades.
    /// Currently v1, no migrations needed.
    /// </summary>
    private static AppConfig Migrate(AppConfig cfg, int fromVersion)
    {
        if (fromVersion >= CurrentVersion) return cfg;

        // Future migrations:
        // if (fromVersion < 2) { cfg = MigrateV1ToV2(cfg); }

        LogService.Info($"Config migrated from v{fromVersion} to v{CurrentVersion}");
        return cfg with { Version = CurrentVersion };
    }

    /// <summary>
    /// Writes config atomically using temp file + File.Replace.
    /// Handles TOCTOU race: if File.Replace fails with FileNotFoundException,
    /// falls back to File.Move.
    /// </summary>
    private void TryWrite(AppConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tmp, json);

            try
            {
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                // Target file was deleted between check and replace — fall back to Move
                File.Move(tmp, _filePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to write config file", ex);
        }
    }

    // Nullable intermediate models for distinguishing missing vs explicit values

    private class NullableAppConfig
    {
        public int? Version { get; set; }
        public NullableTimeoutConfig? Work { get; set; }
        public NullableTimeoutConfig? Away { get; set; }
        public NullableHotkeyConfig? Hotkey { get; set; }
        public bool? AutoStart { get; set; }
        public AppMode? CurrentMode { get; set; }
        public string? Language { get; set; }
    }

    private class NullableTimeoutConfig
    {
        public int? AcMinutes { get; set; }
        public int? DcMinutes { get; set; }
    }

    private class NullableHotkeyConfig
    {
        public string? Modifiers { get; set; }
        public string? Key { get; set; }
    }
}
