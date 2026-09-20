using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using Xunit;

namespace OBDim.Tests.Monitoring.Network;

/// <summary>
/// networkEnabled settings integration (schema v3): default off everywhere — fresh
/// configs, upgraded configs that never mention the field, and v1 files alike.
/// </summary>
public class NetworkSettingsTests : IDisposable
{
    private readonly string _dir;

    public NetworkSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"obdim-net-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private MonitoringSettingsService Service() =>
        new(Path.Combine(_dir, "monitoring.json"));

    [Fact]
    public void FreshConfig_NetworkDisabledByDefault()
    {
        var loaded = Service().Load();
        Assert.False(loaded.NetworkEnabled);
        Assert.Equal(3, loaded.SchemaVersion);
    }

    [Fact]
    public void UpgradeFromV2_MissingField_StaysDisabled_AndBumpsSchema()
    {
        File.WriteAllText(Path.Combine(_dir, "monitoring.json"),
            """{"schemaVersion":2,"memoryEnabled":false,"remindersEnabled":true,"refreshIntervalMinutes":15}""");

        var loaded = Service().Load();

        Assert.Equal(3, loaded.SchemaVersion);
        Assert.False(loaded.NetworkEnabled); // opt-in: upgrades never silently enable observation
        Assert.False(loaded.MemoryEnabled);  // existing choices survive the migration
        Assert.True(loaded.RemindersEnabled);
        Assert.Equal(15, loaded.RefreshIntervalMinutes);
    }

    [Fact]
    public void UpgradeFromV1_NetworkDisabledByDefault()
    {
        File.WriteAllText(Path.Combine(_dir, "monitoring.json"),
            """{"schemaVersion":1,"leftClickOpensPopover":false}""");

        var loaded = Service().Load();

        Assert.Equal(3, loaded.SchemaVersion);
        Assert.False(loaded.NetworkEnabled);
        Assert.False(loaded.LeftClickOpensPopover);
    }

    [Fact]
    public void RoundTrip_EnabledChoice_Persists()
    {
        var service = Service();
        Assert.True(service.Save(MonitoringSettings.CreateDefault() with { NetworkEnabled = true }));

        var loaded = service.Load();
        Assert.True(loaded.NetworkEnabled);
    }

    [Fact]
    public void SavedFile_CarriesSchemaVersion3()
    {
        var service = Service();
        Assert.True(service.Save(MonitoringSettings.CreateDefault()));

        var json = File.ReadAllText(Path.Combine(_dir, "monitoring.json"));
        Assert.Contains("\"schemaVersion\": 3", json, StringComparison.Ordinal);
        Assert.Contains("\"networkEnabled\": false", json, StringComparison.Ordinal);
    }
}
