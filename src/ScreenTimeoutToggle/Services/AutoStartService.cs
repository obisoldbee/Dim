using Microsoft.Win32;

namespace OBDim.Services;

public class AutoStartService
{
    public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string ValueName = "ScreenTimeoutToggle";

    private readonly Func<bool> _isEnabledFn;
    private readonly Action _enableFn;
    private readonly Action _disableFn;
    private readonly string _exePath;

    /// <summary>Production constructor: uses real registry.</summary>
    public AutoStartService()
        : this(executablePath: null, isEnabledFn: null, enableFn: null, disableFn: null) { }

    /// <summary>Testable constructor: inject functions that read/write the autostart state.</summary>
    public AutoStartService(
        string? executablePath = null,
        Func<bool>? isEnabledFn = null,
        Action? enableFn = null,
        Action? disableFn = null)
    {
        _exePath = executablePath ?? Environment.ProcessPath ?? "";
        _isEnabledFn = isEnabledFn ?? RealIsEnabled;
        _enableFn = enableFn ?? (() => RealEnable(_exePath));
        _disableFn = disableFn ?? RealDisable;
    }

    public bool IsEnabled() => _isEnabledFn();

    public void Enable() => _enableFn();

    public void Disable() => _disableFn();

    /// <summary>
    /// Checks whether the registered autostart path matches the current executable path.
    /// If the EXE was moved/renamed, updates the registry to point to the new location.
    /// Only applies when using real registry (not injected functions).
    /// </summary>
    public void EnsurePathSync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!IsEnabled()) return;

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var expectedValue = $"\"{currentPath}\"";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var registeredValue = key?.GetValue(ValueName, null) as string;

            if (registeredValue != null && registeredValue != expectedValue)
            {
                // Path changed (e.g., EXE moved) — update registry
                LogService.Warn($"Autostart path sync: updating from '{registeredValue}' to '{expectedValue}'");
                Enable();
            }
        }
        catch
        {
            // Best effort — registry access failure is non-fatal
        }
    }

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
