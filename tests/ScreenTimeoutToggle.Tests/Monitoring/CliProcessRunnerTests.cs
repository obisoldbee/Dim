using OBDim.Monitoring.Infrastructure;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// Process-layer tests against REAL child processes (cmd.exe), covering the contract the
/// quota providers depend on: exit codes, output capture, the 1 MiB cap, timeout kill and
/// cancellation — plus the Job Object tree-kill that guarantees no orphaned CLI children.
/// </summary>
public class CliProcessRunnerTests
{
    private static CliRequest Cmd(string args, TimeSpan? timeout = null, long? maxOutput = null) =>
        new()
        {
            ExePath = Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe",
            Arguments = ["/d", "/c", args],
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
            MaxOutputBytes = maxOutput ?? 1024 * 1024,
        };

    [Fact]
    public async Task Success_CapturesStdout_ExitCodeAndStderr()
    {
        var runner = new CliProcessRunner();
        var result = await runner.RunAsync(Cmd("echo hello-stdout & echo hello-stderr 1>&2"), CancellationToken.None);

        Assert.True(result.Success, $"exit={result.ExitCode} timedOut={result.TimedOut}");
        Assert.Contains("hello-stdout", result.Stdout);
        Assert.Contains("hello-stderr", result.StderrTail);
    }

    [Fact]
    public async Task NonZeroExit_IsReported()
    {
        var runner = new CliProcessRunner();
        var result = await runner.RunAsync(Cmd("exit /b 7"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task OutputBeyondCap_IsTruncated()
    {
        var runner = new CliProcessRunner();
        // ~230 KB of native cmd output vs a 64 KB cap.
        var result = await runner.RunAsync(
            Cmd("for /l %i in (1,1,5000) do @echo XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", maxOutput: 64 * 1024),
            CancellationToken.None);

        Assert.True(result.OutputTruncated);
        Assert.True(result.Stdout.Length <= 64 * 1024 / sizeof(char) + 4096,
            $"stdout len {result.Stdout.Length}");
    }

    [Fact]
    public async Task Timeout_KillsProcess()
    {
        var runner = new CliProcessRunner();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Cmd("ping -n 60 127.0.0.1 > nul", timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None);

        sw.Stop();
        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"kill took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task Cancel_KillsProcess()
    {
        var runner = new CliProcessRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await runner.RunAsync(
            Cmd("ping -n 60 127.0.0.1 > nul", timeout: TimeSpan.FromSeconds(30)),
            cts.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task MissingExe_LaunchError_NoThrow()
    {
        var runner = new CliProcessRunner();
        var result = await runner.RunAsync(
            new CliRequest { ExePath = @"C:\definitely\not\here.exe", Timeout = TimeSpan.FromSeconds(5) },
            CancellationToken.None);

        Assert.NotNull(result.LaunchError);
        Assert.False(result.Success);
    }

    /// <summary>
    /// The whole reason the Job Object exists: a child that spawns ITS OWN child must lose
    /// the entire tree when we time out — the user's machine must not accumulate orphaned
    /// pingers. Uses node to create a detached grandchild whose PID we read from stdout.
    /// Skips quietly when node is not installed (the property, not the mechanism, is env).
    /// </summary>
    [Fact]
    public async Task Timeout_KillsEntireTree_IncludingGrandchild()
    {
        var nodePath = ManagedProcess.FindNodeExe();
        if (nodePath is null)
        {
            return; // node not installed here; Job Object kill is still covered by Timeout_KillsProcess
        }

        var script = Path.Combine(Path.GetTempPath(), $"obdim-tree-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(script, """
            const { spawn } = require('child_process');
            const grandchild = spawn('ping', ['-n', '60', '127.0.0.1'], { stdio: 'ignore', detached: true });
            console.log('GRANDCHILD_PID=' + grandchild.pid);
            grandchild.unref();
            setTimeout(() => process.exit(0), 60000);
            """);

        var runner = new CliProcessRunner();
        try
        {
            // 10 s, not 3 s: the point of this test is that a TIMEOUT kills the whole tree,
            // and a cold node start under a parallel test run can easily eat 3 s — which
            // killed node before it ever printed the grandchild PID and turned a passing
            // test into a flaky one. The script runs for 60 s, so a timeout still happens.
            var result = await runner.RunAsync(
                new CliRequest { ExePath = nodePath, Arguments = [script], Timeout = TimeSpan.FromSeconds(10) },
                CancellationToken.None);
            Assert.True(result.TimedOut);

            var pidLine = result.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("GRANDCHILD_PID="));
            Assert.NotNull(pidLine);
            var pid = int.Parse(pidLine!["GRANDCHILD_PID=".Length..].Trim());

            // Give the OS a beat to finish the job-wide termination.
            await Task.Delay(500);
            bool grandchildAlive;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                grandchildAlive = !p.HasExited;
            }
            catch (ArgumentException)
            {
                grandchildAlive = false; // no such process — killed, exactly what we want
            }
            catch (InvalidOperationException)
            {
                grandchildAlive = false; // already terminated
            }
            Assert.False(grandchildAlive, "grandchild survived the job kill — the tree is not being cleaned up");
        }
        finally
        {
            File.Delete(script);
        }
    }
}
