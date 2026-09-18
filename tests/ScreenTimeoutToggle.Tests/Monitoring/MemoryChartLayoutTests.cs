using OBDim.Monitoring.Models;
using OBDim.UI.Memory;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// R29 / AM02: the pipeline must segment on original samples, reduce each run without losing peaks,
/// keep single points, leave real gaps empty, and scale the axis from zero. Every case below is a
/// failure mode the previous Paint-time implementation had, so these tests are the regression net.
/// </summary>
public class MemoryChartLayoutTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UnixEpoch;

    private const ulong GiB = 1024 * 1024 * 1024;
    private const ulong PhysTotal = 32UL * GiB;
    private const ulong CommitLimit = 48UL * GiB;

    private static MemorySample Sample(
        DateTimeOffset at,
        ulong available = 16UL * GiB,
        ulong total = PhysTotal,
        ulong commit = 20UL * GiB,
        ulong commitLimit = CommitLimit,
        bool? lowSignal = false) => new()
        {
            SampledAtUtc = at,
            PhysicalTotalBytes = total,
            PhysicalAvailableBytes = available,
            CommitTotalBytes = commit,
            CommitLimitBytes = commitLimit,
            LowMemorySignal = lowSignal,
        };

    private static MemorySample At(double second, ulong available = 16UL * GiB) =>
        Sample(Base.AddSeconds(second), available);

    private static MemoryChartGeometry Build(
        IReadOnlyList<MemorySample> samples,
        MemoryTrendMetric metric = MemoryTrendMetric.PhysicalUsed,
        DateTimeOffset? windowEnd = null,
        MemoryTrendRange range = MemoryTrendRange.Hour1) =>
        MemoryChartGeometry.Build(
            samples,
            metric,
            windowEnd ?? Base.AddHours(1),
            range,
            plotWidth: 456,
            plotHeight: 280);

    // ---------- the reproduction that motivated the rewrite ----------

    /// <summary>
    /// The delivery package's jitter fixture: 720 samples on a 5-second cadence with ±80 ms of timer
    /// jitter and no real discontinuity. Stride-sampling first (the old order) turned this into 43
    /// fragments and dropped 80 samples; segmenting first must yield exactly one continuous run.
    /// Expectations are the JS reference's measured output, so the C# port and the prototype agree.
    /// </summary>
    [Fact]
    public void JitterCadence_SevenTwentySamples_RendersAsOneContinuousRun()
    {
        var samples = new List<MemorySample>(720);
        for (var i = 0; i < 720; i++)
        {
            var jitteredMs = i * 5000.0 + 100 + Math.Sin(i * 0.37) * 80;
            var used = (ulong)(16 * GiB + Math.Sin(i * 0.11) * 1.5 * GiB + i * 3.0);
            samples.Add(Sample(
                Base.AddMilliseconds(jitteredMs),
                available: PhysTotal - used,
                commit: (ulong)(20 * GiB + Math.Cos(i * 0.09) * 2 * GiB)));
        }

        var geometry = Build(samples);

        Assert.Single(geometry.RawRuns);
        Assert.Single(geometry.Series);
        Assert.Equal(720, geometry.RawPointCount);
        Assert.Equal(720, geometry.RawRuns[0].Count);
        Assert.Equal(240, geometry.DrawPointCount);
        Assert.Empty(geometry.IsolatedPoints);
    }

    /// <summary>The same fixture on the commit series — two metrics, one chart each, never stacked.</summary>
    [Fact]
    public void CommitMetric_HasItsOwnSeriesAndCapacityAxis()
    {
        var samples = Enumerable.Range(0, 300)
            .Select(i => Sample(Base.AddSeconds(i * 5), commit: 20UL * GiB + (ulong)(i * 1024)))
            .ToList();

        var physical = Build(samples);
        var commit = Build(samples, MemoryTrendMetric.Commit);

        Assert.Single(physical.Series);
        Assert.Single(commit.Series);
        Assert.Equal((double)PhysTotal, physical.AxisCapacity);
        Assert.Equal((double)CommitLimit, commit.AxisCapacity);
    }

    // ---------- segmentation ----------

    [Fact]
    public void RealGap_ThirtySeconds_BreaksIntoTwoRuns()
    {
        var samples = new[] { At(0), At(5), At(10), At(40), At(45) };

        var geometry = Build(samples);

        Assert.Equal(2, geometry.RawRuns.Count);
        Assert.Equal(3, geometry.RawRuns[0].Count);
        Assert.Equal(2, geometry.RawRuns[1].Count);
        Assert.Equal(2, geometry.Series.Count);
    }

    /// <summary>The 15-second threshold is applied to original timestamps, and it is strictly exceeding.</summary>
    [Theory]
    [InlineData(15.0, 1)]
    [InlineData(15.001, 2)]
    public void GapThreshold_IsAppliedOnOriginalTimestamps(double gapSeconds, int expectedRuns)
    {
        var geometry = Build(new[] { At(0), At(gapSeconds) });
        Assert.Equal(expectedRuns, geometry.RawRuns.Count);
    }

    /// <summary>
    /// An implausible sample (more available than total) is not plotted at zero and not bridged over —
    /// it ends the run, so the chart shows "nothing was measured here" instead of a fake valley.
    /// </summary>
    [Fact]
    public void InvalidSample_SplitsRunsInsteadOfPlottingZero()
    {
        var samples = new[] { At(0), At(5), At(10, available: PhysTotal + 1), At(15), At(20) };

        var geometry = Build(samples);

        Assert.Equal(2, geometry.RawRuns.Count);
        Assert.Equal(new[] { 0d, 5d, 15d, 20d },
            geometry.RawRuns.SelectMany(r => r).Select(p => (p.AtUtc - Base).TotalSeconds));
        Assert.Equal(4, geometry.RawPointCount);
    }

    [Fact]
    public void CommitExceedingLimit_IsInvalidForCommitMetric_ButPhysicalStaysValid()
    {
        var broken = Sample(Base, commit: 60UL * GiB, commitLimit: CommitLimit);

        Assert.Null(MemoryChartGeometry.ValueOf(broken, MemoryTrendMetric.Commit));
        Assert.NotNull(MemoryChartGeometry.ValueOf(broken, MemoryTrendMetric.PhysicalUsed));
    }

    [Fact]
    public void ZeroCapacity_IsNeverClaimedAndTheSampleIsValidatedAway()
    {
        var zero = Sample(Base, total: 0, commitLimit: 0);

        Assert.Null(MemoryChartGeometry.CapacityOf(zero, MemoryTrendMetric.PhysicalUsed));
        Assert.Null(MemoryChartGeometry.CapacityOf(zero, MemoryTrendMetric.Commit));
        Assert.Null(MemoryChartGeometry.ValueOf(zero, MemoryTrendMetric.PhysicalUsed));
    }

    /// <summary>A legitimate zero reading stays zero — it is not "unknown", and a wrapped value is worse.</summary>
    [Fact]
    public void FullyUsedPhysicalMemory_IsAValidZeroAvailable_AndNotDropped()
    {
        var geometry = Build(new[] { At(0, available: 0), At(5, available: 0) });

        Assert.Single(geometry.RawRuns);
        Assert.Equal(2, geometry.RawRuns[0].Count);
        Assert.All(geometry.RawRuns[0], p => Assert.Equal(PhysTotal, p.Value));
    }

    /// <summary>
    /// available &gt; total would make the naive total − available subtraction wrap to a huge number.
    /// The layout must refuse the sample instead of drawing a 16-exabyte spike.
    /// </summary>
    [Fact]
    public void AvailableAboveTotal_NeverBecomesAWrappedValue()
    {
        var geometry = Build(new[] { Sample(Base, available: PhysTotal + 1), At(5) });

        Assert.Single(geometry.RawRuns);
        Assert.Equal(PhysTotal - 16UL * GiB, Assert.Single(geometry.RawRuns[0]).Value);
    }

    // ---------- single points and empty states ----------

    [Fact]
    public void SingleSample_IsDrawnAsOnePoint_WithoutAreaOrLine()
    {
        var geometry = Build(new[] { At(0) });

        Assert.False(geometry.IsEmpty);
        var isolated = Assert.Single(geometry.IsolatedPoints);
        Assert.Empty(geometry.Series);
        Assert.Equal(isolated, geometry.Latest);
    }

    [Fact]
    public void IsolatedSample_InTheMiddle_KeepsItsRealTimestampAndDrawsNoArea()
    {
        var samples = new[] { At(0), At(5), At(200), At(400), At(405) };

        var geometry = Build(samples);

        var isolated = Assert.Single(geometry.IsolatedPoints);
        Assert.Equal(2, geometry.Series.Count);
        Assert.Equal(200d, (isolated.AtUtc - Base).TotalSeconds);
    }

    [Fact]
    public void NoSamples_ProduceEmptyGeometry_WithoutInventingAnAxis()
    {
        var geometry = Build(Array.Empty<MemorySample>());

        Assert.True(geometry.IsEmpty);
        Assert.Equal(0, geometry.AxisCapacity);
        Assert.Empty(geometry.Series);
        Assert.Empty(geometry.IsolatedPoints);
        Assert.Null(geometry.Latest);
    }

    /// <summary>
    /// A short history must not be stretched to the right edge: the newest point sits at its own time,
    /// so the remaining window is visibly empty rather than "flat and full".
    /// </summary>
    [Fact]
    public void ShortHistory_EndsAtTheRealSampleTime_NotAtTheWindowEdge()
    {
        var geometry = Build(new[] { At(0), At(5) });

        Assert.True(geometry.Latest!.Value.X < geometry.PlotWidth * 0.01,
            $"latest X={geometry.Latest!.Value.X} of {geometry.PlotWidth}");
    }

    /// <summary>
    /// Ten minutes of nothing at the tail of an hour window leaves exactly one sixth of the axis blank.
    /// Extending the last value to "now" would hide the outage entirely.
    /// </summary>
    [Fact]
    public void StaleTail_LeavesTheRecentWindowBlank()
    {
        var geometry = Build(new[] { At(3000), At(3005) });

        var blank = geometry.PlotWidth - geometry.Latest!.Value.X;
        Assert.True(Math.Abs(blank - geometry.PlotWidth / 6) < geometry.PlotWidth * 0.01,
            $"expected ~1/6 blank tail, got {blank} of {geometry.PlotWidth}");
    }

    // ---------- ordering, duplicates, window bounds ----------

    [Fact]
    public void OutOfOrderInput_IsSorted_AndStillRendersOneRun()
    {
        var samples = new[] { At(20), At(0), At(10), At(5), At(15) };
        var geometry = Build(samples);

        Assert.Single(geometry.RawRuns);
        Assert.Equal(new[] { 0d, 5d, 10d, 15d, 20d },
            geometry.RawRuns[0].Select(p => (p.AtUtc - Base).TotalSeconds));
    }

    /// <summary>
    /// Same-timestamp correction: the later entry replaces the earlier one at that instant, so a
    /// re-read never appears as two points or as a zero-length segment.
    /// </summary>
    [Fact]
    public void DuplicateTimestamp_LastSampleWins()
    {
        var geometry = Build(new[] { At(5, available: 16UL * GiB), At(5, available: 14UL * GiB) });

        var point = Assert.Single(geometry.RawRuns[0]);
        Assert.Equal(PhysTotal - 14UL * GiB, point.Value);
    }

    /// <summary>
    /// Both edges of the window belong to it. Two samples a window apart are also a genuine gap, so
    /// the assertion is about coverage of the two timestamps, not about run count.
    /// </summary>
    [Fact]
    public void WindowBounds_AreInclusive()
    {
        var geometry = Build(new[] { At(0), At(3600) }, windowEnd: Base.AddSeconds(3600));

        Assert.Equal(2, geometry.RawPointCount);
        Assert.Equal(new[] { 0d, 3600d }, geometry.RawRuns.SelectMany(r => r).Select(p => (p.AtUtc - Base).TotalSeconds));
    }

    [Fact]
    public void SamplesOutsideTheWindow_AreIgnored()
    {
        var geometry = Build(new[] { At(0), At(10), At(7200) }, windowEnd: Base.AddSeconds(3600));

        Assert.Equal(new[] { 0d, 10d }, geometry.RawRuns.Single().Select(p => (p.AtUtc - Base).TotalSeconds));
    }

    // ---------- axis ----------

    /// <summary>
    /// The axis starts at zero and ends at the largest capacity inside the window. Local min/max
    /// scaling was what made a 2 GB wobble look like memory exhaustion.
    /// </summary>
    [Fact]
    public void Axis_RunsFromZeroToCapacity_NotLocalExtrema()
    {
        var samples = new[] { At(0, available: 16UL * GiB), At(5, available: 15UL * GiB) };
        var geometry = Build(samples);

        Assert.Equal((double)PhysTotal, geometry.AxisCapacity);
        var line = geometry.Series[0].Line;
        var top = line.Min(p => p.Y);
        var bottom = line.Max(p => p.Y);

        // 15–16 GiB of 32 GiB sits in the middle band, not pinned to both edges as before.
        Assert.True(top > geometry.PlotHeight * 0.4, $"top Y={top}");
        Assert.True(bottom < geometry.PlotHeight * 0.55, $"bottom Y={bottom}");
    }

    [Fact]
    public void ZeroUsage_DrawsOnTheBaseline_AndFullUsageAtTheTop()
    {
        var geometry = Build(new[] { At(0, available: PhysTotal), At(5, available: 0) });
        var line = geometry.Series[0].Line;

        Assert.Equal(geometry.PlotHeight, line[0].Y, 3);
        Assert.Equal(0d, line[1].Y, 3);
    }

    /// <summary>
    /// A capacity that changes mid-window must not crop the older data: the axis keeps the maximum over
    /// the whole window, while each sample's own percentage still uses its own denominator.
    /// </summary>
    [Fact]
    public void CapacityChange_KeepsOlderLargerCapacityOnTheAxis()
    {
        var samples = new[]
        {
            Sample(Base, available: 16UL * GiB, total: PhysTotal),
            Sample(Base.AddSeconds(5), available: 8UL * GiB, total: 16UL * GiB),
        };

        var geometry = Build(samples);

        Assert.Equal((double)PhysTotal, geometry.AxisCapacity);
        Assert.Equal(PhysTotal, geometry.RawRuns[0][0].Capacity);
        Assert.Equal(16UL * GiB, geometry.RawRuns[0][1].Capacity);
    }

    [Fact]
    public void CommitLimitChange_KeepsOlderLargerLimitOnTheAxis()
    {
        var samples = new[]
        {
            Sample(Base, commitLimit: CommitLimit),
            Sample(Base.AddSeconds(5), commitLimit: 40UL * GiB),
        };

        var geometry = Build(samples, MemoryTrendMetric.Commit);

        Assert.Equal((double)CommitLimit, geometry.AxisCapacity);
    }

    // ---------- reduction ----------

    /// <summary>
    /// Bucket min/max reduction must preserve a spike that lands inside a bucket, plus both endpoints.
    /// A stride sampler stepped straight over it.
    /// </summary>
    [Fact]
    public void Reduction_KeepsInteriorSpikeAndBothEndpoints()
    {
        var samples = new List<MemorySample>();
        for (var i = 0; i < 1000; i++)
        {
            // 30 GiB used at i=637 against a ~16 GiB baseline: an excursion no bucket boundary would show.
            var available = i == 637 ? PhysTotal - 30UL * GiB : 16UL * GiB;
            samples.Add(At(i * 5, available));
        }

        var geometry = Build(samples, range: MemoryTrendRange.Hour2, windowEnd: Base.AddSeconds(995 * 5));
        var drawn = geometry.Series.Single().Line;

        Assert.True(drawn.Count <= MemoryChartGeometry.MaxDrawPoints, $"drawn={drawn.Count}");
        var spike = Assert.Single(drawn, p => p.Value == 30UL * GiB);
        Assert.Equal(0d, (drawn[0].AtUtc - Base).TotalSeconds);
        Assert.Equal(995 * 5d, (drawn[^1].AtUtc - Base).TotalSeconds);
        Assert.True(spike.Y < drawn[0].Y, "larger usage must sit higher on the plot (Y grows downward)");
    }

    [Fact]
    public void Reduction_KeepsChronologicalOrder()
    {
        var samples = Enumerable.Range(0, 1200).Select(i => At(i * 5, 16UL * GiB + (ulong)i)).ToList();
        var drawn = Build(samples).Series.Single().Line;

        Assert.Equal(drawn.Select(p => p.AtUtc).OrderBy(t => t).ToList(), drawn.Select(p => p.AtUtc).ToList());
    }

    /// <summary>
    /// Per-run budgets are proportional, so one long run is not squeezed by several short ones —
    /// but the total stays bounded. This pins "bounded overall", which is what the prototype's
    /// "≥ 240 per run" wording is not.
    /// </summary>
    [Fact]
    public void Reduction_TotalDrawnPointsStayBoundedAcrossManyRuns()
    {
        var samples = new List<MemorySample>();
        for (var run = 0; run < 8; run++)
        {
            for (var i = 0; i < 300; i++)
            {
                // 1-second spacing keeps 2400 raw points inside one hour; runs are 21s apart.
                samples.Add(At(run * 320 + i, 16UL * GiB + (ulong)(i * 1024)));
            }
        }

        var geometry = Build(samples);

        Assert.Equal(8, geometry.RawRuns.Count);
        Assert.Equal(2400, geometry.RawPointCount);
        Assert.True(geometry.DrawPointCount <= MemoryChartGeometry.MaxDrawPoints,
            $"drawn={geometry.DrawPointCount}");
    }

    // ---------- fill / stroke separation ----------

    /// <summary>
    /// The filled polygon closes along the baseline; the stroked path must stay open. If a renderer ever
    /// strokes <see cref="MemoryChartSeries.Area"/> the closing vertical edges reappear as the thin bars
    /// this rewrite exists to remove, so the two paths are kept structurally distinct.
    /// </summary>
    [Fact]
    public void AreaPath_ClosesOnBaseline_WhileLinePathStaysOpen()
    {
        var geometry = Build(new[] { At(0), At(5), At(10) });
        var series = geometry.Series.Single();

        Assert.Equal(3, series.Line.Count);
        Assert.Equal(series.Line.Count + 2, series.Area.Count);
        Assert.Equal(series.Line.Take(series.Line.Count).ToList(), series.Area.Take(series.Line.Count).ToList());
        Assert.Equal(series.Line[^1].X, series.Area[^2].X);
        Assert.Equal(geometry.PlotHeight, series.Area[^2].Y);
        Assert.Equal(series.Line[0].X, series.Area[^1].X);
        Assert.Equal(geometry.PlotHeight, series.Area[^1].Y);
        Assert.DoesNotContain(series.Area[^2], series.Line);
        Assert.DoesNotContain(series.Area[^1], series.Line);
    }

    /// <summary>A single point contributes a dot only — never an area, never a horizontal stub.</summary>
    [Fact]
    public void SinglePointRun_ProducesNoAreaAtAll()
    {
        var geometry = Build(new[] { At(100), At(1000) });

        Assert.Empty(geometry.Series);
        Assert.Equal(2, geometry.IsolatedPoints.Count);
    }

    // ---------- hover ----------

    /// <summary>
    /// Hover resolves against the raw series, not the ~240 points that survived reduction: a reading
    /// that only existed because the reducer happened to keep that one sample would be a coincidence,
    /// and a reading that jumped to the nearest drawn point would misreport the value.
    /// </summary>
    [Fact]
    public void Hover_ReturnsNearestRawSample_EvenWhenTheViewIsReduced()
    {
        var samples = Enumerable.Range(0, 1000).Select(i => At(i * 5, 16UL * GiB + (ulong)i)).ToList();
        var geometry = Build(samples);

        var hit = MemoryChartGeometry.PointAt(geometry, Base.AddSeconds(412));

        Assert.NotNull(hit);
        Assert.Equal(410d, hit.Value.AtUtc.Subtract(Base).TotalSeconds);
        Assert.Equal(16UL * GiB - 82, hit.Value.Value);

        var drawnSeconds = geometry.Series.SelectMany(s => s.Line).Select(p => (p.AtUtc - Base).TotalSeconds).ToList();
        Assert.DoesNotContain(410d, drawnSeconds);
    }

    /// <summary>Never snap across a real gap: the tooltip must not invent coverage where none exists.</summary>
    [Fact]
    public void Hover_InsideRealGap_ReturnsNull()
    {
        var geometry = Build(new[] { At(0), At(5), At(600), At(605) });

        Assert.Null(MemoryChartGeometry.PointAt(geometry, Base.AddSeconds(300)));
        Assert.NotNull(MemoryChartGeometry.PointAt(geometry, Base.AddSeconds(3)));
    }

    [Fact]
    public void Hover_OutsideWindow_ReturnsNull()
    {
        var geometry = Build(new[] { At(0), At(5) });

        Assert.Null(MemoryChartGeometry.PointAt(geometry, Base.AddHours(-1)));
        Assert.Null(MemoryChartGeometry.PointAt(geometry, Base.AddHours(5)));
    }

    [Fact]
    public void Hover_SinglePointRun_AcceptsOnlyItsOwnSamplePeriod()
    {
        var geometry = Build(new[] { At(100) });

        Assert.NotNull(MemoryChartGeometry.PointAt(geometry, Base.AddSeconds(102)));
        Assert.Null(MemoryChartGeometry.PointAt(geometry, Base.AddSeconds(120)));
    }

    // ---------- input hygiene and guards ----------

    [Fact]
    public void Build_DoesNotModifyTheCallerSampleArray()
    {
        var samples = new[] { At(20), At(0) };
        var before = samples.ToArray();

        MemoryChartGeometry.Build(samples, MemoryTrendMetric.PhysicalUsed, Base.AddHours(1),
            MemoryTrendRange.Hour1, 456, 280);

        Assert.Equal(before, samples);
    }

    [Fact]
    public void DefaultTimestampSamples_AreDroppedRatherThanSortedAsYearOne()
    {
        var geometry = Build(new[] { Sample(default), At(5) });

        Assert.Equal(1, geometry.RawPointCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositivePlotSize_IsRejected(double size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MemoryChartGeometry.Build(new[] { At(0) }, MemoryTrendMetric.PhysicalUsed,
                Base.AddHours(1), MemoryTrendRange.Hour1, size, 280));
    }

    [Fact]
    public void EveryRange_SelectsItsOwnWindow()
    {
        var samples = Enumerable.Range(0, 1441).Select(i => At(i * 5)).ToList();
        var windowEnd = Base.AddSeconds(1440 * 5);

        foreach (var range in Enum.GetValues<MemoryTrendRange>())
        {
            var geometry = MemoryChartGeometry.Build(samples, MemoryTrendMetric.PhysicalUsed,
                windowEnd, range, 456, 280);
            Assert.Equal(range.Span(), geometry.WindowEnd - geometry.WindowStart);
            Assert.Equal(windowEnd - range.Span(), geometry.WindowStart);
        }

        // 10 minutes of 5-second samples is exactly 121 inclusive points, no more and no less.
        Assert.Equal(121, MemoryChartGeometry.Build(samples, MemoryTrendMetric.PhysicalUsed,
            windowEnd, MemoryTrendRange.Minute10, 456, 280).RawPointCount);
    }

    /// <summary>
    /// A two-hour window needs 1441 samples at the 5-second cadence to be full; the buffer bound that
    /// feeds it is M2's job, so pin the arithmetic here rather than letting the UI claim a range the
    /// history cannot serve.
    /// </summary>
    [Fact]
    public void TwoHourWindow_RequiresFourteenHundredAndFortyOneSamplesAtFiveSecondCadence()
    {
        Assert.Equal(1441, (int)(TimeSpan.FromHours(2).TotalSeconds / TimeSpan.FromSeconds(5).TotalSeconds) + 1);

        var exactlyEnough = Enumerable.Range(0, 1441).Select(i => At(i * 5)).ToList();
        var windowEnd = Base.AddSeconds(1440 * 5);
        var full = MemoryChartGeometry.Build(exactlyEnough, MemoryTrendMetric.PhysicalUsed,
            windowEnd, MemoryTrendRange.Hour2, 456, 280);
        Assert.Equal(1441, full.RawPointCount);

        var oneShort = MemoryChartGeometry.Build(exactlyEnough.Take(1440).ToList(), MemoryTrendMetric.PhysicalUsed,
            windowEnd, MemoryTrendRange.Hour2, 456, 280);
        Assert.Equal(1440, oneShort.RawPointCount);
    }
}
