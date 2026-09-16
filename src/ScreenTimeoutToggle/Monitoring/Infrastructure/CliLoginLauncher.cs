using System.Diagnostics;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Infrastructure;

/// <summary>Official interactive CLI only; no passwords, tokens, or shell command strings.</summary>
public static class CliLoginLauncher
{
    internal static IReadOnlyList<string> Arguments(ProviderId id) => id switch
    {
        ProviderId.Codex => ["login"],
        ProviderId.MiniMax => ["auth", "login", "--recommend"],
        ProviderId.Ark => ["auth", "login", "volc-sso"],
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    internal static ProcessStartInfo BuildStartInfo(ProviderSettings settings)
    {
        var cli = settings.Id switch { ProviderId.Codex => "codex", ProviderId.MiniMax => "mmx", _ => "arkcli" };
        var location = CliLocator.Locate(settings.CliPath,cli);
        if (!location.Found) throw new IOException("cli.unavailable");
        var start = new ProcessStartInfo { UseShellExecute=false, CreateNoWindow=false,
            WindowStyle=ProcessWindowStyle.Normal,
            FileName=location.NodeScriptPath is null ? location.ExePath! : ManagedProcess.FindNodeExe() ?? throw new IOException("cli.node_not_found") };
        if (location.NodeScriptPath is not null) start.ArgumentList.Add(location.NodeScriptPath);
        foreach (var arg in Arguments(settings.Id)) start.ArgumentList.Add(arg);
        if (settings.Id == ProviderId.Ark) start.Environment["ARKCLI_NO_UPDATE_NOTIFIER"]="1";
        return start;
    }

    public static async Task<int> LoginAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = Process.Start(BuildStartInfo(settings)) ?? throw new IOException("cli.start_failed");
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); return process.ExitCode; }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree:true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
