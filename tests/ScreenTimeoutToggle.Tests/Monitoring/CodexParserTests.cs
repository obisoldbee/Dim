using System.Text.Json;
using OBDim.Monitoring.Providers;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// Parser tests for the Codex rate-limits read, using a desensitized fixture captured from
/// a real codex-cli 0.154.0 `account/rateLimits/read` response on Windows (2026-09-13).
/// Asserts business results — bucket content, window semantics, reset credits — not just
/// "did not throw".
/// </summary>
public class CodexRateLimitsMapperTests
{
    private static JsonElement Root(string fixture)
    {
        using var doc = JsonDocument.Parse(MonitoringFixtures.Load(fixture));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RealFixture_PrefersMultiBucketView_AndMapsAllWindows()
    {
        var result = CodexRateLimitsMapper.Map(Root("codex-ratelimits-real.json"));

        Assert.True(result.Ok);
        Assert.Equal("00000000-0000-4000-8000-000000000001", result.AccountId);
        Assert.Equal(2, result.Buckets.Count);

        var spark = result.Buckets[0];
        Assert.Equal("codex_bengalfox", spark.SourceKey);
        Assert.Equal("GPT-5.3-Codex-Spark", spark.DisplayName);
        Assert.Equal("pro", spark.Tier);
        Assert.Equal(2, spark.Windows.Count);

        var primary = spark.Windows[0];
        Assert.Equal("primary", primary.SourceKey);
        Assert.Equal(0, primary.UsedPercent);
        Assert.Equal(100, primary.RemainingPercent);
        Assert.Equal(300, primary.WindowDurationMinutes);
        Assert.False(primary.PercentOutOfRange);
        Assert.NotNull(primary.ResetsAtUtc);
        Assert.True(primary.HasAnyQuotaField);
    }

    [Fact]
    public void RealFixture_ResetCredits_KeepCountAndDetails()
    {
        var result = CodexRateLimitsMapper.Map(Root("codex-ratelimits-real.json"));

        var credits = result.ResetCredits;
        Assert.NotNull(credits);
        Assert.Equal(3, credits!.AvailableCount);
        Assert.NotNull(credits.Details);
        // Details list may be shorter than the count (backend caps it) — never inferred.
        Assert.Equal(2, credits.Details!.Count);
        Assert.All(credits.Details, c => Assert.Equal("available", c.Status));
        Assert.All(credits.Details, c => Assert.NotNull(c.ExpiresAtUtc));
    }

    [Fact]
    public void LegacyOnlyResponse_FallsBackToRateLimits()
    {
        var result = CodexRateLimitsMapper.Map(Root("codex-ratelimits-legacy-only.json"));

        Assert.True(result.Ok);
        var bucket = Assert.Single(result.Buckets);
        Assert.Equal("codex", bucket.SourceKey);
        Assert.Equal(42, bucket.Windows[0].UsedPercent);
        Assert.Equal(58, bucket.Windows[0].RemainingPercent);
        Assert.Equal(7, bucket.Windows[1].UsedPercent);
    }

    [Fact]
    public void OutOfRangePercent_FlaggedNotClamped_RemainingNull()
    {
        var result = CodexRateLimitsMapper.Map(Root("codex-ratelimits-out-of-range.json"));

        Assert.True(result.Ok);
        var window = result.Buckets[0].Windows[0];
        Assert.Equal(140, window.UsedPercent);
        Assert.True(window.PercentOutOfRange);
        Assert.Null(window.RemainingPercent);
    }

    [Fact]
    public void MissingResetsAt_YieldsNullResetTime_NeverGuesses()
    {
        var json = """{"rateLimits":{"limitId":"codex","primary":{"usedPercent":10}}}""";
        using var doc = JsonDocument.Parse(json);
        var result = CodexRateLimitsMapper.Map(doc.RootElement);

        Assert.True(result.Ok);
        var window = result.Buckets[0].Windows[0];
        Assert.Null(window.ResetsAtUtc);
        Assert.Null(window.WindowDurationMinutes);
    }

    [Fact]
    public void EmptyObject_IsParseFailure()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = CodexRateLimitsMapper.Map(doc.RootElement);
        Assert.False(result.Ok);
    }

    /// <summary>
    /// A02 百分比方向: for every window in every fixture, RemainingPercent must equal
    /// 100 − UsedPercent exactly when the used value is in range. This is the guard that
    /// would catch a parser reading a "remaining" field as "used".
    /// </summary>
    [Fact]
    public void PercentDirection_InvariantAcrossAllBuckets()
    {
        foreach (var fixture in new[] { "codex-ratelimits-real.json", "codex-ratelimits-legacy-only.json" })
        {
            var result = CodexRateLimitsMapper.Map(Root(fixture));
            Assert.True(result.Ok);
            foreach (var bucket in result.Buckets)
            {
                foreach (var window in bucket.Windows)
                {
                    if (window.PercentOutOfRange) continue;
                    Assert.NotNull(window.UsedPercent);
                    Assert.NotNull(window.RemainingPercent);
                    Assert.Equal(100, window.UsedPercent + window.RemainingPercent);
                }
            }
        }
    }
}

/// <summary>Codex account/read mapping: identity stays out of keys, masked for display.</summary>
public class CodexAccountMapperTests
{
    [Fact]
    public void ChatgptAccount_ExtractsEmailAndPlan()
    {
        var json = """{"account":{"type":"chatgpt","email":"someone@example.com","planType":"pro"},"requiresOpenaiAuth":true}""";
        using var doc = JsonDocument.Parse(json);
        var (email, planType, type) = CodexAccountMapper.Map(doc.RootElement);

        Assert.Equal("someone@example.com", email);
        Assert.Equal("pro", planType);
        Assert.Equal("chatgpt", type);
    }

    [Fact]
    public void ApiKeyAccount_HasNoEmail()
    {
        var json = """{"account":{"type":"apiKey"},"requiresOpenaiAuth":true}""";
        using var doc = JsonDocument.Parse(json);
        var (email, planType, type) = CodexAccountMapper.Map(doc.RootElement);

        Assert.Null(email);
        Assert.Null(planType);
        Assert.Equal("apiKey", type);
    }

    [Fact]
    public void MissingAccount_ReturnsNulls()
    {
        var json = """{"requiresOpenaiAuth":true,"account":null}""";
        using var doc = JsonDocument.Parse(json);
        var (email, planType, type) = CodexAccountMapper.Map(doc.RootElement);
        Assert.Null(email);
        Assert.Null(planType);
        Assert.Null(type);
    }

    [Fact]
    public void IdentityHash_IsStableAndDoesNotContainEmail()
    {
        var a = MonitoringIdentity.Hash("codex", "acct-1", "someone@example.com");
        var b = MonitoringIdentity.Hash("codex", "acct-1", "someone@example.com");
        var c = MonitoringIdentity.Hash("codex", "acct-2", "someone@example.com");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.DoesNotContain("someone@example.com", a);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void MaskEmail_MasksLocalPart()
    {
        Assert.Equal("s***@example.com", MonitoringIdentity.MaskEmail("someone@example.com"));
        Assert.Equal("***", MonitoringIdentity.MaskEmail("not-an-email"));
    }
}
