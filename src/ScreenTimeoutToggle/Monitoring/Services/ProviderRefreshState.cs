using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Services;

/// <summary>
/// When a provider may auto-refresh again. Pure logic over injected time so the backoff
/// ladder and manual-refresh gating are unit-testable (spec §7 defaults).
/// </summary>
public sealed class ProviderRefreshState
{
    /// <summary>Network-ish failures back off: 5, 10, 20, then 30 min. A success resets the ladder.</summary>
    public static readonly TimeSpan[] BackoffLadder =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(30),
    ];

    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ManualMinInterval = TimeSpan.FromSeconds(15);

    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>Set after a successful refresh with at least one valid bucket.</summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    public int BackoffIndex { get; set; }

    /// <summary>
    /// Auth/config-classified failures pause auto-refresh; only a settings change or an
    /// explicit user retry resumes it (spec §7).
    /// </summary>
    public bool PausedUntilUserRetry { get; set; }

    /// <summary>In-flight guard — the coordinator checks this before dispatching.</summary>
    public bool InFlight { get; set; }

    public DateTimeOffset? LastManualStartUtc { get; set; }

    public DateTimeOffset? NextAutoAttemptUtc { get; set; }

    public static bool IsPauseableFailure(ProviderErrorKind kind) =>
        kind is ProviderErrorKind.NotSignedIn or ProviderErrorKind.UnsupportedEntry or ProviderErrorKind.CliNotFound;

    /// <summary>
    /// Records an attempt outcome, advancing or resetting the backoff ladder.
    /// <paramref name="autoInterval"/> is the provider's EFFECTIVE auto-refresh cadence
    /// (global default or per-source override); <see cref="TimeSpan.Zero"/> means the
    /// source is manual-only, so a success schedules nothing. Failure backoff keeps its
    /// own ladder — a short user interval must not shrink it, manual-only must not disable it.
    /// </summary>
    public void RecordAttempt(ProviderSnapshot snapshot, DateTimeOffset nowUtc) =>
        RecordAttempt(snapshot, nowUtc, AutoRefreshInterval);

    public void RecordAttempt(ProviderSnapshot snapshot, DateTimeOffset nowUtc, TimeSpan autoInterval)
    {
        LastAttemptUtc = nowUtc;
        InFlight = false;
        if (!snapshot.HasError && snapshot.SucceededAtUtc is not null)
        {
            LastSuccessUtc = snapshot.SucceededAtUtc;
            BackoffIndex = 0;
            PausedUntilUserRetry = false;
            NextAutoAttemptUtc = autoInterval > TimeSpan.Zero ? nowUtc + autoInterval : null;
            return;
        }

        if (IsPauseableFailure(snapshot.Error))
        {
            PausedUntilUserRetry = true;
            NextAutoAttemptUtc = null;
            return;
        }

        var delay = BackoffLadder[Math.Min(BackoffIndex, BackoffLadder.Length - 1)];
        BackoffIndex++;
        NextAutoAttemptUtc = nowUtc + delay;
    }

    public void Reschedule(TimeSpan interval)
    {
        if (PausedUntilUserRetry) return;
        if (interval == TimeSpan.Zero) { NextAutoAttemptUtc = null; return; }
        if (LastAttemptUtc is { } last)
        {
            var delay = BackoffIndex > 0
                ? BackoffLadder[Math.Min(BackoffIndex - 1, BackoffLadder.Length - 1)] : interval;
            NextAutoAttemptUtc = last + delay;
        }
    }

    public void ResetPause()
    {
        PausedUntilUserRetry = false;
        NextAutoAttemptUtc = null; // next tick decides (immediate for user retry)
    }

    /// <summary>True when an automatic refresh may start now.</summary>
    public bool IsDueForAutoRefresh(DateTimeOffset nowUtc) =>
        !InFlight &&
        !PausedUntilUserRetry &&
        NextAutoAttemptUtc.HasValue &&
        nowUtc >= NextAutoAttemptUtc.Value;

    /// <summary>
    /// True when a user-triggered refresh may start: at least <see cref="ManualMinInterval"/>
    /// since the last manual start, never stacking on an in-flight one, and it may bypass
    /// backoff and pause (user intent wins).
    /// </summary>
    public bool IsManualRefreshAllowed(DateTimeOffset nowUtc) =>
        !InFlight &&
        (LastManualStartUtc is null || nowUtc - LastManualStartUtc.Value >= ManualMinInterval);

    public void RecordManualStart(DateTimeOffset nowUtc)
    {
        LastManualStartUtc = nowUtc;
    }
}
