using System.Text.Json;
using System.Text.RegularExpressions;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Providers;

public sealed record ProviderFailure(ProviderErrorKind Kind, string Code);

/// <summary>Classify evidence privately; only fixed codes leave this boundary.</summary>
public static class ProviderFailureClassifier
{
    public static bool IsAuthenticationFailure(ProviderErrorKind kind) =>
        kind is ProviderErrorKind.NotSignedIn or ProviderErrorKind.AuthenticationExpired;

    public static ProviderFailure Classify(string? text, int? exitCode = null)
    {
        var value = (text ?? "").ToLowerInvariant();
        try { using var parsed=JsonDocument.Parse(text ?? ""); value=Evidence(parsed.RootElement).ToLowerInvariant(); }
        catch(JsonException) { }
        bool Has(params string[] values) => values.Any(value.Contains);
        if (Has("invalid_refresh_token", "refresh_token is invalid", "refresh token is invalid",
            "refresh token expired", "refresh token has expired", "refresh_token expired", "invalid_grant",
            "token has expired", "token expired", "session expired", "登录已过期", "认证已过期", "凭证已过期"))
            return new(ProviderErrorKind.AuthenticationExpired, "auth.session_invalid");
        if (Has("not logged in", "not signed in", "needs_login", "please login", "please log in",
            "please run `arkcli auth login", "authentication required", "authfailure", "unauthorized", "invalid api key",
            "invalid_api_key", "invalidapikey", "未登录", "请先登录") || Http(value, 401))
            return new(ProviderErrorKind.NotSignedIn, "auth.login_required");
        if (Has("subscription expired", "plan expired", "subscription_expired", "plan_expired", "套餐已过期", "订阅已过期", "套餐过期"))
            return new(ProviderErrorKind.SubscriptionExpired, "plan.expired");
        if (Has("not subscribed", "no active subscription", "subscription_not_found", "未订阅", "未开通套餐"))
            return new(ProviderErrorKind.NoSubscription, "plan.not_subscribed");
        if (Has("quota exhausted", "quota_exhausted", "insufficient_quota", "quota exceeded", "额度已用完", "额度耗尽"))
            return new(ProviderErrorKind.QuotaExhausted, "quota.exhausted");
        if (Has("permission denied", "access denied", "accessdenied", "forbidden", "权限不足", "无权访问") || Http(value,403))
            return new(ProviderErrorKind.PermissionDenied, "access.denied");
        if (Has("rate limit", "rate_limit", "too many requests", "请求过于频繁") || Http(value,429))
            return new(ProviderErrorKind.RateLimited, "request.rate_limited");
        if (Has("econnrefused", "econnreset", "enotfound", "getaddrinfo", "network is unreachable", "no such host",
            "proxyconnect", "tls handshake", "certificate verify", "connection refused", "连接失败", "网络不可达"))
            return new(ProviderErrorKind.NetworkError, "network.unavailable");
        if (Has("timed out", "timeout", "deadline exceeded", "超时"))
            return new(ProviderErrorKind.Timeout, "request.timeout");
        if (Has("service unavailable", "bad gateway", "internal server error") || Regex.IsMatch(value,@"\b(?:http|status|status_code)\s*[:= ]\s*5\d\d\b"))
            return new(ProviderErrorKind.ServiceUnavailable, "service.unavailable");
        if (Has("unknown command", "unknown flag", "unrecognized argument", "unexpected argument", "unknown subcommand"))
            return new(ProviderErrorKind.CliVersionUnsupported, "cli.command_unsupported");
        return new(exitCode is not null and not 0 ? ProviderErrorKind.ExecutionFailed : ProviderErrorKind.ApiError,
            exitCode is not null and not 0 ? "cli.nonzero_exit" : "api.unclassified");
    }

    private static bool Http(string text, int code) => Regex.IsMatch(text,
        $@"\b(?:http(?:/\d(?:\.\d)?)?|status(?:_code)?|response)\s*[:= ]\s*{code}\b");

    public static ProviderSnapshot FromResult(ProviderId id, CliResult result, DateTimeOffset at)
    {
        ProviderFailure failure;
        if (result.Cancelled) failure = new(ProviderErrorKind.Cancelled,"request.cancelled");
        else if (result.TimedOut) failure = new(ProviderErrorKind.Timeout,"request.timeout");
        else if (result.OutputTruncated) failure = new(ProviderErrorKind.OutputLimitExceeded,"cli.output_limit");
        else if (result.LaunchError is not null) failure = new(ProviderErrorKind.ExecutionFailed,"cli.launch_failed");
        else
        {
            failure=Classify(result.Stdout,result.ExitCode);
            if(failure.Kind is ProviderErrorKind.ExecutionFailed or ProviderErrorKind.ApiError)
                failure=Classify(result.StderrTail,result.ExitCode);
        }
        return SnapshotFactory.Failure(id,failure.Kind,null,at) with { ErrorCode=failure.Code,ExitCode=result.ExitCode };
    }

    private static string Evidence(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Object => string.Join(" ",value.EnumerateObject().Where(p=>p.Name is "error" or "message" or "code" or "status" or "status_code" or "detail" or "description").Select(p=>p.Name+" "+Evidence(p.Value))),
        JsonValueKind.Array => string.Join(" ",value.EnumerateArray().Select(Evidence)),
        _ => value.ToString(),
    };

    public static ProviderFailure? Envelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var error=root.TryGetProperty("error");
        var ok=root.TryGetProperty("ok").AsNullableBool();
        var success=root.TryGetProperty("success").AsNullableBool();
        if (error is { ValueKind: not JsonValueKind.Null and not JsonValueKind.False } || ok==false || success==false)
            return Classify(Evidence(root));
        return null;
    }
}
