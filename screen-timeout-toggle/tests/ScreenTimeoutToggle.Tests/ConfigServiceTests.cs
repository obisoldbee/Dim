using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

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

    [Fact]
    public void Load_MissingFields_FillsWithDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":7}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(7, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes); // default
        Assert.Equal(1, cfg.Away.AcMinutes);  // default
        Assert.Equal("Ctrl+Alt", cfg.Hotkey.Modifiers); // default
    }
}
