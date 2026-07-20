using OBDim.Models;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

public class ConfigServiceTests
{
    private string TempFile() => Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");

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
        var dir = Path.GetDirectoryName(path) ?? ".";
        var backup = Directory.GetFiles(dir, "cfg_*.json.bak.*").FirstOrDefault();
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

    /// <summary>
    /// QA: Invalid CurrentMode string (not a number) triggers JsonException backup path,
    /// returning all factory defaults. System.Text.Json expects enum as number by default.
    /// </summary>
    [Fact]
    public void Load_InvalidCurrentModeString_TriggersBackupAndReturnsDefault()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"currentMode":"InvalidMode"}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        // JsonException triggers backup + default return
        Assert.Equal(AppMode.Work, cfg.CurrentMode); // factory default
    }
}
