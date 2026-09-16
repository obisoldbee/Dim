using System.Text.Json;
using OBDim.Monitoring.Models;
namespace OBDim.Monitoring.Providers;

public static class ArkPlanMetadata
{
    public static IReadOnlyList<QuotaBucket> Apply(IReadOnlyList<QuotaBucket> buckets,string json)
    {
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.TryGetProperty("plans") is not {ValueKind:JsonValueKind.Array} plans) return buckets;
            var candidates=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(var plan in plans.EnumerateArray())
            {
                var key=plan.TryGetProperty("key").AsNullableString();
                var scope=plan.TryGetProperty("scope").AsNullableString();
                var tier=plan.TryGetProperty("tier").AsNullableString()?.Trim().ToLowerInvariant();
                if(scope?.ToLowerInvariant()!="personal" || key is null || tier is null || plan.TryGetProperty("error") is not null) continue;
                if(!((key=="coding-plan" && tier is "lite" or "pro") || (key=="agent-plan" && tier is "small" or "medium" or "large" or "max"))) continue;
                if(!candidates.TryGetValue(key,out var entries)) candidates[key]=entries=[];
                entries.Add(tier);
            }
            return buckets.Select(b=> b.Subscribed==true && b.Error is null && string.IsNullOrWhiteSpace(b.Tier) &&
                candidates.TryGetValue(b.SourceKey,out var tiers) && tiers.Count==1 ? b with {Tier=tiers[0]} : b).ToArray();
        }
        catch(JsonException) { return buckets; }
    }
}
