using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OBDim.Services;

public record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Classifies a powercfg failure so callers can show a localized, actionable message
/// instead of bubbling a raw English exception message up to the user.
/// </summary>
public enum PowerConfigErrorKind
{
    /// <summary>Unclassified failure — callers should fall back to the raw exception message.</summary>
    Unknown = 0,

    /// <summary>powercfg.exe did not exit within <see cref="PowerConfigService.TimeoutMs"/>.</summary>
    Timeout,

    /// <summary>The process could not be started at all (blocked, missing, access denied).</summary>
    InvocationFailed,

    /// <summary>powercfg.exe ran but returned a non-zero exit code.</summary>
    NonZeroExit,

    /// <summary>powercfg.exe ran successfully but its output could not be parsed.</summary>
    ParseFailed
}

public class PowerConfigException : Exception
{
    /// <summary>Classification of the failure, used to pick a localized user-facing message.</summary>
    public PowerConfigErrorKind Kind { get; }

    public PowerConfigException(string message, PowerConfigErrorKind kind = PowerConfigErrorKind.Unknown)
        : base(message)
    {
        Kind = kind;
    }

    public PowerConfigException(string message, Exception inner,
                                PowerConfigErrorKind kind = PowerConfigErrorKind.Unknown)
        : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>
/// Wraps the Windows powercfg.exe command-line tool.
/// Uses the SCHEME_CURRENT alias instead of parsing GUIDs from localized output.
/// </summary>
public class PowerConfigService
{
    /// <summary>powercfg alias for the currently active power scheme.</summary>
    private const string SchemeCurrent = "SCHEME_CURRENT";

    /// <summary>
    /// Timeout for a single powercfg invocation, in milliseconds.
    /// v1.0.5: raised from 5s to 15s. powercfg normally finishes in a few hundred
    /// milliseconds, so a timeout at 5s almost always means the system stalled
    /// (EDR/AV interception, group-policy locked scheme, power saving, heavy load)
    /// — too aggressive a threshold turned those transient stalls into hard failures.
    /// </summary>
    public const int TimeoutMs = 15000;

    /// <summary>
    /// Grace period after <see cref="System.Diagnostics.Process.Kill()"/> so the async
    /// stdout/stderr readers can drain and the OS can release the process handle.
    /// </summary>
    private const int KillGraceMs = 2000;

    /// <summary>Total attempts per powercfg invocation: 1 initial try + 1 retry on timeout.</summary>
    public const int MaxAttempts = 2;

    public static readonly Guid SubVideoGuid = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdleGuid = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    // Regexes for English powercfg output (primary parsing path)
    private static readonly Regex AcRegex =
        new(@"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    private static readonly Regex DcRegex =
        new(@"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    private readonly Func<ProcessStartInfo, ProcessResult> _runner;

    public PowerConfigService() : this(RealRunner) { }

    public PowerConfigService(Func<ProcessStartInfo, ProcessResult> runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// Queries the current power scheme's VIDEOIDLE AC and DC settings.
    /// Uses SCHEME_CURRENT alias (locale-independent).
    /// Returns values as long to handle values exceeding int.MaxValue.
    /// </summary>
    public virtual (long acSeconds, long dcSeconds) GetCurrentVideoIdle()
    {
        var psi = NewPsi("/query", SchemeCurrent,
            SubVideoGuid.ToString(), VideoIdleGuid.ToString());
        var result = RunWithRetry(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"query failed: {result.Stderr}",
                PowerConfigErrorKind.NonZeroExit);

        return ParseVideoIdle(result.Stdout);
    }

    /// <summary>
    /// Sets the current power scheme's VIDEOIDLE AC and DC values and activates the scheme.
    /// Uses SCHEME_CURRENT alias (locale-independent).
    /// Throws PowerConfigException with step number on partial failure.
    /// </summary>
    public virtual void SetVideoIdle(int acSeconds, int dcSeconds)
    {
        RunStrict(NewPsi("/setacvalueindex", SchemeCurrent,
            SubVideoGuid.ToString(), VideoIdleGuid.ToString(), acSeconds.ToString()),
            "setacvalueindex", 1);
        RunStrict(NewPsi("/setdcvalueindex", SchemeCurrent,
            SubVideoGuid.ToString(), VideoIdleGuid.ToString(), dcSeconds.ToString()),
            "setdcvalueindex", 2);
        RunStrict(NewPsi("/setactive", SchemeCurrent),
            "setactive", 3);
    }

    /// <summary>
    /// Parses AC and DC hex values from powercfg /query output.
    /// First tries English regex match; falls back to structural parsing
    /// (finds lines containing 0x hex values) for localized output.
    /// </summary>
    private static (long ac, long dc) ParseVideoIdle(string output)
    {
        var acMatch = AcRegex.Match(output);
        var dcMatch = DcRegex.Match(output);

        if (acMatch.Success && dcMatch.Success)
        {
            long ac = Convert.ToInt64(acMatch.Groups[1].Value, 16);
            long dc = Convert.ToInt64(dcMatch.Groups[1].Value, 16);
            return (ac, dc);
        }

        // Fallback: find all 0x hex values in the output, take the last two
        // (the AC and DC setting index lines appear after all GUID/alias lines)
        var hexValues = new List<long>();
        foreach (var line in output.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                var hexStr = new StringBuilder();
                for (int i = idx + 2; i < trimmed.Length && Uri.IsHexDigit(trimmed[i]); i++)
                    hexStr.Append(trimmed[i]);

                if (hexStr.Length > 0)
                    hexValues.Add(Convert.ToInt64(hexStr.ToString(), 16));

                // Look for next 0x in the same line
                idx = trimmed.IndexOf("0x", idx + 2, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (hexValues.Count < 2)
            throw new PowerConfigException($"Cannot parse AC/DC from: {output}",
                PowerConfigErrorKind.ParseFailed);

        // AC is second-to-last, DC is last (matching powercfg output structure)
        return (hexValues[^2], hexValues[^1]);
    }

    /// <summary>
    /// Runs powercfg and throws on non-zero exit code.
    /// Includes step number for partial-failure diagnostics.
    /// </summary>
    private void RunStrict(ProcessStartInfo psi, string stepName, int stepNumber)
    {
        var result = RunWithRetry(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException(
                $"Step {stepNumber}/3 ({stepName}) failed (exit {result.ExitCode}): {result.Stderr}",
                PowerConfigErrorKind.NonZeroExit);
    }

    /// <summary>
    /// Invokes the process runner, retrying once when the run times out.
    /// The retry lives at this level (above the injected runner) so it is exercised by
    /// tests that supply their own runner: a test runner throwing
    /// <see cref="PowerConfigException"/> with <see cref="PowerConfigErrorKind.Timeout"/>
    /// will be called a second time, exactly like the real runner would be.
    /// </summary>
    private ProcessResult RunWithRetry(ProcessStartInfo psi)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                LogService.Info($"powercfg attempt {attempt}/{MaxAttempts}: {Describe(psi)}");
                return _runner(psi);
            }
            catch (PowerConfigException ex) when (ex.Kind == PowerConfigErrorKind.Timeout && attempt < MaxAttempts)
            {
                LogService.Warn($"powercfg timed out (attempt {attempt}/{MaxAttempts}), retrying: {Describe(psi)}");
            }
        }
    }

    /// <summary>
    /// Renders the exact command line so it can be recorded in the log file.
    /// Users can hand this log back for diagnosis.
    /// </summary>
    private static string Describe(ProcessStartInfo psi)
        => $"{psi.FileName} {string.Join(' ', psi.ArgumentList)}";

    /// <summary>
    /// Creates a ProcessStartInfo for powercfg.exe with the given arguments.
    /// Uses ArgumentList (no string splitting) to handle arguments safely.
    /// </summary>
    private static ProcessStartInfo NewPsi(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// Real process runner with async dual-pipe reading, timeout, and exception normalization.
    /// All exceptions are converted to PowerConfigException.
    /// </summary>
    private static ProcessResult RealRunner(ProcessStartInfo psi)
    {
        Process? p = null;
        try
        {
            p = Process.Start(psi)
                ?? throw new PowerConfigException("powercfg invocation failed: Process.Start returned null",
                                                  PowerConfigErrorKind.InvocationFailed);

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

            // Async dual-pipe reading avoids deadlock when stdout buffer fills
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(); } catch { /* best effort */ }
                // Give the killed process a short grace period so the async readers can
                // drain and the OS can release the handle before the Process is disposed.
                try { p.WaitForExit(KillGraceMs); } catch { /* best effort */ }
                throw new PowerConfigException($"powercfg timeout after {TimeoutMs / 1000}s",
                                               PowerConfigErrorKind.Timeout);
            }

            // Wait for async I/O to complete (process already exited, so this returns quickly)
            p.WaitForExit();

            return new ProcessResult(p.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
        catch (PowerConfigException)
        {
            throw; // Re-throw our own exceptions as-is
        }
        catch (Exception ex)
        {
            // Normalize all other exceptions (Win32Exception, etc.) into PowerConfigException
            throw new PowerConfigException($"powercfg invocation failed: {ex.Message}", ex,
                                           PowerConfigErrorKind.InvocationFailed);
        }
        finally
        {
            p?.Dispose();
        }
    }
}
