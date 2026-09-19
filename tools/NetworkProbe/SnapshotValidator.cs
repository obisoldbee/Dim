using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBDim.NetworkProbe;

/// <summary>
/// Executable check for the frozen network snapshot contract v1
/// (contracts/network/v1/README.md + network-snapshot.v1.schema.json).
/// Implements the schema-level constraints (required/type/enum/range/length/pattern/
/// additionalProperties=false) plus semantic rules S1-S8 that JSON Schema cannot express.
/// Exit 0 = valid, 3 = invalid, 1 = unreadable input.
/// </summary>
internal static class SnapshotValidator
{
    private const long MaxSafeInt = 9007199254740991L;
    private const long MaxMessageBytes = 16777216; // S8: 16 MiB
    private const int MaxTotalRecords = 100000;    // S7

    private static readonly string[] CoverageValues =
        ["active", "partial", "stopped", "starting", "denied", "disconnected"];

    private static readonly string[] ReasonValues =
        ["capture-initializing", "user-disabled", "permission-denied", "host-unreachable",
         "helper-crashed", "etw-unavailable", "pid-table-partial", "interface-counters-unavailable",
         "proxy-correlation-unavailable", "domain-resolution-unavailable"];

    private static readonly Regex AppIdPattern = new(@"^[a-z0-9][a-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex ConnIdPattern = new(@"^[A-Za-z0-9._:/-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex IfaceIdPattern = new(@"^[A-Za-z0-9._:{}-]{1,128}$", RegexOptions.Compiled);
    private static readonly Regex IdentitySourcePattern = new(@"^[a-z0-9-]{1,64}$", RegexOptions.Compiled);

    public static int Run(string path)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{path}: unreadable: {ex.Message}");
            return 1;
        }

        var errors = Validate(raw);
        if (errors.Count == 0)
        {
            Console.WriteLine($"{Path.GetFileName(path)}: VALID");
            return 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: INVALID ({errors.Count} violation(s))");
        foreach (var e in errors.Take(10))
            Console.WriteLine($"  {e}");
        return 3;
    }

    internal static List<string> Validate(byte[] raw)
    {
        var errors = new List<string>();
        void Fail(string rule, string detail) => errors.Add($"{rule}: {detail}");

        if (raw.LongLength > MaxMessageBytes) // S8
            Fail("S8", $"message {raw.LongLength} bytes exceeds {MaxMessageBytes}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            Fail("JSON", $"parse failed: {ex.Message}");
            return errors;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail("SCHEMA", "root is not an object");
                return errors;
            }

            string[] rootKeys =
                ["schemaVersion", "origin", "asOf", "captureStartedAt", "epoch", "sequence",
                 "coverage", "coverageReasons", "capabilities", "droppedEvents", "truncated",
                 "apps", "interfaces"];
            CheckExactKeys(root, rootKeys, "root", errors);

            if (!CheckConstInt(root, "schemaVersion", 1, "root", errors)) { /* reported */ }
            CheckEnum(root, "origin", ["host", "fixture"], "root", errors);
            var asOf = CheckUnixMs(root, "asOf", "root", errors);
            var captureStartedAt = CheckUnixMs(root, "captureStartedAt", "root", errors);
            CheckSafeInt(root, "epoch", "root", errors);
            CheckSafeInt(root, "sequence", "root", errors);
            var coverage = CheckEnum(root, "coverage", CoverageValues, "root", errors);
            var reasons = CheckStringEnumArray(root, "coverageReasons", ReasonValues, "root", errors);
            CheckCapabilities(root, errors);
            CheckNullableCounter(root, "droppedEvents", "root", errors);
            CheckBool(root, "truncated", "root", errors);

            if (asOf >= 0 && captureStartedAt >= 0 && captureStartedAt > asOf) // S4
                Fail("S4", "captureStartedAt > asOf");

            var nonData = coverage is "stopped" or "starting" or "denied" or "disconnected";
            if (coverage == "active" && reasons.Count != 0) // S6
                Fail("S6", "coverage=active but coverageReasons is not empty");
            if (coverage is not null && coverage != "active" && reasons.Count == 0)
                Fail("S6", $"coverage={coverage} but coverageReasons is empty");

            var totalRecords = 0L;
            var appIds = new HashSet<string>();
            if (TryGet(root, "apps", out var apps))
            {
                if (apps.ValueKind != JsonValueKind.Array)
                    Fail("SCHEMA", "root.apps is not an array");
                else
                {
                    if (apps.GetArrayLength() > 2000)
                        Fail("SCHEMA", "apps exceeds 2000");
                    if (nonData && apps.GetArrayLength() != 0) // S5
                        Fail("S5", $"coverage={coverage} (non-data) but apps is not empty — unknown must not be faked");
                    var i = 0;
                    foreach (var app in apps.EnumerateArray())
                        totalRecords += ValidateApp(app, $"apps[{i++}]", asOf, appIds, errors);
                }
            }

            var ifaceIds = new HashSet<string>();
            if (TryGet(root, "interfaces", out var ifaces))
            {
                if (ifaces.ValueKind != JsonValueKind.Array)
                    Fail("SCHEMA", "root.interfaces is not an array");
                else
                {
                    if (ifaces.GetArrayLength() > 128)
                        Fail("SCHEMA", "interfaces exceeds 128");
                    if (nonData && ifaces.GetArrayLength() != 0) // S5
                        Fail("S5", $"coverage={coverage} (non-data) but interfaces is not empty");
                    var i = 0;
                    foreach (var iface in ifaces.EnumerateArray())
                        totalRecords += ValidateInterface(iface, $"interfaces[{i++}]", asOf, ifaceIds, errors);
                }
            }

            if (totalRecords > MaxTotalRecords) // S7
                Fail("S7", $"total records {totalRecords} exceeds {MaxTotalRecords}");
        }

        return errors;
    }

    private static long ValidateApp(
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
        CheckExactKeys(app, keys, path, errors);

        var id = CheckPatternString(app, "id", AppIdPattern, path, errors);
        if (id is not null && !appIds.Add(id)) // S1
            Fail("S1", $"duplicate app id '{id}'");
        CheckString(app, "name", 512, path, errors, minLength: 1);
        CheckNullableString(app, "bundleId", 512, path, errors);
        CheckNullableString(app, "path", 1024, path, errors);
        CheckPatternString(app, "identitySource", IdentitySourcePattern, path, errors);
        CheckNullableCounter(app, "uploadBytes", path, errors);
        CheckNullableCounter(app, "downloadBytes", path, errors);
        CheckNullableRate(app, "upRate", path, errors);
        CheckNullableRate(app, "downRate", path, errors);
        CheckNullableCounter(app, "connectionCount", path, errors);
        CheckSafeInt(app, "epoch", path, errors);

        long records = 0;
        var connIds = new HashSet<string>();
        if (TryGet(app, "connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
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
        else if (TryGet(app, "connections", out _))
            Fail("SCHEMA", "connections is not an array");

        records += ValidateHistory(app, path, asOf, errors);

        var procKeys = new HashSet<string>();
        if (TryGet(app, "processes", out var procs) && procs.ValueKind == JsonValueKind.Array)
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
        else if (TryGet(app, "processes", out _))
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
        CheckExactKeys(conn, keys, path, errors);

        var id = CheckPatternString(conn, "id", ConnIdPattern, path, errors);
        if (id is not null && !connIds.Add(id)) // S1
            Fail("S1", $"duplicate connection id '{id}'");
        CheckUnixMs(conn, "startedAt", path, errors);
        CheckEnum(conn, "direction", ["up", "down"], path, errors);
        CheckEnum(conn, "initiator", ["local", "remote", "unknown"], path, errors);
        var hostname = CheckNullableString(conn, "hostname", 253, path, errors);
        CheckNullableString(conn, "ip", 45, path, errors);
        CheckIntRange(conn, "port", 1, 65535, path, errors);
        CheckEnum(conn, "protocol", ["TCP", "UDP"], path, errors);
        CheckNullableCounter(conn, "bytes", path, errors);
        var domainSource = CheckEnum(conn, "domainSource", ["system", "proxy", "unknown"], path, errors);
        CheckString(conn, "route", 256, path, errors);
        var proxyFlowId = CheckNullableString(conn, "proxyFlowId", 128, path, errors);
        CheckString(conn, "state", 64, path, errors);

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
        CheckExactKeys(proc, keys, path, errors);

        var pid = CheckSafeInt(proc, "pid", path, errors);
        var startTime = CheckUnixMs(proc, "startTime", path, errors);
        CheckString(proc, "name", 256, path, errors, minLength: 1);
        CheckNullableCounter(proc, "parentPid", path, errors);
        CheckNullableString(proc, "executablePath", 1024, path, errors);

        if (pid >= 0 && startTime >= 0 && !procKeys.Add($"{pid}:{startTime}")) // S1
            Fail("S1", $"duplicate process identity (pid={pid}, startTime={startTime})");
    }

    private static long ValidateInterface(
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
        CheckExactKeys(iface, keys, path, errors);

        var id = CheckPatternString(iface, "id", IfaceIdPattern, path, errors);
        if (id is not null && !ifaceIds.Add(id)) // S1
            Fail("S1", $"duplicate interface id '{id}'");
        CheckString(iface, "name", 256, path, errors, minLength: 1);
        CheckEnum(iface, "kind", ["physical", "tunnel", "loopback", "unknown"], path, errors);
        CheckNullableRate(iface, "upRate", path, errors);
        CheckNullableRate(iface, "downRate", path, errors);
        CheckNullableCounter(iface, "rxBytes", path, errors);
        CheckNullableCounter(iface, "txBytes", path, errors);
        CheckSafeInt(iface, "epoch", path, errors);
        return ValidateHistory(iface, path, asOf, errors);
    }

    private static long ValidateHistory(JsonElement owner, string path, long asOf, List<string> errors)
    {
        if (!TryGet(owner, "history", out var history))
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
            CheckExactKeys(point, ["t", "up", "down"], p, errors);
            var t = CheckUnixMs(point, "t", p, errors);
            // up/down independently nullable: the N0 ruling, checked per field.
            CheckNullableRate(point, "up", p, errors);
            CheckNullableRate(point, "down", p, errors);
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

    // ---- primitive field checkers (schema-level parity) ----

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }

    private static void CheckExactKeys(
        JsonElement obj, string[] expected, string path, List<string> errors)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            present.Add(prop.Name);
            if (!expected.Contains(prop.Name, StringComparer.Ordinal))
                errors.Add($"SCHEMA: {path}: additional property '{prop.Name}' not allowed");
        }
        foreach (var key in expected)
            if (!present.Contains(key))
                errors.Add($"SCHEMA: {path}: required property '{key}' missing");
    }

    private static bool CheckConstInt(
        JsonElement obj, string name, long expected, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind != JsonValueKind.Number ||
            !el.TryGetInt64(out var v) || v != expected)
        {
            errors.Add($"SCHEMA: {path}.{name}: must be the integer {expected}");
            return false;
        }
        return true;
    }

    private static string? CheckEnum(
        JsonElement obj, string name, string[] values, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            if (TryGet(obj, name, out _))
                errors.Add($"SCHEMA: {path}.{name}: not a string");
            return null;
        }
        var v = el.GetString()!;
        if (!values.Contains(v, StringComparer.Ordinal))
            errors.Add($"SCHEMA: {path}.{name}: '{v}' not in [{string.Join(", ", values)}]");
        return v;
    }

    private static List<string> CheckStringEnumArray(
        JsonElement obj, string name, string[] values, string path, List<string> errors)
    {
        var result = new List<string>();
        if (!TryGet(obj, name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            if (TryGet(obj, name, out _))
                errors.Add($"SCHEMA: {path}.{name}: not an array");
            return result;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"SCHEMA: {path}.{name}: non-string element");
                continue;
            }
            var v = item.GetString()!;
            if (!values.Contains(v, StringComparer.Ordinal))
                errors.Add($"SCHEMA: {path}.{name}: '{v}' not an allowed reason code");
            if (!seen.Add(v))
                errors.Add($"SCHEMA: {path}.{name}: duplicate reason '{v}'");
            result.Add(v);
        }
        return result;
    }

    private static long CheckUnixMs(JsonElement obj, string name, string path, List<string> errors) =>
        CheckInteger(obj, name, 0, MaxSafeInt, path, errors);

    private static long CheckSafeInt(JsonElement obj, string name, string path, List<string> errors) =>
        CheckInteger(obj, name, 0, MaxSafeInt, path, errors);

    private static long CheckIntRange(
        JsonElement obj, string name, long min, long max, string path, List<string> errors) =>
        CheckInteger(obj, name, min, max, path, errors);

    private static long CheckInteger(
        JsonElement obj, string name, long min, long max, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el))
            return -1;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v) || v < min || v > max)
        {
            errors.Add($"SCHEMA: {path}.{name}: not an integer in [{min}, {max}]");
            return -1;
        }
        return v;
    }

    private static void CheckNullableCounter(
        JsonElement obj, string name, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v) || v < 0 || v > MaxSafeInt)
            errors.Add($"SCHEMA: {path}.{name}: must be null or an integer in [0, {MaxSafeInt}]");
    }

    private static void CheckNullableRate(JsonElement obj, string name, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v) ||
            !double.IsFinite(v) || v < 0 || v > MaxSafeInt)
            errors.Add($"SCHEMA: {path}.{name}: must be null or a finite number in [0, {MaxSafeInt}]");
    }

    private static void CheckBool(JsonElement obj, string name, string path, List<string> errors)
    {
        if (TryGet(obj, name, out var el) &&
            el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add($"SCHEMA: {path}.{name}: not a boolean");
    }

    private static string? CheckString(
        JsonElement obj, string name, int maxLength, string path, List<string> errors, int minLength = 0)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            if (TryGet(obj, name, out _))
                errors.Add($"SCHEMA: {path}.{name}: not a string");
            return null;
        }
        var v = el.GetString()!;
        if (v.Length < minLength || v.Length > maxLength)
            errors.Add($"SCHEMA: {path}.{name}: length {v.Length} outside [{minLength}, {maxLength}]");
        return v;
    }

    private static string? CheckNullableString(
        JsonElement obj, string name, int maxLength, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        return CheckString(obj, name, maxLength, path, errors);
    }

    private static string? CheckPatternString(
        JsonElement obj, string name, Regex pattern, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            if (TryGet(obj, name, out _))
                errors.Add($"SCHEMA: {path}.{name}: not a string");
            return null;
        }
        var v = el.GetString()!;
        if (!pattern.IsMatch(v))
            errors.Add($"SCHEMA: {path}.{name}: '{v}' violates pattern {pattern}");
        return v;
    }

    private static void CheckCapabilities(JsonElement root, List<string> errors)
    {
        if (!TryGet(root, "capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
        {
            if (TryGet(root, "capabilities", out _))
                errors.Add("SCHEMA: root.capabilities: not an object");
            return;
        }
        CheckExactKeys(caps, ["observe", "block", "terminate", "permissions"], "root.capabilities", errors);
        foreach (var key in new[] { "observe", "block", "terminate", "permissions" })
            CheckBool(caps, key, "root.capabilities", errors);
    }
}
