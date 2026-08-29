using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Models;

namespace OBDim.Services;

public class ConfigService
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// Options used when reading config.json. Deliberately permissive: a hand-edited
    /// file must keep working, and the cost of being strict is that one typo wipes
    /// every value the user cares about.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    /// accepts both <c>PascalCase</c> (what v1.0.0–v1.0.6 wrote) and <c>camelCase</c>
    /// (what spec §5 documents, and what we write from now on).</description></item>
    /// <item><description><see cref="TolerantAppModeConverter"/> accepts both the numeric
    /// enum v1.0.6 wrote and the string enum spec §5 documents.</description></item>
    /// <item><description><see cref="JsonNumberHandling.AllowReadingFromString"/> accepts
    /// a quoted number (<c>"acMinutes": "30"</c>) — a very common hand-edit
    /// slip.</description></item>
    /// </list>
    /// </remarks>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new TolerantAppModeConverter() }
    };

    /// <summary>
    /// Options used when writing config.json. Exactly one shape, matching spec §5:
    /// camelCase property names and string enums. Old files are upgraded on the next
    /// save, with no action required from the user.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
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

    /// <summary>
    /// True when the most recent <see cref="Load"/> could not parse the file at all and
    /// had to start over from factory defaults (the unreadable file is backed up first).
    /// </summary>
    /// <remarks>
    /// v1.0.7: this outcome used to be invisible — the user's four timeout values, hotkey
    /// and language silently reverted to defaults with nothing but a log line. Callers
    /// read this flag to say so out loud.
    /// </remarks>
    public bool LastLoadWasRecovered { get; private set; }

    public ConfigService(string filePath)
    {
        _filePath = filePath;
    }

    public static ConfigService CreateDefault() => new(DefaultFilePath);

    public AppConfig Load()
    {
        LastLoadWasRecovered = false;

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
            var nullable = JsonSerializer.Deserialize<NullableAppConfig>(json, ReadOptions);
            if (nullable == null)
                return AppConfig.CreateDefault();

            return MergeWithDefaults(nullable);
        }
        catch (JsonException ex)
        {
            // Backup corrupt file and log
            LogService.Error($"Config file backed up due to JSON error: {ex.Message}");
            LastLoadWasRecovered = true;

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

    /// <summary>
    /// Persists the configuration.
    /// </summary>
    /// <param name="config">Configuration to write.</param>
    /// <returns>
    /// True when the file was written. False when it could not be (disk full, permissions,
    /// file locked) — the value is still in memory, so the application keeps running, but
    /// the caller must tell the user the change will not survive a restart.
    /// </returns>
    public bool Save(AppConfig config)
    {
        return TryWrite(config);
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
    /// <remarks>
    /// v1.0.7: reachable only for values the converter already classified as "absent"
    /// (null) — a present-but-unrecognised value never gets this far, because
    /// <see cref="TolerantAppModeConverter"/> turns it into Unknown instead of throwing.
    /// </remarks>
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
    /// <param name="config">Configuration to write.</param>
    /// <returns>True on success, false if the write failed for any reason.</returns>
    private bool TryWrite(AppConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(config, WriteOptions);
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
            return true;
        }
        catch (Exception ex)
        {
            // v1.0.7: log AND report. Previously the caller had no way to learn that the
            // user's change was lost, so a full disk looked identical to a successful save.
            LogService.Error("Failed to write config file", ex);
            return false;
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

    /// <summary>
    /// Reads <see cref="AppMode"/> from either a JSON number (v1.0.6 and earlier wrote
    /// <c>"CurrentMode": 0</c>) or a JSON string (spec §5 documents
    /// <c>"currentMode": "Work"</c>), and always writes it back as a string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written rather than delegating to <see cref="JsonStringEnumConverter"/> with
    /// <c>allowIntegerValues: true</c>, because the difference shows up in exactly the
    /// case we have to survive: a hand-edited file. The stock converter throws
    /// <see cref="JsonException"/> for an unrecognised string such as
    /// <c>"InvalidMode"</c>, and the caller turns that into "back the file up and reset
    /// everything" — one typo costs the user four timeout values, the hotkey and the
    /// language. Anything unrecognised becomes <see cref="AppMode.Unknown"/> here, so the
    /// rest of the file survives intact.
    /// </para>
    /// <para>
    /// An out-of-range number is treated the same way: <c>99</c> becomes Unknown, which
    /// is what the existing <c>Load_InvalidEnumValue_FallsBackToUnknown</c> test asserts.
    /// </para>
    /// </remarks>
    private sealed class TolerantAppModeConverter : JsonConverter<AppMode?>
    {
        /// <summary>Reads a mode value that may be a number, a string, null, or junk.</summary>
        /// <param name="reader">Reader positioned at the value token.</param>
        /// <param name="typeToConvert">Always <see cref="AppMode"/> or its nullable form.</param>
        /// <param name="options">Options in effect for the current deserialization.</param>
        /// <returns>
        /// The parsed mode, <see cref="AppMode.Unknown"/> for an unrecognised value, or
        /// <c>null</c> when nothing usable is present (which lets the caller apply its default).
        /// </returns>
        public override AppMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.Number:
                    return reader.TryGetInt32(out var number) &&
                           Enum.IsDefined(typeof(AppMode), number)
                        ? (AppMode)number
                        : AppMode.Unknown;

                case JsonTokenType.String:
                    // Enum.TryParse also accepts numeric strings ("0", "99"), which is
                    // why IsDefined is checked separately: "99" parses but is not a mode.
                    var text = reader.GetString();
                    return Enum.TryParse<AppMode>(text, ignoreCase: true, out var parsed) &&
                           Enum.IsDefined(typeof(AppMode), parsed)
                        ? parsed
                        : AppMode.Unknown;

                case JsonTokenType.True:
                case JsonTokenType.False:
                    // Not a mode at all — report as absent so the default is used.
                    return null;

                default:
                    // An object or array where a single mode was expected. Consume it so
                    // the reader stays in sync, then report as absent.
                    reader.Skip();
                    return null;
            }
        }

        /// <summary>Writes the mode as its string name, as spec §5 requires.</summary>
        /// <param name="writer">Writer to emit the value into.</param>
        /// <param name="value">Mode to write; null writes a JSON null.</param>
        /// <param name="options">Options in effect for the current serialization.</param>
        public override void Write(Utf8JsonWriter writer, AppMode? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString());
            else
                writer.WriteNullValue();
        }
    }
}
