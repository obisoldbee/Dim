using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Services;

/// <summary>Three-way merge: untouched draft fields follow live settings; explicit edits win at submit.</summary>
public static class MonitoringSettingsMerge
{
    private static T Pick<T>(T baseline, T draft, T current) =>
        EqualityComparer<T>.Default.Equals(baseline, draft) ? current : draft;

    public static MonitoringSettings Apply(MonitoringSettings baseline, MonitoringSettings draft, MonitoringSettings current) => current with
    {
        NetworkEnabled = Pick(baseline.NetworkEnabled, draft.NetworkEnabled, current.NetworkEnabled),
        MemoryEnabled = Pick(baseline.MemoryEnabled, draft.MemoryEnabled, current.MemoryEnabled),
        RemindersEnabled = Pick(baseline.RemindersEnabled, draft.RemindersEnabled, current.RemindersEnabled),
        LeftClickOpensPopover = Pick(baseline.LeftClickOpensPopover, draft.LeftClickOpensPopover, current.LeftClickOpensPopover),
        PopoverHotkey = Pick(baseline.PopoverHotkey, draft.PopoverHotkey, current.PopoverHotkey),
        RefreshIntervalMinutes = Pick(baseline.RefreshIntervalMinutes, draft.RefreshIntervalMinutes, current.RefreshIntervalMinutes),
        ShowArkAgentPlan = Pick(baseline.ShowArkAgentPlan, draft.ShowArkAgentPlan, current.ShowArkAgentPlan),
        ShowArkCodingPlan = Pick(baseline.ShowArkCodingPlan, draft.ShowArkCodingPlan, current.ShowArkCodingPlan),
        Providers = Enum.GetValues<ProviderId>().Select(id =>
        {
            var b = baseline.Provider(id); var d = draft.Provider(id); var c = current.Provider(id);
            return c with
            {
                Enabled = Pick(b.Enabled, d.Enabled, c.Enabled),
                CliPath = Pick(b.CliPath, d.CliPath, c.CliPath),
                RefreshIntervalMinutes = Pick(b.RefreshIntervalMinutes, d.RefreshIntervalMinutes, c.RefreshIntervalMinutes),
            };
        }).ToArray(),
    };
}
