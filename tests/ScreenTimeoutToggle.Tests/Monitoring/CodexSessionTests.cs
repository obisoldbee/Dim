using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// End-to-end Codex app-server session tests against a stub CLI launched through a real
/// npm-style .cmd shim (the production launch path: node.exe + parsed script, never
/// cmd.exe). Covers the two process-boundary defects found in the 2026-09-15 review:
/// chatty stderr that nobody drains stalls the protocol, and a stdout flood must surface
/// as <see cref="ProviderErrorKind.OutputLimitExceeded"/>, not ExecutionFailed.
/// Skips quietly when node is not installed (env, not mechanism — same policy as
/// CliProcessRunnerTests).
/// </summary>
public class CodexSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"obdim-codexstub-{Guid.NewGuid():N}");

    public CodexSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static bool NodeAvailable() => ManagedProcess.FindNodeExe() is not null;

    /// <summary>Writes the stub script plus an npm-shaped shim so CliLocator resolves it exactly like a real CLI.</summary>
    private ProviderSettings StubSettings(string scriptBody)
    {
        var jsPath = Path.Combine(_dir, "stub-app-server.js");
        File.WriteAllText(jsPath, scriptBody);
        var shimPath = Path.Combine(_dir, "stub.cmd");
        File.WriteAllText(shimPath, """
            @IF EXIST "%~dp0\node.exe" (
              "%~dp0\node.exe"  "%~dp0\stub-app-server.js" %*
            ) ELSE (
              node  "%~dp0\stub-app-server.js" %*
            )
            """);
        return new ProviderSettings { Id = ProviderId.Codex, Enabled = true, CliPath = shimPath };
    }

    private static CodexProvider MakeProvider() =>
        new(new FakeClock(), new CliProcessRunner(), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ChattyStderr_DoesNotStallTheSession()
    {
        if (!NodeAvailable()) return;

        // Blocks on a 256 KB blocking stderr write before answering anything — mimics an
        // app-server whose log burst exceeds the ~4 KB pipe buffer. Without a stderr pump
        // the child wedges inside that write and every request times out (v1.1.2 P1 fix).
        var settings = StubSettings("""
            const fs = require('fs');
            if (process.argv.includes('--version')) { console.log('stub 1.0'); process.exit(0); }
            const noise = 'E'.repeat(262144);
            let off = 0;
            while (off < noise.length) { off += fs.writeSync(2, noise, off, noise.length - off); }
            let buf = '';
            process.stdin.setEncoding('utf8');
            process.stdin.on('data', d => {
              buf += d;
              let i;
              while ((i = buf.indexOf('\n')) >= 0) {
                const line = buf.slice(0, i);
                buf = buf.slice(i + 1);
                try {
                  const frame = JSON.parse(line);
                  if (frame && frame.id !== undefined) {
                    fs.writeSync(1, JSON.stringify({ id: frame.id, result: {} }) + '\n');
                  }
                } catch {}
              }
            });
            setTimeout(() => process.exit(0), 15000);
            """);

        var snapshot = await MakeProvider().QueryAsync(settings, CancellationToken.None);

        // The stub answers every request with an empty result, which the rate-limits
        // mapper rejects — the point is that the session COMPLETED instead of timing out.
        Assert.NotEqual(ProviderErrorKind.Timeout, snapshot.Error);
        Assert.Equal(ProviderErrorKind.ParseFailed, snapshot.Error);
    }

    [Fact]
    public async Task StdoutFlood_MapsToOutputLimitExceeded()
    {
        if (!NodeAvailable()) return;

        var settings = StubSettings("""
            const fs = require('fs');
            if (process.argv.includes('--version')) { console.log('stub 1.0'); process.exit(0); }
            const line = 'J'.repeat(63) + '\n';
            for (let i = 0; i < 20000; i++) { fs.writeSync(1, line); }
            process.exit(0);
            """);

        var snapshot = await MakeProvider().QueryAsync(settings, CancellationToken.None);

        // 20000 x 64 chars ≈ 1.25 MB chars > the 512 K char cap (1 MiB bytes / sizeof(char)).
        // The channel marks itself dead; the classification must be 输出超限 like Ark and
        // MiniMax report it (spec §5.3), not a generic execution failure (v1.1.2 P2 fix).
        Assert.Equal(ProviderErrorKind.OutputLimitExceeded, snapshot.Error);
    }
}
