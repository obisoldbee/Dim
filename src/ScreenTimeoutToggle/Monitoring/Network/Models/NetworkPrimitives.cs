namespace OBDim.Monitoring.Network.Models;

/// <summary>Snapshot provenance. Production builds only ever emit <see cref="Host"/> (contract §3.1).</summary>
public enum SnapshotOrigin
{
    Host,
    Fixture,
}

/// <summary>The six coverage states of contract §4.</summary>
public enum NetworkCoverage
{
    Active,
    Partial,
    Stopped,
    Starting,
    Denied,
    Disconnected,
}

/// <summary>Interface classification of contract §6.4. Unknown when evidence is insufficient — never guessed.</summary>
public enum InterfaceKind
{
    Physical,
    Tunnel,
    Loopback,
    Unknown,
}

/// <summary>Byte direction of one connection observation (contract §6.3): Up = local to remote.</summary>
public enum ConnectionDirection
{
    Up,
    Down,
}

/// <summary>Which side initiated the connection, separated from byte direction. Unknown without evidence.</summary>
public enum ConnectionInitiator
{
    Local,
    Remote,
    Unknown,
}

/// <summary>Hostname provenance (contract §8.2). No evidence means Unknown.</summary>
public enum DomainSource
{
    System,
    Proxy,
    Unknown,
}

public enum NetworkProtocol
{
    Tcp,
    Udp,
}

/// <summary>Machine-readable coverage reason codes (contract §4). String values are the frozen wire form.</summary>
public static class CoverageReason
{
    public const string CaptureInitializing = "capture-initializing";
    public const string UserDisabled = "user-disabled";
    public const string PermissionDenied = "permission-denied";
    public const string HostUnreachable = "host-unreachable";
    public const string HelperCrashed = "helper-crashed";
    public const string EtwUnavailable = "etw-unavailable";
    public const string PidTablePartial = "pid-table-partial";
    public const string InterfaceCountersUnavailable = "interface-counters-unavailable";
    public const string ProxyCorrelationUnavailable = "proxy-correlation-unavailable";
    public const string DomainResolutionUnavailable = "domain-resolution-unavailable";
}

/// <summary>
/// One history sample. Up and Down are INDEPENDENTLY nullable (contract §7 ruling):
/// a point may know one direction and not the other. Null = unknown, never 0.
/// </summary>
public sealed record TrafficPoint
{
    /// <summary>UTC Unix milliseconds.</summary>
    public required long T { get; init; }

    /// <summary>Upload rate in decimal bytes/second. Null = unknown.</summary>
    public required double? Up { get; init; }

    /// <summary>Download rate in decimal bytes/second. Null = unknown.</summary>
    public required double? Down { get; init; }
}

/// <summary>
/// Real capability flags of the capture service (contract §5). During the read-only
/// phase (N2) Block and Terminate are ALWAYS false; the UI disables those entries.
/// </summary>
public sealed record NetworkCapabilities
{
    public required bool Observe { get; init; }
    public required bool Block { get; init; }
    public required bool Terminate { get; init; }
    public required bool Permissions { get; init; }

    /// <summary>The only capabilities shape the read-only phase can produce.</summary>
    public static NetworkCapabilities ReadOnly(bool observe, bool permissions) => new()
    {
        Observe = observe,
        Block = false,
        Terminate = false,
        Permissions = permissions,
    };
}
