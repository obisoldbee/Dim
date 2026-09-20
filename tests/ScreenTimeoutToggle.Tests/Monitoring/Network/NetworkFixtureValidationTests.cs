using OBDim.Monitoring.Network.Validation;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>
/// The 14 frozen fixtures (contract §14) through the host-side validator:
/// 12 must pass, 2 must fail. This mirrors the probe's end-to-end gate.
/// </summary>
public class NetworkFixtureValidationTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Network", name);

    [Theory]
    [InlineData("normal.json")]
    [InlineData("partial-coverage.json")]
    [InlineData("denied.json")]
    [InlineData("disconnected.json")]
    [InlineData("starting.json")]
    [InlineData("stopped.json")]
    [InlineData("epoch-rollover.json")]
    [InlineData("counter-wrap.json")]
    [InlineData("tun-and-loopback.json")]
    [InlineData("ipv6-quic.json")]
    [InlineData("unknown-domain.json")]
    [InlineData("single-sample-history.json")]
    public void ValidFixtures_Pass(string file)
    {
        var errors = NetworkSnapshotValidator.Validate(File.ReadAllBytes(FixturePath(file)));
        Assert.True(errors.Count == 0, $"{file}: {string.Join("; ", errors)}");
    }

    [Theory]
    [InlineData("invalid-zero-as-unknown.json", "S5")]      // denied carrying apps data
    [InlineData("invalid-missing-epoch.json", "SCHEMA")]    // app missing required epoch
    public void InvalidFixtures_Fail_WithTheExpectedRule(string file, string rule)
    {
        var errors = NetworkSnapshotValidator.Validate(File.ReadAllBytes(FixturePath(file)));
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith(rule + ":", StringComparison.Ordinal));
    }
}
