using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OBDim.Monitoring.Infrastructure;

/// <summary>Request for one run-to-completion CLI invocation.</summary>
public sealed record CliRequest
{
    public required string ExePath { get; init; }

    /// <summary>When set, the launch is node.exe running this script; ExePath is only the located shim (for logs).</summary>
    public string? NodeScriptPath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Total timeout including spawn. Default: spec §7 process boundary (30 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra wait after kill before giving up on reaping. Default: 5 s.</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Combined stdout+stderr cap. Default: 1 MiB.</summary>
    public long MaxOutputBytes { get; init; } = 1024 * 1024;

    public static CliRequest ForLocation(CliLocation location, IReadOnlyList<string> arguments) =>
        new()
        {
            ExePath = location.ExePath ?? "",
            NodeScriptPath = location.NodeScriptPath,
            Arguments = arguments,
        };
}

/// <summary>Outcome of one run-to-completion CLI invocation.</summary>
public sealed record CliResult
{
    public int ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

    public bool OutputTruncated { get; init; }

    /// <summary>Stable token when the process could not be started at all ("win32:2", "cli.node_not_found", …).</summary>
    public string? LaunchError { get; init; }

    public required string Stdout { get; init; }

    /// <summary>Small stderr tail for diagnostics. Never logged verbatim (may contain identity).</summary>
    public required string StderrTail { get; init; }

    public bool Success => !TimedOut && !Cancelled && LaunchError is null && ExitCode == 0;
}

/// <summary>
/// Run-to-completion CLI executor used by the MiniMax and Ark providers. The process tree
/// is placed in a kill-on-close Job Object so timeouts, cancellation and disposal end every
/// descendant — never just the direct child (plan §2.2(5)).
/// </summary>
public interface ICliProcessRunner
{
    Task<CliResult> RunAsync(CliRequest request, CancellationToken cancellationToken);
}

public sealed class CliProcessRunner : ICliProcessRunner
{
    public async Task<CliResult> RunAsync(CliRequest request, CancellationToken cancellationToken)
    {
        using var process = ManagedProcess.Start(request);
        if (process.LaunchError is not null)
        {
            return new CliResult { LaunchError = process.LaunchError, Stdout = "", StderrTail = "" };
        }

        var output = new OutputCapper(request.MaxOutputBytes);
        var stdoutTask = process.PumpStdoutAsync(output, cancellationToken);
        var stderrTask = process.PumpStderrAsync(output, cancellationToken);

        var exit = await process.WaitForExitAsync(request.Timeout, request.KillGrace, cancellationToken)
            .ConfigureAwait(false);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var (stdout, stderr, truncated) = output.Snapshot();

        return new CliResult
        {
            ExitCode = exit.ExitCode ?? -1,
            TimedOut = exit.TimedOut,
            Cancelled = exit.Cancelled,
            OutputTruncated = truncated,
            Stdout = stdout,
            StderrTail = stderr,
        };
    }
}

/// <summary>Shared bounded sink for stdout+stderr. Content past the cap is drained and discarded, never buffered.</summary>
internal sealed class OutputCapper
{
    private readonly long _charCap;
    private readonly object _lock = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private long _totalChars;
    private bool _truncated;

    public OutputCapper(long byteCap) => _charCap = Math.Max(1, byteCap / sizeof(char));

    public void AppendStdout(string chunk)
    {
        lock (_lock) Append(_stdout, chunk);
    }

    public void AppendStderr(string chunk)
    {
        lock (_lock) Append(_stderr, chunk);
    }

    private void Append(StringBuilder target, string chunk)
    {
        var remaining = _charCap - _totalChars;
        if (remaining <= 0)
        {
            _truncated = true;
            return;
        }
        if (chunk.Length > remaining)
        {
            chunk = chunk[..(int)remaining];
            _truncated = true;
        }
        target.Append(chunk);
        _totalChars += chunk.Length;
    }

    public (string stdout, string stderr, bool truncated) Snapshot()
    {
        lock (_lock)
        {
            return (_stdout.ToString(), _stderr.ToString(), _truncated);
        }
    }
}

/// <summary>Exit outcome of a managed process wait.</summary>
internal sealed record ManagedExit(int? ExitCode, bool TimedOut, bool Cancelled);

/// <summary>
/// A child process bound to a kill-on-close Job Object, with redirected streams. Disposing
/// the object terminates anything still running inside the job; a kill that does not reap
/// the process within the grace period is reported via the exit flags, not swallowed.
/// </summary>
internal sealed class ManagedProcess : IDisposable
{
    private readonly Process? _process;
    private JobObjectHandle? _job;
    private StreamReader? _stdoutReader;
    private StreamReader? _stderrReader;
    private StreamWriter? _stdinWriter;

    private ManagedProcess(Process? process, JobObjectHandle? job)
    {
        _process = process;
        _job = job;
    }

    public string? LaunchError { get; private set; }

    /// <summary>Starts the process inside a fresh job. Never throws for launch problems — check <see cref="LaunchError"/>.</summary>
    public static ManagedProcess Start(CliRequest request)
    {
        JobObjectHandle? job = null;
        try
        {
            job = JobObjectHandle.Create();
        }
        catch (Win32Exception)
        {
            return new ManagedProcess(null, null) { LaunchError = "cli.job_create_failed" };
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (request.NodeScriptPath is not null)
        {
            // node.exe + script: the .cmd shim is never interpreted by cmd.exe, so no shell
            // metacharacter can turn a path into shell syntax (plan §2.2(3)).
            var nodePath = FindNodeExe();
            if (nodePath is null)
            {
                job.Dispose();
                return new ManagedProcess(null, null) { LaunchError = "cli.node_not_found" };
            }
            psi.FileName = nodePath;
            psi.ArgumentList.Add(request.NodeScriptPath);
        }
        else
        {
            psi.FileName = request.ExePath;
        }

        foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or FileNotFoundException)
        {
            job.Dispose();
            var token = ex is Win32Exception win32 ? $"win32:{win32.NativeErrorCode}" : "cli.start_failed";
            return new ManagedProcess(null, null) { LaunchError = token };
        }

        if (process is null)
        {
            job.Dispose();
            return new ManagedProcess(null, null) { LaunchError = "cli.start_failed" };
        }

        try
        {
            job.Assign(process);
        }
        catch (Win32Exception)
        {
            // Without the job there is no tree cleanup, so do not leave the child around.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { process.Dispose(); } catch { /* best effort */ }
            job.Dispose();
            return new ManagedProcess(null, null) { LaunchError = "cli.job_assign_failed" };
        }

        var managed = new ManagedProcess(process, job)
        {
            _stdoutReader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8),
            _stderrReader = new StreamReader(process.StandardError.BaseStream, Encoding.UTF8),
            _stdinWriter = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true },
        };
        return managed;
    }

    /// <summary>Locates node.exe for shim launching. Null when node is not installed.</summary>
    public static string? FindNodeExe()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "node.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public Task PumpStdoutAsync(OutputCapper sink, CancellationToken ct) =>
        PumpAsync(_stdoutReader!, sink.AppendStdout, ct);

    public Task PumpStderrAsync(OutputCapper sink, CancellationToken ct) =>
        PumpAsync(_stderrReader!, sink.AppendStderr, ct);

    public Task PumpStdoutLinesAsync(Func<string, Task> onLine, CancellationToken ct) =>
        PumpLinesAsync(_stdoutReader!, onLine, ct);

    private static async Task PumpAsync(StreamReader reader, Action<string> sink, CancellationToken ct)
    {
        var buffer = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) break;
                sink(new string(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal pump exit; the wait layer reports it.
        }
        catch (IOException)
        {
            // The process died mid-read; whatever we captured is what the caller gets.
        }
    }

    private static async Task PumpLinesAsync(StreamReader reader, Func<string, Task> onLine, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                await onLine(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal pump exit on cancellation.
        }
        catch (IOException)
        {
            // Stdout broke before EOF (process killed / pipe closed).
        }
    }

    /// <summary>Closes stdin, waits within the total timeout; on timeout kills the job and waits up to the grace period.</summary>
    public async Task<ManagedExit> WaitForExitAsync(TimeSpan total, TimeSpan grace, CancellationToken ct)
    {
        CloseStdin();
        var effectiveGrace = grace <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : grace;

        try
        {
            var exited = _process!.WaitForExitAsync(ct);
            var finished = await Task.WhenAny(exited, Task.Delay(total, ct)).ConfigureAwait(false);
            if (finished == exited && exited.IsCompletedSuccessfully)
            {
                return new ManagedExit(_process.ExitCode, TimedOut: false, Cancelled: ct.IsCancellationRequested);
            }

            // Cancellation during the wait must be reported as Cancelled, not Timeout —
            // the two states drive different provider messages.
            var cancelled = ct.IsCancellationRequested;
            KillJob();
            var reaped = await WaitReapAsync(effectiveGrace).ConfigureAwait(false);
            return new ManagedExit(reaped ? _process.ExitCode : null, TimedOut: !cancelled, Cancelled: cancelled);
        }
        catch (OperationCanceledException)
        {
            KillJob();
            return new ManagedExit(null, TimedOut: false, Cancelled: true);
        }
    }

    /// <summary>Waits for exit up to <paramref name="grace"/> (caller already closed stdin); kills the job on timeout.</summary>
    public async Task<ManagedExit> WaitForExitAfterCloseAsync(TimeSpan grace, CancellationToken ct)
    {
        CloseStdin();
        try
        {
            var exited = _process!.WaitForExitAsync(ct);
            var finished = await Task.WhenAny(exited, Task.Delay(grace, ct)).ConfigureAwait(false);
            if (finished == exited && exited.IsCompletedSuccessfully)
            {
                return new ManagedExit(_process.ExitCode, TimedOut: false, Cancelled: false);
            }
            KillJob();
            var reaped = await WaitReapAsync(grace).ConfigureAwait(false);
            return new ManagedExit(reaped ? _process.ExitCode : null, TimedOut: true, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            KillJob();
            return new ManagedExit(null, TimedOut: false, Cancelled: true);
        }
    }

    private async Task<bool> WaitReapAsync(TimeSpan grace)
    {
        try
        {
            var exited = _process!.WaitForExitAsync();
            var finished = await Task.WhenAny(exited, Task.Delay(grace)).ConfigureAwait(false);
            return finished == exited;
        }
        catch
        {
            return false;
        }
    }

    public void CloseStdin()
    {
        try { _stdinWriter?.Dispose(); } catch (IOException) { /* pipe already closed */ }
    }

    /// <summary>Writes one newline-terminated frame to stdin (UTF-8, no BOM).</summary>
    public void WriteStdinLine(string line)
    {
        var writer = _stdinWriter ?? throw new IOException("stdin not available");
        writer.Write(line);
        writer.Write('\n');
    }

    private void KillJob()
    {
        try { _job?.Terminate(); }
        catch (Win32Exception) { /* job already gone */ }
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) KillJob(); } catch { /* best effort */ }
        try { _stdinWriter?.Dispose(); } catch { /* best effort */ }
        try { _stdoutReader?.Dispose(); } catch { /* best effort */ }
        try { _stderrReader?.Dispose(); } catch { /* best effort */ }
        try { _process?.Dispose(); } catch { /* best effort */ }
        _job?.Dispose();
        _job = null;
    }
}

/// <summary>Owns the Job Object handle. Disposal closes the handle, which kills the tree via KILL_ON_JOB_CLOSE.</summary>
internal sealed class JobObjectHandle : IDisposable
{
    private IntPtr _handle;

    private JobObjectHandle(IntPtr handle) => _handle = handle;

    public static JobObjectHandle Create()
    {
        var handle = JobObjectNative.CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception("CreateJobObject failed");
        }
        var extended = new JobObjectNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        extended.BasicLimitInformation.LimitFlags = JobObjectNative.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var ok = JobObjectNative.SetInformationJobObject(
            handle,
            JobObjectNative.JobObjectExtendedLimitInformation,
            ref extended,
            Marshal.SizeOf<JobObjectNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        if (!ok)
        {
            JobObjectNative.CloseHandle(handle);
            throw new Win32Exception("SetInformationJobObject failed");
        }
        return new JobObjectHandle(handle);
    }

    public void Assign(Process process)
    {
        if (!JobObjectNative.AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception("AssignProcessToJobObject failed");
        }
    }

    public void Terminate()
    {
        if (_handle != IntPtr.Zero)
        {
            JobObjectNative.TerminateJobObject(_handle, unchecked((int)0xDEAD10CC));
        }
    }

    public void Dispose()
    {
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h != IntPtr.Zero) JobObjectNative.CloseHandle(h);
    }
}

internal static class JobObjectNative
{
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    internal const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(IntPtr hJob, int uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
