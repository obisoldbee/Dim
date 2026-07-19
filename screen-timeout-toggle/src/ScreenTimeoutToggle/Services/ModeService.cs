using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class ModeService
{
    private AppConfig _config;
    private readonly PowerConfigService _power;

    public AppMode CurrentMode { get; private set; }
    public event EventHandler<AppMode>? ModeChanged;

    public ModeService(AppConfig config, PowerConfigService power)
    {
        _config = config;
        _power = power;
        CurrentMode = AppMode.Unknown;
    }

    public void SetCurrentMode(AppMode mode) => CurrentMode = mode;

    public void UpdateConfig(AppConfig newConfig) => _config = newConfig;

    public void SwitchTo(AppMode target)
    {
        if (target == AppMode.Unknown)
            throw new ArgumentException("Cannot switch to Unknown mode", nameof(target));

        var (acMin, dcMin) = target == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);

        CurrentMode = target;
        ModeChanged?.Invoke(this, target);
    }

    public AppMode MatchCurrentMode(int acSeconds, int dcSeconds)
    {
        if (acSeconds == _config.Work.AcMinutes * 60 && dcSeconds == _config.Work.DcMinutes * 60)
            return AppMode.Work;
        if (acSeconds == _config.Away.AcMinutes * 60 && dcSeconds == _config.Away.DcMinutes * 60)
            return AppMode.Away;
        return AppMode.Unknown;
    }

    public void ReapplyCurrentMode()
    {
        if (CurrentMode == AppMode.Unknown) return;
        var (acMin, dcMin) = CurrentMode == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);
    }
}
