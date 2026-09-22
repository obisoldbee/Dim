namespace OBDim.Monitoring.Network.V2;

/// <summary>Port of handoff core.js: identity before filtering, edges before thinning.</summary>
public static class NetworkSeries
{
    public sealed record Normalized(IReadOnlyList<RateSample> Samples, int Duplicates, int Invalid, int OutOfOrder, int IdentityConflicts);
    public sealed record Point(DateTimeOffset At, double Value, RateSample Sample);
    public sealed record Gap(DateTimeOffset From, DateTimeOffset To, string Reason);
    public sealed record DirectionPlot(IReadOnlyList<IReadOnlyList<Point>> Runs, IReadOnlyList<Point> Raw,
        IReadOnlyList<Gap> Gaps, double? Peak);
    public sealed record Projection(DateTimeOffset From, DateTimeOffset To, Directions<DirectionPlot> Directions);
    public sealed record Axis(string Key, double Max, double? LowSince, double ChangedAt);

    public static bool Known(double? v) => v is >= 0 && double.IsFinite(v.Value);
    public static string Key(RateSample s) =>
        $"{s.SourceID}|{s.InterfaceID}|{s.CaptureSessionID}|{s.CounterEpoch}|{s.MonotonicNs}";
    public static Normalized Normalize(IEnumerable<RateSample> input)
    {
        var result = new List<RateSample>();
        var keys = new HashSet<string>();
        var ids = new Dictionary<string, string>();
        var last = new Dictionary<(string, string, string), ulong>();
        int duplicates = 0, invalid = 0, order = 0, conflicts = 0;
        foreach (var s in input)
        {
            if (string.IsNullOrEmpty(s.SampleID) || string.IsNullOrEmpty(s.SourceID)
                || string.IsNullOrEmpty(s.InterfaceID) || string.IsNullOrEmpty(s.CaptureSessionID)
                || string.IsNullOrEmpty(s.CounterEpoch) || s.Cadence is null || s.Rates is null || s.Continuity is null
                || !Known(s.Cadence.SourceIntervalMs) || s.Cadence.SourceIntervalMs <= 0
                || (s.Cadence.HistoryIntervalMs is { } h && (!Known(h) || h <= 0))
                || (s.Rates.Upload is { } up && !Known(up)) || (s.Rates.Download is { } down && !Known(down))
                || s.Continuity.Upload.State is not ("continuous" or "break" or "unknown")
                || s.Continuity.Download.State is not ("continuous" or "break" or "unknown"))
            { invalid++; continue; }
            var key = Key(s);
            if (keys.Contains(key)) { duplicates++; continue; }
            if (ids.TryGetValue(s.SampleID, out var old) && old != key) { conflicts++; continue; }
            var stream = (s.SourceID, s.InterfaceID, s.CaptureSessionID);
            if (last.TryGetValue(stream, out var previous) && s.MonotonicNs <= previous) { order++; continue; }
            keys.Add(key); ids[s.SampleID] = key; last[stream] = s.MonotonicNs; result.Add(s);
        }
        return new(result, duplicates, invalid, order, conflicts);
    }
    public static string? Boundary(RateSample? prev, RateSample s, bool upload)
    {
        if (prev is null) return "start";
        if (prev.InterfaceID != s.InterfaceID || prev.SourceID != s.SourceID) return "interface-switch";
        if (prev.CaptureSessionID != s.CaptureSessionID) return "session-switch";
        if (prev.CounterEpoch != s.CounterEpoch) return "epoch-switch";
        if (s.SampledAt <= prev.SampledAt) return "wall-clock-change";
        var edge = s.Continuity[upload];
        if (edge.State != "continuous") return edge.Reason ?? "continuity-unknown";
        if (edge.PreviousSampleID != prev.SampleID) return "missing-observation";
        if (prev.Cadence.HistoryIntervalMs is not { } a || s.Cadence.HistoryIntervalMs is not { } b) return "legacy-cadence-unknown";
        if (s.MonotonicNs <= prev.MonotonicNs) return "non-monotonic";
        var delta = (s.MonotonicNs - prev.MonotonicNs) / 1e6;
        return delta > Math.Max(2000, 2.5 * Math.Max(a, b)) ? "silence" : null;
    }
    public static IReadOnlyList<Point> Thin(IReadOnlyList<Point> points, int limit = 160)
    {
        limit = Math.Max(4, limit);
        if (points.Count <= limit) return points;
        var keep = new SortedSet<int> { 0, points.Count - 1 };
        var buckets = (limit - 2) / 2;
        for (var b = 0; b < buckets; b++)
        {
            var from = 1 + b * (points.Count - 2) / buckets;
            var to = 1 + (b + 1) * (points.Count - 2) / buckets;
            int lo = from, hi = from;
            for (var i = from; i < to; i++)
            {
                if (points[i].Value < points[lo].Value) lo = i;
                if (points[i].Value > points[hi].Value) hi = i;
            }
            keep.Add(lo); keep.Add(hi);
        }
        return keep.Select(i => points[i]).ToArray();
    }
    public static Projection Project(IEnumerable<RateSample> input, string? id, DateTimeOffset now, TimeSpan window)
    {
        var samples = Normalize(input).Samples.Where(s => s.InterfaceID == id).ToArray();
        var from = now - window;
        DirectionPlot Build(bool upload)
        {
            var runs = new List<IReadOnlyList<Point>>();
            var run = new List<Point>();
            var gaps = new List<Gap>();
            void Flush() { if (run.Count > 0) runs.Add(run); run = []; }
            RateSample? prev = null;
            foreach (var s in samples)
            {
                var reason = Boundary(prev, s, upload);
                if (reason is not null && prev is not null)
                {
                    Flush();
                    if (s.SampledAt > prev.SampledAt && s.SampledAt >= from && prev.SampledAt <= now)
                        gaps.Add(new(prev.SampledAt < from ? from : prev.SampledAt, s.SampledAt > now ? now : s.SampledAt, reason));
                }
                if (s.Rates[upload] is not { } value)
                {
                    Flush();
                    if (prev is not null && s.SampledAt > prev.SampledAt && s.SampledAt >= from && s.SampledAt <= now)
                        gaps.Add(new(prev.SampledAt < from ? from : prev.SampledAt, s.SampledAt, "unknown-value"));
                }
                else
                {
                    if (prev is not null && prev.Rates[upload] is null) Flush();
                    run.Add(new(s.SampledAt, value, s));
                }
                prev = s;
            }
            Flush();
            var visible = runs.Select(r => (IReadOnlyList<Point>)r.Where(p => p.At >= from && p.At <= now).ToArray()).Where(r => r.Count > 0).ToArray();
            var raw = visible.SelectMany(r => r).ToArray();
            return new(visible.Select(r => Thin(r)).ToArray(), raw, gaps, raw.Length == 0 ? null : raw.Max(p => p.Value));
        }
        return new(from, now, new(Build(true), Build(false)));
    }
    public static double? Probe(DirectionPlot dir, DateTimeOffset at)
    {
        if (dir.Raw.Count == 0 || dir.Gaps.Any(g => at > g.From.AddMilliseconds(1) && at < g.To.AddMilliseconds(-1))) return null;
        var p = dir.Raw.MinBy(p => Math.Abs((p.At - at).TotalMilliseconds))!;
        var tolerance = Math.Max(500, (p.Sample.Cadence.HistoryIntervalMs ?? 0) * .55);
        return Math.Abs((p.At - at).TotalMilliseconds) <= tolerance ? p.Value : null;
    }
    public static double NiceCeiling(double? peak)
    {
        if (!Known(peak) || peak == 0) return 1;
        var desired = peak!.Value * 1.12;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(desired)));
        return new[] { 1, 2, 3, 5, 8, 10 }.First(v => v * exp >= desired) * exp;
    }
    public static Axis UpdateAxis(Axis? previous, double? peak, double monotonicMs, string key)
    {
        var wanted = NiceCeiling(peak);
        if (previous is null || previous.Key != key || wanted > previous.Max)
            return new(key, wanted, null, monotonicMs);
        if (Known(peak) && peak < previous.Max * .45 && wanted < previous.Max)
        {
            var low = previous.LowSince ?? monotonicMs;
            if (monotonicMs - low >= 8000 && monotonicMs - previous.ChangedAt >= 8000)
                return new(key, wanted, null, monotonicMs);
            return previous with { LowSince = low };
        }
        return previous with { LowSince = null };
    }
    public static string Rate(double? value, string unknown = "未知")
    {
        if (!Known(value)) return unknown;
        var v = value!.Value;
        var i = v == 0 ? 0 : Math.Clamp((int)Math.Floor(Math.Log10(v) / 3), 0, 3);
        return $"{v / Math.Pow(1000, i):F1} {new[] { "B/s", "KB/s", "MB/s", "GB/s" }[i]}" is var text && i == 0
            ? $"{v.ToString(v is > 0 and < 1 ? "0.00" : "0", System.Globalization.CultureInfo.CurrentCulture)} B/s" : text;
    }
    public static string Bytes(ulong? value, string unknown = "未知")
    {
        if (value is null) return unknown;
        decimal v = value.Value;
        var i = 0;
        while (v >= 1000 && i < 4) { v /= 1000; i++; }
        return $"{v.ToString(i == 0 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture)} {new[] { "B", "KB", "MB", "GB", "TB" }[i]}";
    }
}
