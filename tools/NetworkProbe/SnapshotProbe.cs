using OBDim.Monitoring.Network;
using OBDim.Monitoring.Network.Json;

namespace OBDim.NetworkProbe;

/// <summary>
/// Probe (N2): drives the REAL NetworkMonitorService (Windows readers, no stubs) and
/// prints one live snapshot as contract v1 JSON on stdout — the manual on-machine
/// verification entry. Waits long enough for a second sample so interface rates are
/// populated (the first sample of every epoch is null by design). Pipe the output into
/// `NetworkProbe validate` for the full S1-S8 check. Exit 0 = snapshot produced and
/// self-validated; 1 = the snapshot failed the contract validator.
/// </summary>
internal static class SnapshotProbe
{
    public static int Run()
    {
        using var service = NetworkMonitorService.CreateDefault();
        service.Start();
        // 1 Hz sampling; give the collector a second sample so rates exist.
        Thread.Sleep(2600);
        service.Stop();

        var snapshot = service.LastGoodDataSnapshot ?? service.Current;
        var json = NetworkSnapshotJson.Serialize(snapshot);
        Console.WriteLine(json);

        var errors = OBDim.Monitoring.Network.Validation.NetworkSnapshotValidator.Validate(json);
        foreach (var e in errors.Take(10))
            Console.Error.WriteLine($"contract violation: {e}");
        Console.Error.WriteLine(errors.Count == 0
            ? $"snapshot: coverage={snapshot.Coverage} apps={snapshot.Apps.Count} interfaces={snapshot.Interfaces.Count} VALID"
            : $"snapshot: INVALID ({errors.Count} violation(s))");
        return errors.Count == 0 ? 0 : 1;
    }
}
