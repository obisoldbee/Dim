using System.Text.Json;
using System.Text.RegularExpressions;

namespace OBDim.Monitoring.Network.Validation;

/// <summary>Primitive field checkers mirroring the frozen JSON Schema constraints.</summary>
internal static class SnapshotFieldChecks
{
    internal static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }

    internal static void CheckExactKeys(
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

    internal static bool CheckConstInt(
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

    internal static string? CheckEnum(
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

    internal static List<string> CheckStringEnumArray(
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

    internal static long CheckUnixMs(JsonElement obj, string name, string path, List<string> errors) =>
        CheckInteger(obj, name, 0, NetworkSnapshotValidator.MaxSafeInt, path, errors);

    internal static long CheckSafeInt(JsonElement obj, string name, string path, List<string> errors) =>
        CheckInteger(obj, name, 0, NetworkSnapshotValidator.MaxSafeInt, path, errors);

    internal static long CheckIntRange(
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

    internal static void CheckNullableCounter(
        JsonElement obj, string name, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v) ||
            v < 0 || v > NetworkSnapshotValidator.MaxSafeInt)
            errors.Add($"SCHEMA: {path}.{name}: must be null or an integer in [0, {NetworkSnapshotValidator.MaxSafeInt}]");
    }

    internal static void CheckNullableRate(JsonElement obj, string name, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v) ||
            !double.IsFinite(v) || v < 0 || v > NetworkSnapshotValidator.MaxSafeInt)
            errors.Add($"SCHEMA: {path}.{name}: must be null or a finite number in [0, {NetworkSnapshotValidator.MaxSafeInt}]");
    }

    internal static void CheckBool(JsonElement obj, string name, string path, List<string> errors)
    {
        if (TryGet(obj, name, out var el) &&
            el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add($"SCHEMA: {path}.{name}: not a boolean");
    }

    internal static string? CheckString(
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

    internal static string? CheckNullableString(
        JsonElement obj, string name, int maxLength, string path, List<string> errors)
    {
        if (!TryGet(obj, name, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        return CheckString(obj, name, maxLength, path, errors);
    }

    internal static string? CheckPatternString(
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
}
