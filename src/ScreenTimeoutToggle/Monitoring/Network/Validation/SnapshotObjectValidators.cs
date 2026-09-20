using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBDim.Monitoring.Network.Validation;

/// <summary>Object-level validators for apps, connections, processes, interfaces and history.</summary>
internal static class SnapshotObjectValidators
{
    internal static readonly Regex AppIdPattern = new(@"^[a-z0-9][a-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    internal static readonly Regex ConnIdPattern = new(@"^[A-Za-z0-9._:/-]{1,128}$", RegexOptions.Compiled);
    internal static readonly Regex IfaceIdPattern = new(@"^[A-Za-z0-9._:{}-]{1,128}$", RegexOptions.Compiled);
    internal static readonly Regex IdentitySourcePattern = new(@"^[a-z0-9-]{1,64}$", RegexOptions.Compiled);

    internal static long ValidateApp(
        JsonElement app, string path, long asOf, HashSet<string> appIds, List<string> errors)
    {
        void Fail(string rule, string detail) => errors.Add($"{rule}: {path}: {detail}");
        if (app.ValueKind != JsonValueKind.Object)
        {
            Fail("SCHEMA", "not an object");
            return 0;
        }

        string[] keys =
            ["id", "name", "bundleId", "path", "identitySource", "uploadBytes", "downloadBytes",
             "upRate", "downRate", "connectionCount", "epoch", "connections", "history", "processes"];
        SnapshotFieldChecks.CheckExactKeys(app, keys, path, errors);

        var id = SnapshotFieldChecks.CheckPatternString(app, "id", AppIdPattern, path, errors);
        if (id is not null && !appIds.Add(id)) // S1
            Fail("S1", $"duplicate app id '{id}'");
        SnapshotFieldChecks.CheckString(app, "name", 512, path, errors, minLength: 1);
        SnapshotFieldChecks.CheckNullableString(app, "bundleId", 512, path, errors);
        SnapshotFieldChecks.CheckNullableString(app, "path", 1024, path, errors);
        SnapshotFieldChecks.CheckPatternString(app, "identitySource", IdentitySourcePattern, path, errors);
        SnapshotFieldChecks.CheckNullableCounter(app, "uploadBytes", path, errors);
        SnapshotFieldChecks.CheckNullableCounter(app, "downloadBytes", path, errors);
        SnapshotFieldChecks.CheckNullableRate(app, "upRate", path, errors);
        SnapshotFieldChecks.CheckNullableRate(app, "downRate", path, errors);
        SnapshotFieldChecks.CheckNullableCounter(app, "connectionCount", path, errors);
        SnapshotFieldChecks.CheckSafeInt(app, "epoch", path, errors);

        long records = 0;
        var connIds = new HashSet<string>(StringComparer.Ordinal);
        if (SnapshotFieldChecks.TryGet(app, "connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
        {
            if (conns.GetArrayLength() > 2000)
                Fail("SCHEMA", "connections exceeds 2000");
            var i = 0;
            foreach (var conn in conns.EnumerateArray())
            {
                ValidateConnection(conn, $"{path}.connections[{i++}]", connIds, errors);
                records++;
            }
        }
        else if (SnapshotFieldChecks.TryGet(app, "connections", out _))
            Fail("SCHEMA", "connections is not an array");

        records += ValidateHistory(app, path, asOf, errors);

        var procKeys = new HashSet<string>(StringComparer.Ordinal);
        if (SnapshotFieldChecks.TryGet(app, "processes", out var procs) && procs.ValueKind == JsonValueKind.Array)
        {
            if (procs.GetArrayLength() > 256)
                Fail("SCHEMA", "processes exceeds 256");
            var i = 0;
            foreach (var proc in procs.EnumerateArray())
            {
                ValidateProcess(proc, $"{path}.processes[{i++}]", procKeys, errors);
                records++;
            }
        }
        else if (SnapshotFieldChecks.TryGet(app, "processes", out _))
            Fail("SCHEMA", "processes is not an array");

        return records;
    }

    private static void ValidateConnection(
        JsonElement conn, string path, HashSet<string> connIds, List<string> errors)
    {
        void Fail(string rule, string detail) => errors.Add($"{rule}: {path}: {detail}");
        if (conn.ValueKind != JsonValueKind.Object)
        {
            Fail("SCHEMA", "not an object");
            return;
        }

        string[] keys =
            ["id", "startedAt", "direction", "initiator", "hostname", "ip", "port", "protocol",
             "bytes", "domainSource", "route", "proxyFlowId", "state"];
        SnapshotFieldChecks.CheckExactKeys(conn, keys, path, errors);

        var id = SnapshotFieldChecks.CheckPatternString(conn, "id", ConnIdPattern, path, errors);
        if (id is not null && !connIds.Add(id)) // S1
            Fail("S1", $"duplicate connection id '{id}'");
        SnapshotFieldChecks.CheckUnixMs(conn, "startedAt", path, errors);
        SnapshotFieldChecks.CheckEnum(conn, "direction", ["up", "down"], path, errors);
        SnapshotFieldChecks.CheckEnum(conn, "initiator", ["local", "remote", "unknown"], path, errors);
        var hostname = SnapshotFieldChecks.CheckNullableString(conn, "hostname", 253, path, errors);
        SnapshotFieldChecks.CheckNullableString(conn, "ip", 45, path, errors);
        SnapshotFieldChecks.CheckIntRange(conn, "port", 1, 65535, path, errors);
        SnapshotFieldChecks.CheckEnum(conn, "protocol", ["TCP", "UDP"], path, errors);
        SnapshotFieldChecks.CheckNullableCounter(conn, "bytes", path, errors);
        var domainSource = SnapshotFieldChecks.CheckEnum(conn, "domainSource", ["system", "proxy", "unknown"], path, errors);
        SnapshotFieldChecks.CheckString(conn, "route", 256, path, errors);
        var proxyFlowId = SnapshotFieldChecks.CheckNullableString(conn, "proxyFlowId", 128, path, errors);
        SnapshotFieldChecks.CheckString(conn, "state", 64, path, errors);

        if (hostname is not null && domainSource == "unknown") // S2
            Fail("S2", "hostname present but domainSource=unknown");
        if (proxyFlowId is not null && domainSource is not null && domainSource != "proxy") // S2
            Fail("S2", "proxyFlowId present but domainSource!=proxy");
    }

    private static void ValidateProcess(
        JsonElement proc, string path, HashSet<string> procKeys, List<string> errors)
    {
        void Fail(string rule, string detail) => errors.Add($"{rule}: {path}: {detail}");
        if (proc.ValueKind != JsonValueKind.Object)
        {
            Fail("SCHEMA", "not an object");
            return;
        }

        string[] keys = ["pid", "startTime", "name", "parentPid", "executablePath"];
        SnapshotFieldChecks.CheckExactKeys(proc, keys, path, errors);

        var pid = SnapshotFieldChecks.CheckSafeInt(proc, "pid", path, errors);
        var startTime = SnapshotFieldChecks.CheckUnixMs(proc, "startTime", path, errors);
        SnapshotFieldChecks.CheckString(proc, "name", 256, path, errors, minLength: 1);
        SnapshotFieldChecks.CheckNullableCounter(proc, "parentPid", path, errors);
        SnapshotFieldChecks.CheckNullableString(proc, "executablePath", 1024, path, errors);

        if (pid >= 0 && startTime >= 0 && !procKeys.Add($"{pid}:{startTime}")) // S1
            Fail("S1", $"duplicate process identity (pid={pid}, startTime={startTime})");
    }

    internal static long ValidateInterface(
        JsonElement iface, string path, long asOf, HashSet<string> ifaceIds, List<string> errors)
    {
        void Fail(string rule, string detail) => errors.Add($"{rule}: {path}: {detail}");
        if (iface.ValueKind != JsonValueKind.Object)
        {
            Fail("SCHEMA", "not an object");
            return 0;
        }

        string[] keys =
            ["id", "name", "kind", "upRate", "downRate", "rxBytes", "txBytes", "epoch", "history"];
        SnapshotFieldChecks.CheckExactKeys(iface, keys, path, errors);

        var id = SnapshotFieldChecks.CheckPatternString(iface, "id", IfaceIdPattern, path, errors);
        if (id is not null && !ifaceIds.Add(id)) // S1
            Fail("S1", $"duplicate interface id '{id}'");
        SnapshotFieldChecks.CheckString(iface, "name", 256, path, errors, minLength: 1);
        SnapshotFieldChecks.CheckEnum(iface, "kind", ["physical", "tunnel", "loopback", "unknown"], path, errors);
        SnapshotFieldChecks.CheckNullableRate(iface, "upRate", path, errors);
        SnapshotFieldChecks.CheckNullableRate(iface, "downRate", path, errors);
        SnapshotFieldChecks.CheckNullableCounter(iface, "rxBytes", path, errors);
        SnapshotFieldChecks.CheckNullableCounter(iface, "txBytes", path, errors);
        SnapshotFieldChecks.CheckSafeInt(iface, "epoch", path, errors);
        return ValidateHistory(iface, path, asOf, errors);
    }

    private static long ValidateHistory(JsonElement owner, string path, long asOf, List<string> errors)
    {
        if (!SnapshotFieldChecks.TryGet(owner, "history", out var history))
            return 0;
        if (history.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"SCHEMA: {path}: history is not an array");
            return 0;
        }
        if (history.GetArrayLength() > 7200)
            errors.Add($"SCHEMA: {path}: history exceeds 7200 points");

        long previous = -1;
        var i = 0;
        foreach (var point in history.EnumerateArray())
        {
            var p = $"{path}.history[{i++}]";
            if (point.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"SCHEMA: {p}: not an object");
                continue;
            }
            SnapshotFieldChecks.CheckExactKeys(point, ["t", "up", "down"], p, errors);
            var t = SnapshotFieldChecks.CheckUnixMs(point, "t", p, errors);
            // up/down independently nullable: the N0 ruling, checked per field.
            SnapshotFieldChecks.CheckNullableRate(point, "up", p, errors);
            SnapshotFieldChecks.CheckNullableRate(point, "down", p, errors);
            if (t >= 0)
            {
                if (t <= previous) // S3: strictly increasing
                    errors.Add($"S3: {p}: timestamp {t} is not strictly increasing");
                if (asOf >= 0 && t > asOf)
                    errors.Add($"S3: {p}: timestamp {t} is after asOf");
                previous = t;
            }
        }
        return history.GetArrayLength();
    }
}
