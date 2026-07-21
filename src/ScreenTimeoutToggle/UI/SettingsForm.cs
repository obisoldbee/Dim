#pragma warning disable CS8618 // WinForms controls initialized in BuildUi, not constructor

using OBDim.Models;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// Settings dialog: 4 timeout values + hotkey capture + autostart toggle + language selector.
/// </summary>
public class SettingsForm : Form
{
    private readonly AppConfig _initial;
    private readonly HotkeyService _hotkeySvc;
    private readonly AutoStartService _autoStartSvc;

    private NumericUpDown _workAc;
    private NumericUpDown _workDc;
    private NumericUpDown _awayAc;
    private NumericUpDown _awayDc;
    private TextBox _hotkeyBox;
    private CheckBox _autoStartCheck;
    private ComboBox _languageBox;
    private Button _okBtn;
    private Button _cancelBtn;
    private readonly ToolTip _toolTip;

    private HotkeyConfig _capturedHotkey;

    public AppConfig Result { get; private set; }

    public SettingsForm(AppConfig initial, HotkeyService hotkeySvc, AutoStartService autoStartSvc)
    {
        _initial = initial;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;
        _capturedHotkey = initial.Hotkey;
        _toolTip = new ToolTip();

        Text = LocalizationService.Get("settings.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 360);

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var lblWork = new Label { Text = LocalizationService.Get("settings.work_mode"), Left = 16, Top = 16, Width = 300 };
        _workAc = new NumericUpDown { Left = 24, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        _workDc = new NumericUpDown { Left = 120, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblWorkAc = new Label { Text = LocalizationService.Get("settings.plugged"), Left = 24, Top = 64, Width = 80 };
        var lblWorkDc = new Label { Text = LocalizationService.Get("settings.battery"), Left = 120, Top = 64, Width = 80 };

        var lblAway = new Label { Text = LocalizationService.Get("settings.away_mode"), Left = 16, Top = 98, Width = 300 };
        _awayAc = new NumericUpDown { Left = 24, Top = 122, Width = 80, Minimum = 0, Maximum = 99999 };
        _awayDc = new NumericUpDown { Left = 120, Top = 122, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblAwayAc = new Label { Text = LocalizationService.Get("settings.plugged"), Left = 24, Top = 146, Width = 80 };
        var lblAwayDc = new Label { Text = LocalizationService.Get("settings.battery"), Left = 120, Top = 146, Width = 80 };

        var lblHotkey = new Label { Text = LocalizationService.Get("settings.hotkey_label"), Left = 16, Top = 180, Width = 300 };
        _hotkeyBox = new TextBox { Left = 24, Top = 204, Width = 176, ReadOnly = true };
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;

        // D3: Explicitly note Win key is not supported in capture
        var lblWinNote = new Label { Text = LocalizationService.Get("settings.win_note"), Left = 208, Top = 207, Width = 120,
            ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 7f) };

        _autoStartCheck = new CheckBox { Text = LocalizationService.Get("settings.autostart"), Left = 24, Top = 236, Width = 200 };

        // Language selector
        var lblLanguage = new Label { Text = LocalizationService.Get("settings.language"), Left = 24, Top = 266, Width = 80 };
        _languageBox = new ComboBox
        {
            Left = 120,
            Top = 263,
            Width = 120,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _languageBox.Items.AddRange(new object[] { "中文", "English" });

        _okBtn = new Button { Text = LocalizationService.Get("settings.ok"), Left = 160, Top = 320, Width = 80, DialogResult = DialogResult.OK };
        _cancelBtn = new Button { Text = LocalizationService.Get("settings.cancel"), Left = 248, Top = 320, Width = 80, DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] {
            lblWork, _workAc, _workDc, lblWorkAc, lblWorkDc,
            lblAway, _awayAc, _awayDc, lblAwayAc, lblAwayDc,
            lblHotkey, _hotkeyBox, lblWinNote,
            _autoStartCheck,
            lblLanguage, _languageBox,
            _okBtn, _cancelBtn
        });

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;
        _okBtn.Click += OnOkClick;

        // D2: Tooltip for large values
        _workAc.ValueChanged += (_, _) => CheckLargeValue(_workAc);
        _workDc.ValueChanged += (_, _) => CheckLargeValue(_workDc);
        _awayAc.ValueChanged += (_, _) => CheckLargeValue(_awayAc);
        _awayDc.ValueChanged += (_, _) => CheckLargeValue(_awayDc);
    }

    /// <summary>
    /// Shows a tooltip warning when a timeout value exceeds 180 minutes (3 hours).
    /// </summary>
    private void CheckLargeValue(NumericUpDown nud)
    {
        if (nud.Value > 180)
        {
            _toolTip.SetToolTip(nud, LocalizationService.Get("settings.large_value_warning"));
        }
        else
        {
            _toolTip.SetToolTip(nud, null);
        }
    }

    private void LoadValues()
    {
        _workAc.Value = _initial.Work.AcMinutes;
        _workDc.Value = _initial.Work.DcMinutes;
        _awayAc.Value = _initial.Away.AcMinutes;
        _awayDc.Value = _initial.Away.DcMinutes;
        _hotkeyBox.Text = $"{_initial.Hotkey.Modifiers}+{_initial.Hotkey.Key}";
        _autoStartCheck.Checked = _autoStartSvc.IsEnabled();
        _languageBox.SelectedIndex = _initial.Language == "en-US" ? 1 : 0;
    }

    /// <summary>
    /// Captures the pressed key combination.
    /// v1.0.2: Relaxed validation — function keys (F1-F24, PrintScreen, Pause, etc.)
    /// are allowed without modifiers. Letter/digit keys without modifiers show a
    /// confirmation warning but are still permitted. The key must be a valid VK.
    /// </summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var mods = new List<string>();
        if (e.Control) mods.Add("Ctrl");
        if (e.Alt) mods.Add("Alt");
        if (e.Shift) mods.Add("Shift");

        var key = e.KeyCode;
        // Ignore pure modifier presses
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return;

        var keyStr = key.ToString();

        // Validate the key is a recognizable virtual key
        if (HotkeyService.KeyStringToVk(keyStr) == 0)
        {
            MessageBox.Show(
                LocalizationService.Get("hotkey.invalid_key", keyStr),
                LocalizationService.Get("hotkey.invalid_title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // v1.0.2: No modifier required for function keys; warn for others
        if (mods.Count == 0 && !IsFunctionKey(keyStr))
        {
            var confirm = MessageBox.Show(
                LocalizationService.Get("hotkey.single_key_warning"),
                LocalizationService.Get("hotkey.single_key_warning_title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        _capturedHotkey = new HotkeyConfig
        {
            Modifiers = string.Join("+", mods),
            Key = keyStr
        };
        _hotkeyBox.Text = $"{_capturedHotkey.Modifiers}+{_capturedHotkey.Key}";
    }

    /// <summary>
    /// Determines whether a key string represents a function key that is safe to use
    /// as a single-key hotkey (no modifier needed). Includes F1-F24, PrintScreen,
    /// Pause, ScrollLock, NumLock, and CapsLock.
    /// </summary>
    private static bool IsFunctionKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim().ToUpperInvariant();

        // F1-F24
        if (key.StartsWith('F') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return true;

        // Named function keys (match both Keys enum names and common aliases)
        return key switch
        {
            "PRINTSCREEN" or "SNAPSHOT" => true,
            "PAUSE" => true,
            "SCROLL" or "SCROLLLOCK" => true,
            "CAPITAL" or "CAPSLOCK" => true,
            "NUMLOCK" => true,
            _ => false
        };
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        var selectedLanguage = _languageBox.SelectedIndex == 1 ? "en-US" : "zh-CN";
        Result = _initial with
        {
            Work = new TimeoutConfig { AcMinutes = (int)_workAc.Value, DcMinutes = (int)_workDc.Value },
            Away = new TimeoutConfig { AcMinutes = (int)_awayAc.Value, DcMinutes = (int)_awayDc.Value },
            Hotkey = _capturedHotkey,
            AutoStart = _autoStartCheck.Checked,
            Language = selectedLanguage
        };
        DialogResult = DialogResult.OK;
    }
}

#pragma warning restore CS8618
