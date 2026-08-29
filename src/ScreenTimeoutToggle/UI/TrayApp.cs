using System.Reflection;
using OBDim.Models;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// Tray application context: manages NotifyIcon, context menu, hotkey dispatch,
/// mode switching, and settings. Implements IDisposable for proper resource cleanup.
/// </summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly ConfigService _configSvc;
    private readonly PowerConfigService _powerSvc;
    private readonly ModeService _modeSvc;
    private readonly HotkeyService _hotkeySvc;
    private readonly AutoStartService _autoStartSvc;
    private readonly HiddenMessageWindow _msgWindow;
    private readonly Control _syncRoot;
    private AppConfig _config;

    private readonly Icon _iconWork;
    private readonly Icon _iconAway;
    private readonly Icon _iconUnknown;

    private bool _disposed;

    /// <summary>
    /// Creates the tray application.
    /// </summary>
    /// <param name="configSvc">Config persistence service.</param>
    /// <param name="powerSvc">powercfg wrapper.</param>
    /// <param name="modeSvc">Mode state machine.</param>
    /// <param name="hotkeySvc">Global hotkey service.</param>
    /// <param name="autoStartSvc">Autostart registry service.</param>
    /// <param name="initialConfig">Pre-loaded config (avoids double-load).</param>
    public TrayApp(ConfigService configSvc,
                   PowerConfigService powerSvc,
                   ModeService modeSvc,
                   HotkeyService hotkeySvc,
                   AutoStartService autoStartSvc,
                   AppConfig initialConfig)
    {
        _configSvc = configSvc;
        _powerSvc = powerSvc;
        _modeSvc = modeSvc;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;
        _config = initialConfig;

        // Set localization language from config (also set by Program.cs for early messages)
        LocalizationService.CurrentLanguage = _config.Language;

        // Hidden control for thread marshaling (NotifyIcon is not a Control)
        _syncRoot = new Control();

        // Load icons from embedded resources
        _iconWork = LoadIcon("icon-work.ico");
        _iconAway = LoadIcon("icon-away.ico");
        _iconUnknown = LoadIcon("icon-unknown.ico");

        // Create hidden message window for WM_HOTKEY
        _msgWindow = new HiddenMessageWindow(_hotkeySvc);
        _msgWindow.CreateHandle();
        _hotkeySvc.SetHwnd(_msgWindow.Handle);

        // Match current system state (reads powercfg, may throw — non-fatal)
        try
        {
            var (ac, dc) = _powerSvc.GetCurrentVideoIdle();
            var matched = _modeSvc.MatchCurrentMode(ac, dc);
            _modeSvc.SetCurrentMode(matched);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Startup mode match failed: {ex.Message}");
            // leave as Unknown
        }

        // Register global hotkey (non-fatal if it fails)
        if (!_hotkeySvc.Register(_config.Hotkey))
        {
            ShowBubble(LocalizationService.Get("bubble.hotkey_unavailable_title"),
                       LocalizationService.Get("bubble.hotkey_unavailable", _config.Hotkey.Modifiers, _config.Hotkey.Key),
                       ToolTipIcon.Warning);
        }
        _hotkeySvc.HotkeyPressed += OnHotkeyPressed;
        _modeSvc.ModeChanged += OnModeChanged;

        // Create tray icon
        _notify = new NotifyIcon
        {
            Icon = IconFor(_modeSvc.CurrentMode),
            Visible = true,
            Text = TooltipFor(_modeSvc.CurrentMode)
        };
        _notify.DoubleClick += async (_, _) => await ToggleModeAsync();

        BuildContextMenu();
        UpdateSwitchMenuItem();

        // Sync autostart on first launch (C4): if config says autostart but registry doesn't have it
        if (_config.AutoStart && !_autoStartSvc.IsEnabled())
        {
            _autoStartSvc.Enable();
            LogService.Info("Autostart enabled on first launch (config had AutoStart=true but registry was missing)");
        }

        // Ensure autostart path is current (F1): update registry if EXE was moved
        _autoStartSvc.EnsurePathSync();

        // Hook ApplicationExit for cleanup on all exit paths (C2)
        Application.ApplicationExit += (_, _) => Dispose();

        LogService.Info($"OB Dim started. CurrentMode={_modeSvc.CurrentMode}");
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var switchItem = new ToolStripMenuItem(LocalizationService.Get("menu.switch_to_away"), null, async (_, _) => await ToggleModeAsync())
        {
            Name = "switchItem"
        };
        var settingsItem = new ToolStripMenuItem(LocalizationService.Get("menu.settings"), null, (_, _) => OpenSettings());
        var exitItem = new ToolStripMenuItem(LocalizationService.Get("menu.exit"), null, (_, _) => ExitApp());

        menu.Items.AddRange(new ToolStripItem[] { switchItem, settingsItem, new ToolStripSeparator(), exitItem });
        _notify.ContextMenuStrip = menu;
    }

    /// <summary>
    /// Toggles between Work and Away modes asynchronously.
    /// Runs powercfg on a thread pool thread to avoid blocking the UI.
    /// </summary>
    private async Task ToggleModeAsync()
    {
        // C8: switch expression for clarity
        var target = _modeSvc.CurrentMode switch
        {
            AppMode.Work => AppMode.Away,
            AppMode.Away => AppMode.Work,
            _ => AppMode.Work // Unknown → default to Work
        };

        try
        {
            // C6: run powercfg on thread pool to avoid blocking UI
            await Task.Run(() => _modeSvc.SwitchTo(target));
            _config = _config with { CurrentMode = target };
            _configSvc.Save(_config);
        }
        catch (PowerConfigException ex)
        {
            // v1.0.5: raw powercfg messages are English and internal. Show a localized,
            // actionable message derived from the failure category; the raw text stays
            // in the log for diagnosis.
            LogService.Error($"Switch failed ({ex.Kind}): {ex.Message}", ex);
            ShowBubble(LocalizationService.Get("bubble.switch_failed_title"),
                       LocalizationService.GetPowerConfigError(ex.Kind, ex.Message),
                       ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            // A2: catch all exceptions (not just PowerConfigException)
            LogService.Error("Switch failed", ex);
            ShowBubble(LocalizationService.Get("bubble.switch_failed_title"), ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnHotkeyPressed()
    {
        // Fire-and-forget async — the hotkey event handler must not block
        _ = ToggleModeAsync();
    }

    /// <summary>
    /// Handles mode change events. May fire from a background thread (Task.Run),
    /// so we marshal UI updates back to the UI thread.
    /// </summary>
    private void OnModeChanged(object? sender, AppMode mode)
    {
        if (_syncRoot.InvokeRequired)
        {
            _syncRoot.BeginInvoke(() => UpdateModeUI(mode));
        }
        else
        {
            UpdateModeUI(mode);
        }
    }

    private void UpdateModeUI(AppMode mode)
    {
        _notify.Icon = IconFor(mode);
        _notify.Text = TooltipFor(mode);
        UpdateSwitchMenuItem();
        var modeName = ModeDisplayName(mode);
        ShowBubble(LocalizationService.Get("bubble.mode_changed_title"),
                   LocalizationService.Get("bubble.mode_changed", modeName),
                   ToolTipIcon.Info);
        LogService.Info($"Mode changed to {mode}");
    }

    private void UpdateSwitchMenuItem()
    {
        if (_notify.ContextMenuStrip?.Items["switchItem"] is ToolStripMenuItem item)
        {
            item.Text = _modeSvc.CurrentMode == AppMode.Work
                ? LocalizationService.Get("menu.switch_to_away")
                : LocalizationService.Get("menu.switch_to_work");
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config, _hotkeySvc, _autoStartSvc);
        if (form.ShowDialog() == DialogResult.OK)
        {
            var newCfg = form.Result;
            var oldCfg = _config;
            // Effective config = what actually gets persisted. Starts as the user's choice;
            // a hotkey that fails to register is rolled back BEFORE saving so the config
            // file never keeps a value we know is unusable (would fail again on next launch).
            //
            // From here on, ONLY effectiveCfg is used. Mixing newCfg and effectiveCfg in the
            // same block is a maintenance trap: they differ in exactly one field, so every
            // read has to be re-checked to know whether the user's choice or the rolled-back
            // value is in play.
            var effectiveCfg = newCfg;

            // E1: Hotkey change — try new key first, rollback on failure (before persisting).
            // The only thing read off newCfg from here on is the key the user ASKED for;
            // every other field is read off effectiveCfg.
            if (!Equals(newCfg.Hotkey, oldCfg.Hotkey))
            {
                var attemptedHotkey = newCfg.Hotkey;

                // Register internally resolves the key first, then unregisters the old key
                var registered = _hotkeySvc.Register(attemptedHotkey);
                effectiveCfg = ConfigResolver.ResolveEffectiveConfig(oldCfg, newCfg, registered);

                if (registered)
                {
                    LogService.Info($"Hotkey changed to {effectiveCfg.Hotkey.Modifiers}+{effectiveCfg.Hotkey.Key}");
                }
                else
                {
                    // New key unavailable — try to bring the old key back. This can fail too
                    // (something else may have grabbed it in the meantime), in which case NO
                    // hotkey is live. Say so loudly instead of claiming we "reverted".
                    var rolledBack = _hotkeySvc.Register(oldCfg.Hotkey);
                    var newKeyText = $"{attemptedHotkey.Modifiers}+{attemptedHotkey.Key}";
                    var oldKeyText = $"{oldCfg.Hotkey.Modifiers}+{oldCfg.Hotkey.Key}";

                    if (rolledBack)
                    {
                        ShowBubble(LocalizationService.Get("bubble.hotkey_change_failed_title"),
                                   LocalizationService.Get("bubble.hotkey_change_failed",
                                       attemptedHotkey.Modifiers, attemptedHotkey.Key),
                                   ToolTipIcon.Warning);
                        LogService.Warn($"Hotkey change to {newKeyText} failed, reverted to {oldKeyText}");
                    }
                    else
                    {
                        ShowBubble(LocalizationService.Get("bubble.hotkey_rollback_failed_title"),
                                   LocalizationService.Get("bubble.hotkey_rollback_failed",
                                       attemptedHotkey.Modifiers, attemptedHotkey.Key,
                                       oldCfg.Hotkey.Modifiers, oldCfg.Hotkey.Key),
                                   ToolTipIcon.Error);
                        LogService.Error($"Hotkey change to {newKeyText} failed and rollback to {oldKeyText} also failed; no hotkey is registered");
                    }
                }
            }

            _configSvc.Save(effectiveCfg);
            _config = effectiveCfg;

            if (effectiveCfg.AutoStart != oldCfg.AutoStart)
            {
                if (effectiveCfg.AutoStart) _autoStartSvc.Enable();
                else _autoStartSvc.Disable();
            }

            _modeSvc.UpdateConfig(effectiveCfg);

            // C7: Only re-apply if the current mode's timeout values actually changed
            bool currentModeValuesChanged = _modeSvc.CurrentMode switch
            {
                AppMode.Work => !Equals(effectiveCfg.Work, oldCfg.Work),
                AppMode.Away => !Equals(effectiveCfg.Away, oldCfg.Away),
                _ => false // Unknown — no reapply needed
            };

            if (currentModeValuesChanged)
            {
                try
                {
                    _modeSvc.ReapplyCurrentMode();
                }
                catch (PowerConfigException ex)
                {
                    LogService.Error($"Reapply failed ({ex.Kind}): {ex.Message}", ex);
                    ShowBubble(LocalizationService.Get("bubble.switch_failed_title"),
                               LocalizationService.GetPowerConfigError(ex.Kind, ex.Message),
                               ToolTipIcon.Error);
                }
                catch (Exception ex)
                {
                    LogService.Error("Reapply failed", ex);
                    ShowBubble(LocalizationService.Get("bubble.switch_failed_title"), ex.Message, ToolTipIcon.Error);
                }
            }

            // Language change: update runtime language, rebuild menu, notify user
            if (effectiveCfg.Language != oldCfg.Language)
            {
                LocalizationService.CurrentLanguage = effectiveCfg.Language;
                BuildContextMenu();
                UpdateSwitchMenuItem();
                var langName = LocalizationService.GetLanguageDisplayName(effectiveCfg.Language);
                ShowBubble(LocalizationService.Get("bubble.language_changed_title"),
                           LocalizationService.Get("bubble.language_changed", langName),
                           ToolTipIcon.Info);
            }

            _notify.Text = TooltipFor(_modeSvc.CurrentMode);
        }
    }

    private void ExitApp()
    {
        LogService.Info("OB Dim exiting via ExitApp");
        Dispose();
        ExitThread();
    }

    private Icon IconFor(AppMode mode) => mode switch
    {
        AppMode.Work => _iconWork,
        AppMode.Away => _iconAway,
        _ => _iconUnknown
    };

    private string TooltipFor(AppMode mode)
    {
        var (acMin, dcMin) = mode == AppMode.Away
            ? (_config.Away.AcMinutes, _config.Away.DcMinutes)
            : (_config.Work.AcMinutes, _config.Work.DcMinutes);
        var acTxt = acMin == 0
            ? LocalizationService.Get("common.never")
            : LocalizationService.Get("common.minutes", acMin);
        var dcTxt = dcMin == 0
            ? LocalizationService.Get("common.never")
            : LocalizationService.Get("common.minutes", dcMin);
        var tooltipKey = mode switch
        {
            AppMode.Work => "tooltip.work",
            AppMode.Away => "tooltip.away",
            _ => "tooltip.unknown"
        };
        return LocalizationService.Get(tooltipKey, acTxt, dcTxt);
    }

    /// <summary>
    /// Returns the localized display name for an AppMode.
    /// </summary>
    private static string ModeDisplayName(AppMode mode) => mode switch
    {
        AppMode.Work => LocalizationService.Get("mode.work"),
        AppMode.Away => LocalizationService.Get("mode.away"),
        _ => LocalizationService.Get("mode.unknown")
    };

    private void ShowBubble(string title, string text, ToolTipIcon icon)
    {
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = text;
        _notify.BalloonTipIcon = icon;
        _notify.ShowBalloonTip(800);
    }

    private static Icon LoadIcon(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var fullName = $"OBDim.assets.{name}";
        using var stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {fullName}");
        return new Icon(stream);
    }

    /// <summary>
    /// Releases all resources (NotifyIcon, Icons, hidden window, hotkey).
    /// Called by ExitApp, Application.ApplicationExit, and base.Dispose.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try { _hotkeySvc.Unregister(); } catch { /* best effort */ }
            try { _notify.Visible = false; } catch { /* best effort */ }
            try { _notify.Dispose(); } catch { /* best effort */ }
            try { _iconWork.Dispose(); } catch { /* best effort */ }
            try { _iconAway.Dispose(); } catch { /* best effort */ }
            try { _iconUnknown.Dispose(); } catch { /* best effort */ }
            try { _msgWindow.DestroyHandle(); } catch { /* best effort */ }
            try { _syncRoot.Dispose(); } catch { /* best effort */ }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// A message-only window (HWND_MESSAGE parent) that receives WM_HOTKEY
    /// and forwards to HotkeyService.
    /// </summary>
    private class HiddenMessageWindow : NativeWindow
    {
        private readonly HotkeyService _hotkey;
        public HiddenMessageWindow(HotkeyService hotkey) { _hotkey = hotkey; }

        public void CreateHandle()
        {
            var cp = new CreateParams
            {
                Caption = "OBDimMsg",
                Parent = (IntPtr)(-3) // HWND_MESSAGE
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (_hotkey.WndProc(m)) return;
            base.WndProc(ref m);
        }
    }
}
