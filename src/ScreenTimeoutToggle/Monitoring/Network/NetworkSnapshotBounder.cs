using OBDim.Monitoring.Network.Json;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network;

/// <summary>
/// Message-level safety net for the contract §9 bounds. The collector already applies
/// the stateful bounds (list sizes, history dual limit); this pure pass enforces what
/// the collector cannot see: string length caps, the S7 total-record cap and the S8
/// serialized-byte cap. Every cut sets Truncated=true. Non-data snapshots pass through
/// untouched (their payload is empty by rule S5).
/// </summary>
public static class NetworkSnapshotBounder
{
    private const int MaxTotalRecords = 100000;
    private const long MaxMessageBytes = 16777216; // 16 MiB, S8

    /// <summary>Mutable flag carrier — ref/out parameters cannot be captured by lambdas.</summary>
    private sealed class CutFlag
    {
        public bool Value;
    }

    public static NetworkSnapshot Enforce(NetworkSnapshot snapshot)
    {
        if (snapshot.Coverage is not (NetworkCoverage.Active or NetworkCoverage.Partial))
            return snapshot;

        var cut = new CutFlag { Value = snapshot.Truncated };

        var apps = new List<AppObservation>(snapshot.Apps.Count);
        foreach (var app in snapshot.Apps)
            apps.Add(BoundApp(app, snapshot.AsOf, cut));

        var interfaces = new List<InterfaceObservation>(snapshot.Interfaces.Count);
        foreach (var iface in snapshot.Interfaces)
            interfaces.Add(BoundInterface(iface, snapshot.AsOf, cut));

        if (apps.Count > 2000)
        {
            apps = apps.Take(2000).ToList();
            cut.Value = true;
        }
        if (interfaces.Count > 128)
        {
            interfaces = interfaces.Take(128).ToList();
            cut.Value = true;
        }

        var bounded = snapshot with { Apps = apps, Interfaces = interfaces, Truncated = cut.Value };
        bounded = EnforceTotalRecords(bounded, cut);
        bounded = EnforceMessageBytes(bounded, cut);
        return bounded with { Truncated = cut.Value };
    }

    private static AppObservation BoundApp(AppObservation app, long asOf, CutFlag cut)
    {
        var connections = app.Connections;
        if (connections.Count > 2000)
        {
            connections = connections.Take(2000).ToList();
            cut.Value = true;
        }
        var boundedConnections = new List<ConnectionObservation>(connections.Count);
        foreach (var c in connections)
        {
            boundedConnections.Add(c with
            {
                Id = Cut(c.Id, 128, cut),
                Hostname = CutNullable(c.Hostname, 253, cut),
                Ip = CutNullable(c.Ip, 45, cut),
                Route = Cut(c.Route, 256, cut),
                ProxyFlowId = CutNullable(c.ProxyFlowId, 128, cut),
                State = Cut(c.State, 64, cut),
            });
        }

        var processes = app.Processes;
        if (processes.Count > 256)
        {
            processes = processes.Take(256).ToList();
            cut.Value = true;
        }
        var boundedProcesses = new List<ObservedProcessIdentity>(processes.Count);
        foreach (var p in processes)
        {
            boundedProcesses.Add(p with
            {
                Name = Cut(p.Name, 256, cut),
                ExecutablePath = CutNullable(p.ExecutablePath, 1024, cut),
            });
        }

        return app with
        {
            Id = Cut(app.Id, 128, cut),
            Name = Cut(app.Name, 512, cut),
            BundleId = CutNullable(app.BundleId, 512, cut),
            Path = CutNullable(app.Path, 1024, cut),
            IdentitySource = Cut(app.IdentitySource, 64, cut),
            Connections = boundedConnections,
            History = BoundHistory(app.History, asOf, cut),
            Processes = boundedProcesses,
        };
    }

    private static InterfaceObservation BoundInterface(InterfaceObservation iface, long asOf, CutFlag cut)
    {
        return iface with
        {
            Id = Cut(iface.Id, 128, cut),
            Name = Cut(iface.Name, 256, cut),
            History = BoundHistory(iface.History, asOf, cut),
        };
    }

    private static IReadOnlyList<TrafficPoint> BoundHistory(
        IReadOnlyList<TrafficPoint> history, long asOf, CutFlag cut)
    {
        var cutoff = asOf - 7_200_000;
        var list = new List<TrafficPoint>(history.Count);
        foreach (var point in history)
        {
            if (point.T >= cutoff) list.Add(point);
            else cut.Value = true;
        }
        if (list.Count > 7200)
        {
            list = list.Skip(list.Count - 7200).ToList(); // cut the oldest
            cut.Value = true;
        }
        return list;
    }

    /// <summary>S7: total records (connections + history + processes) ≤ 100000.</summary>
    private static NetworkSnapshot EnforceTotalRecords(NetworkSnapshot snapshot, CutFlag cut)
    {
        var total = 0L;
        foreach (var app in snapshot.Apps)
            total += app.Connections.Count + app.History.Count + app.Processes.Count;
        foreach (var iface in snapshot.Interfaces)
            total += iface.History.Count;
        if (total <= MaxTotalRecords) return snapshot;

        cut.Value = true;
        // Shed the oldest history first (cheapest signal loss), then connection tails.
        var excess = total - MaxTotalRecords;
        var rebuilt = new List<AppObservation>(snapshot.Apps.Count);
        foreach (var app in snapshot.Apps)
        {
            if (excess <= 0 || app.History.Count == 0)
            {
                rebuilt.Add(app);
                continue;
            }
            var drop = (int)Math.Min(excess, app.History.Count);
            excess -= drop;
            rebuilt.Add(app with { History = app.History.Skip(drop).ToList() });
        }
        return snapshot with { Apps = rebuilt, Truncated = true };
    }

    /// <summary>S8: serialized UTF-8 ≤ 16 MiB. Histories are halved until the message fits.</summary>
    private static NetworkSnapshot EnforceMessageBytes(NetworkSnapshot snapshot, CutFlag cut)
    {
        var current = snapshot;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (NetworkSnapshotJson.SerializeToUtf8Bytes(current).LongLength <= MaxMessageBytes)
                return current;
            cut.Value = true;
            current = current with
            {
                Apps = current.Apps.Select(a => a with { History = Halve(a.History) }).ToList(),
                Interfaces = current.Interfaces.Select(i => i with { History = Halve(i.History) }).ToList(),
                Truncated = true,
            };
        }
        // Still too large with emptied histories: drop connection tails (last resort).
        cut.Value = true;
        return current with
        {
            Apps = current.Apps.Select(a => a with { Connections = [] }).ToList(),
            Truncated = true,
        };
    }

    private static IReadOnlyList<TrafficPoint> Halve(IReadOnlyList<TrafficPoint> history) =>
        history.Count <= 1 ? [] : history.Skip(history.Count / 2).ToList();

    private static string Cut(string value, int maxLength, CutFlag cut)
    {
        if (value.Length <= maxLength) return value;
        cut.Value = true;
        return value[..maxLength];
    }

    private static string? CutNullable(string? value, int maxLength, CutFlag cut) =>
        value is null ? null : Cut(value, maxLength, cut);
}
