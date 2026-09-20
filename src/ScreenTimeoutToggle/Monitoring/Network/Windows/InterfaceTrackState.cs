using OBDim.Monitoring.Network.Models;
using OBDim.Monitoring.Network.Validation;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// Per-interface cumulative-counter tracker. Owns the interface epoch (contract §3.3):
/// a counter wrap (current &lt; previous) or a re-enumeration after disappearance starts
/// a new epoch; the first sample of every epoch reports null rates — never a negative or
/// fabricated rate. Rates use the REAL elapsed time between samples (Δbytes/Δt).
/// History is bounded by the dual limit (count AND time window); real gaps stay gaps.
/// </summary>
internal sealed class InterfaceTrackState
{
    private readonly List<TrafficPoint> _history = [];

    public InterfaceTrackState(InterfaceCounterRow firstRow)
    {
        Id = firstRow.Id;
        Name = firstRow.Name;
        Kind = firstRow.Kind;
    }

    public string Id { get; }
    public string Name { get; private set; }
    public InterfaceKind Kind { get; private set; }
    public long Epoch { get; private set; }

    private ulong? _lastRx;
    private ulong? _lastTx;
    private long _lastSampleAtMs = -1;
    private bool _wasMissingLastCycle;

    /// <summary>Marks the interface absent this cycle; its reappearance counts as re-enumeration.</summary>
    public void MarkMissing() => _wasMissingLastCycle = true;

    /// <summary>
    /// Folds one counter row into the track and returns the observation for this snapshot.
    /// <paramref name="trimmed"/> is true when the dual history bound cut old points.
    /// </summary>
    public InterfaceObservation Observe(
        InterfaceCounterRow row, long nowMs, NetworkCollectorOptions limits, out bool trimmed)
    {
        Name = row.Name;
        Kind = row.Kind;

        var rateReset = false;
        if (_wasMissingLastCycle && _lastRx is not null)
        {
            Epoch++; // removed and re-enumerated (contract §3.3 interface epoch)
            rateReset = true;
        }
        _wasMissingLastCycle = false;

        double? upRate = null;
        double? downRate = null;

        if (_lastRx is not null && _lastTx is not null && !rateReset)
        {
            if (row.RxBytes < _lastRx.Value || row.TxBytes < _lastTx.Value)
            {
                // Counter wrap / reset: new epoch, first sample of the epoch has unknown
                // rates. A wrap is NOT a truncation and NOT a dropped event.
                Epoch++;
            }
            else if (_lastSampleAtMs >= 0 && nowMs > _lastSampleAtMs)
            {
                var dtSeconds = (nowMs - _lastSampleAtMs) / 1000.0;
                downRate = (row.RxBytes - _lastRx.Value) / dtSeconds;
                upRate = (row.TxBytes - _lastTx.Value) / dtSeconds;
            }
            // nowMs <= _lastSampleAtMs: no real elapsed time — rates stay unknown, never guessed.
        }

        _lastRx = row.RxBytes;
        _lastTx = row.TxBytes;
        _lastSampleAtMs = nowMs;

        // A point is appended only for a strictly later timestamp (S3); a same-ms re-read
        // is a correction of the current rates, not a second observation.
        if (_history.Count == 0 || _history[^1].T < nowMs)
        {
            _history.Add(new TrafficPoint { T = nowMs, Up = upRate, Down = downRate });
        }
        else if (_history[^1].T == nowMs)
        {
            _history[^1] = new TrafficPoint { T = nowMs, Up = upRate, Down = downRate };
        }

        trimmed = TrimHistory(nowMs, limits);

        return new InterfaceObservation
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            UpRate = upRate,
            DownRate = downRate,
            RxBytes = ToSignedCount(row.RxBytes),
            TxBytes = ToSignedCount(row.TxBytes),
            Epoch = Epoch,
            History = [.. _history],
        };
    }

    // Wrap arithmetic is done unsigned, but the contract field is a JSON integer capped
    // at 2^53-1: a count beyond what the contract can carry becomes unknown rather than an
    // out-of-range (or wrapped negative) reading.
    private static long? ToSignedCount(ulong? bytes) =>
        bytes is null || bytes.Value > NetworkSnapshotValidator.MaxSafeInt
            ? null
            : (long)bytes.Value;

    /// <summary>Dual bound: oldest points are cut when EITHER the window or the count overflows.</summary>
    private bool TrimHistory(long nowMs, NetworkCollectorOptions limits)
    {
        var trimmed = false;
        var cutoff = nowMs - limits.HistoryWindowMs;
        var start = 0;
        while (start < _history.Count && _history[start].T < cutoff) start++;
        if (start > 0)
        {
            _history.RemoveRange(0, start);
            trimmed = true;
        }
        if (_history.Count > limits.HistoryMaxPoints)
        {
            _history.RemoveRange(0, _history.Count - limits.HistoryMaxPoints);
            trimmed = true;
        }
        return trimmed;
    }
}
