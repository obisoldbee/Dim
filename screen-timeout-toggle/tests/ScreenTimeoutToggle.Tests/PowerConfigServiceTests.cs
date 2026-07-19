using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class PowerConfigServiceTests
{
    private const string GetActiveSchemeOutput =
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n";

    private const string QueryOutput =
        "Subgroup GUID: 7516b95f-f776-4464-8c53-06167f40cc99  (Video timeout)\r\n" +
        "  GUID Alias: SUB_VIDEO\r\n" +
        "  Power Setting GUID: 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e  (Video idle timeout)\r\n" +
        "    GUID Alias: VIDEOIDLE\r\n" +
        "    Possible Setting Group Index: 001\r\n\r\n" +
        "  Current AC Power Setting Index: 0x0000003c\r\n" +
        "  Current DC Power Setting Index: 0x00000078\r\n";

    [Fact]
    public void GetActiveSchemeGuid_ParsesGuidFromOutput()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(0, GetActiveSchemeOutput, ""));

        var guid = svc.GetActiveSchemeGuid();

        Assert.Equal(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), guid);
    }

    [Fact]
    public void GetCurrentVideoIdle_ParsesAcAndDcHex()
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var calls = 0;
        var svc = new PowerConfigService(_ =>
        {
            calls++;
            return new ProcessResult(0, QueryOutput, "");
        });

        var (ac, dc) = svc.GetCurrentVideoIdle(scheme);

        Assert.Equal(60, ac);   // 0x3c
        Assert.Equal(120, dc);  // 0x78
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetCurrentVideoIdle_HandlesZeroNever()
    {
        var scheme = Guid.NewGuid();
        var zeroOutput = QueryOutput
            .Replace("0x0000003c", "0x00000000")
            .Replace("0x00000078", "0x00000000");
        var svc = new PowerConfigService(_ => new ProcessResult(0, zeroOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle(scheme);

        Assert.Equal(0, ac);
        Assert.Equal(0, dc);
    }

    [Fact]
    public void SetVideoIdle_CallsThreeCommands_InOrder()
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var calls = new List<string>();
        var svc = new PowerConfigService(psi =>
        {
            calls.Add(string.Join(" ", psi.ArgumentList));
            return new ProcessResult(0, "", "");
        });

        svc.SetVideoIdle(scheme, acSeconds: 60, dcSeconds: 120);

        Assert.Equal(3, calls.Count);
        Assert.Contains("setacvalueindex", calls[0]);
        Assert.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", calls[0]);
        Assert.Contains("7516b95f-f776-4464-8c53-06167f40cc99", calls[0]);
        Assert.Contains("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", calls[0]);
        Assert.Contains("60", calls[0]);
        Assert.Contains("setdcvalueindex", calls[1]);
        Assert.Contains("120", calls[1]);
        Assert.Contains("setactive", calls[2]);
    }

    [Fact]
    public void SetVideoIdle_NonZeroExit_ThrowsWithStderr()
    {
        var scheme = Guid.NewGuid();
        var svc = new PowerConfigService(_ => new ProcessResult(1, "", "Access denied"));

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(scheme, 60, 60));
        Assert.Contains("Access denied", ex.Message);
    }
}
