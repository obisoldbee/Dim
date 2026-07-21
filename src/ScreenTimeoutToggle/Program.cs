using OBDim.Models;
using OBDim.Services;
using OBDim.UI;

namespace OBDim;

internal static class Program
{
    // Mutex name kept as "ScreenTimeoutToggle" for backward compatibility
    // (avoids single-instance conflict when upgrading from v1.0.0)
    private const string GlobalMutexName = "Global\\ScreenTimeoutToggle_SingleInstance";
    private const string LocalMutexName = "Local\\ScreenTimeoutToggle_SingleInstance";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Load config early to set localization language for all UI messages
        var configSvc = ConfigService.CreateDefault();
        AppConfig initialCfg;
        try
        {
            initialCfg = configSvc.Load();
        }
        catch
        {
            initialCfg = AppConfig.CreateDefault();
        }
        LocalizationService.CurrentLanguage = initialCfg.Language;

        // Single instance check with fallback for restricted users (H2)
        Mutex? mutex = null;
        bool createdNew = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name: GlobalMutexName, createdNew: out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Global\ prefix requires SeCreateGlobalPrivilege — fall back to Local\
            try
            {
                mutex = new Mutex(initiallyOwned: true, name: LocalMutexName, createdNew: out createdNew);
            }
            catch
            {
                // If even Local\ fails, proceed without single-instance enforcement
                createdNew = true;
            }
        }
        catch
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            // H3: Notify user instead of silent exit
            MessageBox.Show(LocalizationService.Get("error.already_running"),
                LocalizationService.Get("error.app_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            mutex?.Dispose();
            return;
        }

        try
        {
            // C5: Load config once and pass the same instance to ModeService and TrayApp
            var powerSvc = new PowerConfigService();
            var modeSvc = new ModeService(initialCfg, powerSvc);
            // hwnd is IntPtr.Zero for now — TrayApp's hidden window will SetHwnd after creating its handle.
            var hotkeySvc = new HotkeyService(IntPtr.Zero);
            var autoStartSvc = new AutoStartService();

            var app = new TrayApp(configSvc, powerSvc, modeSvc, hotkeySvc, autoStartSvc, initialCfg);

            Application.Run(app);
        }
        catch (Exception ex)
        {
            // C3/H1: Catch all unhandled exceptions and show user-friendly error
            LogService.Error("OB Dim failed to start", ex);
            MessageBox.Show(LocalizationService.Get("error.startup_failed", ex.Message),
                LocalizationService.Get("error.app_title"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            mutex?.Dispose();
        }
    }
}
