using System.Text.Json;

namespace OBDim.Monitoring.Network.Validation;

/// <summary>
/// Host-side executable check for the frozen network snapshot contract v1
/// (contracts/network/v1/README.md §11). Implements the schema-level constraints
/// (required/type/enum/range/length/pattern/additionalProperties=false) plus the
/// semantic rules S1-S8 that JSON Schema cannot express. The collector's own output
/// and every fixture are validated through this in tests.
/// </summary>
public static class NetworkSnapshotValidator
{
    internal const long MaxSafeInt = 9007199254740991L;
    internal const long MaxMessageBytes = 16777216; // S8: 16 MiB
    internal const int MaxTotalRecords = 100000;    // S7

    internal static readonly string[] CoverageValues =
        ["active", "partial", "stopped", "starting", "denied", "disconnected"];

    internal static readonly string[] ReasonValues =
        ["capture-initializing", "user-disabled", "permission-denied", "host-unreachable",
         "helper-crashed", "etw-unavailable", "pid-table-partial", "interface-counters-unavailable",
         "proxy-correlation-unavailable", "domain-resolution-unavailable"];

    /// <summary>Returns the list of violations; empty means the snapshot is contract-valid.</summary>
    public static List<string> Validate(byte[] raw)
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
            SnapshotFieldChecks.CheckExactKeys(root, rootKeys, "root", errors);

            SnapshotFieldChecks.CheckConstInt(root, "schemaVersion", 1, "root", errors);
            SnapshotFieldChecks.CheckEnum(root, "origin", ["host", "fixture"], "root", errors);
            var asOf = SnapshotFieldChecks.CheckUnixMs(root, "asOf", "root", errors);
            var captureStartedAt = SnapshotFieldChecks.CheckUnixMs(root, "captureStartedAt", "root", errors);
            SnapshotFieldChecks.CheckSafeInt(root, "epoch", "root", errors);
            SnapshotFieldChecks.CheckSafeInt(root, "sequence", "root", errors);
            var coverage = SnapshotFieldChecks.CheckEnum(root, "coverage", CoverageValues, "root", errors);
            var reasons = SnapshotFieldChecks.CheckStringEnumArray(root, "coverageReasons", ReasonValues, "root", errors);
            CheckCapabilities(root, errors);
            SnapshotFieldChecks.CheckNullableCounter(root, "droppedEvents", "root", errors);
            SnapshotFieldChecks.CheckBool(root, "truncated", "root", errors);

            if (asOf >= 0 && captureStartedAt >= 0 && captureStartedAt > asOf) // S4
                Fail("S4", "captureStartedAt > asOf");

            var nonData = coverage is "stopped" or "starting" or "denied" or "disconnected";
            if (coverage == "active" && reasons.Count != 0) // S6
                Fail("S6", "coverage=active but coverageReasons is not empty");
            if (coverage is not null && coverage != "active" && reasons.Count == 0)
                Fail("S6", $"coverage={coverage} but coverageReasons is empty");

            var totalRecords = 0L;
            var appIds = new HashSet<string>(StringComparer.Ordinal);
            if (SnapshotFieldChecks.TryGet(root, "apps", out var apps))
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
                        totalRecords += SnapshotObjectValidators.ValidateApp(app, $"apps[{i++}]", asOf, appIds, errors);
                }
            }

            var ifaceIds = new HashSet<string>(StringComparer.Ordinal);
            if (SnapshotFieldChecks.TryGet(root, "interfaces", out var ifaces))
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
                        totalRecords += SnapshotObjectValidators.ValidateInterface(iface, $"interfaces[{i++}]", asOf, ifaceIds, errors);
                }
            }

            if (totalRecords > MaxTotalRecords) // S7
                Fail("S7", $"total records {totalRecords} exceeds {MaxTotalRecords}");
        }

        return errors;
    }

    /// <summary>Convenience: validates the UTF-8 JSON text form.</summary>
    public static List<string> Validate(string json) =>
        Validate(System.Text.Encoding.UTF8.GetBytes(json));

    private static void CheckCapabilities(JsonElement root, List<string> errors)
    {
        if (!SnapshotFieldChecks.TryGet(root, "capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
        {
            if (SnapshotFieldChecks.TryGet(root, "capabilities", out _))
                errors.Add("SCHEMA: root.capabilities: not an object");
            return;
        }
        SnapshotFieldChecks.CheckExactKeys(caps, ["observe", "block", "terminate", "permissions"], "root.capabilities", errors);
        foreach (var key in new[] { "observe", "block", "terminate", "permissions" })
            SnapshotFieldChecks.CheckBool(caps, key, "root.capabilities", errors);
    }
}
