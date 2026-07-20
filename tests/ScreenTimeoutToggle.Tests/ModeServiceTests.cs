using OBDim.Models;
using OBDim.Services;
using Moq;
using Xunit;

namespace OBDim.Tests;

public class ModeServiceTests
{
    private static AppConfig Cfg() => new AppConfig
    {
        Work = new TimeoutConfig { AcMinutes = 0, DcMinutes = 30 },
        Away = new TimeoutConfig { AcMinutes = 1, DcMinutes = 1 }
    };

    [Fact]
    public void SwitchTo_Away_AppliesAwayTimeouts()
    {
        var cfg = Cfg();
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Away);

        power.Verify(p => p.SetVideoIdle(60, 60), Times.Once); // 1min * 60
        Assert.Equal(AppMode.Away, svc.CurrentMode);
    }

    [Fact]
    public void SwitchTo_Work_AppliesWorkTimeouts_WithZeroForNever()
    {
        var cfg = Cfg();
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Work);

        power.Verify(p => p.SetVideoIdle(0, 1800), Times.Once); // 0 + 30min
        Assert.Equal(AppMode.Work, svc.CurrentMode);
    }

    [Fact]
    public void SwitchTo_Unknown_Throws()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        Assert.Throws<ArgumentException>(() => svc.SwitchTo(AppMode.Unknown));
    }

    [Fact]
    public void SwitchTo_FiresModeChanged()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);
        AppMode? fired = null;
        svc.ModeChanged += (_, m) => fired = m;

        svc.SwitchTo(AppMode.Away);

        Assert.Equal(AppMode.Away, fired);
    }

    [Fact]
    public void MatchCurrentMode_MatchesWork_ReturnsWork()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Work: AC=0min→0s, DC=30min→1800s
        Assert.Equal(AppMode.Work, svc.MatchCurrentMode(0L, 1800L));
    }

    [Fact]
    public void MatchCurrentMode_MatchesAway_ReturnsAway()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Away: AC=1min→60s, DC=1min→60s
        Assert.Equal(AppMode.Away, svc.MatchCurrentMode(60L, 60L));
    }

    [Fact]
    public void MatchCurrentMode_NeitherMatches_ReturnsUnknown()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        Assert.Equal(AppMode.Unknown, svc.MatchCurrentMode(300L, 300L));
    }

    [Fact]
    public void ReapplyCurrentMode_ReappliesCurrentTimeouts()
    {
        var cfg = Cfg();
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(cfg, power.Object);
        svc.SwitchTo(AppMode.Work);
        power.Invocations.Clear();

        svc.ReapplyCurrentMode();

        power.Verify(p => p.SetVideoIdle(0, 1800), Times.Once);
    }

    /// <summary>
    /// New test: UpdateConfig then SwitchTo should use the new config values.
    /// </summary>
    [Fact]
    public void SwitchTo_UsesUpdatedConfig_AfterUpdateConfig()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        var newCfg = Cfg() with
        {
            Work = new TimeoutConfig { AcMinutes = 10, DcMinutes = 20 }
        };
        svc.UpdateConfig(newCfg);

        svc.SwitchTo(AppMode.Work);

        power.Verify(p => p.SetVideoIdle(600, 1200), Times.Once); // 10min*60, 20min*60
    }

    /// <summary>
    /// New test: ReapplyCurrentMode when Unknown should do nothing.
    /// </summary>
    [Fact]
    public void ReapplyCurrentMode_WhenUnknown_DoesNothing()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);
        // CurrentMode is Unknown by default

        svc.ReapplyCurrentMode();

        power.Verify(p => p.SetVideoIdle(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// New test: MatchCurrentMode with large values (exceeding int.MaxValue).
    /// </summary>
    [Fact]
    public void MatchCurrentMode_HandlesLargeValues()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Large values that don't match any config
        Assert.Equal(AppMode.Unknown, svc.MatchCurrentMode(4294967295L, 0L));
    }
}
