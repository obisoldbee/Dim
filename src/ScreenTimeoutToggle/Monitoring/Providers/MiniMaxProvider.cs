using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Providers;

/// <summary>
/// MiniMax provider: `mmx quota show --output json` (verified against mmx 1.0.25).
/// Read-only Token Plan quotas. The response carries NO account identity, so per spec §8.4
/// the snapshot is marked IdentityVerified=false and never reuses cross-session cache.
/// </summary>
public sealed class MiniMaxProvider : IProviderAdapter
{
    private readonly IClock _clock;
    private readonly ICliProcessRunner _runner;

    public MiniMaxProvider(IClock clock, ICliProcessRunner runner)
    {
        _clock = clock;
        _runner = runner;
    }

    public ProviderId Id => ProviderId.MiniMax;

    public async Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var attemptedAt = _clock.UtcNow;
        var location = CliLocator.Locate(settings.CliPath, "mmx");
        if (!location.Found)
        {
            return SnapshotFactory.Failure(
                ProviderId.MiniMax, ProviderErrorKind.CliNotFound,
                location.Error == "cli.locate_configured_missing" ? "monitor.error.configured_path_missing" : "monitor.error.cli_not_installed",
                attemptedAt);
        }
        if (location.NodeScriptPath is null && location.ExePath!.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.UnsupportedEntry, location.Error, attemptedAt);
        }

        var request = CliRequest.ForLocation(location, ["quota", "show", "--output", "json"]);
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Cancelled) return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.Cancelled, null, attemptedAt);
        if (result.TimedOut) return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.Timeout, null, attemptedAt);
        if (result.LaunchError is not null)
        {
            return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.ExecutionFailed,
                $"launch failed ({result.LaunchError})", attemptedAt);
        }
        if (result.OutputTruncated)
        {
            return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.OutputLimitExceeded, null, attemptedAt);
        }
        if (!result.Success)
        {
            // Exit code is all we may report; raw stderr could carry identity.
            return SnapshotFactory.Failure(ProviderId.MiniMax, ProviderErrorKind.ExecutionFailed,
                $"exit code {result.ExitCode}", attemptedAt);
        }

        var parsed = MiniMaxQuotaParser.Parse(result.Stdout);
        if (!parsed.Ok)
        {
            return SnapshotFactory.Failure(ProviderId.MiniMax, parsed.ErrorKind, parsed.Error, attemptedAt);
        }

        return new ProviderSnapshot
        {
            Provider = ProviderId.MiniMax,
            IdentityKey = MonitoringIdentity.Hash("minimax", "unverified", Environment.UserName),
            IdentityVerified = false,
            IdentityDisplay = null,
            AttemptedAtUtc = attemptedAt,
            SucceededAtUtc = _clock.UtcNow,
            Buckets = parsed.Buckets,
        };
    }
}

/// <summary>
/// Pure parser for `mmx quota show --output json`. Field semantics verified against real
/// output (mmx 1.0.25, 2026-09-13): `current_interval_remaining_percent` /
/// `current_weekly_remaining_percent` are REMAINING percents (0–100); interval windows are
/// epoch-millisecond start/end pairs; counts (`*_total_count` / `*_usage_count`) are
/// request counts, shown verbatim when total > 0. `base_resp.status_code != 0` is an
/// API error, not a parse failure.
/// </summary>
public static class MiniMaxQuotaParser
{
    public sealed record ParseResult(
        bool Ok,
        ProviderErrorKind ErrorKind,
        string? Error,
        IReadOnlyList<QuotaBucket> Buckets);

    public static ParseResult Parse(string stdout)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(stdout).RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "stdout is not valid JSON", []);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "response is not a JSON object", []);
        }

        var baseResp = root.TryGetProperty("base_resp");
        var statusCode = baseResp.TryGetProperty("status_code").AsNullableInt32();
        if (statusCode is not null and not 0)
        {
            // status_msg is provider-generated; it may contain account hints, so it is not
            // shown raw — the UI maps the kind, the log keeps only the code.
            return new ParseResult(false, ProviderErrorKind.ApiError, $"api status {statusCode}", []);
        }

        var remains = root.TryGetProperty("model_remains");
        if (remains is null || remains.Value.ValueKind != JsonValueKind.Array)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "model_remains missing", []);
        }

        var buckets = new List<QuotaBucket>();
        foreach (var model in remains.Value.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object) continue;
            var bucket = MapModel(model);
            if (bucket is not null) buckets.Add(bucket);
        }

        // An empty list means the source returned no plan rows — displayed as "not
        // returned", NOT as "no subscription" (spec §5.2(7)).
        return new ParseResult(true, ProviderErrorKind.None, null, buckets);
    }

    private static QuotaBucket? MapModel(JsonElement model)
    {
        var name = model.TryGetProperty("model_name").AsNullableString();
        if (string.IsNullOrEmpty(name)) return null;

        var windows = new List<QuotaWindow>();
        AddWindow(windows, "interval",
            startMs: model.TryGetProperty("start_time").AsNullableInt64(),
            endMs: model.TryGetProperty("end_time").AsNullableInt64(),
            remainingPercent: model.TryGetProperty("current_interval_remaining_percent").AsNullableDouble(),
            usedCount: model.TryGetProperty("current_interval_usage_count").AsNullableInt64(),
            totalCount: model.TryGetProperty("current_interval_total_count").AsNullableInt64());
        AddWindow(windows, "weekly",
            startMs: model.TryGetProperty("weekly_start_time").AsNullableInt64(),
            endMs: model.TryGetProperty("weekly_end_time").AsNullableInt64(),
            remainingPercent: model.TryGetProperty("current_weekly_remaining_percent").AsNullableDouble(),
            usedCount: model.TryGetProperty("current_weekly_usage_count").AsNullableInt64(),
            totalCount: model.TryGetProperty("current_weekly_total_count").AsNullableInt64());

        return new QuotaBucket
        {
            SourceKey = name,
            DisplayName = name,
            Windows = windows,
        };
    }

    private static void AddWindow(
        List<QuotaWindow> windows, string sourceKey,
        long? startMs, long? endMs, double? remainingPercent, long? usedCount, long? totalCount)
    {
        var (used, remaining, outOfRange) = PercentNormalizer.FromRemaining(remainingPercent);
        var start = EpochTime.FromMilliseconds(startMs);
        var end = EpochTime.FromMilliseconds(endMs);
        var hasCounts = totalCount is > 0;
        // total == 0 且未消耗 = 该窗口不设上限（真实数据里 general/video 的周窗口如此），
        // 显示为 ∞ 无限制而不是一根 100% 的绿条。
        var unlimited = totalCount == 0 && remainingPercent == 100;
        windows.Add(new QuotaWindow
        {
            SourceKey = sourceKey,
            DisplayAsUsed = true,
            IsUnlimited = unlimited,
            UsedPercent = used,
            RemainingPercent = remaining,
            PercentOutOfRange = outOfRange,
            ResetsAtUtc = end,
            UsedText = hasCounts ? usedCount?.ToString() : null,
            TotalText = hasCounts ? totalCount?.ToString() : null,
            HasAnyQuotaField = remainingPercent is not null || hasCounts,
        });
    }
}
