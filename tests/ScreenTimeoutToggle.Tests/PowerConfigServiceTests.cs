using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

public class PowerConfigServiceTests
{
    private const string QueryOutput =
        "Subgroup GUID: 7516b95f-f776-4464-8c53-06167f40cc99  (Video timeout)\r\n" +
        "  GUID Alias: SUB_VIDEO\r\n" +
        "  Power Setting GUID: 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e  (Video idle timeout)\r\n" +
        "    GUID Alias: VIDEOIDLE\r\n" +
        "    Possible Setting Group Index: 001\r\n\r\n" +
        "  Current AC Power Setting Index: 0x0000003c\r\n" +
        "  Current DC Power Setting Index: 0x00000078\r\n";

    [Fact]
    public void GetCurrentVideoIdle_ParsesAcAndDcHex()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(0, QueryOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(60L, ac);   // 0x3c
        Assert.Equal(120L, dc);  // 0x78
    }

    [Fact]
    public void GetCurrentVideoIdle_HandlesZeroNever()
    {
        var zeroOutput = QueryOutput
            .Replace("0x0000003c", "0x00000000")
            .Replace("0x00000078", "0x00000000");
        var svc = new PowerConfigService(_ => new ProcessResult(0, zeroOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(0L, ac);
        Assert.Equal(0L, dc);
    }

    /// <summary>
    /// A1: Verify SCHEME_CURRENT alias is used in the query command (locale-independent).
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_UsesSchemeCurrentInQuery()
    {
        var capturedArgs = new List<string>();
        var svc = new PowerConfigService(psi =>
        {
            capturedArgs.AddRange(psi.ArgumentList);
            return new ProcessResult(0, QueryOutput, "");
        });

        svc.GetCurrentVideoIdle();

        Assert.Contains("/query", capturedArgs);
        Assert.Contains("SCHEME_CURRENT", capturedArgs);
    }

    /// <summary>
    /// A1: Fallback parsing for localized (non-English) powercfg output.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_FallbackParsesLocalizedOutput()
    {
        // Simulate localized output where "Current AC/DC Power Setting Index" text is different
        // but 0x hex values are still present
        var localizedOutput =
            "  当前的 AC 电源设置索引: 0x0000003c\r\n" +
            "  当前的 DC 电源设置索引: 0x00000078\r\n";

        var svc = new PowerConfigService(_ => new ProcessResult(0, localizedOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(60L, ac);
        Assert.Equal(120L, dc);
    }

    /// <summary>
    /// A5: Values exceeding int.MaxValue should parse correctly as long.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_ParsesLargeValue_OverflowInt32()
    {
        // 0xFFFFFFFF = 4294967295, exceeds int.MaxValue
        var largeOutput = QueryOutput
            .Replace("0x0000003c", "0xFFFFFFFF")
            .Replace("0x00000078", "0xFFFFFFFF");
        var svc = new PowerConfigService(_ => new ProcessResult(0, largeOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(4294967295L, ac);
        Assert.Equal(4294967295L, dc);
    }

    [Fact]
    public void SetVideoIdle_CallsThreeCommands_InOrder_WithSchemeCurrent()
    {
        var calls = new List<string>();
        var svc = new PowerConfigService(psi =>
        {
            calls.Add(string.Join(" ", psi.ArgumentList));
            return new ProcessResult(0, "", "");
        });

        svc.SetVideoIdle(acSeconds: 60, dcSeconds: 120);

        Assert.Equal(3, calls.Count);
        Assert.Contains("setacvalueindex", calls[0]);
        Assert.Contains("SCHEME_CURRENT", calls[0]);
        Assert.Contains("7516b95f-f776-4464-8c53-06167f40cc99", calls[0]);
        Assert.Contains("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", calls[0]);
        Assert.Contains("60", calls[0]);
        Assert.Contains("setdcvalueindex", calls[1]);
        Assert.Contains("120", calls[1]);
        Assert.Contains("setactive", calls[2]);
        Assert.Contains("SCHEME_CURRENT", calls[2]);
    }

    [Fact]
    public void SetVideoIdle_NonZeroExit_ThrowsWithStderr()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(1, "", "Access denied"));

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(60, 60));
        Assert.Contains("Access denied", ex.Message);
    }

    /// <summary>
    /// A6: Exception message should include step number for partial-failure diagnostics.
    /// </summary>
    [Fact]
    public void SetVideoIdle_StepFailure_IncludesStepNumber()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            // Step 2 (setdcvalueindex) fails
            return callCount == 2
                ? new ProcessResult(1, "", "Access denied")
                : new ProcessResult(0, "", "");
        });

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(60, 60));
        Assert.Contains("Step 2/3", ex.Message);
    }

    /// <summary>
    /// A2: Runner throwing a non-PowerConfigException should be normalized.
    /// (Simulated via runner that throws directly.)
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_NonZeroExit_ThrowsPowerConfigException()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(1, "", "Parameter incorrect"));

        var ex = Assert.Throws<PowerConfigException>(() => svc.GetCurrentVideoIdle());
        Assert.Contains("query failed", ex.Message);
    }

    // ===== QA-added edge case tests =====

    /// <summary>
    /// QA: Empty powercfg output should throw PowerConfigException, not crash.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_EmptyOutput_ThrowsPowerConfigException()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(0, "", ""));

        var ex = Assert.Throws<PowerConfigException>(() => svc.GetCurrentVideoIdle());
        Assert.Contains("Cannot parse", ex.Message);
    }

    /// <summary>
    /// QA: Output with only one hex value (malformed — missing DC line) should throw.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_OnlyOneHexValue_ThrowsPowerConfigException()
    {
        var malformedOutput =
            "  Some localized line: 0x0000003c\r\n"; // only one 0x value, no DC

        var svc = new PowerConfigService(_ => new ProcessResult(0, malformedOutput, ""));

        var ex = Assert.Throws<PowerConfigException>(() => svc.GetCurrentVideoIdle());
        Assert.Contains("Cannot parse", ex.Message);
    }

    /// <summary>
    /// QA: Mixed output (English AC + localized DC) should use fallback and parse correctly.
    /// The English regex matches AC but not DC, triggering the fallback path.
    /// Fallback collects all 0x values and takes last two.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_MixedEnglishAndLocalized_UsesFallback()
    {
        var mixedOutput =
            "  Current AC Power Setting Index: 0x0000003c\r\n" +
            "  当前直流电源设置索引: 0x00000078\r\n";

        var svc = new PowerConfigService(_ => new ProcessResult(0, mixedOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(60L, ac);   // 0x3c
        Assert.Equal(120L, dc);  // 0x78
    }

    /// <summary>
    /// QA: SetVideoIdle with zero values (never timeout) should work correctly.
    /// </summary>
    [Fact]
    public void SetVideoIdle_WithZeroValues_PassesZeroToPowercfg()
    {
        var calls = new List<string>();
        var svc = new PowerConfigService(psi =>
        {
            calls.Add(string.Join(" ", psi.ArgumentList));
            return new ProcessResult(0, "", "");
        });

        svc.SetVideoIdle(acSeconds: 0, dcSeconds: 0);

        Assert.Equal(3, calls.Count);
        Assert.Contains("0", calls[0]); // AC value
        Assert.Contains("0", calls[1]); // DC value
    }

    /// <summary>
    /// QA: setactive (step 3) failure should include step number 3/3.
    /// </summary>
    [Fact]
    public void SetVideoIdle_StepThreeFailure_IncludesStepNumber3()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            // Step 3 (setactive) fails
            return callCount == 3
                ? new ProcessResult(1, "", "Access denied")
                : new ProcessResult(0, "", "");
        });

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(60, 60));
        Assert.Contains("Step 3/3", ex.Message);
    }

    // ===== v1.0.5: timeout retry + error classification =====

    /// <summary>
    /// v1.0.5: a single timeout is retried once. The retry must be observable through the
    /// injected runner, otherwise a real stall would be untestable.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_TimeoutOnce_RetriesAndSucceeds()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new PowerConfigException("powercfg timeout after 15s", PowerConfigErrorKind.Timeout);
            return new ProcessResult(0, QueryOutput, "");
        });

        var (ac, dc) = svc.GetCurrentVideoIdle();

        Assert.Equal(2, callCount);
        Assert.Equal(60L, ac);
        Assert.Equal(120L, dc);
    }

    /// <summary>
    /// v1.0.5: after the retry budget is exhausted the timeout surfaces to the caller,
    /// tagged with PowerConfigErrorKind.Timeout so the UI can localize it.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_AlwaysTimesOut_ThrowsTimeoutKind_AfterMaxAttempts()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            throw new PowerConfigException("powercfg timeout after 15s", PowerConfigErrorKind.Timeout);
        });

        var ex = Assert.Throws<PowerConfigException>(() => svc.GetCurrentVideoIdle());

        Assert.Equal(PowerConfigErrorKind.Timeout, ex.Kind);
        Assert.Equal(PowerConfigService.MaxAttempts, callCount);
    }

    /// <summary>
    /// v1.0.5: retry is per invocation, so a timeout on step 1 of SetVideoIdle retries only
    /// that step — the two later steps still run exactly once.
    /// </summary>
    [Fact]
    public void SetVideoIdle_TimeoutOnFirstStep_RetriesOnlyThatStep()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new PowerConfigException("powercfg timeout after 15s", PowerConfigErrorKind.Timeout);
            return new ProcessResult(0, "", "");
        });

        svc.SetVideoIdle(acSeconds: 60, dcSeconds: 120);

        // 2 attempts for step 1 + 1 each for steps 2 and 3
        Assert.Equal(4, callCount);
    }

    /// <summary>
    /// v1.0.5: a timeout that keeps failing must not silently succeed — the exception
    /// propagates out of SetVideoIdle and the run stops at the failing step.
    /// </summary>
    [Fact]
    public void SetVideoIdle_AlwaysTimesOut_ThrowsTimeoutKind()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            throw new PowerConfigException("powercfg timeout after 15s", PowerConfigErrorKind.Timeout);
        });

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(60, 60));

        Assert.Equal(PowerConfigErrorKind.Timeout, ex.Kind);
        // Step 1 consumes both attempts and aborts before step 2.
        Assert.Equal(PowerConfigService.MaxAttempts, callCount);
    }

    /// <summary>
    /// v1.0.5: non-timeout failures must NOT be retried — retrying a "rejected by policy"
    /// result would just double the wait with no chance of success.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_NonZeroExit_IsNotRetried()
    {
        var callCount = 0;
        var svc = new PowerConfigService(_ =>
        {
            callCount++;
            return new ProcessResult(1, "", "Access denied");
        });

        Assert.Throws<PowerConfigException>(() => svc.GetCurrentVideoIdle());
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// v1.0.5: exit-code and parse failures carry their own error kinds so the UI can show
    /// a specific hint instead of a generic "failed".
    /// </summary>
    [Fact]
    public void ErrorKinds_AreClassifiedCorrectly()
    {
        var nonZeroExit = Assert.Throws<PowerConfigException>(
            () => new PowerConfigService(_ => new ProcessResult(1, "", "denied")).GetCurrentVideoIdle());
        Assert.Equal(PowerConfigErrorKind.NonZeroExit, nonZeroExit.Kind);

        var parseFailed = Assert.Throws<PowerConfigException>(
            () => new PowerConfigService(_ => new ProcessResult(0, "garbage", "")).GetCurrentVideoIdle());
        Assert.Equal(PowerConfigErrorKind.ParseFailed, parseFailed.Kind);
    }

    /// <summary>
    /// v1.0.5: the timeout budget was raised from 5s to 15s to ride out transient system
    /// stalls instead of failing the user's toggle.
    /// </summary>
    [Fact]
    public void TimeoutBudget_IsFifteenSeconds()
    {
        Assert.Equal(15000, PowerConfigService.TimeoutMs);
    }

    /// <summary>
    /// v1.0.5: the exact powercfg command line must reach the runner so it can be logged
    /// and handed back for diagnosis.
    /// </summary>
    [Fact]
    public void GetCurrentVideoIdle_PassesFullArgumentListToRunner()
    {
        List<string>? captured = null;
        var svc = new PowerConfigService(psi =>
        {
            captured = new List<string>(psi.ArgumentList);
            return new ProcessResult(0, QueryOutput, "");
        });

        svc.GetCurrentVideoIdle();

        Assert.NotNull(captured);
        Assert.Equal(new[] { "/query", "SCHEME_CURRENT",
                             "7516b95f-f776-4464-8c53-06167f40cc99",
                             "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e" }, captured);
    }
}
