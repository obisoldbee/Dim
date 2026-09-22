namespace OBDim.Monitoring.Network.V2;

/// <summary>Independent directional segments, matching the handoff counter fixtures.</summary>
public sealed class CounterSettlement
{
    private sealed record State(ulong? Raw, string Epoch, ulong Mono, DirectionTotal Total);
    private readonly Dictionary<(string, string, string, bool), State> _states = [];
    public int ResetCount { get; private set; }
    public Directions<DirectionTotal> Apply(CounterInput row)
    {
        DirectionTotal Fold(bool upload)
        {
            var key = (row.SourceID, row.InterfaceID, row.CaptureSessionID, upload);
            _states.TryGetValue(key, out var p);
            if (p is not null && row.MonotonicNs <= p.Mono) return p.Total;
            var raw = row.Bytes[upload];
            string? reason = null;
            if (p is not null && p.Epoch != row.CounterEpoch) reason = "epoch-switch";
            else if (p?.Raw is { } old && raw is { } current && current < old) reason = "counter-reset-or-wrap";
            else if (row.GapBefore?[upload] is { } gap) reason = gap;
            else if (p is not null && p.Raw is null && raw is not null) reason = "counter-unavailable";
            DirectionTotal total;
            if (raw is null) total = new(null, null, null, false, "counter-unavailable");
            else if (p is null || reason is not null)
            {
                total = new(null, row.SampledAt, row.MonotonicNs, reason is null, reason);
                if (reason is not null) ResetCount++;
            }
            else
            {
                // checked arithmetic avoids silently wrapping a multi-interval total.
                try { total = p.Total with { Bytes = checked((p.Total.Bytes ?? 0) + (raw.Value - p.Raw!.Value)) }; }
                catch (OverflowException) { total = new(null, row.SampledAt, row.MonotonicNs, false, "counter-overflow"); }
            }
            _states[key] = new(raw, row.CounterEpoch, row.MonotonicNs, total);
            return total;
        }
        return new(Fold(true), Fold(false));
    }
}
