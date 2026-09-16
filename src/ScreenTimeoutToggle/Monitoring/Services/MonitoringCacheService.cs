using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Services;

namespace OBDim.Monitoring.Services;

/// <summary>Everything persisted for one provider under one verified identity.</summary>
public sealed record CachedProviderState
{
    public int SchemaVersion { get; init; } = 1;

    public ProviderId Provider { get; init; }

    public required string IdentityKey { get; init; }

    /// <summary>The most recent good snapshot. Null when no refresh has ever succeeded for this identity.</summary>
    public ProviderSnapshot? LastGood { get; init; }

    /// <summary>Reminder dedupe marks, keyed by reminder key (provider|bucket|window|resetpoint|level). Survives restarts.</summary>
    public Dictionary<string, bool> ReminderFiredMarks { get; init; } = [];
}

/// <summary>
/// Device-local cache for normalized quota snapshots (spec §8(2), §8(8)):
/// <list type="bullet">
/// <item>Location: %LocalAppData%\ScreenTimeoutToggle\monitoring-cache\v1\.</item>
/// <item>File names are FIXED identifiers (provider + hex hash of the identity key) — never
/// response-derived paths, so no CLI field can make us write outside the cache directory.</item>
/// <item>One identity per file; switching accounts removes the provider's other state files
/// so an old account's data never lingers (spec §8(4)).</item>
/// <item>Total cache size is capped (10 MiB default); a write that would exceed the cap is
/// SKIPPED and reported — never truncated in place, and memory display continues.</item>
/// <item>Atomic writes (temp + File.Replace); corrupt files are backed up and treated as
/// absent.</item>
/// </list>
/// </summary>
public interface IMonitoringCache
{
    CachedProviderState? Load(ProviderId provider, string identityKey);
    bool Save(CachedProviderState state);
    void ClearProvider(ProviderId provider);
}

public sealed class MonitoringCacheService : IMonitoringCache
{
    public const long DefaultMaxTotalBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DirectoryPath { get; }
    public long MaxTotalBytes { get; }

    public MonitoringCacheService(string? directoryPath = null, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        DirectoryPath = directoryPath ?? DefaultDirectoryPath();
        MaxTotalBytes = maxTotalBytes;
    }

    public static string DefaultDirectoryPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeoutToggle",
            "monitoring-cache",
            "v1");

    /// <summary>Loads the cached state for one provider+identity, or null when absent/corrupt.</summary>
    public CachedProviderState? Load(ProviderId provider, string identityKey)
    {
        var path = StateFilePath(provider, identityKey);
        if (!File.Exists(path)) return null;

        try
        {
            var state = JsonSerializer.Deserialize<CachedProviderState>(File.ReadAllText(path), JsonOptions);
            if (state is null) return null;
            if (state.Provider != provider || state.IdentityKey != identityKey) return null;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            BackupCorruptFile(path);
            return null;
        }
    }

    /// <summary>
    /// Persists the state atomically and removes other identities' files for the same
    /// provider (account switch → old account's quota and reminder marks are gone).
    /// Returns false when the write was skipped (size cap) or failed — the caller keeps
    /// the in-memory state either way.
    /// </summary>
    public bool Save(CachedProviderState state)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            RemoveOtherIdentities(state.Provider, state.IdentityKey);

            if (TotalSizeBytes() + EstimateStateSize(state) > MaxTotalBytes)
            {
                LogService.Warn("Monitoring cache size cap reached — snapshot not persisted (memory display continues)");
                return false;
            }

            var path = StateFilePath(state.Provider, state.IdentityKey);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
            try
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                File.Move(tmp, path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogService.Error("Failed to write monitoring cache", ex);
            return false;
        }
    }

    /// <summary>Deletes every state file for one provider (identity reset / diagnostics).</summary>
    public void ClearProvider(ProviderId provider)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, FileNamePrefix(provider) + "*.state.json"))
            File.Delete(file); // callers must see failures; never report a false successful clear
    }

    public long TotalSizeBytes()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return 0;
            return Directory.EnumerateFiles(DirectoryPath).Sum(f =>
            {
                try { return new FileInfo(f).Length; } catch (IOException) { return 0L; }
            });
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private long EstimateStateSize(CachedProviderState state)
    {
        try
        {
            return JsonSerializer.Serialize(state, JsonOptions).Length;
        }
        catch (ArgumentException)
        {
            return long.MaxValue; // unserializable state must not be written
        }
    }

    private void RemoveOtherIdentities(ProviderId provider, string keepIdentityKey)
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            var keep = StateFileName(provider, keepIdentityKey);
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, FileNamePrefix(provider) + "*.state.json"))
            {
                if (!string.Equals(Path.GetFileName(file), keep, StringComparison.Ordinal))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Best effort — an undeletable stale file only costs a little cap headroom.
        }
    }

    private static string FileNamePrefix(ProviderId provider) => provider switch
    {
        ProviderId.Codex => "codex-",
        ProviderId.MiniMax => "minimax-",
        ProviderId.Ark => "ark-",
        _ => "provider-",
    };

    private static string StateFileName(ProviderId provider, string identityKey) =>
        // identityKey is already a 32-char hex hash; belt and braces: re-hash to guarantee
        // a fixed, path-safe charset for every component (spec §8(8)).
        $"{FileNamePrefix(provider)}{MonitoringIdentity.Hash(identityKey)[..16]}.state.json";

    private string StateFilePath(ProviderId provider, string identityKey) =>
        Path.Combine(DirectoryPath, StateFileName(provider, identityKey));

    private static void BackupCorruptFile(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(path, path + $".corrupt.{stamp}", overwrite: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
