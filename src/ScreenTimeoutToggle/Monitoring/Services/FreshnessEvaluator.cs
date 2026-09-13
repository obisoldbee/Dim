using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Services;

/// <summary>
/// Freshness decision for a snapshot or memory sample (spec §7):
/// - quota: stale after 15 min since the last SUCCESS, or once a window's reset point has
///   passed without re-verification.
/// - memory: stale after 30 s without a new sample.
/// Stale data stays visible, clearly marked, and never feeds reminders.
/// </summary>
public static class FreshnessEvaluator
{
    public static readonly TimeSpan QuotaMaxAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MemoryMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>True when the snapshot must be shown as expired (its data can no longer be called current).</summary>
    public static bool IsStale(ProviderSnapshot snapshot, DateTimeOffset nowUtc)
    {
        if (snapshot.SucceededAtUtc is null) return true; // never succeeded → nothing fresh about it
        if (nowUtc - snapshot.SucceededAtUtc.Value > QuotaMaxAge) return true;

        // "窗口重置点已过而未复核": any window whose reset time passed without a newer
        // successful refresh makes the whole snapshot stale — the reset may have changed
        // everything, and pretending otherwise would be fabrication.
        return snapshot.Buckets.SelectMany(b => b.Windows)
            .Any(w => w.ResetsAtUtc.HasValue && w.ResetsAtUtc.Value <= nowUtc);
    }

    public static bool IsStale(MemorySample? sample, DateTimeOffset nowUtc) =>
        sample is null || nowUtc - sample.SampledAtUtc > MemoryMaxAge;
}

/// <summary>One fired quota reminder: which window crossed which remaining-percent level.</summary>
public sealed record QuotaReminderEvent(
    ProviderId Provider,
    string BucketKey,
    string WindowKey,
    int Level,
    double RemainingPercent,
    DateTimeOffset? ResetsAtUtc,
    string ReminderKey);

/// <summary>
/// Quota reminder rules (spec §7, R09):
/// - Default OFF (the caller checks settings before calling).
/// - Levels fire at remaining ≤ 20 % (level 1) and ≤ 5 % (level 2); one refresh that jumps
///   both levels produces ONLY the more severe alert.
/// - Dedupe keys include provider, identity, bucket, window, reset point and level; marks
///   persist across restarts; a new reset point re-arms the window.
/// - Only fresh, valid, identity-verified data with an explicit reset point may alert.
///   Out-of-range percents, expired snapshots and unverified identities never do.
/// </summary>
public static class ReminderEvaluator
{
    public const double Level1RemainingPercent = 20.0;
    public const double Level2RemainingPercent = 5.0;

    /// <summary>
    /// Evaluates one fresh, verified snapshot against the persisted marks.
    /// <paramref name="marks"/> is read AND written: newly fired levels are added so the
    /// caller can persist them.
    /// </summary>
    public static IReadOnlyList<QuotaReminderEvent> Evaluate(
        ProviderSnapshot snapshot, HashSet<string> marks, DateTimeOffset nowUtc)
    {
        if (!snapshot.IdentityVerified) return [];
        if (FreshnessEvaluator.IsStale(snapshot, nowUtc)) return [];

        var events = new List<QuotaReminderEvent>();
        foreach (var bucket in snapshot.Buckets)
        {
            if (bucket.Error is not null) continue;
            foreach (var window in bucket.Windows)
            {
                if (!window.TryGetReminderKey(snapshot.Provider.ToString(), bucket.SourceKey, out var windowKey))
                {
                    continue; // no reset point → panel hint only (spec §7)
                }
                if (window.PercentOutOfRange) continue;
                if (window.RemainingPercent is null) continue;

                var level = window.RemainingPercent switch
                {
                    <= Level2RemainingPercent => 2,
                    <= Level1RemainingPercent => 1,
                    _ => 0,
                };
                if (level == 0) continue;

                // Firing the severe level also silences the mild one: one refresh that
                // crosses both thresholds sends one notification, the worse one.
                var mildKey = $"{windowKey}|level1";
                var severeKey = $"{windowKey}|level2";
                var key = level == 2 ? severeKey : mildKey;
                if (marks.Contains(key)) continue;

                marks.Add(key);
                if (level == 2) marks.Add(mildKey); // the worse event subsumes the milder one
                events.Add(new QuotaReminderEvent(
                    snapshot.Provider, bucket.SourceKey, window.SourceKey, level,
                    window.RemainingPercent.Value, window.ResetsAtUtc, key));
            }
        }

        return events;
    }
}
