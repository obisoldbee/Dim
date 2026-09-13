using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// Ark `arkcli usage plan --format json` parser tests (official arkcli-usage-plan contract;
/// real response captured 2026-09-13). `percent` is USED percent; Coding Plan periods carry
/// no absolute values; per-bucket errors stay isolated; auth_method "none" = not signed in.
/// </summary>
public class ArkPlanParserTests
{
    [Fact]
    public void RealFixture_MapsCodingPlan_PercentIsUsed()
    {
        var result = ArkPlanParser.Parse(MonitoringFixtures.Load("ark-plan-real.json"));

        Assert.True(result.Ok);
        Assert.Equal("1000000000", result.AccountId);
        Assert.Equal("sso", result.AuthMethod);

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal("coding-plan", bucket.SourceKey);
        Assert.Equal("personal", bucket.Tier);
        Assert.True(bucket.Subscribed);
        Assert.Null(bucket.Error);

        Assert.Equal(3, bucket.Windows.Count);
        var weekly = bucket.Windows[1];
        Assert.Equal("weekly", weekly.SourceKey);
        Assert.Equal(97.92592506666666, weekly.UsedPercent!.Value, 10);
        // Remaining is rounded to 3 decimals by the normalizer.
        Assert.Equal(2.074, weekly.RemainingPercent!.Value, 3);
        Assert.Null(weekly.UsedText);           // Coding Plan: no absolute used
        Assert.Null(weekly.TotalText);
        Assert.NotNull(weekly.ResetsAtUtc);
        // reset_at is +08:00 — must be preserved as an absolute instant, converted to UTC.
        Assert.Equal(TimeSpan.Zero, weekly.ResetsAtUtc!.Value.ToUniversalTime().Offset);
    }

    [Fact]
    public void PartialErrorFixture_IsolatedBucket_OthersStayValid()
    {
        var result = ArkPlanParser.Parse(MonitoringFixtures.Load("ark-plan-partial-error.json"));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Buckets.Count);

        var agent = result.Buckets[0];
        Assert.Null(agent.Error);
        Assert.True(agent.Subscribed);
        // Agent Plan HAS absolute values.
        var fiveHour = agent.Windows[0];
        Assert.Equal("5h", fiveHour.SourceKey);
        Assert.Equal(25, fiveHour.UsedPercent);
        Assert.Equal("250", fiveHour.UsedText);
        Assert.Equal("1000", fiveHour.TotalText);

        var coding = result.Buckets[1];
        Assert.NotNull(coding.Error);
        Assert.Empty(coding.Windows);
    }

    [Fact]
    public void NotSubscribed_IsExplicit_PerBuckets()
    {
        var result = ArkPlanParser.Parse(MonitoringFixtures.Load("ark-plan-not-subscribed.json"));

        Assert.True(result.Ok);
        var agent = result.Buckets[0];
        Assert.False(agent.Subscribed);
        Assert.Empty(agent.Windows);
        var coding = result.Buckets[1];
        Assert.True(coding.Subscribed);
    }

    [Fact]
    public void AuthMethodNone_IsNotSignedIn()
    {
        var json = """{"viewer":{"auth_method":"none"},"items":[]}""";
        var result = ArkPlanParser.Parse(json);

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.NotSignedIn, result.ErrorKind);
    }

    [Fact]
    public void MissingViewer_StillParses_UnverifiedIdentity()
    {
        var json = """{"items":[{"product":"coding-plan","subscribed":true,"periods":[{"label":"session","percent":0}]}]}""";
        var result = ArkPlanParser.Parse(json);

        Assert.True(result.Ok);
        Assert.Null(result.AccountId);
        Assert.Null(result.UserId);
    }

    [Fact]
    public void EmptyItems_Ok_MeaningNotReturned_NotNoSubscription()
    {
        var json = """{"viewer":{"auth_method":"sso","account_id":"1"},"items":[]}""";
        var result = ArkPlanParser.Parse(json);

        Assert.True(result.Ok);
        Assert.Empty(result.Buckets);
    }

    [Fact]
    public void InvalidJson_IsParseFailure()
    {
        var result = ArkPlanParser.Parse("{broken");
        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.ParseFailed, result.ErrorKind);
    }

    [Fact]
    public void OutOfRangePercent_Flagged()
    {
        var json = """{"items":[{"product":"coding-plan","periods":[{"label":"session","percent":250}]}]}""";
        var result = ArkPlanParser.Parse(json);
        Assert.True(result.Ok);
        var window = result.Buckets[0].Windows[0];
        Assert.True(window.PercentOutOfRange);
        Assert.Null(window.RemainingPercent);
    }
}
