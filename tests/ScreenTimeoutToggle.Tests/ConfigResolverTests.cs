using OBDim.Models;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// v1.0.5: unit tests for the hotkey rollback decision extracted out of
/// TrayApp.OpenSettings. The rollback path previously had zero coverage, and a
/// regression there is silent — the config file would keep a hotkey value that can
/// never be registered again.
/// </summary>
public class ConfigResolverTests
{
    private static AppConfig MakeConfig(string hotkeyKey, string modifiers = "Ctrl+Alt") => new()
    {
        Work = new TimeoutConfig { AcMinutes = 0, DcMinutes = 30 },
        Away = new TimeoutConfig { AcMinutes = 1, DcMinutes = 1 },
        Hotkey = new HotkeyConfig { Modifiers = modifiers, Key = hotkeyKey },
        AutoStart = true,
        CurrentMode = AppMode.Work,
        Language = "zh-CN"
    };

    /// <summary>
    /// Case 1: registration succeeded → the user's config is persisted verbatim.
    /// </summary>
    [Fact]
    public void ResolveEffectiveConfig_Registered_ReturnsNewConfig()
    {
        var oldCfg = MakeConfig("S");
        var newCfg = MakeConfig("F5");

        var result = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, hotkeyRegistered: true);

        Assert.Same(newCfg, result);
        Assert.Equal("F5", result.Hotkey.Key);
    }

    /// <summary>
    /// Case 2 (the critical one): registration failed → the hotkey is rolled back to the
    /// old value, but Work / Away / Language / AutoStart must all keep the NEW values the
    /// user picked in the dialog. A rollback that clobbers the other fields would silently
    /// discard the user's timeout edits.
    /// </summary>
    [Fact]
    public void ResolveEffectiveConfig_Rollback_KeepsAllOtherUserEdits()
    {
        var oldCfg = MakeConfig("S");
        var newCfg = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 45, DcMinutes = 15 },
            Away = new TimeoutConfig { AcMinutes = 2, DcMinutes = 3 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "F5" },
            AutoStart = false,
            CurrentMode = AppMode.Work,
            Language = "en-US"
        };

        var result = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, hotkeyRegistered: false);

        // Rolled back
        Assert.Equal(oldCfg.Hotkey, result.Hotkey);
        Assert.Equal("Ctrl+Alt", result.Hotkey.Modifiers);
        Assert.Equal("S", result.Hotkey.Key);

        // ...and nothing else was touched
        Assert.Equal(45, result.Work.AcMinutes);
        Assert.Equal(15, result.Work.DcMinutes);
        Assert.Equal(2, result.Away.AcMinutes);
        Assert.Equal(3, result.Away.DcMinutes);
        Assert.Equal("en-US", result.Language);
        Assert.False(result.AutoStart);
    }

    /// <summary>
    /// Case 3: the hotkey was not changed, so no registration was attempted. The flag is
    /// irrelevant and the user's other edits must pass through untouched even when the
    /// flag happens to be false.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveEffectiveConfig_HotkeyUnchanged_ReturnsNewConfigRegardlessOfFlag(bool flag)
    {
        var oldCfg = MakeConfig("S");
        var newCfg = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 7, DcMinutes = 8 },
            Away = new TimeoutConfig { AcMinutes = 9, DcMinutes = 10 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Alt", Key = "S" }, // same as old
            AutoStart = false,
            CurrentMode = AppMode.Work,
            Language = "en-US"
        };

        var result = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, flag);

        Assert.Same(newCfg, result);
        Assert.Equal("S", result.Hotkey.Key);
        Assert.Equal(7, result.Work.AcMinutes);
        Assert.Equal("en-US", result.Language);
    }

    /// <summary>
    /// Rollback must also apply when only the modifiers changed (same key, different
    /// modifiers) — HotkeyConfig equality covers both fields.
    /// </summary>
    [Fact]
    public void ResolveEffectiveConfig_ModifiersOnlyChange_RollbackRestoresBothFields()
    {
        var oldCfg = MakeConfig("S", "Ctrl+Alt");
        var newCfg = MakeConfig("S", "Ctrl+Shift");

        var result = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, hotkeyRegistered: false);

        Assert.Equal("Ctrl+Alt", result.Hotkey.Modifiers);
        Assert.Equal("S", result.Hotkey.Key);
    }

    /// <summary>
    /// Case 4 (end-to-end): after a rollback the config file on disk must read back with
    /// the OLD hotkey and the NEW timeout values — i.e. what the code persists is what
    /// the next launch will actually try to register.
    /// </summary>
    [Fact]
    public void ResolveEffectiveConfig_Rollback_ThenPersist_ReloadsOldHotkey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cfg_rollback_{Guid.NewGuid():N}.json");
        try
        {
            var configSvc = new ConfigService(path);
            var oldCfg = MakeConfig("S");
            var newCfg = new AppConfig
            {
                Work = new TimeoutConfig { AcMinutes = 20, DcMinutes = 10 },
                Away = new TimeoutConfig { AcMinutes = 5, DcMinutes = 5 },
                Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "F5" },
                AutoStart = false,
                CurrentMode = AppMode.Work,
                Language = "en-US"
            };

            var effective = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, hotkeyRegistered: false);
            configSvc.Save(effective);
            var reloaded = configSvc.Load();

            Assert.Equal(oldCfg.Hotkey.Modifiers, reloaded.Hotkey.Modifiers);
            Assert.Equal(oldCfg.Hotkey.Key, reloaded.Hotkey.Key);
            Assert.Equal(20, reloaded.Work.AcMinutes);
            Assert.Equal(10, reloaded.Work.DcMinutes);
            Assert.Equal(5, reloaded.Away.AcMinutes);
            Assert.Equal("en-US", reloaded.Language);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The effective config must carry the hotkey that is actually registered, so that
    /// "what the user sees" and "what the config says" can never diverge.
    /// </summary>
    [Fact]
    public void ResolveEffectiveConfig_NullArguments_Throw()
    {
        var cfg = MakeConfig("S");

        Assert.Throws<ArgumentNullException>(
            () => ConfigResolver.ResolveEffectiveConfig(oldCfg: null!, cfg, true));
        Assert.Throws<ArgumentNullException>(
            () => ConfigResolver.ResolveEffectiveConfig(cfg, newCfg: null!, true));
    }
}
