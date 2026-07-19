using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

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
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
            return MergeWithDefaults(cfg);
        }
        catch (JsonException)
        {
            // Backup corrupt file
            var dir = Path.GetDirectoryName(_filePath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(_filePath);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(_filePath, Path.Combine(dir, $"{name}.bak.{stamp}"), overwrite: true); }
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

    private static AppConfig MergeWithDefaults(AppConfig cfg)
    {
        var def = AppConfig.CreateDefault();
        return cfg with
        {
            Version = cfg.Version == 0 ? def.Version : cfg.Version,
            Work = cfg.Work is null ? def.Work : cfg.Work,
            Away = cfg.Away is null ? def.Away : cfg.Away,
            Hotkey = cfg.Hotkey is null ? def.Hotkey : cfg.Hotkey,
            CurrentMode = cfg.CurrentMode
        };
    }

    private void TryWrite(AppConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else File.Move(tmp, _filePath);
        }
        catch
        {
            // best effort — Load will fall back to defaults next time
        }
    }
}
