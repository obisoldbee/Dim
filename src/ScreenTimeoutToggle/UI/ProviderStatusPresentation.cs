using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;

namespace OBDim.UI;

public sealed record ProviderStatusText(string Title,string Detail,bool NeedsLogin=false);

public static class ProviderStatusPresentation
{
    public static ProviderStatusText Failure(ProviderErrorKind kind, int? exitCode=null, bool english=false)
    {
        (string zh,string en,string detailZh,string detailEn) text = kind switch
        {
            ProviderErrorKind.AuthenticationExpired => ("认证已失效","Authentication expired","登录凭据已过期或续期被拒绝。请重新登录；当前无法核实额度。","The session expired or renewal was rejected. Sign in again; quota cannot currently be verified."),
            ProviderErrorKind.NotSignedIn => ("需要登录","Sign-in required","未登录或凭据被拒绝。请使用官方登录入口重新授权。","No accepted credentials. Sign in through the official CLI."),
            ProviderErrorKind.CliNotFound => ("未找到 CLI","CLI not found","请展开 CLI 路径，选择已安装的程序后保存并重新检测。","Select the installed CLI path, save, then check again."),
            ProviderErrorKind.UnsupportedEntry => ("CLI 入口不受支持","Unsupported CLI entry","请选择 .exe 或有效的 npm .cmd 入口。","Select an executable or a supported npm .cmd entry."),
            ProviderErrorKind.CliVersionUnsupported => ("CLI 命令不兼容","Incompatible CLI version","当前程序不支持所需命令或参数。请更新对应 CLI 后重试。","The CLI does not support the required command or options. Update it and retry."),
            ProviderErrorKind.PermissionDenied => ("账号权限不足","Permission denied","服务端拒绝访问。请检查账号、项目或团队席位权限。","The service denied access. Check account, project or team-seat permissions."),
            ProviderErrorKind.SubscriptionExpired => ("套餐已过期","Subscription expired","服务端明确报告订阅已过期。请到服务商控制台检查或续订。","The service explicitly reported an expired subscription. Check it in the provider console."),
            ProviderErrorKind.NoSubscription => ("未订阅此套餐","No active subscription","服务端明确报告没有有效订阅。请核对当前账号的套餐。","The service reported no active subscription. Check the current account's plan."),
            ProviderErrorKind.QuotaExhausted => ("额度已用完","Quota exhausted","服务端明确报告额度耗尽。请等待额度重置或检查套餐权益。","The service explicitly reported exhausted quota. Wait for reset or check plan entitlements."),
            ProviderErrorKind.RateLimited => ("查询受到限流","Queries rate-limited","查询过于频繁。稍后重试；限流本身不能证明套餐额度已用完。","Too many requests. Retry later; throttling alone does not establish exhausted plan quota."),
            ProviderErrorKind.NetworkError => ("网络连接失败","Network connection failed","无法连接服务。请检查网络、代理或证书后重试。","Check the network, proxy or certificate configuration and retry."),
            ProviderErrorKind.ServiceUnavailable => ("服务暂时不可用","Service unavailable","服务端返回故障。请稍后重试。","The provider returned a server error. Retry later."),
            ProviderErrorKind.Timeout => ("查询超时","Query timed out","在时限内未收到完整结果。请检查网络或代理后重试。","No complete result arrived within the time limit. Check the connection and retry."),
            ProviderErrorKind.ParseFailed => ("无法解析 CLI 返回","Unrecognized CLI response","返回格式与已支持的协议不符。请检查 CLI 版本并导出诊断。","The response does not match the supported protocol. Check the CLI version and export diagnostics."),
            ProviderErrorKind.OutputLimitExceeded => ("CLI 输出异常过大","CLI output limit exceeded","输出超过安全上限，已停止本次查询。请检查 CLI 版本与路径。","The response exceeded the output limit. Check the CLI version and path."),
            ProviderErrorKind.Cancelled => ("检测已取消","Check cancelled","可重新检测。","Check again when ready."),
            ProviderErrorKind.ApiError => ("服务接口返回错误","Provider API error","尚不能确定为认证或套餐问题。请重试或导出诊断查看分类代码。","No supported authentication or plan cause was established. Retry or export diagnostics."),
            _ => ("CLI 执行失败","CLI execution failed","CLI 未正常完成，返回信息不足以确定具体原因。请重新检测或导出诊断。","The CLI did not complete normally; its output did not establish a specific cause. Retry or export diagnostics."),
        };
        var title=english?text.en:text.zh;
        if (exitCode is not null and not 0) title += english?$" (exit {exitCode})":$"（退出码 {exitCode}）";
        return new(title,english?text.detailEn:text.detailZh,ProviderFailureClassifier.IsAuthenticationFailure(kind));
    }

    public static ProviderStatusText Connection(ProviderDisplayState state,bool english=false)
    {
        if (state.Authenticating) return new(english?"Waiting for sign-in":"正在登录",english?"Complete the official CLI or browser flow. A fresh query will verify it afterwards.":"请在官方 CLI 窗口或浏览器完成操作；结束后会重新查询验证。");
        if (!state.Enabled) return new(english?"Monitoring disabled":"监控已关闭",english?"This does not sign out the CLI.":"关闭监控不会退出 CLI 登录。");
        if (state.Refreshing) return new(english?"Checking connection and quota…":"正在检测连接与额度…",english?"Waiting for an authenticated provider response.":"正在等待服务端返回，检测完成前不会显示已连接。");
        if (state.LastAttempt is { HasError:true } failed)
        {
            var result=Failure(failed.Error,failed.ExitCode,english);
            if (state.LastGood?.SucceededAtUtc is { } old)
                result=result with { Detail=result.Detail+(english?$" Last successful data: {old.ToLocalTime():MM/dd HH:mm:ss}.":$" 上次成功数据：{old.ToLocalTime():MM/dd HH:mm:ss}。") };
            return result;
        }
        if (state.LastGood is { } good)
        {
            var date=good.SucceededAtUtc?.ToLocalTime().ToString("MM/dd HH:mm:ss")??"—";
            if (good.IsPartial) return new(english?"Partially updated":"部分项目查询失败",english?$"Some entries were not updated. Last query: {date}.":$"部分项目未更新，请查看额度面板的具体项目。最近查询：{date}。");
            if (state.Stale) return new(english?"Previous data is stale":"上次数据已过期",english?$"Last success: {date}. Check again to verify current access.":$"上次成功：{date}。请重新检测当前登录与额度。");
            return new(english?"Connected":"已连接",english?$"Provider query succeeded at {date}. CLI credentials were accepted.":$"{date} 查询成功，服务端已接受当前凭据。");
        }
        return new(english?"Not checked":"尚未检测",english?"Check the connection to verify credentials and quota.":"点击重新检测，核实当前凭据和额度。");
    }
}
