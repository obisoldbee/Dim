using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network;

/// <summary>
/// The single consumption point of the network module (contract §10). The UI only ever
/// talks to this interface: it reads the current bounded snapshot and subscribes to
/// <see cref="SnapshotUpdated"/>. Every delivered snapshot is a COMPLETE REPLACEMENT —
/// consumers must drop the previous snapshot wholesale, never merge, never re-add old
/// cumulative counters. Staleness protection: compare (epoch, sequence) and refuse
/// regressions; a snapshot from a different epoch replaces without inheriting anything.
/// </summary>
public interface INetworkSnapshotSource
{
    /// <summary>The most recently published snapshot. Never null; starts as a stopped snapshot.</summary>
    NetworkSnapshot Current { get; }

    /// <summary>True while capture has been requested (Start called, Stop not yet called).</summary>
    bool IsRunning { get; }

    /// <summary>Raised after a new snapshot replaces <see cref="Current"/>. Marshaling to the UI thread is the consumer's job.</summary>
    event Action<NetworkSnapshot>? SnapshotUpdated;

    /// <summary>Requests capture. Idempotent: calling Start twice changes nothing.</summary>
    void Start();

    /// <summary>Stops capture (user-disabled). Idempotent. Closing a window must NOT call this implicitly.</summary>
    void Stop();
}
