using System.Text.Json.Serialization;

namespace OBDim.Monitoring.Network.V2;

// Domain v2 is separate from the N0 wire contract. Names map to the common fixtures.
// Raw UInt64 counters never pass through floating point.
public sealed record Directions<T>(T Upload, T Download)
{
    public T this[bool upload] => upload ? Upload : Download;
}
public sealed record SampleCadence(double SourceIntervalMs, double? HistoryIntervalMs, string RegimeID, string Reason);
public sealed record DirectionContinuity(string State, string? PreviousSampleID, string? Reason);
public sealed record RateSample(
    string SampleID, string SourceID, string InterfaceID, string CaptureSessionID,
    string CounterEpoch, DateTimeOffset SampledAt, DateTimeOffset PublishedAt,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong MonotonicNs,
    double RateWindowMs, SampleCadence Cadence,
    Directions<DirectionContinuity> Continuity, Directions<double?> Rates);
public sealed record DirectionTotal(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong? Bytes,
    DateTimeOffset? Since,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong? SinceMonotonicNs,
    bool Continuous, string? BreakReason, string Scope = "observedSegment");
public sealed record CounterInput(
    string SourceID, string InterfaceID, string CaptureSessionID, string CounterEpoch,
    DateTimeOffset SampledAt,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] ulong MonotonicNs,
    Directions<ulong?> Bytes, Directions<string?>? GapBefore = null);
public sealed record InterfaceReading(string Id, string Name, string Kind,
    DateTimeOffset? SampledAt, Directions<double?> Rates, Directions<DirectionTotal> Totals,
    Directions<ulong?> RawCounters, IReadOnlyList<RateSample> Samples, int ResetCount,
    bool HistoryTruncated, bool Available = true);
public sealed record ObservationCapabilities(bool Observe = true, bool PerApp = false,
    bool Targets = false, bool BlockNewConnections = false, bool TerminateExistingConnections = false);
public sealed record ObservationSnapshot(long Version, string SessionID, DateTimeOffset PublishedAt,
    string State, string? Reason, string? SystemInterfaceID, IReadOnlyList<InterfaceReading> Interfaces)
{
    public const string SchemaVersion = "obdim.network.observation.v2";
    public string Origin => "native";
    public bool IsDemo => false;
    public ObservationCapabilities Capabilities { get; } = new();
    public static ObservationSnapshot Empty { get; } = new(0, "", DateTimeOffset.MinValue, "stopped", null, null, []);
}
public interface INetworkObservationSource
{
    ObservationSnapshot Current { get; }
    bool IsRunning { get; }
    event Action? Changed;
    void SetEnabled(bool enabled);
    void Refresh();
}
