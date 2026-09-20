namespace OBDim.Monitoring.Network.Models;

/// <summary>
/// The frozen network snapshot contract v1 (contracts/network/v1/README.md).
/// All timestamps are UTC Unix milliseconds; all byte counts are decimal bytes;
/// all rates are decimal bytes/second. Unknown is ALWAYS null, never 0.
/// A snapshot is a complete replacement: consumers drop the previous one wholesale
/// (contract §10) — no incremental merge, no re-adding of old cumulative counters.
/// </summary>
public sealed record NetworkSnapshot
{
    public const int ContractSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required SnapshotOrigin Origin { get; init; }

    /// <summary>Sampling cutoff instant of this snapshot (UTC Unix ms).</summary>
    public required long AsOf { get; init; }

    /// <summary>Start of the current capture session (current snapshot epoch).</summary>
    public required long CaptureStartedAt { get; init; }

    /// <summary>Capture-session epoch. Increments on session restart; sequence resets to 0.</summary>
    public required long Epoch { get; init; }

    /// <summary>Monotonic within one snapshot epoch, starting at 0, +1 per snapshot.</summary>
    public required long Sequence { get; init; }

    public required NetworkCoverage Coverage { get; init; }

    /// <summary>Empty iff Coverage = Active; at least one entry otherwise (semantic rule S6).</summary>
    public required IReadOnlyList<string> CoverageReasons { get; init; }

    public required NetworkCapabilities Capabilities { get; init; }

    /// <summary>Raw events dropped in the current epoch. Null = unknown; never encode unknown as 0.</summary>
    public required long? DroppedEvents { get; init; }

    /// <summary>True when any list/history was cut by the bounds of contract §9.</summary>
    public required bool Truncated { get; init; }

    /// <summary>App-scope observations. Never summed with Interfaces (contract §8.1).</summary>
    public required IReadOnlyList<AppObservation> Apps { get; init; }

    /// <summary>Interface-scope observations. Never summed with Apps (contract §8.1).</summary>
    public required IReadOnlyList<InterfaceObservation> Interfaces { get; init; }

    public static long ToUnixMs(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds();

    /// <summary>
    /// Builds a non-data lifecycle snapshot (starting/stopped/denied/disconnected):
    /// apps and interfaces MUST be empty (semantic rule S5) and droppedEvents is null
    /// (nothing is being aggregated in these states). Capabilities follow the frozen
    /// per-state truth table: block/terminate always false.
    /// </summary>
    public static NetworkSnapshot Lifecycle(
        NetworkCoverage coverage,
        string reason,
        long asOfMs,
        long captureStartedAtMs,
        long epoch,
        long sequence)
    {
        if (coverage is NetworkCoverage.Active or NetworkCoverage.Partial)
            throw new ArgumentException("data states require real payloads", nameof(coverage));

        var (observe, permissions) = coverage switch
        {
            NetworkCoverage.Denied => (false, true),
            NetworkCoverage.Disconnected => (false, false),
            _ => (true, true),
        };

        return new NetworkSnapshot
        {
            SchemaVersion = ContractSchemaVersion,
            Origin = SnapshotOrigin.Host,
            AsOf = asOfMs,
            CaptureStartedAt = captureStartedAtMs,
            Epoch = epoch,
            Sequence = sequence,
            Coverage = coverage,
            CoverageReasons = [reason],
            Capabilities = NetworkCapabilities.ReadOnly(observe, permissions),
            DroppedEvents = null,
            Truncated = false,
            Apps = [],
            Interfaces = [],
        };
    }
}
