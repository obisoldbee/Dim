using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;
using Moq;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

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
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Away);

        power.Verify(p => p.SetVideoIdle(scheme, 60, 60), Times.Once); // 1min * 60
        Assert.Equal(AppMode.Away, svc.CurrentMode);
    }

    [Fact]
    public void SwitchTo_Work_AppliesWorkTimeouts_WithZeroForNever()
    {
        var cfg = Cfg();
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Work);

        power.Verify(p => p.SetVideoIdle(scheme, 0, 1800), Times.Once); // 0 + 30min
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
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(Guid.NewGuid());
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
        Assert.Equal(AppMode.Work, svc.MatchCurrentMode(0, 1800));
    }

    [Fact]
    public void MatchCurrentMode_MatchesAway_ReturnsAway()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Away: AC=1min→60s, DC=1min→60s
        Assert.Equal(AppMode.Away, svc.MatchCurrentMode(60, 60));
    }

    [Fact]
    public void MatchCurrentMode_NeitherMatches_ReturnsUnknown()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        Assert.Equal(AppMode.Unknown, svc.MatchCurrentMode(300, 300));
    }

    [Fact]
    public void ReapplyCurrentMode_ReappliesCurrentTimeouts()
    {
        var cfg = Cfg();
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);
        svc.SwitchTo(AppMode.Work);
        power.Invocations.Clear();

        svc.ReapplyCurrentMode();

        power.Verify(p => p.SetVideoIdle(scheme, 0, 1800), Times.Once);
    }
}
