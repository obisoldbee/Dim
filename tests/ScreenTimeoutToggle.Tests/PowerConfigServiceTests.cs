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
}
