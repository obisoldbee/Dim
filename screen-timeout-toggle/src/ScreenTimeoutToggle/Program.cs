using ScreenTimeoutToggle.Services;
using ScreenTimeoutToggle.UI;

namespace ScreenTimeoutToggle;

internal static class Program
{
    private const string MutexName = "Global\\ScreenTimeoutToggle_SingleInstance";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Already running — exit silently. (Could activate the existing tray,
            // but NotifyIcon doesn't expose a handle to ping; silent exit is fine.)
            return;
        }

        var configSvc = ConfigService.CreateDefault();
        var powerSvc = new PowerConfigService();
        var initialCfg = configSvc.Load();
        var modeSvc = new ModeService(initialCfg, powerSvc);
        // hwnd is IntPtr.Zero for now — TrayApp's hidden window will SetHwnd after creating its handle.
        var hotkeySvc = new HotkeyService(IntPtr.Zero);
        var autoStartSvc = new AutoStartService();

        var app = new TrayApp(configSvc, powerSvc, modeSvc, hotkeySvc, autoStartSvc);

        Application.Run(app);
    }
}
