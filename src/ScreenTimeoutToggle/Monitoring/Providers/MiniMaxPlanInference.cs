using System.Text.Json;
namespace OBDim.Monitoring.Providers;

/// <summary>Port of Usage-Butler minimax-video-daily-v1 approved entitlement rule.
/// This is an inference, never a provider-reported subscription name.</summary>
public static class MiniMaxPlanInference
{
    public static string? Resolve(JsonElement root)
    {
        if(root.TryGetProperty("base_resp").TryGetProperty("status_code").AsNullableInt32()!=0 ||
           root.TryGetProperty("model_remains") is not { ValueKind:JsonValueKind.Array } models) return null;
        var names=new HashSet<string>(StringComparer.Ordinal);
        JsonElement? video=null;
        foreach(var model in models.EnumerateArray())
        {
            var name=model.TryGetProperty("model_name").AsNullableString();
            if(string.IsNullOrWhiteSpace(name) || !names.Add(name)) return null;
            // An incomplete or invalid row makes the read unsuitable for plan inference.
            if(!Valid(model,false) || !Valid(model,true)) return null;
            if(name=="video") video=model;
        }
        if(video is not {} row) return null;
        var status=row.TryGetProperty("current_interval_status").AsNullableInt32();
        var weeklyStatus=row.TryGetProperty("current_weekly_status").AsNullableInt32();
        if(status is not (1 or 3) || weeklyStatus is not (1 or 3)) return null;
        var total=row.TryGetProperty("current_interval_total_count").AsNullableInt64();
        var weeklyTotal=row.TryGetProperty("current_weekly_total_count").AsNullableInt64();
        if(status==1 && total==3) return "Max";
        if(status==1 && total==5) return "Ultra";
        if(status==3 && weeklyStatus==3 && total==0 && weeklyTotal==0) return "Plus";
        return null;
    }
    private static bool Valid(JsonElement row,bool weekly)
    {
        var prefix=weekly?"weekly_":"";
        var values=weekly?"current_weekly_":"current_interval_";
        var start=row.TryGetProperty(prefix+"start_time").AsNullableInt64();
        var end=row.TryGetProperty(prefix+"end_time").AsNullableInt64();
        var remains=row.TryGetProperty(prefix+"remains_time").AsNullableInt64();
        var total=row.TryGetProperty(values+"total_count").AsNullableInt64();
        var used=row.TryGetProperty(values+"usage_count").AsNullableInt64();
        var percent=row.TryGetProperty(values+"remaining_percent").AsNullableDouble();
        return start is >0 && end>start && remains is >=0 && remains<=end-start &&
            total is >=0 && used is >=0 && used<=total && percent is >=0 and <=100;
    }
}
