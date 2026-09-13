using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Providers;

/// <summary>Uniform adapter contract for the three subscription sources.</summary>
public interface IProviderAdapter
{
    ProviderId Id { get; }

    /// <summary>Performs one read-only quota query. Never throws for query problems — failures come back as snapshots.</summary>
    Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken);
}

/// <summary>Identity helpers: reversible data never enters the cache key; emails are masked for display.</summary>
public static class MonitoringIdentity
{
    /// <summary>sha256 over the given parts (joined with an unlikely separator), truncated hex. Non-reversible.</summary>
    public static string Hash(params string?[] parts)
    {
        var payload = string.Join("\u001f", parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes)[..32]; // 128 bits is plenty for cache segregation
    }

    /// <summary>"operator@gmail.com" → "o***@gmail.com". Non-email input comes back as "***".</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var domain = email[(at + 1)..];
        var head = local[0];
        return $"{head}***@{domain}";
    }
}

/// <summary>Shared assembly point for a successful or failed snapshot.</summary>
public static class SnapshotFactory
{
    public static ProviderSnapshot Failure(
        ProviderId provider, ProviderErrorKind kind, string? detail,
        DateTimeOffset attemptedAt, string? cliVersion = null, string? identityKey = null) =>
        new()
        {
            Provider = provider,
            CliVersion = cliVersion,
            IdentityKey = identityKey ?? MonitoringIdentity.Hash(provider.ToString(), "no-identity"),
            IdentityVerified = false,
            IdentityDisplay = null,
            AttemptedAtUtc = attemptedAt,
            SucceededAtUtc = null,
            Error = kind,
            ErrorMessage = detail,
            Buckets = [],
        };

    /// <summary>
    /// Classifies a failed <see cref="CliLocator"/> lookup. Two genuinely different
    /// problems hide behind "not found": the CLI was never installed (or the configured
    /// path is gone), versus an entry that EXISTS but whose launch form we refuse to guess
    /// (.ps1, a bare POSIX shim, an unparsable/too-large npm shim). The second group is
    /// <see cref="ProviderErrorKind.UnsupportedEntry"/>, and it is only reachable through
    /// here — the providers used to collapse every locate error into CliNotFound, which
    /// left that classification dead code and gave the user the wrong instructions (A07).
    /// </summary>
    public static ProviderSnapshot LocateFailure(
        ProviderId provider, CliLocation location, DateTimeOffset attemptedAt)
    {
        var (kind, message) = location.Error switch
        {
            "cli.locate_configured_missing" =>
                (ProviderErrorKind.CliNotFound, "monitor.error.configured_path_missing"),
            "cli.locate_unsupported_entry" or "cli.locate_shim_unparsable"
                or "cli.locate_shim_too_large" or "cli.locate_shim_unreadable" =>
                (ProviderErrorKind.UnsupportedEntry, "monitor.error.unsupported_entry"),
            _ => (ProviderErrorKind.CliNotFound, "monitor.error.cli_not_installed"),
        };
        return Failure(provider, kind, message, attemptedAt);
    }
}

/// <summary>JsonElement helpers shared by the parsers. All parsing is tolerant: schema drift must yield ParseFailed, not exceptions.</summary>
public static class JsonExtensions
{
    public static JsonElement? TryGetProperty(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;
    }

    /// <summary>Nullable overload so chains like <c>root.TryGetProperty("a")?.TryGetProperty("b")</c> work.</summary>
    public static JsonElement? TryGetProperty(this JsonElement? element, string name)
    {
        return element is { } e ? e.TryGetProperty(name) : null;
    }

    /// <summary>Numbers come back as double; non-numeric values (strings, bools, null) yield null. NaN/Infinity rejected.</summary>
    public static double? AsNullableDouble(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } e) return null;
        if (e.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        return null;
    }

    public static long? AsNullableInt64(this JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Number } e)
        {
            if (e.TryGetInt64(out var l)) return l;
            if (e.TryGetDouble(out var d) && double.IsFinite(d)) return (long)d;
        }
        return null;
    }

    public static int? AsNullableInt32(this JsonElement? element)
    {
        var l = element.AsNullableInt64();
        return l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
    }

    public static string? AsNullableString(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static bool? AsNullableBool(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.True } ? true
        : element is { ValueKind: JsonValueKind.False } ? false
        : null;
}

/// <summary>
/// Normalizes a used/remaining percent pair (spec §5.2(1)): out-of-range input is flagged
/// and never silently clamped; remaining is derived only from a valid used value.
/// </summary>
public static class PercentNormalizer
{
    public static (double? used, double? remaining, bool outOfRange) FromUsed(double? usedPercent)
    {
        if (usedPercent is null) return (null, null, false);
        if (usedPercent is < 0 or > 100) return (usedPercent, null, true);
        return (usedPercent, Math.Round(100 - usedPercent.Value, 3), false);
    }

    /// <summary>For sources that report remaining directly (MiniMax): derive used = 100 − remaining.</summary>
    public static (double? used, double? remaining, bool outOfRange) FromRemaining(double? remainingPercent)
    {
        if (remainingPercent is null) return (null, null, false);
        if (remainingPercent is < 0 or > 100) return (null, remainingPercent, true);
        return (Math.Round(100 - remainingPercent.Value, 3), remainingPercent, false);
    }
}

/// <summary>
/// Turns epoch values into UTC. Sources disagree about seconds vs milliseconds, so a
/// magnitude heuristic applies (≥ 10^12 → ms); documented per fixture.
/// </summary>
public static class EpochTime
{
    public static DateTimeOffset? FromSeconds(long? seconds) =>
        seconds is null or <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);

    public static DateTimeOffset? FromMilliseconds(long? ms) =>
        ms is null or <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);

    /// <summary>For "updated_at"-style fields where the unit is unreliable across CLI versions.</summary>
    public static DateTimeOffset? Auto(long? value) =>
        value is null or <= 0 ? null
        : value >= 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
        : DateTimeOffset.FromUnixTimeSeconds(value.Value);
}
