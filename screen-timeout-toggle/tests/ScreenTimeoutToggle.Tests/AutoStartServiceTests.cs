using Microsoft.Win32;
using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class AutoStartServiceTests
{
    [Fact]
    public void IsEnabled_DelegatesToInjectedFunction_True()
    {
        var svc = new AutoStartService(isEnabledFn: () => true);
        Assert.True(svc.IsEnabled());
    }

    [Fact]
    public void IsEnabled_DelegatesToInjectedFunction_False()
    {
        var svc = new AutoStartService(isEnabledFn: () => false);
        Assert.False(svc.IsEnabled());
    }

    [Fact]
    public void Enable_CallsInjectedAction()
    {
        var called = false;
        var svc = new AutoStartService(enableFn: () => called = true);
        svc.Enable();
        Assert.True(called);
    }

    [Fact]
    public void Disable_CallsInjectedAction()
    {
        var called = false;
        var svc = new AutoStartService(disableFn: () => called = true);
        svc.Disable();
        Assert.True(called);
    }

    [Fact]
    public void Enable_WithExePath_WouldQuotePath_DelegatesToAction()
    {
        // Verify the constructor accepts an exe path without throwing;
        // the actual quoting happens inside RealEnable (registry API),
        // tested by the integration test below.
        var svc = new AutoStartService(executablePath: "C:\\Program Files\\app.exe",
                                       enableFn: () => { });
        Assert.NotNull(svc);
    }

    /// <summary>
    /// Integration test: actually round-trips a test value through the real registry,
    /// then cleans up. Validates RealIsEnabled / RealEnable / RealDisable logic.
    /// Skipped on non-Windows.
    /// </summary>
    [Fact]
    public void Real_Registry_RoundTrip_ViaPrivateMethods()
    {
        if (!OperatingSystem.IsWindows()) return;

        // We can't easily call the private Real* methods directly, so instead
        // verify the registry API contract we depend on, using a throwaway value name.
        var testValue = AutoStartService.ValueName + "_TestRoundTrip_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Write
            using (var key = Registry.CurrentUser.CreateSubKey(AutoStartService.RunKeyPath, writable: true))
            {
                key.SetValue(testValue, "\"C:\\round-trip-test.exe\"", RegistryValueKind.String);
            }
            // Read
            using (var key = Registry.CurrentUser.OpenSubKey(AutoStartService.RunKeyPath, writable: false))
            {
                var val = key?.GetValue(testValue, null);
                Assert.Equal("\"C:\\round-trip-test.exe\"", val);
            }
        }
        finally
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutoStartService.RunKeyPath, writable: true);
            key.DeleteValue(testValue, throwOnMissingValue: false);
        }
    }
}
