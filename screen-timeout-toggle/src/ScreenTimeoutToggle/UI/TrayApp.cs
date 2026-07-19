using System.Reflection;
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;

namespace ScreenTimeoutToggle.UI;

public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly ConfigService _configSvc;
    private readonly PowerConfigService _powerSvc;
    private readonly ModeService _modeSvc;
    private readonly HotkeyService _hotkeySvc;
    private readonly AutoStartService _autoStartSvc;
    private readonly HiddenMessageWindow _msgWindow;
    private AppConfig _config;

    private readonly Icon _iconWork;
    private readonly Icon _iconAway;
    private readonly Icon _iconUnknown;

    public TrayApp(ConfigService configSvc,
                   PowerConfigService powerSvc,
                   ModeService modeSvc,
                   HotkeyService hotkeySvc,
                   AutoStartService autoStartSvc)
    {
        _configSvc = configSvc;
        _powerSvc = powerSvc;
        _modeSvc = modeSvc;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;

        _iconWork = LoadIcon("icon-work.ico");
        _iconAway = LoadIcon("icon-away.ico");
        _iconUnknown = LoadIcon("icon-unknown.ico");

        _msgWindow = new HiddenMessageWindow(_hotkeySvc);
        _msgWindow.CreateHandle();
        _hotkeySvc.SetHwnd(_msgWindow.Handle);

        _config = _configSvc.Load();

        // Match current system state (reads powercfg, may throw — non-fatal)
        try
        {
            var scheme = _powerSvc.GetActiveSchemeGuid();
            var (ac, dc) = _powerSvc.GetCurrentVideoIdle(scheme);
            var matched = _modeSvc.MatchCurrentMode(ac, dc);
            _modeSvc.SetCurrentMode(matched);
        }
        catch
        {
            // leave as Unknown
        }

        // Register global hotkey (non-fatal if it fails)
        if (!_hotkeySvc.Register(_config.Hotkey))
        {
            ShowBubble("Hotkey unavailable",
                       $"Hotkey {_config.Hotkey.Modifiers}+{_config.Hotkey.Key} could not be registered.",
                       ToolTipIcon.Warning);
        }
        _hotkeySvc.HotkeyPressed += OnHotkeyPressed;
        _modeSvc.ModeChanged += OnModeChanged;

        _notify = new NotifyIcon
        {
            Icon = IconFor(_modeSvc.CurrentMode),
            Visible = true,
            Text = TooltipFor(_modeSvc.CurrentMode)
        };
        _notify.DoubleClick += (_, _) => ToggleMode();

        BuildContextMenu();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var switchItem = new ToolStripMenuItem("Switch to Away", null, (_, _) => ToggleMode())
        {
            Name = "switchItem"
        };
        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        menu.Items.AddRange(new ToolStripItem[] { switchItem, settingsItem, new ToolStripSeparator(), exitItem });
        _notify.ContextMenuStrip = menu;
    }

    private void ToggleMode()
    {
        var target = _modeSvc.CurrentMode == AppMode.Work ? AppMode.Away : AppMode.Work;
        // If Unknown, default to Work on first toggle
        if (_modeSvc.CurrentMode == AppMode.Unknown) target = AppMode.Work;

        try
        {
            _modeSvc.SwitchTo(target);
            _config = _config with { CurrentMode = target };
            _configSvc.Save(_config);
        }
        catch (PowerConfigException ex)
        {
            ShowBubble("Switch failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnHotkeyPressed() => ToggleMode();

    private void OnModeChanged(object? sender, AppMode mode)
    {
        _notify.Icon = IconFor(mode);
        _notify.Text = TooltipFor(mode);
        UpdateSwitchMenuItem();
        ShowBubble("Mode changed", $"Switched to {mode} mode", ToolTipIcon.Info);
    }

    private void UpdateSwitchMenuItem()
    {
        if (_notify.ContextMenuStrip?.Items["switchItem"] is ToolStripMenuItem item)
        {
            item.Text = _modeSvc.CurrentMode == AppMode.Work ? "Switch to Away" : "Switch to Work";
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config, _hotkeySvc, _autoStartSvc);
        if (form.ShowDialog() == DialogResult.OK)
        {
            var newCfg = form.Result;
            var oldCfg = _config;
            _configSvc.Save(newCfg);
            _config = newCfg;

            if (!Equals(newCfg.Hotkey, oldCfg.Hotkey))
            {
                _hotkeySvc.Unregister();
                _hotkeySvc.Register(newCfg.Hotkey);
            }
            if (newCfg.AutoStart != oldCfg.AutoStart)
            {
                if (newCfg.AutoStart) _autoStartSvc.Enable();
                else _autoStartSvc.Disable();
            }
            // Re-apply timeouts if current mode's values changed
            _modeSvc.UpdateConfig(newCfg);
            _modeSvc.ReapplyCurrentMode();
            _notify.Text = TooltipFor(_modeSvc.CurrentMode);
        }
    }

    private void ExitApp()
    {
        _hotkeySvc.Unregister();
        _notify.Visible = false;
        _msgWindow.DestroyHandle();
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
        var acTxt = acMin == 0 ? "Never" : $"{acMin}min";
        var dcTxt = dcMin == 0 ? "Never" : $"{dcMin}min";
        return $"{mode} · AC {acTxt} / DC {dcTxt}";
    }

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
        var fullName = $"ScreenTimeoutToggle.assets.{name}";
        using var stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {fullName}");
        return new Icon(stream);
    }

    /// <summary>
    /// A message-only window (HWND_MESSAGE parent) that receives WM_HOTKEY
    /// and forwards to HotkeyService. Required because NotifyIcon alone
    /// doesn't pump window messages needed by RegisterHotKey.
    /// </summary>
    private class HiddenMessageWindow : NativeWindow
    {
        private readonly HotkeyService _hotkey;
        public HiddenMessageWindow(HotkeyService hotkey) { _hotkey = hotkey; }

        public void CreateHandle()
        {
            var cp = new CreateParams
            {
                Caption = "ScreenTimeoutToggleMsg",
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
