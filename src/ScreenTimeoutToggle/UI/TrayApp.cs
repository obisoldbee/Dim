using System.Diagnostics;
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

    /// <summary>
    /// v1.0.7: the three mode icons were byte-identical (all four .ico files are the same
    /// unified OB Dim mark — a deliberate branding decision). Keeping three embedded
    /// copies and three loads around would have been pure dead weight, so there is one
    /// icon resource. Mode is communicated by the tooltip, the context menu and the
    /// balloon instead.
    /// </summary>
    private readonly Icon _iconApp;

    private volatile bool _disposed;

    /// <summary>
    /// VIDEOIDLE values (seconds) last read from the system. -1 means "never measured".
    /// </summary>
    /// <remarks>
    /// v1.0.7: the startup read produced these values and then threw them away after
    /// <see cref="ModeService.MatchCurrentMode"/>. They are the only honest numbers to
    /// show while the mode is Unknown, so they are kept.
    /// </remarks>
    private readonly object _measuredLock = new();
    private long _measuredAcSeconds = -1;
    private long _measuredDcSeconds = -1;

    /// <summary>
    /// True once the startup read has failed outright, meaning no measurement will ever
    /// arrive unless the app is restarted. Distinguishes "not yet" from "never".
    /// Guarded by <see cref="_measuredLock"/>.
    /// </summary>
    private bool _startupReadFailed;

    /// <summary>
    /// Re-entrancy guards for the two operations that shell out to powercfg.
    /// powercfg can legitimately take tens of seconds (see
    /// <see cref="PowerConfigService.TimeoutMs"/>), so overlapping calls would queue up
    /// long stalls and fight over the same power scheme. Interlocked is used rather than
    /// a plain bool because the hotkey can fire while a switch is already in flight.
    /// </summary>
    private int _switching;
    private int _applying;

    /// <summary>
    /// Set when the user has switched mode themselves (tray click, context menu, hotkey).
    /// </summary>
    /// <remarks>
    /// v1.0.8: the startup powercfg read starts in the constructor, before the tray icon
    /// exists, and worst case takes ~34 s (2 attempts x (15 s timeout + 2 s kill grace)).
    /// During that window the tray is already interactive, so the user can switch mode
    /// while the read — and the values it returns — still describe the system as it was
    /// <em>before</em> their switch. Applying that stale result would leave the tray
    /// showing one mode while the system runs another, which is the one thing this app
    /// must never get wrong.
    /// <para>
    /// Written on a thread-pool thread inside <c>Task.Run</c>, immediately after
    /// <see cref="ModeService.SwitchTo"/> succeeds and before any UI-thread continuation
    /// is posted, so the flag is always set before the startup result can be applied.
    /// Read on the UI thread. <c>volatile</c> for cross-thread visibility.
    /// </para>
    /// </remarks>
    private volatile bool _userToggled;

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

        _syncRoot = CreateSyncRoot();

        // Load the single unified icon from embedded resources
        _iconApp = LoadIcon("obdim.ico");

        // Create hidden message window for WM_HOTKEY
        _msgWindow = new HiddenMessageWindow(_hotkeySvc);
        _msgWindow.CreateHandle();
        _hotkeySvc.SetHwnd(_msgWindow.Handle);

        // Register global hotkey (non-fatal if it fails)
        if (!_hotkeySvc.Register(_config.Hotkey))
        {
            ShowBubble(LocalizationService.Get("bubble.hotkey_unavailable_title"),
                       LocalizationService.Get("bubble.hotkey_unavailable", _config.Hotkey.Modifiers, _config.Hotkey.Key),
                       ToolTipIcon.Warning);
        }
        _hotkeySvc.HotkeyPressed += OnHotkeyPressed;
        _modeSvc.ModeChanged += OnModeChanged;

        // v1.0.7: the tray icon is created BEFORE the startup powercfg read and starts out
        // as Unknown. Matching the system state used to happen synchronously right here,
        // in the constructor, on the UI thread: worst case 2 attempts x (15 s timeout +
        // 2 s kill grace) = ~34 s during which no tray icon existed at all and the app
        // looked hung. v1.0.6 fixed the toggle path and the settings-apply path but
        // missed this one.
        _notify = new NotifyIcon
        {
            Icon = IconFor(_modeSvc.CurrentMode),
            Visible = true,
            Text = TooltipFor(_modeSvc.CurrentMode)
        };
        // spec §2.1(1): a single left click toggles — it used to be DoubleClick, so a
        // plain click did nothing.
        _notify.Click += async (_, _) => await ToggleModeAsync();

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

        // v1.0.7: the config file could not be parsed and every value is back to factory
        // defaults. Say so — the previous behaviour was a silent full reset.
        if (_configSvc.LastLoadWasRecovered)
        {
            ShowBubble(LocalizationService.Get("bubble.config_reset_title"),
                       LocalizationService.Get("bubble.config_reset"),
                       ToolTipIcon.Warning);
        }

        LogService.Info($"OB Dim started. CurrentMode={_modeSvc.CurrentMode} (startup match pending)");

        // Must come after _notify exists. The continuation is posted to the UI thread's
        // message loop, which is not pumped until Application.Run, so it cannot land
        // before the constructor finishes.
        _ = MatchStartupModeAsync();
    }

    /// <summary>
    /// Creates the hidden control used to marshal UI work back to the UI thread.
    /// </summary>
    /// <returns>A control that already owns a window handle.</returns>
    /// <remarks>
    /// <para>
    /// v1.0.7 CRITICAL FIX, extracted in v1.0.8 so a test can reach it.
    /// <see cref="Control"/> creates its window handle lazily. Until it exists,
    /// <c>InvokeRequired</c> walks the parent chain looking for a marshaling control,
    /// finds none, and returns <b>false</b> — so every <c>BeginInvoke</c> branch below
    /// was dead code and <see cref="UpdateModeUI"/> ran on whatever thread raised
    /// <c>ModeChanged</c>. <c>ModeChanged</c> is raised inside <c>Task.Run</c>
    /// (see <see cref="ToggleModeCoreAsync"/>), so that was every single mode switch.
    /// Touching <see cref="Control.Handle"/> here forces creation; the caller runs on the
    /// UI thread, which is exactly the thread the handle must belong to.
    /// </para>
    /// <para>
    /// Why this is a separate method and not three inlined lines: the v1.0.7 guard test
    /// only exercised a bare <see cref="Control"/>, so commenting the handle line out left
    /// the whole suite green. A guard that cannot fail is worse than no guard — it buys
    /// confidence the code does not deserve. Keeping the invariant in one named place is
    /// what makes <c>UiMarshallingTests</c> able to assert it.
    /// </para>
    /// </remarks>
    internal static Control CreateSyncRoot()
    {
        var control = new Control();

        // Force handle creation. Do NOT remove: InvokeRequired is false without it.
        _ = control.Handle;

        Debug.Assert(control.IsHandleCreated,
            "_syncRoot must own a window handle, otherwise InvokeRequired always returns false and UI marshalling silently stops working.");
        return control;
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
    /// Reads the real system VIDEOIDLE values and resolves the startup mode off the UI
    /// thread, then refreshes the icon, tooltip and menu item.
    /// </summary>
    /// <remarks>
    /// Deliberately does not call <see cref="ModeService.SwitchTo"/>: spec §6.2 says an
    /// unmatched system must be left alone, and this must never write to the power scheme.
    /// <para>
    /// v1.0.8: the resolved mode is discarded when the user has switched mode in the
    /// meantime. The measured values are still recorded — they are what the Unknown
    /// tooltip reports, and they are a fact about the system regardless of who moved last.
    /// </para>
    /// </remarks>
    private async Task MatchStartupModeAsync()
    {
        try
        {
            var (ac, dc) = await Task.Run(() => _powerSvc.GetCurrentVideoIdle());

            // Keep the measured values: they are what the Unknown tooltip reports.
            SetMeasured(ac, dc);

            var matched = _modeSvc.MatchCurrentMode(ac, dc);

            // The read started before the tray icon existed. If the user has since
            // switched, their switch already changed the power scheme; the mode we
            // derived from this older read is stale and must not overwrite it. The
            // discard is logged, not silent.
            if (_userToggled)
            {
                LogService.Info(
                    $"Startup mode match result ({matched}) discarded: the user already switched to {_modeSvc.CurrentMode}");
                return;
            }

            _modeSvc.SetCurrentMode(matched);
            RefreshStartupModeUI(matched);
        }
        catch (Exception ex)
        {
            // v1.0.8: say why there will never be a measurement, so the tooltip stops
            // claiming a read is in progress when no read is running.
            SetStartupReadFailed();
            LogService.Warn($"Startup mode match failed: {ex.Message}");
            // leave as Unknown
        }
    }

    /// <summary>
    /// Applies the resolved startup mode to the tray, marshalling back to the UI thread.
    /// No balloon: nothing changed from the user's point of view, we only just found out
    /// what the system was already doing.
    /// </summary>
    /// <param name="mode">Mode resolved from the system.</param>
    private void RefreshStartupModeUI(AppMode mode)
    {
        if (_disposed) return;

        if (_syncRoot.InvokeRequired)
            _syncRoot.BeginInvoke(() => ApplyStartupModeUI(mode));
        else
            ApplyStartupModeUI(mode);
    }

    private void ApplyStartupModeUI(AppMode mode)
    {
        if (_disposed) return;

        _notify.Icon = IconFor(mode);
        _notify.Text = TooltipFor(mode);
        UpdateSwitchMenuItem();
        LogService.Info($"Startup mode match resolved: {mode}");
    }

    private void SetMeasured(long acSeconds, long dcSeconds)
    {
        lock (_measuredLock)
        {
            _measuredAcSeconds = acSeconds;
            _measuredDcSeconds = dcSeconds;
            _startupReadFailed = false;
        }
    }

    /// <summary>
    /// Records that the startup powercfg read will not produce a measurement at all.
    /// </summary>
    /// <remarks>
    /// v1.0.8: without this the tooltip keeps saying "reading system settings…" forever,
    /// while in fact no read is running and none will be retried.
    /// </remarks>
    private void SetStartupReadFailed()
    {
        lock (_measuredLock)
        {
            _startupReadFailed = true;
        }
    }

    /// <summary>
    /// Localization key describing why no measured values are available.
    /// </summary>
    /// <returns>
    /// <c>tooltip.read_failed</c> when the startup read failed outright,
    /// <c>tooltip.detecting</c> while it is still in flight.
    /// </returns>
    private string TooltipKeyWhileUnmeasured()
    {
        lock (_measuredLock)
        {
            return _startupReadFailed ? "tooltip.read_failed" : "tooltip.detecting";
        }
    }

    /// <summary>
    /// Gets the most recent measured system values, if any have been read yet.
    /// </summary>
    /// <param name="acSeconds">Measured AC VIDEOIDLE in seconds.</param>
    /// <param name="dcSeconds">Measured DC VIDEOIDLE in seconds.</param>
    /// <returns>True when a measurement is available.</returns>
    private bool TryGetMeasured(out long acSeconds, out long dcSeconds)
    {
        lock (_measuredLock)
        {
            acSeconds = _measuredAcSeconds;
            dcSeconds = _measuredDcSeconds;
            return acSeconds >= 0 && dcSeconds >= 0;
        }
    }

    /// <summary>
    /// Toggles between Work and Away modes asynchronously.
    /// Runs powercfg on a thread pool thread to avoid blocking the UI.
    /// </summary>
    private async Task ToggleModeAsync()
    {
        // v1.0.6: drop a toggle that arrives while one is still running. Holding the
        // hotkey down used to stack concurrent powercfg calls on top of each other.
        if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
        {
            LogService.Info("Mode switch ignored: a switch is already in progress");
            return;
        }

        try
        {
            await ToggleModeCoreAsync();
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
        }
    }

    /// <summary>
    /// Performs the actual mode toggle. Only ever entered via <see cref="ToggleModeAsync"/>,
    /// which owns the re-entrancy guard.
    /// </summary>
    private async Task ToggleModeCoreAsync()
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
            await Task.Run(() =>
            {
                _modeSvc.SwitchTo(target);

                // v1.0.8: flag the switch from here, not from the continuation. The
                // startup match's continuation can be posted between SwitchTo returning
                // and this method's continuation running, and it must see the flag.
                _userToggled = true;
            });
            _config = _config with { CurrentMode = target };
            if (!_configSvc.Save(_config))
            {
                // v1.0.7: the switch already took effect in the power scheme, but it will
                // not survive a restart. Silently swallowing the failure left the user
                // believing everything was fine.
                ShowBubbleAsync(LocalizationService.Get("bubble.save_failed_title"),
                                LocalizationService.Get("bubble.save_failed"),
                                ToolTipIcon.Warning);
            }
        }
        catch (PowerConfigException ex)
        {
            // v1.0.5: raw powercfg messages are English and internal. Show a localized,
            // actionable message derived from the failure category; the raw text stays
            // in the log for diagnosis.
            LogService.Error($"Switch failed ({ex.Kind}): {ex.Message}", ex);
            ShowBubbleAsync(LocalizationService.Get("bubble.switch_failed_title"),
                            LocalizationService.GetPowerConfigError(ex.Kind, ex.Message),
                            ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            // A2: catch all exceptions (not just PowerConfigException)
            LogService.Error("Switch failed", ex);
            ShowBubbleAsync(LocalizationService.Get("bubble.switch_failed_title"), ex.Message, ToolTipIcon.Error);
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
        if (_disposed) return;

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
        if (_disposed) return;

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
                    // Two very different failures share one false result: the key itself is
                    // unusable (vk == 0) or the combination is already taken. They need
                    // different advice, so distinguish them before picking the message.
                    var keyIsInvalid = HotkeyService.KeyStringToVk(attemptedHotkey.Key) == 0;

                    // New key unavailable — try to bring the old key back. This can fail too
                    // (something else may have grabbed it in the meantime), in which case NO
                    // hotkey is live. Say so loudly instead of claiming we "reverted".
                    var rolledBack = _hotkeySvc.Register(oldCfg.Hotkey);
                    var newKeyText = $"{attemptedHotkey.Modifiers}+{attemptedHotkey.Key}";
                    var oldKeyText = $"{oldCfg.Hotkey.Modifiers}+{oldCfg.Hotkey.Key}";

                    if (rolledBack)
                    {
                        var titleKey = keyIsInvalid
                            ? "bubble.hotkey_change_invalid_title"
                            : "bubble.hotkey_change_failed_title";
                        var textKey = keyIsInvalid
                            ? "bubble.hotkey_change_invalid"
                            : "bubble.hotkey_change_failed";

                        ShowBubble(LocalizationService.Get(titleKey),
                                   LocalizationService.Get(textKey,
                                       attemptedHotkey.Modifiers, attemptedHotkey.Key),
                                   ToolTipIcon.Warning);
                        LogService.Warn($"Hotkey change to {newKeyText} failed ({(keyIsInvalid ? "invalid key" : "already in use")}), reverted to {oldKeyText}");
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

            // v1.0.7 (M3): SettingsForm is modal but still pumps messages, so a mode switch
            // can complete while the dialog is open. form.Result was built from the config
            // as it was when the dialog was shown, so it still carries the stale
            // CurrentMode; writing it back would undo the switch that just happened (and
            // was already persisted). Re-apply the live mode instead.
            if (_modeSvc.CurrentMode != effectiveCfg.CurrentMode)
            {
                effectiveCfg = effectiveCfg with { CurrentMode = _modeSvc.CurrentMode };
            }

            if (!_configSvc.Save(effectiveCfg))
            {
                // v1.0.7: everything else in this method is about to be applied in memory,
                // so the settings are live but not durable. Tell the user.
                ShowBubble(LocalizationService.Get("bubble.save_failed_title"),
                           LocalizationService.Get("bubble.save_failed"),
                           ToolTipIcon.Warning);
            }
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
                // v1.0.6: powercfg must never run on the UI thread here. Worst case it is
                // 3 steps x MaxAttempts x (timeout + kill grace) — over 100 seconds — which
                // used to freeze the whole tray app while the settings dialog closed.
                // Fire-and-forget is fine: failures surface as a balloon, not a return value.
                _ = ApplyCurrentModeTimeoutsAsync();
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

    /// <summary>
    /// Re-applies the current mode's timeout values via powercfg on a thread-pool thread.
    /// </summary>
    /// <remarks>
    /// v1.0.6: extracted from <see cref="OpenSettings"/> because powercfg is slow and was
    /// running synchronously on the UI thread. All UI work (the error balloon) is
    /// marshalled back through <see cref="_syncRoot"/>, since the continuation after
    /// <c>await Task.Run(...)</c> does not necessarily run on the UI thread.
    /// A second call while one is in flight is dropped (the user can reopen Settings and
    /// confirm again before the first apply has finished).
    /// <para>
    /// v1.0.7: that marshalling is now real. Until this release <c>_syncRoot</c> had no
    /// window handle, so <c>InvokeRequired</c> was always false and the comment above was
    /// describing behaviour the code did not have.
    /// </para>
    /// </remarks>
    private async Task ApplyCurrentModeTimeoutsAsync()
    {
        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
        {
            LogService.Info("Timeout re-apply ignored: an apply is already in progress");
            return;
        }

        try
        {
            await Task.Run(() => _modeSvc.ReapplyCurrentMode());
        }
        catch (PowerConfigException ex)
        {
            LogService.Error($"Reapply failed ({ex.Kind}): {ex.Message}", ex);
            ShowBubbleAsync(LocalizationService.Get("bubble.switch_failed_title"),
                            LocalizationService.GetPowerConfigError(ex.Kind, ex.Message),
                            ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            LogService.Error("Reapply failed", ex);
            ShowBubbleAsync(LocalizationService.Get("bubble.switch_failed_title"), ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    /// <summary>
    /// Shows a balloon, marshalling to the UI thread when called from a worker thread.
    /// NotifyIcon is not a Control, so <see cref="_syncRoot"/> is used for marshalling.
    /// </summary>
    /// <param name="title">Balloon title.</param>
    /// <param name="text">Balloon body.</param>
    /// <param name="icon">Balloon icon.</param>
    private void ShowBubbleAsync(string title, string text, ToolTipIcon icon)
    {
        if (_disposed) return;

        if (_syncRoot.InvokeRequired)
        {
            _syncRoot.BeginInvoke(() => ShowBubble(title, text, icon));
        }
        else
        {
            ShowBubble(title, text, icon);
        }
    }

    private void ExitApp()
    {
        LogService.Info("OB Dim exiting via ExitApp");
        Dispose();
        ExitThread();
    }

    /// <summary>
    /// Returns the tray icon for a mode.
    /// </summary>
    /// <param name="mode">Current mode (retained for call-site readability).</param>
    /// <returns>The unified OB Dim icon.</returns>
    /// <remarks>
    /// v1.0.7: this used to select between three embedded icons that were byte-identical
    /// copies of each other. One resource, one icon; the mode is reported by the tooltip,
    /// the menu item and the balloon.
    /// </remarks>
    private Icon IconFor(AppMode mode) => _iconApp;

    private string TooltipFor(AppMode mode)
    {
        // v1.0.7 (I3): Unknown means "the system matches no configured mode", so the only
        // honest numbers are the ones actually read from the system. Showing the Work
        // configuration here (the v1.0.6 behaviour) reported a value the system is not
        // running — the worst possible thing to be wrong about in an app whose entire job
        // is telling the user what the screen is going to do.
        if (mode == AppMode.Unknown)
        {
            // Two different "no numbers yet" cases, and only one of them is transient:
            // the startup read may still be in flight, or it may already have failed and
            // will not be retried. Claiming "reading…" forever would be a lie.
            if (!TryGetMeasured(out var acSeconds, out var dcSeconds))
                return LocalizationService.Get(TooltipKeyWhileUnmeasured());

            return FormatTooltip("tooltip.unknown", SecondsToMinutes(acSeconds), SecondsToMinutes(dcSeconds));
        }

        var (acMin, dcMin) = mode == AppMode.Away
            ? (_config.Away.AcMinutes, _config.Away.DcMinutes)
            : (_config.Work.AcMinutes, _config.Work.DcMinutes);
        var tooltipKey = mode switch
        {
            AppMode.Work => "tooltip.work",
            AppMode.Away => "tooltip.away",
            _ => "tooltip.unknown"
        };
        return FormatTooltip(tooltipKey, acMin, dcMin);
    }

    /// <summary>
    /// Formats a tooltip line, rendering 0 as "never".
    /// </summary>
    /// <param name="key">Localization key of the tooltip template.</param>
    /// <param name="acMin">AC timeout in minutes.</param>
    /// <param name="dcMin">DC timeout in minutes.</param>
    /// <returns>The localized tooltip.</returns>
    private static string FormatTooltip(string key, long acMin, long dcMin)
    {
        var acTxt = acMin == 0
            ? LocalizationService.Get("common.never")
            : LocalizationService.Get("common.minutes", acMin);
        var dcTxt = dcMin == 0
            ? LocalizationService.Get("common.never")
            : LocalizationService.Get("common.minutes", dcMin);
        return LocalizationService.Get(key, acTxt, dcTxt);
    }

    /// <summary>
    /// Converts a system VIDEOIDLE value from seconds to whole minutes.
    /// </summary>
    /// <param name="seconds">Value read from powercfg, in seconds.</param>
    /// <returns>Minutes, clamped so an implausibly large value cannot overflow the UI.</returns>
    private static long SecondsToMinutes(long seconds)
    {
        if (seconds <= 0) return 0;
        return Math.Min(seconds / 60, int.MaxValue);
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
        if (_disposed) return;

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
            // v1.0.7: HotkeyService is IDisposable now, so cleanup no longer depends on
            // the caller remembering to call Unregister().
            try { _hotkeySvc.Dispose(); } catch { /* best effort */ }
            try { _notify.Visible = false; } catch { /* best effort */ }
            try { _notify.Dispose(); } catch { /* best effort */ }
            try { _iconApp.Dispose(); } catch { /* best effort */ }
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
