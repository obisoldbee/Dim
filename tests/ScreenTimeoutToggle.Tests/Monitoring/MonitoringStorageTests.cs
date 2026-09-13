using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>monitoring.json contract: independent of config.json, defaults for missing, corrupt file backed up.</summary>
public class MonitoringSettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MonitoringSettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"obdim-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "monitoring.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void MissingFile_YieldsDefaults_AllProvidersDisabled()
    {
        var svc = new MonitoringSettingsService(_path);
        var settings = svc.Load();

        Assert.False(svc.LastLoadWasRecovered);
        Assert.True(settings.MemoryEnabled);
        Assert.False(settings.RemindersEnabled);
        Assert.All(settings.Providers, p => Assert.False(p.Enabled, "升级后 Provider 默认关闭 (spec §7)"));
    }

    [Fact]
    public void RoundTrip_PreservesExplicitChoices()
    {
        var svc = new MonitoringSettingsService(_path);
        var settings = new MonitoringSettings
        {
            MemoryEnabled = false,
            RemindersEnabled = true,
            Providers =
            [
                new ProviderSettings { Id = ProviderId.Codex, Enabled = true, CliPath = @"C:\tools\codex.exe" },
                new ProviderSettings { Id = ProviderId.MiniMax, Enabled = false },
                new ProviderSettings { Id = ProviderId.Ark, Enabled = true },
            ],
        };
        Assert.True(svc.Save(settings));

        var reloaded = new MonitoringSettingsService(_path).Load();
        Assert.False(reloaded.MemoryEnabled);
        Assert.True(reloaded.RemindersEnabled);
        Assert.True(reloaded.Provider(ProviderId.Codex).Enabled);
        Assert.Equal(@"C:\tools\codex.exe", reloaded.Provider(ProviderId.Codex).CliPath);
        Assert.True(reloaded.Provider(ProviderId.Ark).Enabled);
    }

    [Fact]
    public void CorruptFile_IsBackedUp_DefaultsReturn_AndOriginalConfigUntouched()
    {
        File.WriteAllText(_path, "{ this is not json ");
        var configPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(configPath, "{\"work\":{\"acMinutes\":42}}");
        var configBefore = File.ReadAllText(configPath);

        var svc = new MonitoringSettingsService(_path);
        var settings = svc.Load();

        Assert.True(svc.LastLoadWasRecovered);
        Assert.False(settings.Provider(ProviderId.Codex).Enabled); // factory default again
        Assert.Equal(configBefore, File.ReadAllText(configPath)); // 原息屏配置不动 (spec §8(6))
        Assert.Contains(Directory.GetFiles(_dir, "monitoring.json.bak.*"), f => f.Length > 0);
    }

    [Fact]
    public void PartialFile_MissingProviderRows_GetDefaults()
    {
        File.WriteAllText(_path, """{"memoryEnabled":false,"providers":[{"id":"Codex","enabled":true}]}""");
        var svc = new MonitoringSettingsService(_path);
        var settings = svc.Load();

        Assert.False(settings.MemoryEnabled);
        Assert.True(settings.Provider(ProviderId.Codex).Enabled);
        Assert.False(settings.Provider(ProviderId.MiniMax).Enabled); // filled in
        Assert.False(settings.Provider(ProviderId.Ark).Enabled);
    }
}

/// <summary>Device cache contract: identity isolation, fixed file names, cap, corrupt backup.</summary>
public class MonitoringCacheServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly MonitoringCacheService _cache;

    public MonitoringCacheServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"obdim-cache-{Guid.NewGuid():N}");
        _cache = new MonitoringCacheService(_dir, maxTotalBytes: 16 * 1024);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ProviderSnapshot Snapshot(string identityKey, double usedPercent = 30) => new()
    {
        Provider = ProviderId.Codex,
        IdentityKey = identityKey,
        IdentityVerified = true,
        AttemptedAtUtc = DateTimeOffset.UtcNow,
        SucceededAtUtc = DateTimeOffset.UtcNow,
        Buckets =
        [
            new QuotaBucket
            {
                SourceKey = "codex",
                DisplayName = "codex",
                Windows =
                [
                    new QuotaWindow { SourceKey = "primary", UsedPercent = usedPercent, RemainingPercent = 100 - usedPercent, HasAnyQuotaField = true },
                ],
            },
        ],
    };

    [Fact]
    public void SaveLoad_RoundTrip_PreservesSnapshotAndMarks()
    {
        var snapshot = Snapshot("ident-1");
        _cache.Save(new CachedProviderState
        {
            Provider = ProviderId.Codex,
            IdentityKey = "ident-1",
            LastGood = snapshot,
            ReminderFiredMarks = new Dictionary<string, bool> { ["k|level1"] = true },
        });

        var loaded = _cache.Load(ProviderId.Codex, "ident-1");
        Assert.NotNull(loaded);
        Assert.Equal(30, loaded!.LastGood!.Buckets[0].Windows[0].UsedPercent);
        Assert.Contains("k|level1", loaded.ReminderFiredMarks.Keys);
    }

    [Fact]
    public void Save_NewIdentity_RemovesOtherIdentityFiles_SameProvider()
    {
        // A09: 账号 A → B，A 的额度与提醒标记不得残留。
        _cache.Save(new CachedProviderState { Provider = ProviderId.Codex, IdentityKey = "account-a", LastGood = Snapshot("account-a") });
        _cache.Save(new CachedProviderState { Provider = ProviderId.Codex, IdentityKey = "account-b", LastGood = Snapshot("account-b") });

        Assert.Null(_cache.Load(ProviderId.Codex, "account-a"));
        Assert.NotNull(_cache.Load(ProviderId.Codex, "account-b"));
        // Other providers are untouched.
        _cache.Save(new CachedProviderState { Provider = ProviderId.Ark, IdentityKey = "account-a", LastGood = Snapshot("account-a") });
        Assert.NotNull(_cache.Load(ProviderId.Ark, "account-a"));
    }

    [Fact]
    public void CorruptFile_IsBackedUp_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        var path = Directory.GetFiles(_dir).FirstOrDefault();
        // Generate the real file name via Save, then corrupt it.
        _cache.Save(new CachedProviderState { Provider = ProviderId.Codex, IdentityKey = "ident-x", LastGood = Snapshot("ident-x") });
        path = Directory.GetFiles(_dir, "codex-*.state.json").Single();
        File.WriteAllText(path, "{broken json");

        Assert.Null(_cache.Load(ProviderId.Codex, "ident-x"));
        Assert.Contains(Directory.GetFiles(_dir, "*.corrupt.*"), f => f.Length > 0);
    }

    [Fact]
    public void SizeCap_SkipsWrite_ReportsFailure()
    {
        var tinyCache = new MonitoringCacheService(_dir, maxTotalBytes: 10);
        var ok = tinyCache.Save(new CachedProviderState
        {
            Provider = ProviderId.Codex,
            IdentityKey = "ident-big",
            LastGood = Snapshot("ident-big"),
        });
        Assert.False(ok, "a write past the cap must be skipped and reported, never faked");
    }

    [Fact]
    public void FileNames_AreFixedCharset_NoResponseDerivedPaths()
    {
        // identityKey 已经是十六进制哈希；文件名必须仍是受限字符（spec §8(8)）。
        _cache.Save(new CachedProviderState { Provider = ProviderId.MiniMax, IdentityKey = "../evil/../../path", LastGood = Snapshot("x") });
        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.Matches(@"^minimax-[0-9A-F]{16}\.state\.json$", Path.GetFileName(files[0]));
    }

    [Fact]
    public void TotalSize_TracksSavedFiles()
    {
        Assert.Equal(0, _cache.TotalSizeBytes());
        _cache.Save(new CachedProviderState { Provider = ProviderId.Codex, IdentityKey = "i", LastGood = Snapshot("i") });
        Assert.True(_cache.TotalSizeBytes() > 0);
    }
}
