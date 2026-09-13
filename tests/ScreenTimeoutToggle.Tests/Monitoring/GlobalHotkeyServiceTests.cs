using System.Windows.Forms;
using OBDim.Models;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// The popover toggle hotkey: string parsing, WM_HOTKEY routing by id, and the settings
/// default. WndProc is tested with synthetic messages the same way HotkeyServiceTests do.
/// </summary>
public class GlobalHotkeyServiceTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", "Ctrl+Alt", "D")]
    [InlineData("Ctrl+Alt+S", "Ctrl+Alt", "S")]
    [InlineData("F9", "", "F9")]
    [InlineData("d", "", "d")]
    public void ParseHotkeyString_ValidForms_Parse(string input, string modifiers, string key)
    {
        var parsed = GlobalHotkeyService.ParseHotkeyString(input);
        Assert.NotNull(parsed);
        Assert.Equal(modifiers, parsed!.Modifiers);
        Assert.Equal(key, parsed.Key);
        Assert.NotEqual(0u, HotkeyService.KeyStringToVk(parsed.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+NotAKey")]
    public void ParseHotkeyString_InvalidForms_ReturnNull(string? input)
    {
        Assert.Null(GlobalHotkeyService.ParseHotkeyString(input));
    }

    [Fact]
    public void WndProc_RoutesOnlyOwnId_AndRaisesEvent()
    {
        using var service = new GlobalHotkeyService(IntPtr.Zero, id: 2);
        var fired = 0;
        service.HotkeyPressed += () => Interlocked.Increment(ref fired);

        Assert.False(service.WndProc(new Message { Msg = 0x0312, WParam = 1 }), "mode-switch hotkey id must not be consumed");
        Assert.False(service.WndProc(new Message { Msg = 0x0312, WParam = 3 }), "unknown id must not be consumed");
        Assert.False(service.WndProc(new Message { Msg = 0x0214, WParam = 2 }), "non-WM_HOTKEY must not be consumed");
        Assert.Equal(0, Volatile.Read(ref fired));

        Assert.True(service.WndProc(new Message { Msg = 0x0312, WParam = 2 }));
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void WndProc_OtherMessagesPassThrough()
    {
        using var service = new GlobalHotkeyService(IntPtr.Zero, id: 2);
        Assert.False(service.WndProc(new Message { Msg = 0x0084 }));
    }

    [Fact]
    public void MonitoringSettings_PopoverHotkey_DefaultsToCtrlAltD()
    {
        var settings = MonitoringSettings.CreateDefault();
        Assert.Equal("Ctrl+Alt+D", settings.PopoverHotkey);
        Assert.NotNull(GlobalHotkeyService.ParseHotkeyString(settings.PopoverHotkey));
    }

    [Fact]
    public void MonitoringSettings_PopoverHotkey_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"obdim-hotkey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "monitoring.json");
            var svc = new MonitoringSettingsService(path);
            var customized = MonitoringSettings.CreateDefault() with { PopoverHotkey = "Ctrl+Shift+P" };
            Assert.True(svc.Save(customized));

            var reloaded = new MonitoringSettingsService(path).Load();
            Assert.Equal("Ctrl+Shift+P", reloaded.PopoverHotkey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Registration failure (already-taken key) must be reported, not thrown, and leave no live state.</summary>
    [Fact]
    public void Register_InvalidKey_ReturnsFalse()
    {
        using var service = new GlobalHotkeyService(IntPtr.Zero, id: 2);
        // A key string that resolves to vk 0 can never register.
        Assert.False(service.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt", Key = "NotAKey" }));
        Assert.False(service.IsRegistered);
    }
}
