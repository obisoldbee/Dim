using System.Text.Json;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Providers;

/// <summary>
/// Volcano Ark provider: `arkcli usage plan --format json` — ONE query that covers all
/// subscriptions (auto-discovery), split here into Agent Plan and Coding Plan buckets
/// (spec §7: both Ark plans share one quota query). Percent direction verified against
/// the official skill doc: `percent` is USED percent (0–100); Coding Plan periods carry
/// no absolute used/total.
/// </summary>
public sealed class ArkProvider : IProviderAdapter
{
    private readonly IClock _clock;
    private readonly ICliProcessRunner _runner;

    public ArkProvider(IClock clock, ICliProcessRunner runner)
    {
        _clock = clock;
        _runner = runner;
    }

    public ProviderId Id => ProviderId.Ark;

    public async Task<ProviderSnapshot> QueryAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var attemptedAt = _clock.UtcNow;
        var location = CliLocator.Locate(settings.CliPath, "arkcli");
        if (!location.Found)
        {
            return SnapshotFactory.LocateFailure(ProviderId.Ark, location, attemptedAt);
        }
        if (location.NodeScriptPath is null && location.ExePath!.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return SnapshotFactory.Failure(ProviderId.Ark, ProviderErrorKind.UnsupportedEntry, location.Error, attemptedAt);
        }

        var authentication = await _runner.RunAsync(CliRequest.ForLocation(location,
            ["auth","status","--format","json"]), cancellationToken).ConfigureAwait(false);
        if (!authentication.Success || authentication.OutputTruncated)
            return ProviderFailureClassifier.FromResult(Id,authentication,attemptedAt);
        var authFailure = ArkAuthenticationParser.Parse(authentication.Stdout);
        if(authFailure is not null)
            return SnapshotFactory.Failure(Id,authFailure.Kind,null,attemptedAt) with { ErrorCode=authFailure.Code };

        var request = CliRequest.ForLocation(location, ["usage", "plan", "--format", "json"]);
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.OutputTruncated)
            return ProviderFailureClassifier.FromResult(ProviderId.Ark,result,attemptedAt);

        var parsed = ArkPlanParser.Parse(result.Stdout);
        if (!parsed.Ok)
        {
            return SnapshotFactory.Failure(ProviderId.Ark, parsed.ErrorKind, parsed.Error, attemptedAt) with { ErrorCode = parsed.Error is not null && parsed.Error.StartsWith("api.status.",StringComparison.Ordinal) ? parsed.Error : "provider."+parsed.ErrorKind };
        }

        var buckets = parsed.Buckets;
        if(buckets.Any(b => b.Subscribed==true && b.Error is null && string.IsNullOrWhiteSpace(b.Tier)))
        {
            var metadata=await _runner.RunAsync(CliRequest.ForLocation(location,
                ["plans","get","--format","json"]),cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if(metadata.Success && !metadata.OutputTruncated)
                buckets=ArkPlanMetadata.Apply(buckets,metadata.Stdout);
        }

        var verified = parsed.AccountId is not null || parsed.UserId is not null;
        var identityKey = verified
            ? MonitoringIdentity.Hash("ark", parsed.AccountId, parsed.UserId, parsed.Profile)
            : MonitoringIdentity.Hash("ark", "unverified", Environment.UserName);

        return new ProviderSnapshot
        {
            Provider = ProviderId.Ark,
            IdentityKey = identityKey,
            IdentityVerified = verified,
            IdentityDisplay = parsed.AuthMethod is null ? null : $"auth={parsed.AuthMethod}",
            AttemptedAtUtc = attemptedAt,
            SucceededAtUtc = _clock.UtcNow,
            Buckets = buckets,
            IsPartial = parsed.Buckets.Any(b => b.Error is not null),
        };
    }
}

/// <summary>
/// Pure parser for `arkcli usage plan --format json` (shape verified against the official
/// arkcli-usage-plan reference and a real 2026-09-13 response). Key semantics:
/// - `viewer`: auth_method (sso|aksk|apikey|none), user_id/user_name, account_id, profile,
///   region. `auth_method: "none"` = not signed in.
/// - `items[]`: one per product subscription (agent-plan / coding-plan / *-team), each with
///   `subscribed`, optional `error` (per-bucket failure — other buckets stay valid),
///   `periods[]` with label (5h|weekly|monthly|session), used/total (Agent Plan only),
///   percent = USED %, reset_at = RFC3339 with +08:00 offset.
/// - `subscribed: false` + empty periods = the source explicitly says "not subscribed";
///   an EMPTY items array only means nothing was returned (spec §5.2(7)).
/// - `updated_at` is epoch (unit drifted between versions → Auto heuristic).
/// </summary>
public static class ArkPlanParser
{
    public sealed record ParseResult(
        bool Ok,
        ProviderErrorKind ErrorKind,
        string? Error,
        IReadOnlyList<QuotaBucket> Buckets,
        string? AccountId,
        string? UserId,
        string? Profile,
        string? AuthMethod);

    public static ParseResult Parse(string stdout)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(stdout).RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "stdout is not valid JSON", [], null, null, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "response is not a JSON object", [], null, null, null, null);
        }

        if (ProviderFailureClassifier.Envelope(root) is { } failure)
            return new ParseResult(false, failure.Kind, failure.Code, [], null, null, null, null);

        var viewer = root.TryGetProperty("viewer");
        var accountId = viewer.TryGetProperty("account_id").AsNullableString();
        var userId = viewer.TryGetProperty("user_id").AsNullableString();
        var profile = viewer.TryGetProperty("profile").AsNullableString();
        var authMethod = viewer.TryGetProperty("auth_method").AsNullableString();

        if (authMethod == "none")
        {
            return new ParseResult(false, ProviderErrorKind.NotSignedIn, null, [], accountId, userId, profile, authMethod);
        }

        var items = root.TryGetProperty("items");
        if (items is null || items.Value.ValueKind != JsonValueKind.Array)
        {
            return new ParseResult(false, ProviderErrorKind.ParseFailed, "items missing", [], accountId, userId, profile, authMethod);
        }

        var buckets = new List<QuotaBucket>();
        foreach (var item in items.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var bucket = MapItem(item);
            if (bucket is not null) buckets.Add(bucket);
        }

        return new ParseResult(true, ProviderErrorKind.None, null, buckets, accountId, userId, profile, authMethod);
    }

    private static QuotaBucket? MapItem(JsonElement item)
    {
        var product = item.TryGetProperty("product").AsNullableString();
        if (string.IsNullOrEmpty(product)) return null;

        var tier = item.TryGetProperty("tier").AsNullableString();
        var seatId = item.TryGetProperty("seat_id").AsNullableString();
        var subscribed = item.TryGetProperty("subscribed").AsNullableBool();
        var errorElement = item.TryGetProperty("error");
        var failure = errorElement is { ValueKind: not JsonValueKind.Null and not JsonValueKind.False } error
            ? ProviderFailureClassifier.Classify(error.GetRawText()) : null;
        var updatedAt = EpochTime.Auto(item.TryGetProperty("updated_at").AsNullableInt64());

        var windows = new List<QuotaWindow>();
        if (item.TryGetProperty("periods") is { } periods && periods.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in periods.EnumerateArray())
            {
                if (period.ValueKind != JsonValueKind.Object) continue;
                var w = MapPeriod(period);
                if (w is not null) windows.Add(w);
            }
        }

        return new QuotaBucket
        {
            SourceKey = product,
            DisplayName = product,
            Tier = tier, // edition (personal/team) is not the subscription tier
            SeatId = seatId,
            Subscribed = subscribed,
            Error = failure?.Code,
            ErrorKind = failure?.Kind ?? ProviderErrorKind.None,
            Windows = windows,
        };

        QuotaWindow? MapPeriod(JsonElement period)
        {
            var label = period.TryGetProperty("label").AsNullableString();
            if (string.IsNullOrEmpty(label)) return null;

            var percent = period.TryGetProperty("percent").AsNullableDouble();
            var (used, remaining, outOfRange) = PercentNormalizer.FromUsed(percent);
            var usedVal = period.TryGetProperty("used").AsNullableInt64();
            var totalVal = period.TryGetProperty("total").AsNullableInt64();
            var resetsAt = ParseResetAt(period.TryGetProperty("reset_at").AsNullableString());

            return new QuotaWindow
            {
                SourceKey = label,
                DisplayAsUsed = true, // ark 的 percent 就是已用（官方语义）
                UsedPercent = used,
                RemainingPercent = remaining,
                PercentOutOfRange = outOfRange,
                ResetsAtUtc = resetsAt,
                UsedText = usedVal is not null ? usedVal.ToString() : null,
                TotalText = totalVal is not null ? totalVal.ToString() : null,
                HasAnyQuotaField = percent is not null || usedVal is not null,
            };
        }
    }

    /// <summary>reset_at is RFC3339 with an explicit offset (e.g. 2026-09-14T00:00:00+08:00) — parse, never guess.</summary>
    private static DateTimeOffset? ParseResetAt(string? resetAt)
    {
        if (string.IsNullOrEmpty(resetAt)) return null;
        if (DateTimeOffset.TryParse(resetAt, out var parsed)) return parsed.ToUniversalTime();
        return null;
    }
}
