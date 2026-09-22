using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Monitoring.Network.V2;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

public class NetworkV2Tests
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    internal static JsonElement Fixture(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkV2", file + ".json"))).RootElement.Clone();
    internal static RateSample[] Samples(string file) => Fixture(file).GetProperty("samples").Deserialize<RateSample[]>(Json)!;
    [Theory]
    [InlineData("single-direction-reset")]
    [InlineData("both-directions-reset")]
    [InlineData("counter-epoch-switch")]
    [InlineData("counter-unknown")]
    public void SharedCounterFixturesMatchEveryExpectedField(string file)
    {
        var f = Fixture(file);
        var rows = f.GetProperty("input").Deserialize<CounterInput[]>(Json)!;
        var settlement = new CounterSettlement();
        Directions<DirectionTotal>? actual = null;
        foreach (var row in rows) actual = settlement.Apply(row);
        var expected = f.GetProperty("expected").GetProperty("states")[0].Deserialize<Directions<DirectionTotal>>(Json);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, settlement.Apply(rows[^1])); // source replay never settles twice
    }
    [Fact]
    public void SharedRepublicationDeduplicatesSourceIdentityNotPublicationTime()
    {
        var rows = Samples("republication");
        var result = NetworkSeries.Normalize(rows);
        Assert.Equal(2, result.Duplicates);
        Assert.Equal(rows.Length - 2, result.Samples.Count);
        var replay = rows[0] with { PublishedAt = rows[0].PublishedAt.AddYears(1), SampleID = "new-delivery-id" };
        Assert.Single(NetworkSeries.Normalize(new[] { rows[0], replay }).Samples);
    }
    [Fact]
    public void SharedMixedCadenceStaysConnectedWhileWindowMoves()
    {
        var rows = Samples("mixed-5s-1s");
        foreach (var now in rows.Skip(1).Select(s => s.SampledAt))
        {
            var p = NetworkSeries.Project(rows, "en0", now, TimeSpan.FromMinutes(10));
            Assert.Single(p.Directions.Upload.Runs); Assert.Single(p.Directions.Download.Runs);
        }
    }
    [Fact]
    public void SharedRealGapIsNeverConnectedOrInterpolated()
    {
        var rows = Samples("real-gap");
        var p = NetworkSeries.Project(rows, "en0", rows.Max(s => s.SampledAt), TimeSpan.FromHours(2));
        Assert.True(p.Directions.Upload.Runs.Count > 1);
        var g = Assert.Single(p.Directions.Upload.Gaps.Where(g => g.Reason == "source-gap"));
        Assert.Null(NetworkSeries.Probe(p.Directions.Upload, g.From + (g.To - g.From) / 2));
    }
    [Fact]
    public void SharedIsolatedPointDoesNotAcquireFakeHistory()
    {
        var rows = Samples("isolated-point");
        var now = rows.Max(s => s.SampledAt);
        var p = NetworkSeries.Project(rows, "en0", now, TimeSpan.FromHours(1));
        Assert.Single(Assert.Single(p.Directions.Upload.Runs));
        Assert.True(p.Directions.Upload.Raw[0].At > p.From);
    }
    [Fact]
    public void UnknownOneDirectionPreservesTheOtherAndMeasuredZero()
    {
        var a = Samples("mixed-5s-1s")[0];
        var p = NetworkSeries.Project([a with { Rates = new(null, 0) }], a.InterfaceID, a.SampledAt, TimeSpan.FromMinutes(1));
        Assert.Empty(p.Directions.Upload.Raw); Assert.Equal(0, p.Directions.Download.Peak);
        Assert.Equal("未知", NetworkSeries.Rate(null)); Assert.Equal("0 B/s", NetworkSeries.Rate(0));
        Assert.Equal("0.25 B/s", NetworkSeries.Rate(.25));
    }
    [Fact]
    public void InterfaceSessionEpochAndClockBoundariesRemainExplicit()
    {
        var a = Samples("mixed-5s-1s")[0]; var b = Samples("mixed-5s-1s")[1];
        Assert.Equal("interface-switch", NetworkSeries.Boundary(a, b with { InterfaceID = "different" }, true));
        Assert.Equal("session-switch", NetworkSeries.Boundary(a, b with { CaptureSessionID = "new", MonotonicNs = 1 }, true));
        Assert.Equal("epoch-switch", NetworkSeries.Boundary(a, b with { CounterEpoch = "new" }, true));
        Assert.Equal("wall-clock-change", NetworkSeries.Boundary(a, b with { SampledAt = a.SampledAt.AddSeconds(-1) }, true));
        var p = NetworkSeries.Project([a, b with { InterfaceID = "different", Rates = new(1e9, 1e9) }], a.InterfaceID, b.SampledAt, TimeSpan.FromMinutes(10));
        Assert.Equal(a.Rates.Upload, p.Directions.Upload.Peak);
    }
    [Fact]
    public void ConflictOutOfOrderAndUnknownCadenceAreNotSilentlyAccepted()
    {
        var a = Samples("mixed-5s-1s")[0]; var b = Samples("mixed-5s-1s")[1];
        Assert.Equal(1, NetworkSeries.Normalize([b, a]).OutOfOrder);
        Assert.Equal(1, NetworkSeries.Normalize([a, b with { SampleID = a.SampleID }]).IdentityConflicts);
        Assert.Equal("legacy-cadence-unknown", NetworkSeries.Boundary(a, b with { Cadence = b.Cadence with { HistoryIntervalMs = null } }, true));
        Assert.Equal("missing-observation", NetworkSeries.Boundary(a, b with { Continuity = new(new("continuous", "lost", null), b.Continuity.Download) }, true));
    }
    [Fact]
    public void UInt64AboveJavaScriptSafeIntegerRemainsExactAndBaselineIsUnknown()
    {
        var a = new CounterInput("source", "i", "session", "0", DateTimeOffset.UtcNow, 1, new(ulong.MaxValue - 100, null));
        var s = new CounterSettlement();
        Assert.Null(s.Apply(a).Upload.Bytes);
        var b = a with { MonotonicNs = 2, SampledAt = a.SampledAt.AddSeconds(1), Bytes = new(ulong.MaxValue - 99, null) };
        Assert.Equal(1UL, s.Apply(b).Upload.Bytes);
        Assert.Equal(1UL, s.Apply(b with { MonotonicNs = 3 }).Upload.Bytes);
    }
    [Fact]
    public void AxesAreIndependentExpandImmediatelyAndShrinkAfterEightSeconds()
    {
        var up = NetworkSeries.UpdateAxis(null, 100, 0, "up");
        var down = NetworkSeries.UpdateAxis(null, 10000, 0, "down");
        Assert.True(down.Max / up.Max >= 50);
        Assert.True(NetworkSeries.UpdateAxis(up, 10000, 1, "up").Max >= 10000);
        var low = NetworkSeries.UpdateAxis(down, 10, 1000, "down");
        Assert.Equal(down.Max, NetworkSeries.UpdateAxis(low, 10, 8999, "down").Max);
        Assert.True(NetworkSeries.UpdateAxis(low, 10, 9000, "down").Max < down.Max);
        Assert.True(NetworkSeries.UpdateAxis(down, 10, 1, "new-selection").Max < down.Max);
        Assert.Equal(1, NetworkSeries.NiceCeiling(0));
    }
    [Fact]
    public void ThinningKeepsEndpointsAndSpikeWithinBudget()
    {
        var sample = Samples("mixed-5s-1s")[0];
        var points = Enumerable.Range(0, 1000).Select(i => new NetworkSeries.Point(sample.SampledAt.AddSeconds(i), i == 517 ? 1e8 : i % 71, sample)).ToArray();
        var result = NetworkSeries.Thin(points);
        Assert.True(result.Count <= 160); Assert.Equal(points[0], result[0]); Assert.Equal(points[^1], result[^1]);
        Assert.Contains(points[517], result);
    }
}
