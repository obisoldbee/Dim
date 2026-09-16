using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Services;

/// <summary>
/// Merges a partial refresh result into the previous good snapshot (PRD §5.2(8)):
/// "局部更新只替换本次有权威数据的桶/字段，保留旧桶时保留旧时间". A partial result is a
/// SUCCESS snapshot in which some buckets carry a per-bucket <see cref="QuotaBucket.Error"/>
/// (Ark). Those errored buckets must not degrade into a bare error row — the previous
/// good value and its old reset times stay on display until a refresh succeeds for them.
/// </summary>
public static class SnapshotMerger
{
    /// <summary>
    /// Keeps every authoritative bucket from <paramref name="partial"/>; for errored
    /// buckets, substitutes the matching error-free bucket from <paramref name="previousGood"/>
    /// (matched by <see cref="QuotaBucket.SourceKey"/>). Errored buckets with no previous
    /// good counterpart stay as-is — inventing data is never an option. Buckets that the
    /// source stopped reporting entirely are not resurrected. Callers must ensure both
    /// snapshots belong to the same identity.
    /// </summary>
    public static ProviderSnapshot MergePartial(ProviderSnapshot previousGood, ProviderSnapshot partial)
    {
        if (!partial.IsPartial || previousGood is null)
        {
            return partial;
        }

        var oldGoodByKey = new Dictionary<string, QuotaBucket>(StringComparer.Ordinal);
        foreach (var bucket in previousGood.Buckets)
        {
            if (bucket.Error is null)
            {
                // Last write wins — duplicates are not expected, and the newest entry wins.
                oldGoodByKey[bucket.SourceKey] = bucket;
            }
        }

        var merged = new List<QuotaBucket>(partial.Buckets.Count);
        foreach (var bucket in partial.Buckets)
        {
            merged.Add(bucket.Error is null
                ? bucket
                : oldGoodByKey.TryGetValue(bucket.SourceKey, out var oldGood) ? oldGood with { RetainedFailureKind=bucket.ErrorKind==ProviderErrorKind.None?ProviderErrorKind.ApiError:bucket.ErrorKind } : bucket);
        }

        return partial with { Buckets = merged };
    }
}
