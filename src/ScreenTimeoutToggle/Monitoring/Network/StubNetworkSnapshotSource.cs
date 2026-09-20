using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network;

/// <summary>
/// Test/demo snapshot source. It is NOT registered anywhere in production code — the
/// production host wires <see cref="NetworkMonitorService"/>; this stub exists so tests
/// and UI bring-up can drive every coverage state deterministically. It never fabricates
/// data on its own: it only republishes snapshots handed to it (fixtures keep
/// origin=fixture; production rejects those, contract §3.1).
/// </summary>
public sealed class StubNetworkSnapshotSource : INetworkSnapshotSource
{
    public StubNetworkSnapshotSource(NetworkSnapshot initial)
    {
        Current = initial;
    }

    public NetworkSnapshot Current { get; private set; }

    public bool IsRunning { get; private set; }

    public event Action<NetworkSnapshot>? SnapshotUpdated;

    /// <summary>Replaces Current wholesale and notifies subscribers (complete-replacement semantics).</summary>
    public void Publish(NetworkSnapshot next)
    {
        Current = next;
        SnapshotUpdated?.Invoke(next);
    }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;
}
