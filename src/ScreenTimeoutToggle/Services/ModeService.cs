using OBDim.Models;

namespace OBDim.Services;

public class ModeService
{
    // volatile ensures reference visibility across threads (UI + Task.Run)
    private volatile AppConfig _config;
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

    /// <summary>
    /// Switches to the target mode by applying its AC/DC timeout values via powercfg.
    /// </summary>
    public void SwitchTo(AppMode target)
    {
        if (target == AppMode.Unknown)
            throw new ArgumentException("Cannot switch to Unknown mode", nameof(target));

        var (acMin, dcMin) = target == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        // No scheme parameter — PowerConfigService uses SCHEME_CURRENT internally
        _power.SetVideoIdle(acMin * 60, dcMin * 60);

        CurrentMode = target;
        ModeChanged?.Invoke(this, target);
    }

    /// <summary>
    /// Matches the current system VIDEOIDLE values against Work/Away configs.
    /// Uses long to handle system values exceeding int.MaxValue.
    /// </summary>
    public AppMode MatchCurrentMode(long acSeconds, long dcSeconds)
    {
        if (acSeconds == (long)_config.Work.AcMinutes * 60 && dcSeconds == (long)_config.Work.DcMinutes * 60)
            return AppMode.Work;
        if (acSeconds == (long)_config.Away.AcMinutes * 60 && dcSeconds == (long)_config.Away.DcMinutes * 60)
            return AppMode.Away;
        return AppMode.Unknown;
    }

    /// <summary>
    /// Re-applies the current mode's timeout values.
    /// No-op if CurrentMode is Unknown.
    /// </summary>
    public void ReapplyCurrentMode()
    {
        if (CurrentMode == AppMode.Unknown) return;
        var (acMin, dcMin) = CurrentMode == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        _power.SetVideoIdle(acMin * 60, dcMin * 60);
    }
}
