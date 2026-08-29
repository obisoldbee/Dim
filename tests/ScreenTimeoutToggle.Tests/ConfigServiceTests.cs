using OBDim.Models;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// v1.0.7: every test in this class gets its own directory under %TEMP% and deletes it on
/// the way out. Previously the files landed directly in <c>Path.GetTempPath()</c> and were
/// never cleaned up, and <see cref="Load_CorruptJson_BackupsFile_AndReturnsDefault"/>
/// searched that shared root with a <c>cfg_*.json.bak.*</c> wildcard — so a leftover
/// backup from an earlier run (or another process) made the assertion pass on its own.
/// </summary>
public class ConfigServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"obdim-cfg-{Guid.NewGuid():N}");

    public ConfigServiceTests() => Directory.CreateDirectory(_dir);

    private string TempFile() => Path.Combine(_dir, $"cfg_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault_AndCreatesFile()
    {
        var path = TempFile();
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes);
        Assert.Equal(1, cfg.Away.AcMinutes);
        Assert.Equal(1, cfg.Away.DcMinutes);
        Assert.True(cfg.AutoStart);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptJson_BackupsFile_AndReturnsDefault()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not json");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes); // default
        // Scoped to this test's own directory, so only THIS test's backup can satisfy it.
        var backup = Directory.GetFiles(_dir, "*.json.bak.*").FirstOrDefault();
        Assert.NotNull(backup);
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var path = TempFile();
        var svc = new ConfigService(path);
        var original = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 5, DcMinutes = 10 },
            Away = new TimeoutConfig { AcMinutes = 2, DcMinutes = 1 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "F5" },
            AutoStart = false,
            CurrentMode = AppMode.Away
        };

        svc.Save(original);
        var loaded = svc.Load();

        Assert.Equal(5, loaded.Work.AcMinutes);
        Assert.Equal(10, loaded.Work.DcMinutes);
        Assert.Equal(2, loaded.Away.AcMinutes);
        Assert.Equal("Ctrl+Shift", loaded.Hotkey.Modifiers);
        Assert.Equal("F5", loaded.Hotkey.Key);
        Assert.False(loaded.AutoStart);
        Assert.Equal(AppMode.Away, loaded.CurrentMode);
    }

    /// <summary>
    /// B1: Missing fields inside an existing object should use defaults, not type defaults (int=0).
    /// Previously dcMinutes would be 0 (int default); now should be 30 (config default).
    /// </summary>
    [Fact]
    public void Load_MissingFields_UsesDefaults()
    {
        // Only work.acMinutes is provided; dcMinutes is missing inside the work object,
        // and the entire away/hotkey objects are missing.
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":7}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(7, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes);   // default (field missing inside work object)
        Assert.Equal(1, cfg.Away.AcMinutes);    // factory default (whole Away object missing)
        Assert.Equal("Ctrl+Alt", cfg.Hotkey.Modifiers); // factory default (whole Hotkey missing)
    }

    /// <summary>
    /// B1: Explicit 0 should be preserved (0 = never), not replaced with default.
    /// </summary>
    [Fact]
    public void Load_ExplicitZero_Preserved_NotReplacedWithDefault()
    {
        var path = TempFile();
        // Explicit 0 for dcMinutes should be preserved (0 = never)
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":7,"dcMinutes":0}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(7, cfg.Work.AcMinutes);
        Assert.Equal(0, cfg.Work.DcMinutes);  // explicit 0 preserved
    }

    /// <summary>
    /// B4: Invalid enum value (e.g., 99) should fall back to Unknown.
    /// </summary>
    [Fact]
    public void Load_InvalidEnumValue_FallsBackToUnknown()
    {
        var path = TempFile();
        // 99 is not a valid AppMode value
        File.WriteAllText(path, """{"version":1,"currentMode":99}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(AppMode.Unknown, cfg.CurrentMode);
    }

    /// <summary>
    /// B2: JSON with comments and trailing commas should parse successfully.
    /// </summary>
    [Fact]
    public void Load_JsonWithCommentsAndTrailingCommas_ParsesSuccessfully()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1, /* comment */ "work":{"acMinutes":5,"dcMinutes":10,},}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(5, cfg.Work.AcMinutes);
        Assert.Equal(10, cfg.Work.DcMinutes);
    }

    /// <summary>
    /// B1: Missing away object entirely should use factory defaults.
    /// </summary>
    [Fact]
    public void Load_MissingAwayObject_UsesFactoryDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":0,"dcMinutes":30}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(1, cfg.Away.AcMinutes);  // factory default
        Assert.Equal(1, cfg.Away.DcMinutes);  // factory default
    }

    // ===== QA-added edge case tests =====

    /// <summary>
    /// QA: Explicit null in JSON (e.g., {"work":{"acMinutes":null}}) should use defaults.
    /// NullableAppConfig should deserialize null as null, triggering the ?? default fallback.
    /// </summary>
    [Fact]
    public void Load_ExplicitNullInJson_UsesDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":null,"dcMinutes":null}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes);   // default Work AC
        Assert.Equal(30, cfg.Work.DcMinutes);  // default Work DC
    }

    /// <summary>
    /// QA: Empty JSON object {} should use all factory defaults.
    /// </summary>
    [Fact]
    public void Load_EmptyJsonObject_UsesAllDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "{}");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes);
        Assert.Equal(1, cfg.Away.AcMinutes);
        Assert.Equal(1, cfg.Away.DcMinutes);
        Assert.Equal("Ctrl+Alt", cfg.Hotkey.Modifiers);
        Assert.Equal("S", cfg.Hotkey.Key);
        Assert.True(cfg.AutoStart);
    }

    /// <summary>
    /// QA: Save then Load should preserve explicit zero for all four timeout values.
    /// </summary>
    [Fact]
    public void Save_ThenLoad_PreservesAllExplicitZeros()
    {
        var path = TempFile();
        var svc = new ConfigService(path);
        var original = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 0, DcMinutes = 0 },
            Away = new TimeoutConfig { AcMinutes = 0, DcMinutes = 0 },
        };

        svc.Save(original);
        var loaded = svc.Load();

        Assert.Equal(0, loaded.Work.AcMinutes);
        Assert.Equal(0, loaded.Work.DcMinutes);
        Assert.Equal(0, loaded.Away.AcMinutes);
        Assert.Equal(0, loaded.Away.DcMinutes);
    }

    // ===== v1.0.7: config.json dual-format support (spec §5) =====

    /// <summary>
    /// v1.0.7: spec §5 documents <c>"currentMode": "Work"</c>, so a user editing the file
    /// by hand will write a string. The previous test asserted the opposite — that
    /// <c>"InvalidMode"</c> triggered the backup-and-reset path — which turned a defect
    /// into a specification. Reading strings must now work.
    /// </summary>
    [Theory]
    [InlineData("Work", AppMode.Work)]
    [InlineData("Away", AppMode.Away)]
    [InlineData("Unknown", AppMode.Unknown)]
    public void Load_StringEnumCurrentMode_IsParsed(string stored, AppMode expected)
    {
        var path = TempFile();
        File.WriteAllText(path, $"{{\"version\":1,\"currentMode\":\"{stored}\"}}");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(expected, cfg.CurrentMode);
    }

    /// <summary>
    /// v1.0.7: string enum names are matched case-insensitively, the way a hand-edited
    /// file is most likely to spell them.
    /// </summary>
    [Theory]
    [InlineData("work")]
    [InlineData("AWAY")]
    [InlineData("UnKnOwN")]
    public void Load_StringEnumCurrentMode_IsCaseInsensitive(string stored)
    {
        var path = TempFile();
        File.WriteAllText(path, $"{{\"version\":1,\"currentMode\":\"{stored}\"}}");
        var svc = new ConfigService(path);

        Assert.Equal(
            Enum.Parse<AppMode>(stored, ignoreCase: true),
            svc.Load().CurrentMode);
    }

    /// <summary>
    /// v1.0.7: v1.0.0–v1.0.6 wrote <c>"CurrentMode": 0</c>. Those files — including the
    /// one on this machine right now — must still load.
    /// </summary>
    [Theory]
    [InlineData(0, AppMode.Work)]
    [InlineData(1, AppMode.Away)]
    [InlineData(2, AppMode.Unknown)]
    public void Load_NumericEnumCurrentMode_StillWorks(int stored, AppMode expected)
    {
        var path = TempFile();
        File.WriteAllText(path, $"{{\"version\":1,\"currentMode\":{stored}}}");
        var svc = new ConfigService(path);

        Assert.Equal(expected, svc.Load().CurrentMode);
    }

    /// <summary>
    /// v1.0.7: the whole point of the tolerant converter. An unrecognised mode used to
    /// raise JsonException, and the caller's answer to that is "back the file up and reset
    /// everything to defaults" — so one typo in currentMode destroyed the four timeout
    /// values, the hotkey and the language, with no message. The bad field must now
    /// degrade on its own and leave the rest of the file alone.
    /// </summary>
    [Theory]
    [InlineData("\"InvalidMode\"")]
    [InlineData("99")]
    [InlineData("\"77\"")]
    public void Load_UnrecognisedCurrentMode_DegradesToUnknown_KeepingEveryOtherValue(string stored)
    {
        var path = TempFile();
        File.WriteAllText(path,
            $"{{\"version\":1,\"currentMode\":{stored}," +
            "\"work\":{\"acMinutes\":45,\"dcMinutes\":15}," +
            "\"away\":{\"acMinutes\":2,\"dcMinutes\":3}," +
            "\"hotkey\":{\"modifiers\":\"Ctrl+Shift\",\"key\":\"F5\"}," +
            "\"autoStart\":false,\"language\":\"en-US\"}");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(AppMode.Unknown, cfg.CurrentMode);
        // Nothing else may be touched.
        Assert.False(svc.LastLoadWasRecovered, "an unreadable mode must not reset the file");
        Assert.Equal(45, cfg.Work.AcMinutes);
        Assert.Equal(15, cfg.Work.DcMinutes);
        Assert.Equal(2, cfg.Away.AcMinutes);
        Assert.Equal(3, cfg.Away.DcMinutes);
        Assert.Equal("Ctrl+Shift", cfg.Hotkey.Modifiers);
        Assert.Equal("F5", cfg.Hotkey.Key);
        Assert.False(cfg.AutoStart);
        Assert.Equal("en-US", cfg.Language);
    }

    /// <summary>
    /// v1.0.7: the four new bubble keys and the new tooltip key must exist in every
    /// supported language, or the UI degrades to the raw key name.
    /// </summary>
    [Theory]
    [InlineData("bubble.save_failed_title")]
    [InlineData("bubble.save_failed")]
    [InlineData("bubble.config_reset_title")]
    [InlineData("bubble.config_reset")]
    [InlineData("tooltip.detecting")]
    public void V107_NewLocalizationKeys_ExistInEveryLanguage(string key)
    {
        foreach (var language in LocalizationService.SupportedLanguages)
        {
            Assert.True(LocalizationService.HasKey(key, language),
                $"'{key}' is missing from {language}");
        }
    }

    /// <summary>
    /// v1.0.7: a value of the wrong JSON kind (a bool where a mode was expected) is not a
    /// mode and not a parse error either — it is treated as "field absent", so the factory
    /// default applies and every other value survives.
    /// </summary>
    [Fact]
    public void Load_CurrentModeOfWrongJsonKind_IsTreatedAsAbsent()
    {
        var path = TempFile();
        File.WriteAllText(path,
            "{\"version\":1,\"currentMode\":true," +
            "\"work\":{\"acMinutes\":45,\"dcMinutes\":15}}");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(AppMode.Work, cfg.CurrentMode); // factory default
        Assert.False(svc.LastLoadWasRecovered);
        Assert.Equal(45, cfg.Work.AcMinutes);
        Assert.Equal(15, cfg.Work.DcMinutes);
    }

    /// <summary>
    /// v1.0.7: writes use camelCase property names and string enums, exactly as spec §5
    /// documents. Old PascalCase files are upgraded on the next save without the user
    /// having to do anything.
    /// </summary>
    [Fact]
    public void Save_WritesCamelCasePropertyNames_AndStringEnums()
    {
        var path = TempFile();
        var svc = new ConfigService(path);
        svc.Save(new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 5, DcMinutes = 10 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Alt", Key = "S" },
            CurrentMode = AppMode.Away
        });

        var json = File.ReadAllText(path);

        Assert.Contains("\"work\"", json);
        Assert.Contains("\"acMinutes\"", json);
        Assert.Contains("\"autoStart\"", json);
        Assert.Contains("\"currentMode\": \"Away\"", json);
        Assert.Contains("\"language\": \"zh-CN\"", json);
        // No PascalCase leftovers from the v1.0.6 writer.
        Assert.DoesNotContain("\"Work\"", json);
        Assert.DoesNotContain("\"AcMinutes\"", json);
    }

    /// <summary>
    /// v1.0.7: a file written by v1.0.6 (PascalCase + numeric enum) must load unchanged in
    /// value, and re-saving it must produce the spec §5 shape.
    /// </summary>
    [Fact]
    public void Load_LegacyPascalCaseFile_RoundTripsAndIsUpgradedOnSave()
    {
        var path = TempFile();
        File.WriteAllText(path,
            "{\"Version\":1,\"Work\":{\"AcMinutes\":0,\"DcMinutes\":30}," +
            "\"Away\":{\"AcMinutes\":1,\"DcMinutes\":1}," +
            "\"Hotkey\":{\"Modifiers\":\"Ctrl+Alt\",\"Key\":\"NumPad0\"}," +
            "\"AutoStart\":true,\"CurrentMode\":0,\"Language\":\"zh-CN\"}");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes);
        Assert.Equal("NumPad0", cfg.Hotkey.Key);
        Assert.Equal(AppMode.Work, cfg.CurrentMode);

        svc.Save(cfg);
        var upgraded = File.ReadAllText(path);
        Assert.Contains("\"currentMode\": \"Work\"", upgraded);
        Assert.Contains("\"dcMinutes\"", upgraded);

        // And the upgraded file must load back identically.
        Assert.Equal(30, svc.Load().Work.DcMinutes);
    }

    /// <summary>
    /// v1.0.7: a hand-edited file often ends up with a quoted number. Accepting it costs
    /// nothing; rejecting it used to cost the entire configuration.
    /// </summary>
    [Fact]
    public void Load_QuotedNumberForMinutes_IsAccepted()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":"30","dcMinutes":"5"}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(30, cfg.Work.AcMinutes);
        Assert.Equal(5, cfg.Work.DcMinutes);
    }

    /// <summary>
    /// v1.0.7: <see cref="ConfigService.Save"/> reports failure instead of swallowing it,
    /// so a caller can tell the user a change will not survive a restart.
    /// </summary>
    [Fact]
    public void Save_ReturnsTrue_OnSuccess()
    {
        var path = TempFile();
        var svc = new ConfigService(path);

        Assert.True(svc.Save(AppConfig.CreateDefault()));
    }

    /// <summary>
    /// v1.0.7: a genuinely unreadable file still falls back to defaults (spec §5), but the
    /// caller can now find out — the reset is no longer silent.
    /// </summary>
    [Fact]
    public void Load_CorruptFile_ReportsRecovery_InsteadOfResettingSilently()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not json");
        var svc = new ConfigService(path);

        Assert.False(svc.LastLoadWasRecovered, "nothing has been loaded yet");

        svc.Load();

        Assert.True(svc.LastLoadWasRecovered);
    }

    /// <summary>
    /// v1.0.7: the recovery flag must not stick — it describes the last load only, or the
    /// user would be warned about a reset on every subsequent save.
    /// </summary>
    [Fact]
    public void LastLoadWasRecovered_IsClearedByASuccessfulLoad()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not json");
        var svc = new ConfigService(path);
        svc.Load();
        Assert.True(svc.LastLoadWasRecovered);

        svc.Save(AppConfig.CreateDefault());
        svc.Load();

        Assert.False(svc.LastLoadWasRecovered);
    }

    // ===== v1.0.2: Language field tests =====

    /// <summary>
    /// v1.0.2: Default config should have Language = "zh-CN".
    /// </summary>
    [Fact]
    public void Load_MissingFile_LanguageDefaultsToZhCn()
    {
        var path = TempFile();
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("zh-CN", cfg.Language);
    }

    /// <summary>
    /// v1.0.2: Language field should round-trip through save/load.
    /// </summary>
    [Fact]
    public void Save_ThenLoad_RoundTripsLanguage()
    {
        var path = TempFile();
        var svc = new ConfigService(path);
        var original = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 5, DcMinutes = 10 },
            Away = new TimeoutConfig { AcMinutes = 2, DcMinutes = 1 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "F5" },
            AutoStart = false,
            CurrentMode = AppMode.Away,
            Language = "en-US"
        };

        svc.Save(original);
        var loaded = svc.Load();

        Assert.Equal("en-US", loaded.Language);
    }

    /// <summary>
    /// v1.0.2: Missing language field in JSON should default to "zh-CN".
    /// </summary>
    [Fact]
    public void Load_MissingLanguageField_UsesDefaultZhCn()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":5,"dcMinutes":10}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("zh-CN", cfg.Language);
    }

    /// <summary>
    /// v1.0.2: Explicit null language in JSON should default to "zh-CN".
    /// </summary>
    [Fact]
    public void Load_NullLanguageField_UsesDefaultZhCn()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"language":null,"work":{"acMinutes":5,"dcMinutes":10}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("zh-CN", cfg.Language);
    }

    /// <summary>
    /// v1.0.2: Empty string language in JSON should default to "zh-CN".
    /// </summary>
    [Fact]
    public void Load_EmptyLanguageField_UsesDefaultZhCn()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"language":"","work":{"acMinutes":5,"dcMinutes":10}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("zh-CN", cfg.Language);
    }

    // ===== v1.0.6: "Back" → "Backspace" hotkey migration =====

    /// <summary>
    /// v1.0.6: configs written by v1.0.3/v1.0.4 stored the Backspace key as "Back"
    /// (that is what Keys.Back.ToString() returns). v1.0.5 dropped "Back" from the hotkey
    /// dictionary, so those users would get a failed registration on every launch.
    /// </summary>
    [Fact]
    public void Load_LegacyBackHotkey_IsMigratedToBackspace()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"hotkey":{"modifiers":"Ctrl+Alt","key":"Back"}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("Backspace", cfg.Hotkey.Key);
        Assert.Equal("Ctrl+Alt", cfg.Hotkey.Modifiers);
    }

    /// <summary>
    /// v1.0.6: the migrated key must resolve to a usable virtual key (VK_BACK = 8),
    /// otherwise the migration would be cosmetic.
    /// </summary>
    [Fact]
    public void Load_LegacyBackHotkey_MigratedKeyIsRegisterable()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"hotkey":{"modifiers":"Ctrl+Alt","key":"Back"}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0x08u, HotkeyService.KeyStringToVk(cfg.Hotkey.Key));
    }

    /// <summary>
    /// v1.0.6: migration is case-insensitive and tolerates surrounding whitespace.
    /// </summary>
    [Theory]
    [InlineData("Back")]
    [InlineData("back")]
    [InlineData("BACK")]
    [InlineData("  Back  ")]
    public void Load_LegacyBackHotkey_AnyCasing_IsMigrated(string storedKey)
    {
        var path = TempFile();
        File.WriteAllText(path, $"{{\"version\":1,\"hotkey\":{{\"key\":\"{storedKey}\"}}}}");
        var svc = new ConfigService(path);

        Assert.Equal("Backspace", svc.Load().Hotkey.Key);
    }

    /// <summary>
    /// v1.0.6: only "Back" is rewritten. Everything else must round-trip untouched —
    /// this is deliberately NOT a general alias system.
    /// </summary>
    [Theory]
    [InlineData("BrowserBack")]
    [InlineData("Backspace")]
    [InlineData("S")]
    [InlineData("F5")]
    [InlineData("NumPad0")]
    public void Load_NonBackHotkey_IsLeftAlone(string key)
    {
        var path = TempFile();
        File.WriteAllText(path, $"{{\"version\":1,\"hotkey\":{{\"key\":\"{key}\"}}}}");
        var svc = new ConfigService(path);

        Assert.Equal(key, svc.Load().Hotkey.Key);
    }

    /// <summary>
    /// v1.0.6: a missing or empty key still falls back to the factory default rather
    /// than being mangled by the migration.
    /// </summary>
    [Theory]
    [InlineData("""{"version":1,"hotkey":{"modifiers":"Ctrl+Alt"}}""")]
    [InlineData("""{"version":1,"hotkey":{"key":null}}""")]
    [InlineData("""{"version":1,"hotkey":{"key":""}}""")]
    [InlineData("""{"version":1}""")]
    public void Load_MissingOrEmptyHotkeyKey_UsesFactoryDefault(string json)
    {
        var path = TempFile();
        File.WriteAllText(path, json);
        var svc = new ConfigService(path);

        Assert.Equal("S", svc.Load().Hotkey.Key);
    }

    /// <summary>
    /// v1.0.2: Whitespace-only language in JSON should default to "zh-CN".
    /// </summary>
    [Fact]
    public void Load_WhitespaceLanguageField_UsesDefaultZhCn()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"language":"   ","work":{"acMinutes":5,"dcMinutes":10}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal("zh-CN", cfg.Language);
    }
}
