using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScreenTimeoutToggle.Services;

public record ProcessResult(int ExitCode, string Stdout, string Stderr);

public class PowerConfigException : Exception
{
    public PowerConfigException(string message) : base(message) { }
    public PowerConfigException(string message, Exception inner) : base(message, inner) { }
}

public class PowerConfigService
{
    public static readonly Guid SubVideoGuid = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdleGuid = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    private static readonly Regex SchemeGuidRegex =
        new(@"Power Scheme GUID:\s*([0-9a-fA-F\-]{36})", RegexOptions.Compiled);

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

    // Methods are virtual so Moq can mock them in ModeServiceTests.
    public virtual Guid GetActiveSchemeGuid()
    {
        var psi = NewPsi("/getactivescheme");
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"getactivescheme failed: {result.Stderr}");

        var m = SchemeGuidRegex.Match(result.Stdout);
        if (!m.Success)
            throw new PowerConfigException($"Cannot parse scheme GUID from: {result.Stdout}");

        return Guid.Parse(m.Groups[1].Value);
    }

    public virtual (int acSeconds, int dcSeconds) GetCurrentVideoIdle(Guid scheme)
    {
        var psi = NewPsi($"/query {scheme} {SubVideoGuid} {VideoIdleGuid}");
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"query failed: {result.Stderr}");

        var acMatch = AcRegex.Match(result.Stdout);
        var dcMatch = DcRegex.Match(result.Stdout);
        if (!acMatch.Success || !dcMatch.Success)
            throw new PowerConfigException($"Cannot parse AC/DC from: {result.Stdout}");

        int ac = Convert.ToInt32(acMatch.Groups[1].Value, 16);
        int dc = Convert.ToInt32(dcMatch.Groups[1].Value, 16);
        return (ac, dc);
    }

    public virtual void SetVideoIdle(Guid scheme, int acSeconds, int dcSeconds)
    {
        RunStrict(NewPsi($"/setacvalueindex {scheme} {SubVideoGuid} {VideoIdleGuid} {acSeconds}"));
        RunStrict(NewPsi($"/setdcvalueindex {scheme} {SubVideoGuid} {VideoIdleGuid} {dcSeconds}"));
        RunStrict(NewPsi($"/setactive {scheme}"));
    }

    private void RunStrict(ProcessStartInfo psi)
    {
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"powercfg {psi.ArgumentList[0]} failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static ProcessStartInfo NewPsi(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static ProcessResult RealRunner(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }
}
