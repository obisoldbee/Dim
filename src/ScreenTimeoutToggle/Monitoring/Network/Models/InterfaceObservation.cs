namespace OBDim.Monitoring.Network.Models;

/// <summary>
/// Interface-scope observation (contract §6.4). A separate measurement scope from apps:
/// physical, tunnel, loopback and proxy-forwarded traffic are NEVER summed together and
/// interface traffic is never labelled "internet usage" (contract §8.1).
/// </summary>
public sealed record InterfaceObservation
{
    /// <summary>Stable interface instance identity (Windows InterfaceGuid). A changed id is a new interface.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>physical / tunnel / loopback / unknown — unknown when classification evidence is insufficient.</summary>
    public required InterfaceKind Kind { get; init; }

    /// <summary>Transmit rate (local to remote), decimal bytes/second. Null = unknown (first sample, epoch change, counters unreadable).</summary>
    public required double? UpRate { get; init; }

    /// <summary>Receive rate (remote to local), decimal bytes/second. Null = unknown.</summary>
    public required double? DownRate { get; init; }

    /// <summary>Cumulative received bytes within the current interface epoch. Comparable only within the same epoch.</summary>
    public required long? RxBytes { get; init; }

    /// <summary>Cumulative transmitted bytes within the current interface epoch. Comparable only within the same epoch.</summary>
    public required long? TxBytes { get; init; }

    /// <summary>Interface counter-series epoch; increments on counter wrap or re-enumeration (contract §3.3).</summary>
    public required long Epoch { get; init; }

    /// <summary>Strictly increasing timestamps; real gaps stay gaps, never zero-filled (S3).</summary>
    public required IReadOnlyList<TrafficPoint> History { get; init; }
}
