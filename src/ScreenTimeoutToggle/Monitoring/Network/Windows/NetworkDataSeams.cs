using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>Outcome of one raw table/counter read.</summary>
public enum NetworkReadStatus
{
    Ok,
    /// <summary>The OS refused the read (win32 ACCESS_DENIED). Maps to coverage=denied.</summary>
    AccessDenied,
    /// <summary>The read failed for any other reason. Maps to partial + the matching reason code.</summary>
    Failed,
}

/// <summary>
/// One interface with cumulative byte counters (dual-family, decimal bytes). A null
/// counter means the driver reported no usable value (some virtual adapters return
/// 0xFFFF...FFFF) — unknown is null, never a garbage number.
/// </summary>
public sealed record InterfaceCounterRow(
    string Id,
    string Name,
    InterfaceKind Kind,
    ulong? RxBytes,
    ulong? TxBytes, bool IsUp = true);

/// <summary>
/// One row of the PID-annotated connection table. UDP rows carry LOCAL endpoints only
/// (the table has no remote side); they are capability evidence, not connections.
/// </summary>
public sealed record ConnectionRow(
    NetworkProtocol Protocol,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int? RemotePort,
    string State,
    int Pid);

/// <summary>Resolved process instance identity. StartTime is UTC Unix ms.</summary>
public sealed record ProcessIdentityInfo(
    int Pid,
    long StartTimeMs,
    string Name,
    int? ParentPid,
    string? ExecutablePath);

/// <summary>Seam over the interface counter source (GetIfTable2 in production, fakes in tests).</summary>
public interface IInterfaceCounterTable
{
    NetworkReadStatus TryRead(out IReadOnlyList<InterfaceCounterRow> rows, out string? error);
}

/// <summary>Seam over the PID-annotated TCP/UDP connection tables (GetExtended*Table in production).</summary>
public interface IConnectionTable
{
    /// <summary>
    /// Reads the TCP tables of both address families. On a partial read (one family
    /// failed) returns Ok with the available rows and a non-null error describing the gap.
    /// </summary>
    NetworkReadStatus TryReadTcp(out IReadOnlyList<ConnectionRow> rows, out string? error);

    /// <summary>Capability probe for the UDP tables. UDP rows are local-endpoint-only evidence.</summary>
    NetworkReadStatus TryReadUdp(out IReadOnlyList<ConnectionRow> rows, out string? error);
}

/// <summary>
/// Seam over process identity resolution (contract §6.2). Implementations follow the
/// degradation chain Process API → NtQuerySystemInformation → unresolved; a null return
/// means the identity cannot be resolved and the caller synthesizes an honest fallback.
/// </summary>
public interface IProcessIdentityProvider
{
    /// <summary>Prepares per-cycle caches (one call per collect cycle).</summary>
    void BeginCycle();

    /// <summary>Resolves one pid. Null = unresolvable (stale row, gone process, all tiers failed).</summary>
    ProcessIdentityInfo? Resolve(int pid);
}
