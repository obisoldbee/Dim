using System.Text.Json;
using System.Windows.Forms;
using OBDim.Monitoring.Providers;
using OBDim.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>Newline-delimited JSON-RPC frame handling for the Codex app-server channel.</summary>
public class JsonRpcTests
{
    [Fact]
    public void ResponseWithResult_IsRecognized()
    {
        var line = """{"id":2,"result":{"rateLimits":{"limitId":"codex"}}}""";
        Assert.True(JsonRpc.TryParse(line, out var frame));
        Assert.True(frame.IsResponse);
        Assert.Equal(2, frame.Id);
        Assert.NotNull(frame.Result);
        Assert.Equal("codex", frame.Result!.Value.TryGetProperty("rateLimits")!.Value.TryGetProperty("limitId")!.Value.GetString());
    }

    [Fact]
    public void NotificationWithoutId_IsNotResponse()
    {
        var line = """{"method":"account/rateLimits/updated","params":{}}""";
        Assert.True(JsonRpc.TryParse(line, out var frame));
        Assert.False(frame.IsResponse);
        Assert.Equal("account/rateLimits/updated", frame.Method);
    }

    [Fact]
    public void ErrorFrame_IsResponseWithError()
    {
        var line = """{"id":1,"error":{"code":-32601,"message":"Not initialized"}}""";
        Assert.True(JsonRpc.TryParse(line, out var frame));
        Assert.True(frame.IsResponse);
        Assert.NotNull(frame.Error);
        Assert.Equal("Not initialized", frame.Error!.Value.TryGetProperty("message")!.Value.GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"id\":true}")]
    public void MalformedLines_AreRejectedNotThrown(string line)
    {
        Assert.False(JsonRpc.TryParse(line, out _));
    }

    [Fact]
    public void EncodeRequest_ProducesParseableFrame_WithIdAndParams()
    {
        var encoded = JsonRpc.EncodeRequest(7, "account/rateLimits/read", new { excludeResetCreditDetails = false });
        Assert.True(JsonRpc.TryParse(encoded, out var frame));
        Assert.Equal(7, frame.Id);
        Assert.Equal("account/rateLimits/read", frame.Method);
        Assert.False(frame.IsResponse);
        Assert.NotNull(frame.Params);
    }

    [Fact]
    public void EncodeNotification_HasNoId()
    {
        var encoded = JsonRpc.EncodeNotification("initialized");
        using var doc = JsonDocument.Parse(encoded);
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
        Assert.Equal("initialized", doc.RootElement.TryGetProperty("method")!.Value.GetString());
    }
}

/// <summary>MonitorForm pure helpers (no message pump needed).</summary>
public class MonitorFormHelperTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(51.5, "51.5")]
    [InlineData(97.92592506666666, "97.9")]
    public void FormatPercent_RoundsSensibly(double value, string expected)
    {
        Assert.Equal(expected, MonitorForm.FormatPercent(value));
    }

    [Theory]
    [InlineData(1_610_612_736, "1.5 GB")]
    [InlineData(800_000_000, "763 MB")]
    public void FormatBytes_UsesTwoScales(ulong bytes, string expected)
    {
        Assert.Equal(expected, MonitorForm.FormatBytes(bytes));
    }

    [Fact]
    public void LocalizeWindowKey_KnownKeysAreLocalized_UnknownPassThrough()
    {
        Assert.Equal(LocalizationService.Get("monitor.window.primary"), MonitorForm.LocalizeWindowKey("primary", null));
        Assert.Equal("weird_key", MonitorForm.LocalizeWindowKey("weird_key", null));
        Assert.Equal("source-label", MonitorForm.LocalizeWindowKey("unknown", "source-label"));
    }

    /// <summary>
    /// A11 UI 冒烟: the real MonitorForm must construct, render one refresh and dispose on
    /// an STA thread with a live coordinator — the guard proves the panel wiring (event
    /// subscription, layout, localization) executes, not just a bare Control. Same STA
    /// pattern as <c>UiMarshallingTests</c>.
    /// </summary>
    [Fact]
    public void MonitorForm_ConstructsRendersAndDisposes_WithLiveCoordinator()
    {
        RunOnStaThread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"obdim-uiform-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var adapters = new Dictionary<OBDim.Monitoring.Models.ProviderId, OBDim.Monitoring.Providers.IProviderAdapter>();
                using var coordinator = new OBDim.Monitoring.Services.MonitoringCoordinator(
                    new FakeClock(),
                    new StubMemoryReader(),
                    adapters,
                    new OBDim.Monitoring.Services.MonitoringSettingsService(Path.Combine(dir, "monitoring.json")),
                    new OBDim.Monitoring.Services.MonitoringCacheService(Path.Combine(dir, "cache")));

                using var form = new MonitorForm(coordinator);
                Assert.False(string.IsNullOrEmpty(form.Text));
                Assert.NotEqual("monitor.title", form.Text); // keys resolved, not shown raw

                form.Show();
                Application.DoEvents();
                form.Close();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        });
    }

    private sealed class StubMemoryReader : OBDim.Monitoring.Infrastructure.IMemoryReader
    {
        public OBDim.Monitoring.Models.MemorySample? Read(out string? error)
        {
            error = null;
            return new OBDim.Monitoring.Models.MemorySample
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                PhysicalTotalBytes = 16_000_000_000,
                PhysicalAvailableBytes = 8_000_000_000,
                CommitTotalBytes = 8_000_000_000,
                CommitLimitBytes = 32_000_000_000,
                LowMemorySignal = false,
            };
        }

        public void Dispose() { }
    }

    /// <summary>Runs <paramref name="body"/> on a dedicated STA thread (same contract as UiMarshallingTests).</summary>
    private static void RunOnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA test thread did not finish in time");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("Assertion failed on the STA thread: " + failure.Message, failure);
        }
    }
}
