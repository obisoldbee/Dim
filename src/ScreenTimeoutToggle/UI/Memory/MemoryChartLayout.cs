using OBDim.Monitoring.Models;

namespace OBDim.UI.Memory;

/// <summary>Which series the single trend chart projects. Physical and commit are never summed.</summary>
public enum MemoryTrendMetric
{
    /// <summary>PhysicalTotalBytes − PhysicalAvailableBytes, i.e. what the machine is actually holding.</summary>
    PhysicalUsed,

    /// <summary>CommitTotalBytes — charge against the commit limit, not physical RAM.</summary>
    Commit,
}

/// <summary>The five selectable time windows. Adding a button is not the same as keeping the history.</summary>
public enum MemoryTrendRange
{
    Minute1,
    Minute10,
    Minute30,
    Hour1,
    Hour2,
}

public static class MemoryTrendRangeExtensions
{
    public static TimeSpan Span(this MemoryTrendRange range) => range switch
    {
        MemoryTrendRange.Minute1 => TimeSpan.FromMinutes(1),
        MemoryTrendRange.Minute10 => TimeSpan.FromMinutes(10),
        MemoryTrendRange.Minute30 => TimeSpan.FromMinutes(30),
        MemoryTrendRange.Hour1 => TimeSpan.FromHours(1),
        MemoryTrendRange.Hour2 => TimeSpan.FromHours(2),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };
}

/// <summary>
/// One valid sample projected onto the time axis, in raw (un-reduced) resolution.
/// </summary>
/// <param name="Value">Bytes, always derived from the original integers — never from formatted text.</param>
/// <param name="Capacity">
/// The denominator for this sample's own percentage (physical total, or commit limit). Always &gt; 0,
/// because a sample with an unusable capacity is treated as invalid and breaks the run instead.
/// </param>
public readonly record struct MemoryTrendPoint(DateTimeOffset AtUtc, ulong Value, ulong Capacity);

/// <summary>A raw point plus its pixel position inside the plot area.</summary>
public readonly record struct MemoryChartPoint(DateTimeOffset AtUtc, ulong Value, double X, double Y);

/// <summary>
/// One drawable series: the open polyline to stroke and the closed polygon to fill.
/// <see cref="Area"/> deliberately repeats the first and last points on the baseline so the fill needs no
/// extra maths — and deliberately must never be stroked, or the closing vertical edges become visible bars.
/// </summary>
public sealed record MemoryChartSeries(
    IReadOnlyList<MemoryChartPoint> Line,
    IReadOnlyList<MemoryChartPoint> Area);

/// <summary>
/// Pure chart geometry: raw samples in, pixel paths out.
/// <para>
/// Deliberately free of <c>Control</c>, GDI+, wall-clock and system-memory reads so it can be tested at
/// desktop CI speed. The order of operations is the fix — see <see cref="Build"/>.
/// </para>
/// </summary>
public sealed record MemoryChartGeometry
{
    public required MemoryTrendMetric Metric { get; init; }

    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    public required double PlotWidth { get; init; }

    public required double PlotHeight { get; init; }

    /// <summary>Vertical axis runs 0 → <see cref="AxisCapacity"/>; 0 means "nothing valid to draw".</summary>
    public required double AxisCapacity { get; init; }

    /// <summary>Continuous runs at original resolution — the only legitimate source for hover lookups.</summary>
    public required IReadOnlyList<IReadOnlyList<MemoryTrendPoint>> RawRuns { get; init; }

    public required IReadOnlyList<MemoryChartSeries> Series { get; init; }

    /// <summary>Runs that really do hold a single sample. These get a dot, and never an area.</summary>
    public required IReadOnlyList<MemoryChartPoint> IsolatedPoints { get; init; }

    /// <summary>Newest drawn point. May be the same location as an isolated point.</summary>
    public MemoryChartPoint? Latest { get; init; }

    public required int RawPointCount { get; init; }

    public required int DrawPointCount { get; init; }

    public bool IsEmpty => RawPointCount == 0;

    /// <summary>
    /// Raw samples in, geometry out. Fixed processing order, and the order is the point:
    /// <list type="number">
    /// <item>validate, de-duplicate identical timestamps, sort ascending;</item>
    /// <item>select the real time window;</item>
    /// <item>break runs on the ORIGINAL timestamps (gap or invalid value);</item>
    /// <item>reduce each run independently, keeping endpoints and per-bucket extrema;</item>
    /// <item>project to pixels against a 0-based capacity axis.</item>
    /// </list>
    /// Doing step 4 before step 3 is the bug this replaces: stride-sampling a 5-second series at
    /// stride 3 puts the drawn points exactly at the 15-second gap threshold, so timer jitter alone
    /// shredded a continuous curve into dozens of fragments.
    /// </summary>
    public static MemoryChartGeometry Build(
        IReadOnlyList<MemorySample> samples,
        MemoryTrendMetric metric,
        DateTimeOffset windowEnd,
        MemoryTrendRange range,
        double plotWidth,
        double plotHeight,
        TimeSpan? gapThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (plotWidth <= 0 || plotHeight <= 0 || double.IsNaN(plotWidth) || double.IsNaN(plotHeight))
        {
            throw new ArgumentOutOfRangeException(nameof(plotWidth), "plot dimensions must be positive");
        }

        var span = range.Span();
        var windowStart = windowEnd - span;
        var rawRuns = SplitRuns(samples, metric, windowStart, windowEnd, gapThreshold ?? TimeSpan.FromSeconds(15));

        var all = rawRuns.SelectMany(r => r).ToList();
        var axisCapacity = 0d;
        foreach (var p in all)
        {
            axisCapacity = Math.Max(axisCapacity, p.Capacity);
        }

        // Each run gets a share of the budget proportional to how many points it holds, so one long
        // run is not squeezed by several short ones. There is no floor of `budget / runCount` here:
        // a global guarantee that every run keeps N points is not something a bounded total allows.
        var totalRaw = Math.Max(all.Count, 1);
        var traces = new List<IReadOnlyList<MemoryTrendPoint>>(rawRuns.Count);
        for (var i = 0; i < rawRuns.Count; i++)
        {
            var share = MaxDrawPoints * rawRuns[i].Count / totalRaw;
            traces.Add(ReduceRun(rawRuns[i], Math.Max(4, share)));
        }

        double X(DateTimeOffset t) => (t - windowStart).TotalSeconds / span.TotalSeconds * plotWidth;
        double Y(ulong v) => axisCapacity > 0 ? plotHeight - (double)v / axisCapacity * plotHeight : plotHeight;

        var series = new List<MemoryChartSeries>();
        var isolated = new List<MemoryChartPoint>();
        MemoryChartPoint? latest = null;
        var drawPointCount = 0;

        foreach (var run in traces)
        {
            drawPointCount += run.Count;
            var projected = new MemoryChartPoint[run.Count];
            for (var i = 0; i < run.Count; i++)
            {
                projected[i] = new MemoryChartPoint(run[i].AtUtc, run[i].Value, X(run[i].AtUtc), Y(run[i].Value));
            }

            if (projected.Length == 0) continue;
            latest = projected[^1];

            if (projected.Length == 1)
            {
                isolated.Add(projected[0]);
                continue;
            }

            var area = new MemoryChartPoint[projected.Length + 2];
            Array.Copy(projected, area, projected.Length);
            area[^2] = projected[^1] with { Y = plotHeight };
            area[^1] = projected[0] with { Y = plotHeight };
            series.Add(new MemoryChartSeries(projected, area));
        }

        return new MemoryChartGeometry
        {
            Metric = metric,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            PlotWidth = plotWidth,
            PlotHeight = plotHeight,
            AxisCapacity = axisCapacity,
            RawRuns = rawRuns,
            Series = series,
            IsolatedPoints = isolated,
            Latest = latest,
            RawPointCount = all.Count,
            DrawPointCount = drawPointCount,
        };
    }

    /// <summary>How many drawn points the widest window targets before runs start competing for budget.</summary>
    public const int MaxDrawPoints = 240;

    /// <summary>
    /// Bytes for this metric, or null when the sample cannot support the claim — an invalid sample is not
    /// plotted at zero, it ends the current run.
    /// </summary>
    public static ulong? ValueOf(MemorySample sample, MemoryTrendMetric metric)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return metric switch
        {
            // The subtraction is guarded: MemorySample.PhysicalUsedBytes would wrap if the OS ever
            // reported more available than total.
            MemoryTrendMetric.PhysicalUsed when sample.PhysicalTotalBytes > 0
                && sample.PhysicalAvailableBytes <= sample.PhysicalTotalBytes
                => sample.PhysicalTotalBytes - sample.PhysicalAvailableBytes,
            MemoryTrendMetric.Commit when sample.CommitLimitBytes > 0
                && sample.CommitTotalBytes <= sample.CommitLimitBytes
                => sample.CommitTotalBytes,
            _ => null,
        };
    }

    /// <summary>The denominator behind the current percentage for this metric, or null when unusable.</summary>
    public static ulong? CapacityOf(MemorySample sample, MemoryTrendMetric metric)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var capacity = metric == MemoryTrendMetric.PhysicalUsed ? sample.PhysicalTotalBytes : sample.CommitLimitBytes;
        return capacity > 0 ? capacity : null;
    }

    /// <summary>
    /// Drops samples with no usable timestamp, collapses identical timestamps to the last one supplied
    /// (so a same-stamp correction replaces rather than duplicates), then sorts ascending.
    /// The caller's collection is never modified.
    /// </summary>
    public static IReadOnlyList<MemorySample> Normalize(IReadOnlyList<MemorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var latestByTimestamp = new Dictionary<DateTimeOffset, MemorySample>();
        var order = new List<DateTimeOffset>();
        foreach (var sample in samples)
        {
            if (sample is null || sample.SampledAtUtc == default) continue;
            if (!latestByTimestamp.ContainsKey(sample.SampledAtUtc)) order.Add(sample.SampledAtUtc);
            latestByTimestamp[sample.SampledAtUtc] = sample;
        }

        order.Sort();
        var normalized = new MemorySample[order.Count];
        for (var i = 0; i < order.Count; i++) normalized[i] = latestByTimestamp[order[i]];
        return normalized;
    }

    /// <summary>
    /// Continuous runs, decided on ORIGINAL timestamps before any reduction. An invalid value or a
    /// gap longer than <paramref name="gapThreshold"/> ends a run; neither is bridged.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MemoryTrendPoint>> SplitRuns(
        IReadOnlyList<MemorySample> samples,
        MemoryTrendMetric metric,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeSpan gapThreshold)
    {
        if (windowEnd <= windowStart)
        {
            throw new ArgumentException("window end must be after window start", nameof(windowEnd));
        }

        if (gapThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gapThreshold), "gap threshold must be positive");
        }

        var runs = new List<IReadOnlyList<MemoryTrendPoint>>();
        var current = new List<MemoryTrendPoint>();
        void Flush()
        {
            if (current.Count > 0) runs.Add(current.ToArray());
            current.Clear();
        }

        foreach (var sample in Normalize(samples))
        {
            var at = sample.SampledAtUtc;
            if (at < windowStart || at > windowEnd) continue;

            var value = ValueOf(sample, metric);
            var capacity = CapacityOf(sample, metric);
            if (value is null || capacity is null)
            {
                Flush();
                continue;
            }

            if (current.Count > 0 && at - current[^1].AtUtc > gapThreshold) Flush();
            current.Add(new MemoryTrendPoint(at, value.Value, capacity.Value));
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// Reduces one run to at most <paramref name="maxPoints"/> while keeping both endpoints and the
    /// min and max of every bucket, so a spike inside a bucket survives. Order stays chronological.
    /// </summary>
    public static IReadOnlyList<MemoryTrendPoint> ReduceRun(
        IReadOnlyList<MemoryTrendPoint> run,
        int maxPoints = MaxDrawPoints)
    {
        ArgumentNullException.ThrowIfNull(run);
        var budget = Math.Max(4, maxPoints);
        if (run.Count <= budget) return run.ToArray();

        var bucketCount = (budget - 2) / 2;
        var kept = new List<MemoryTrendPoint>(budget) { run[0] };
        var interiorCount = run.Count - 2;

        for (var b = 0; b < bucketCount; b++)
        {
            var lo = (int)((long)b * interiorCount / bucketCount) + 1;
            var hi = (int)((long)(b + 1) * interiorCount / bucketCount) + 1;

            var min = lo;
            var max = lo;
            for (var i = lo + 1; i < hi; i++)
            {
                if (run[i].Value < run[min].Value) min = i;
                if (run[i].Value > run[max].Value) max = i;
            }

            if (hi > lo)
            {
                var first = Math.Min(min, max);
                var second = Math.Max(min, max);
                kept.Add(run[first]);
                if (second != first) kept.Add(run[second]);
            }
        }

        kept.Add(run[^1]);
        return kept;
    }

    /// <summary>
    /// The sample nearest <paramref name="atUtc"/>, or null when nothing was sampled there.
    /// <para>
    /// Lookup walks the raw runs, never the reduced ones, and a request that falls in a gap returns
    /// null instead of the nearest neighbour across the break: a tooltip that snaps to the far side of
    /// a sleep period would present fabricated coverage as a reading.
    /// </para>
    /// </summary>
    public static MemoryTrendPoint? PointAt(MemoryChartGeometry geometry, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        foreach (var run in geometry.RawRuns)
        {
            if (run.Count == 0) continue;

            if (run.Count == 1)
            {
                // A lone sample is only its own reading for one sample period either way.
                if (Math.Abs((run[0].AtUtc - atUtc).TotalMilliseconds) <= 2500) return run[0];
                continue;
            }

            if (atUtc < run[0].AtUtc || atUtc > run[^1].AtUtc) continue;

            var lo = 0;
            var hi = run.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (run[mid].AtUtc < atUtc) lo = mid + 1;
                else hi = mid;
            }

            if (lo > 0 && (atUtc - run[lo - 1].AtUtc) < (run[lo].AtUtc - atUtc)) return run[lo - 1];
            return run[lo];
        }

        return null;
    }
}
