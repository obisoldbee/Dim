using Microsoft.Win32;

namespace ScreenTimeoutToggle.Services;

public class AutoStartService
{
    public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string ValueName = "ScreenTimeoutToggle";

    private readonly Func<bool> _isEnabledFn;
    private readonly Action _enableFn;
    private readonly Action _disableFn;

    // Production constructor: uses real registry.
    public AutoStartService()
        : this(executablePath: null, isEnabledFn: null, enableFn: null, disableFn: null) { }

    // Testable constructor: inject functions that read/write the autostart state.
    public AutoStartService(
        string? executablePath = null,
        Func<bool>? isEnabledFn = null,
        Action? enableFn = null,
        Action? disableFn = null)
    {
        var exePath = executablePath ?? Environment.ProcessPath ?? "";
        _isEnabledFn = isEnabledFn ?? RealIsEnabled;
        _enableFn = enableFn ?? (() => RealEnable(exePath));
        _disableFn = disableFn ?? RealDisable;
    }

    public bool IsEnabled() => _isEnabledFn();
    public void Enable() => _enableFn();
    public void Disable() => _disableFn();

    private static bool RealIsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null) != null;
    }

    private static void RealEnable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    private static void RealDisable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
