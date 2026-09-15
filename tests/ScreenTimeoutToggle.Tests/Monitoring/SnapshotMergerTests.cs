using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// PRD §5.2(8) 桶级合并的纯函数守卫（v1.1.2，评审 P2-2）：部分更新只替换本次有权威数据
/// 的桶，失败桶沿用上一个好快照的值与旧窗口/旧重置时间；无旧值可沿用的失败桶保持错误行；
/// 旧快照里的失败桶永不反向盖掉本次数据。
/// </summary>
public class SnapshotMergerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(ProviderId id, string identity, bool partial, params QuotaBucket[] buckets) => new()
    {
        Provider = id,
        IdentityKey = identity,
        IdentityVerified = true,
        AttemptedAtUtc = T0,
        SucceededAtUtc = T0,
        IsPartial = partial,
        Buckets = buckets,
    };

    private static QuotaBucket Bucket(string key, double remaining, string? error = null) => new()
    {
        SourceKey = key,
        Error = error,
        Windows =
        [
            new QuotaWindow
            {
                SourceKey = "primary",
                UsedPercent = 100 - remaining,
                RemainingPercent = remaining,
                ResetsAtUtc = T0.AddHours(3),
                HasAnyQuotaField = true,
            },
        ],
    };

    [Fact]
    public void ErroredBucket_TakesPreviousGoodValueWithOldWindow()
    {
        var previous = Snapshot(ProviderId.Ark, "id1", partial: false, Bucket("b1", 80), Bucket("b2", 60));
        var partial = Snapshot(ProviderId.Ark, "id1", partial: true, Bucket("b1", 70), Bucket("b2", 0, error: "quota query failed"));

        var merged = SnapshotMerger.MergePartial(previous, partial);

        Assert.Equal(70, merged.Buckets.Single(b => b.SourceKey == "b1").Windows[0].RemainingPercent);
        var b2 = merged.Buckets.Single(b => b.SourceKey == "b2");
        Assert.Null(b2.Error);
        Assert.Equal(60, b2.Windows[0].RemainingPercent);
        Assert.Equal(T0.AddHours(3), b2.Windows[0].ResetsAtUtc); // 保留旧桶旧时间
    }

    [Fact]
    public void ErroredBucket_WithoutPreviousCounterpart_StaysErrored()
    {
        var previous = Snapshot(ProviderId.Ark, "id1", partial: false, Bucket("b1", 80));
        var partial = Snapshot(ProviderId.Ark, "id1", partial: true, Bucket("b1", 70), Bucket("bX", 0, error: "boom"));

        var merged = SnapshotMerger.MergePartial(previous, partial);

        Assert.Equal("boom", merged.Buckets.Single(b => b.SourceKey == "bX").Error);
    }

    [Fact]
    public void PreviousErroredBucket_IsNeverCarriedOver()
    {
        var previous = Snapshot(ProviderId.Ark, "id1", partial: false, Bucket("b1", 80, error: "old boom"));
        var partial = Snapshot(ProviderId.Ark, "id1", partial: true, Bucket("b1", 0, error: "new boom"));

        var merged = SnapshotMerger.MergePartial(previous, partial);

        Assert.Equal("new boom", merged.Buckets.Single(b => b.SourceKey == "b1").Error);
    }

    [Fact]
    public void NonPartialOrMissingPrevious_ReturnsPartialAsIs()
    {
        var partial = Snapshot(ProviderId.Ark, "id1", partial: true, Bucket("b1", 0, error: "boom"));
        Assert.Same(partial, SnapshotMerger.MergePartial(null!, partial)); // 无旧快照可合并 → 原样

        // 输入不是 partial（正常全量成功）→ 原样返回，不走合并。
        var plain = Snapshot(ProviderId.Ark, "id1", partial: false, Bucket("b1", 0, error: "boom"));
        var good = Snapshot(ProviderId.Ark, "id1", partial: false, Bucket("b1", 80));
        Assert.Same(plain, SnapshotMerger.MergePartial(good, plain));
    }
}
