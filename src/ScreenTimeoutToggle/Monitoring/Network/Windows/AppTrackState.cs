using System.Security.Cryptography;
using System.Text;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>One tracked transport connection (before it is split into up/down observations).</summary>
internal sealed class ConnectionTrackState
{
    public required string Key { get; init; }
    public required string BaseId { get; init; }
    public required long FirstSeenMs { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required string RemoteAddress { get; init; }
    public required int RemotePort { get; init; }
    public required NetworkProtocol Protocol { get; init; }
    public string State { get; set; } = "";
    public long LastSeenMs { get; set; }
}

/// <summary>
/// Per-app aggregation state. The app id is a STABLE identity derived from the
/// normalized executable path (sha256 prefix, identitySource=executable-path) or, when
/// no path evidence exists, from the process name (identitySource=fallback-name). The
/// id never changes once derived — a later-arriving path upgrades the display path, not
/// the id. PID reuse is detected by (pid, startTime) mismatch and increments the app
/// epoch (contract §3.3), because a restarted process resets the app's cumulative view.
/// </summary>
internal sealed class AppTrackState
{
    private readonly Dictionary<int, ProcessIdentityInfo> _processes = [];
    private readonly Dictionary<string, ConnectionTrackState> _connections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedObservationIds = new(StringComparer.Ordinal);

    public AppTrackState(string id, string name, string? path, string identitySource)
    {
        Id = id;
        Name = name;
        Path = path;
        IdentitySource = identitySource;
    }

    public string Id { get; }
    public string Name { get; private set; }
    public string? Path { get; private set; }
    public string IdentitySource { get; }
    public long Epoch { get; private set; }

    public IReadOnlyDictionary<string, ConnectionTrackState> Connections => _connections;

    /// <summary>
    /// Registers the resolved identity of one pid seen this cycle. Returns true when the
    /// pid now maps to a DIFFERENT (pid, startTime) than before — a reused PID, meaning
    /// the process instance restarted and the app epoch increments.
    /// </summary>
    public bool RegisterProcess(ProcessIdentityInfo info)
    {
        if (Path is null && info.ExecutablePath is not null)
            Path = info.ExecutablePath; // display upgrade only; the app id stays stable

        if (_processes.TryGetValue(info.Pid, out var existing))
        {
            if (existing.StartTimeMs == info.StartTimeMs) return false;
            _processes[info.Pid] = info; // PID reuse: same pid, new instance
            Epoch++;
            return true;
        }
        _processes[info.Pid] = info;
        return false;
    }

    /// <summary>Resolves or creates the track for one live connection tuple.</summary>
    public ConnectionTrackState TrackConnection(
        string key, string localAddress, int localPort,
        string remoteAddress, int remotePort, NetworkProtocol protocol, long nowMs)
    {
        if (_connections.TryGetValue(key, out var track))
        {
            track.LastSeenMs = nowMs;
            return track;
        }

        // The id carries the full 5-tuple: parallel connections to the SAME remote:port
        // (different local ports) are distinct observations and must never collide.
        // A tuple reappearing after it ended is a NEW connection identity: the stable
        // observation id gains a first-seen suffix so it can never collide with the
        // settled one (consumers dedupe settled end events by id, contract §6.3).
        var proto = protocol == NetworkProtocol.Tcp ? "tcp" : "udp";
        var baseId = $"{Id}/{proto}/{localAddress}:{localPort}/{remoteAddress}:{remotePort}";
        if (!_usedObservationIds.Add(baseId))
        {
            baseId = $"{baseId}.{nowMs:x}";
            _usedObservationIds.Add(baseId);
        }

        track = new ConnectionTrackState
        {
            Key = key,
            BaseId = baseId,
            FirstSeenMs = nowMs,
            LocalAddress = localAddress,
            LocalPort = localPort,
            RemoteAddress = remoteAddress,
            RemotePort = remotePort,
            Protocol = protocol,
            LastSeenMs = nowMs,
        };
        _connections[key] = track;
        return track;
    }

    /// <summary>
    /// Settles connections absent from this cycle's table. Settlement is idempotent by
    /// construction: a settled connection leaves the active map exactly once and its ids
    /// stay reserved in <see cref="_usedObservationIds"/> forever, so a repeated end
    /// event for the same id can never be settled twice.
    /// </summary>
    public void SettleEndedConnections(HashSet<string> seenKeys)
    {
        var ended = new List<string>();
        foreach (var key in _connections.Keys)
            if (!seenKeys.Contains(key))
                ended.Add(key);
        foreach (var key in ended)
            _connections.Remove(key);
    }

    public bool HasLiveConnections => _connections.Count > 0;

    /// <summary>
    /// Builds the contract observations: two directional observations per transport
    /// connection (up and down, two ids, contract §6.3). N2 has no per-connection byte
    /// source, so bytes is null — unknown, never 0. Hostname is null with
    /// domainSource=unknown (no evidence), route stays "unknown".
    /// </summary>
    public IReadOnlyList<ConnectionObservation> BuildConnectionObservations(int maxPerApp, out bool truncated)
    {
        var list = new List<ConnectionObservation>();
        truncated = false;
        foreach (var track in _connections.Values)
        {
            foreach (var direction in new[] { ConnectionDirection.Up, ConnectionDirection.Down })
            {
                if (list.Count >= maxPerApp)
                {
                    truncated = true;
                    break;
                }
                var dir = direction == ConnectionDirection.Up ? "up" : "down";
                list.Add(new ConnectionObservation
                {
                    Id = $"{track.BaseId}/{dir}",
                    StartedAt = track.FirstSeenMs,
                    Direction = direction,
                    Initiator = ConnectionInitiator.Unknown, // the table carries no initiator evidence
                    Hostname = null,
                    Ip = track.RemoteAddress,
                    Port = track.RemotePort,
                    Protocol = track.Protocol,
                    Bytes = null, // no per-connection byte counter in the read-only phase
                    DomainSource = DomainSource.Unknown,
                    Route = "unknown",
                    ProxyFlowId = null,
                    State = track.State,
                });
            }
            if (truncated) break;
        }
        return list;
    }

    public IReadOnlyList<ObservedProcessIdentity> BuildProcessList(int maxPerApp, out bool truncated)
    {
        truncated = _processes.Count > maxPerApp;
        return _processes.Values.Take(maxPerApp).Select(p => new ObservedProcessIdentity
        {
            Pid = p.Pid,
            StartTime = p.StartTimeMs,
            Name = p.Name,
            ParentPid = p.ParentPid,
            ExecutablePath = p.ExecutablePath,
        }).ToList();
    }

    /// <summary>Derives the stable app id from the strongest available identity evidence.</summary>
    public static (string Id, string IdentitySource) DeriveAppId(ProcessIdentityInfo? identity, string fallbackName)
    {
        if (identity?.ExecutablePath is { Length: > 0 } path)
            return ($"exe-{Hash16(Normalize(path))}", "executable-path");
        var name = identity?.Name is { Length: > 0 } n ? n : fallbackName;
        return ($"exe-{Hash16(Normalize(name))}", "fallback-name");
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant();

    private static string Hash16(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            builder.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
