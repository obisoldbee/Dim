namespace OBDim.Monitoring.Network.Models;

/// <summary>
/// App-scope observation (contract §6.1). The id is a STABLE app identity — never a
/// bare PID; it survives process restarts (the app epoch increments instead).
/// All byte/rate/count fields are nullable: unknown is never encoded as 0.
/// </summary>
public sealed record AppObservation
{
    /// <summary>Stable native app identity, e.g. "exe-&lt;sha256 prefix&gt;". Pattern ^[a-z0-9][a-z0-9._:-]{0,127}$.</summary>
    public required string Id { get; init; }

    /// <summary>Real primary display name.</summary>
    public required string Name { get; init; }

    /// <summary>Bundle identifier (macOS). Usually null on Windows.</summary>
    public required string? BundleId { get; init; }

    /// <summary>Executable path, or null when it cannot be obtained.</summary>
    public required string? Path { get; init; }

    /// <summary>How the app id was derived: executable-path / package-family / fallback-name.</summary>
    public required string IdentitySource { get; init; }

    /// <summary>Cumulative upload bytes within the current app epoch. Null = unknown.</summary>
    public required long? UploadBytes { get; init; }

    /// <summary>Cumulative download bytes within the current app epoch. Null = unknown.</summary>
    public required long? DownloadBytes { get; init; }

    /// <summary>Decimal bytes/second. Null = unknown.</summary>
    public required double? UpRate { get; init; }

    /// <summary>Decimal bytes/second. Null = unknown.</summary>
    public required double? DownRate { get; init; }

    /// <summary>Observable connection count. Null = unknown (table unreadable); never report unknown as 0.</summary>
    public required long? ConnectionCount { get; init; }

    /// <summary>App cumulative-series epoch; increments when a process restart resets the app counters.</summary>
    public required long Epoch { get; init; }

    public required IReadOnlyList<ConnectionObservation> Connections { get; init; }

    /// <summary>Strictly increasing timestamps; real gaps stay gaps, never zero-filled (S3).</summary>
    public required IReadOnlyList<TrafficPoint> History { get; init; }

    /// <summary>(pid, startTime) pairs unique within this app (S1).</summary>
    public required IReadOnlyList<ObservedProcessIdentity> Processes { get; init; }
}

/// <summary>
/// One directional connection observation (contract §6.3). A single transport connection
/// yields two observations (up and down) with two distinct ids. Bytes is null when the
/// observation source carries no per-connection byte counter — unknown, not 0.
/// </summary>
public sealed record ConnectionObservation
{
    /// <summary>Stable directional observation id, unique within the owning app (S1).</summary>
    public required string Id { get; init; }

    /// <summary>First time this connection was observed (UTC Unix ms).</summary>
    public required long StartedAt { get; init; }

    public required ConnectionDirection Direction { get; init; }
    public required ConnectionInitiator Initiator { get; init; }

    /// <summary>Resolved hostname; non-null requires DomainSource System|Proxy (S2).</summary>
    public required string? Hostname { get; init; }

    /// <summary>Remote endpoint literal (IPv4 or IPv6), or null when there is none to report.</summary>
    public required string? Ip { get; init; }

    /// <summary>Remote port, 1-65535.</summary>
    public required int Port { get; init; }

    public required NetworkProtocol Protocol { get; init; }

    /// <summary>Observed bytes for this direction (not packets). Null = unknown.</summary>
    public required long? Bytes { get; init; }

    public required DomainSource DomainSource { get; init; }

    /// <summary>Display text; unknown stays "unknown".</summary>
    public required string Route { get; init; }

    /// <summary>Proxy (e.g. Mihomo) correlation id. Only set with real correlation evidence, never guessed.</summary>
    public required string? ProxyFlowId { get; init; }

    /// <summary>Free-form connection state (e.g. ESTABLISHED, TIME_WAIT, active).</summary>
    public required string State { get; init; }
}

/// <summary>
/// Process instance identity (contract §6.2). PIDs are reused: pid + startTime together
/// identify a process instance; attributing connections by PID alone is a contract breach.
/// </summary>
public sealed record ObservedProcessIdentity
{
    public required long Pid { get; init; }

    /// <summary>Process creation time (UTC Unix ms).</summary>
    public required long StartTime { get; init; }

    public required string Name { get; init; }
    public required long? ParentPid { get; init; }
    public required string? ExecutablePath { get; init; }
}
