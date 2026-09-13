using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// MiniMax `mmx quota show --output json` parser tests (mmx 1.0.25, real shape captured
/// 2026-09-13). Key semantics under test: percent fields are REMAINING (the used percent
/// is derived as 100 − remaining), interval/weekly windows carry epoch-ms reset points,
/// and base_resp.status_code != 0 is an API error.
/// </summary>
public class MiniMaxQuotaParserTests
{
    [Fact]
    public void RealFixture_MapsTwoModelBuckets_WithIntervalAndWeekly()
    {
        var result = MiniMaxQuotaParser.Parse(MonitoringFixtures.Load("minimax-quota-real.json"));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Buckets.Count);

        var general = result.Buckets[0];
        Assert.Equal("general", general.SourceKey);
        Assert.Equal(2, general.Windows.Count);

        // remaining 79 → used 21 (direction normalized, NOT taken as-is)
        var interval = general.Windows[0];
        Assert.Equal("interval", interval.SourceKey);
        Assert.Equal(21, interval.UsedPercent);
        Assert.Equal(79, interval.RemainingPercent);
        Assert.False(interval.PercentOutOfRange);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789246800000), interval.ResetsAtUtc);
        // total_count == 0 → counts not shown as authoritative used/total
        Assert.Null(interval.UsedText);
        Assert.Null(interval.TotalText);

        var weekly = general.Windows[1];
        Assert.Equal(0, weekly.UsedPercent);
        Assert.Equal(100, weekly.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1789315200000), weekly.ResetsAtUtc);
    }

    [Fact]
    public void RealFixture_PositiveCounts_ArePreservedVerbatim()
    {
        var result = MiniMaxQuotaParser.Parse(MonitoringFixtures.Load("minimax-quota-real.json"));

        var video = result.Buckets[1];
        var interval = video.Windows[0];
        Assert.Equal("1", interval.UsedText);
        Assert.Equal("3", interval.TotalText);
        var weekly = video.Windows[1];
        Assert.Equal("2", weekly.UsedText);
        Assert.Equal("21", weekly.TotalText);
    }

    [Fact]
    public void OutOfRangeRemainingPercent_Flagged_UsedNull()
    {
        var json = """{"model_remains":[{"model_name":"m","current_interval_remaining_percent":120}],"base_resp":{"status_code":0}}""";
        var result = MiniMaxQuotaParser.Parse(json);

        Assert.True(result.Ok);
        var window = result.Buckets[0].Windows[0];
        Assert.True(window.PercentOutOfRange);
        Assert.Null(window.UsedPercent);
        Assert.Equal(120, window.RemainingPercent);
    }

    [Fact]
    public void ApiErrorStatus_IsApiError_NotParseFailure()
    {
        var result = MiniMaxQuotaParser.Parse(MonitoringFixtures.Load("minimax-quota-api-error.json"));

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.ApiError, result.ErrorKind);
        Assert.Contains("1004", result.Error);
    }

    [Fact]
    public void InvalidJson_IsParseFailure()
    {
        var result = MiniMaxQuotaParser.Parse("not json at all");
        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.ParseFailed, result.ErrorKind);
    }

    [Fact]
    public void EmptyModelRemains_OkWithNoBuckets_MeaningNotReturned()
    {
        // 空 items 只表示"未返回套餐"，不得推断为"无订阅" (spec §5.2(7)).
        var json = """{"model_remains":[],"base_resp":{"status_code":0}}""";
        var result = MiniMaxQuotaParser.Parse(json);

        Assert.True(result.Ok);
        Assert.Empty(result.Buckets);
    }

    [Fact]
    public void MissingModelName_SkipsRow()
    {
        var json = """{"model_remains":[{"current_interval_remaining_percent":50}],"base_resp":{"status_code":0}}""";
        var result = MiniMaxQuotaParser.Parse(json);
        Assert.True(result.Ok);
        Assert.Empty(result.Buckets);
    }
}
