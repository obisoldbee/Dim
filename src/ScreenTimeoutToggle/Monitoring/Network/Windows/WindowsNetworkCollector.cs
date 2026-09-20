using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// Combines the interface counter table and the PID-annotated connection table into one
/// bounded, complete-replacement snapshot payload per collect cycle (~1 s, probe report
/// §e: ≈26 ms marginal cost, well inside the budget).
/// <para>
/// Scope honesty (N2 read-only phase): interface rx/tx and rates are REAL; connection
/// observations carry bytes=null and apps carry null byte/rate fields, because the
/// owner-PID table has no per-connection byte counters and ETW belongs to the helper
/// phase. Unknown is null everywhere — never 0. App history stays empty (no app byte
/// series exists yet); interface history is real.
/// </para>
/// <para>
/// Not thread-safe: the owning service serializes collect cycles.
/// </para>
/// </summary>
public sealed class WindowsNetworkCollector
{
    public enum CollectStatus
    {
        Ok,
        /// <summary>A data source was refused by the OS. The service maps this to coverage=denied.</summary>
        AccessDenied,
        /// <summary>An unexpected failure. The service keeps the last-good snapshot.</summary>
        Failed,
    }

    public sealed record CollectPayload(
        long SampleTimeMs,
        IReadOnlyList<AppObservation> Apps,
        IReadOnlyList<InterfaceObservation> Interfaces,
        IReadOnlyList<string> PartialReasons,
        long? DroppedEvents,
        bool Truncated);

    public sealed record CollectResult(CollectStatus Status, CollectPayload? Payload, string? Error)
    {
        public static CollectResult Ok(CollectPayload payload) => new(CollectStatus.Ok, payload, null);
        public static CollectResult Denied(string error) => new(CollectStatus.AccessDenied, null, error);
        public static CollectResult Failed(string error) => new(CollectStatus.Failed, null, error);
    }

    private const string SystemBucketAppId = "unknown-system";

    private readonly Infrastructure.IClock _clock;
    private readonly IInterfaceCounterTable _interfaces;
    private readonly IConnectionTable _connections;
    private readonly IProcessIdentityProvider _identities;
    private readonly NetworkCollectorOptions _limits;

    private readonly Dictionary<string, InterfaceTrackState> _interfaceTracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppTrackState> _appTracks = new(StringComparer.Ordinal);
    private readonly List<string> _lastEmittedAppIds = [];

    public WindowsNetworkCollector(
        IClock clock,
        IInterfaceCounterTable interfaces,
        IConnectionTable connections,
        IProcessIdentityProvider identities,
        NetworkCollectorOptions? limits = null)
    {
        _clock = clock;
        _interfaces = interfaces;
        _connections = connections;
        _identities = identities;
        _limits = limits ?? NetworkCollectorOptions.Default;
    }

    /// <summary>Runs one bounded collect cycle and returns the complete payload for this sample.</summary>
    public CollectResult Collect()
    {
        long nowMs;
        try
        {
            nowMs = NetworkSnapshot.ToUnixMs(_clock.UtcNow);
        }
        catch (Exception ex)
        {
            return CollectResult.Failed($"clock failed: {ex.Message}");
        }

        _identities.BeginCycle();

        var reasons = new List<string>();
        var truncated = false;

        // --- interface scope (real counters) ---
        IReadOnlyList<InterfaceObservation> interfaces;
        var ifaceStatus = _interfaces.TryRead(out var ifaceRows, out var ifaceError);
        if (ifaceStatus == NetworkReadStatus.AccessDenied)
            return CollectResult.Denied(ifaceError ?? "interface counters access denied");
        if (ifaceStatus == NetworkReadStatus.Failed)
        {
            reasons.Add(CoverageReason.InterfaceCountersUnavailable);
            interfaces = []; // unknown is an empty scope here, never zeroed counters
        }
        else
        {
            interfaces = ObserveInterfaces(ifaceRows, nowMs, ref truncated);
        }

        // --- app scope (connection table; UDP tables are capability evidence only) ---
        var udpStatus = _connections.TryReadUdp(out _, out _);
        if (udpStatus == NetworkReadStatus.AccessDenied)
            return CollectResult.Denied("UDP owner-PID table access denied");

        IReadOnlyList<AppObservation> apps;
        var tcpStatus = _connections.TryReadTcp(out var tcpRows, out var tcpError);
        if (tcpStatus == NetworkReadStatus.AccessDenied)
            return CollectResult.Denied(tcpError ?? "TCP owner-PID table access denied");
        if (tcpStatus == NetworkReadStatus.Failed)
        {
            reasons.Add(CoverageReason.PidTablePartial);
            apps = BuildUnknownApps(); // table unreadable: connectionCount=null, never 0
        }
        else
        {
            if (tcpError is not null || udpStatus == NetworkReadStatus.Failed)
                reasons.Add(CoverageReason.PidTablePartial); // one family/udp gap: partial evidence
            apps = ObserveConnections(tcpRows, nowMs, ref truncated);
        }

        // N2 has no raw-event buffer: the polling model provably drops nothing, so 0 is
        // the honest value (fixtures use the same for data states).
        var payload = new CollectPayload(nowMs, apps, interfaces, reasons, DroppedEvents: 0, truncated);
        return CollectResult.Ok(payload);
    }

    private IReadOnlyList<InterfaceObservation> ObserveInterfaces(
        IReadOnlyList<InterfaceCounterRow> rows, long nowMs, ref bool truncated)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<InterfaceObservation>();

        foreach (var row in rows)
        {
            if (!present.Add(row.Id)) continue; // duplicate enumeration row: ignore, never double-count
            if (list.Count >= _limits.MaxInterfaces)
            {
                truncated = true;
                break;
            }
            if (!_interfaceTracks.TryGetValue(row.Id, out var track))
            {
                track = new InterfaceTrackState(row);
                _interfaceTracks[row.Id] = track;
            }
            list.Add(track.Observe(row, nowMs, _limits, out var wasTrimmed));
            truncated |= wasTrimmed;
        }

        foreach (var (id, track) in _interfaceTracks)
            if (!present.Contains(id))
                track.MarkMissing();

        return list;
    }

    private IReadOnlyList<AppObservation> ObserveConnections(
        IReadOnlyList<ConnectionRow> rows, long nowMs, ref bool truncated)
    {
        var seenConnectionKeys = new HashSet<string>(StringComparer.Ordinal);
        var appsThisCycle = new List<AppTrackState>();
        var appLimitHit = false;

        foreach (var row in rows)
        {
            // LISTEN rows are listeners, not connections; rows without a real remote
            // endpoint cannot become a contract connection (port must be 1-65535).
            if (row.RemoteAddress is null || row.RemotePort is null or <= 0) continue;
            if (row.State is "LISTEN" or "CLOSED") continue;

            var identity = ResolveIdentity(row.Pid, nowMs);
            var (appId, identitySource) = row.Pid is 0 or 4
                ? (SystemBucketAppId, "fallback-name") // kernel pids: excluded from per-app attribution
                : AppTrackState.DeriveAppId(identity, $"unknown.exe (PID {row.Pid})");

            if (!_appTracks.TryGetValue(appId, out var app))
            {
                if (appsThisCycle.Count >= _limits.MaxApps)
                {
                    appLimitHit = true;
                    continue;
                }
                var name = row.Pid is 0 or 4
                    ? "Unknown (system process)"
                    : identity?.Name ?? $"unknown.exe (PID {row.Pid})";
                app = new AppTrackState(appId, name, identity?.ExecutablePath, identitySource);
                _appTracks[appId] = app;
            }

            if (identity is not null)
                app.RegisterProcess(identity);

            var key = $"{appId}|{(row.Protocol == NetworkProtocol.Tcp ? "tcp" : "udp")}|" +
                      $"{row.LocalAddress}:{row.LocalPort}|{row.RemoteAddress}:{row.RemotePort}";
            var conn = app.TrackConnection(
                key, row.LocalAddress, row.LocalPort,
                row.RemoteAddress, row.RemotePort.Value, row.Protocol, nowMs);
            conn.State = row.State;
            seenConnectionKeys.Add(key);

            if (!appsThisCycle.Contains(app))
                appsThisCycle.Add(app);
        }

        foreach (var app in _appTracks.Values)
            app.SettleEndedConnections(seenConnectionKeys);

        truncated |= appLimitHit;
        _lastEmittedAppIds.Clear();
        var list = new List<AppObservation>();
        foreach (var app in appsThisCycle)
        {
            if (!app.HasLiveConnections) continue;
            list.Add(BuildAppObservation(app, nowMs, connectionCount: null, ref truncated));
            _lastEmittedAppIds.Add(app.Id);
        }
        return list;
    }

    /// <summary>
    /// The connection table is unreadable: every previously seen app is reported with all
    /// observation fields null (connectionCount=null — unknown is never reported as 0).
    /// </summary>
    private IReadOnlyList<AppObservation> BuildUnknownApps()
    {
        var list = new List<AppObservation>();
        foreach (var id in _lastEmittedAppIds)
        {
            if (list.Count >= _limits.MaxApps) break;
            if (!_appTracks.TryGetValue(id, out var app)) continue;
            list.Add(new AppObservation
            {
                Id = app.Id,
                Name = app.Name,
                BundleId = null,
                Path = app.Path,
                IdentitySource = app.IdentitySource,
                UploadBytes = null,
                DownloadBytes = null,
                UpRate = null,
                DownRate = null,
                ConnectionCount = null,
                Epoch = app.Epoch,
                Connections = [],
                History = [], // no app byte series in the read-only phase
                Processes = app.BuildProcessList(_limits.MaxProcessesPerApp, out _),
            });
        }
        return list;
    }

    private AppObservation BuildAppObservation(
        AppTrackState app, long nowMs, long? connectionCount, ref bool truncated)
    {
        var connections = app.BuildConnectionObservations(_limits.MaxConnectionsPerApp, out var connCut);
        var processes = app.BuildProcessList(_limits.MaxProcessesPerApp, out var procCut);
        truncated |= connCut || procCut;
        return new AppObservation
        {
            Id = app.Id,
            Name = app.Name,
            BundleId = null, // Windows has no bundle id (contract §6.1)
            Path = app.Path,
            IdentitySource = app.IdentitySource,
            UploadBytes = null,   // no per-app byte source in the read-only phase (ETW is helper scope)
            DownloadBytes = null, // unknown, never 0
            UpRate = null,
            DownRate = null,
            ConnectionCount = connectionCount ?? connections.Count,
            Epoch = app.Epoch,
            Connections = connections,
            History = [], // honest absence: no app byte series exists yet
            Processes = processes,
        };
    }

    /// <summary>
    /// Synthetic identities for unresolvable pids, stable across cycles: without
    /// startTime evidence, re-minting one each cycle would fake a PID reuse every second.
    /// </summary>
    private readonly Dictionary<int, ProcessIdentityInfo> _syntheticIdentities = [];

    private ProcessIdentityInfo? ResolveIdentity(int pid, long nowMs)
    {
        // PID reuse check requires a fresh resolution each cycle; the provider's per-cycle
        // cache keeps the kernel tier at one snapshot per cycle.
        var info = _identities.Resolve(pid);
        if (info is not null) return info;
        // Unresolvable (protected/gone and the kernel tier missed): an honest synthetic
        // identity. startTime = first observation of this pid — a documented lower bound
        // (the process provably existed then); never 0, never fabricated as the epoch start.
        if (!_syntheticIdentities.TryGetValue(pid, out var synthetic))
        {
            var name = pid switch
            {
                0 => "System Idle Process",
                4 => "System",
                _ => $"unknown.exe (PID {pid})",
            };
            synthetic = new ProcessIdentityInfo(pid, nowMs, name, ParentPid: null, ExecutablePath: null);
            _syntheticIdentities[pid] = synthetic;
        }
        return synthetic;
    }
}
