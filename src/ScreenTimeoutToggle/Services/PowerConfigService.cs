using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OBDim.Services;

public record ProcessResult(int ExitCode, string Stdout, string Stderr);

public class PowerConfigException : Exception
{
    public PowerConfigException(string message) : base(message) { }
    public PowerConfigException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Wraps the Windows powercfg.exe command-line tool.
/// Uses the SCHEME_CURRENT alias instead of parsing GUIDs from localized output.
/// </summary>
public class PowerConfigService
{
    /// <summary>powercfg alias for the currently active power scheme.</summary>
    private const string SchemeCurrent = "SCHEME_CURRENT";

    /// <summary>Timeout for powercfg process in milliseconds.</summary>
    private const int TimeoutMs = 5000;

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
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"query failed: {result.Stderr}");

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
            throw new PowerConfigException($"Cannot parse AC/DC from: {output}");

        // AC is second-to-last, DC is last (matching powercfg output structure)
        return (hexValues[^2], hexValues[^1]);
    }

    /// <summary>
    /// Runs powercfg and throws on non-zero exit code.
    /// Includes step number for partial-failure diagnostics.
    /// </summary>
    private void RunStrict(ProcessStartInfo psi, string stepName, int stepNumber)
    {
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException(
                $"Step {stepNumber}/3 ({stepName}) failed (exit {result.ExitCode}): {result.Stderr}");
    }

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
                ?? throw new PowerConfigException("powercfg invocation failed: Process.Start returned null");

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
                throw new PowerConfigException($"powercfg timeout after {TimeoutMs / 1000}s");
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
            throw new PowerConfigException($"powercfg invocation failed: {ex.Message}", ex);
        }
        finally
        {
            p?.Dispose();
        }
    }
}
