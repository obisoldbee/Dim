using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;

namespace ScreenTimeoutToggle.UI;

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
    private Button _okBtn;
    private Button _cancelBtn;

    private HotkeyConfig _capturedHotkey;

    public AppConfig Result { get; private set; }

    public SettingsForm(AppConfig initial, HotkeyService hotkeySvc, AutoStartService autoStartSvc)
    {
        _initial = initial;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;
        _capturedHotkey = initial.Hotkey;

        Text = "Screen Timeout Toggle — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 310);

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var lblWork = new Label { Text = "Work mode (minutes, 0 = never)", Left = 16, Top = 16, Width = 300 };
        _workAc = new NumericUpDown { Left = 24, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        _workDc = new NumericUpDown { Left = 120, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblWorkAc = new Label { Text = "Plugged", Left = 24, Top = 64, Width = 80 };
        var lblWorkDc = new Label { Text = "Battery", Left = 120, Top = 64, Width = 80 };

        var lblAway = new Label { Text = "Away mode (minutes, 0 = never)", Left = 16, Top = 98, Width = 300 };
        _awayAc = new NumericUpDown { Left = 24, Top = 122, Width = 80, Minimum = 0, Maximum = 99999 };
        _awayDc = new NumericUpDown { Left = 120, Top = 122, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblAwayAc = new Label { Text = "Plugged", Left = 24, Top = 146, Width = 80 };
        var lblAwayDc = new Label { Text = "Battery", Left = 120, Top = 146, Width = 80 };

        var lblHotkey = new Label { Text = "Hotkey (click box, then press keys)", Left = 16, Top = 180, Width = 300 };
        _hotkeyBox = new TextBox { Left = 24, Top = 204, Width = 176, ReadOnly = true };
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;

        _autoStartCheck = new CheckBox { Text = "Start with Windows", Left = 24, Top = 236, Width = 200 };

        _okBtn = new Button { Text = "OK", Left = 160, Top = 270, Width = 80, DialogResult = DialogResult.OK };
        _cancelBtn = new Button { Text = "Cancel", Left = 248, Top = 270, Width = 80, DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] {
            lblWork, _workAc, _workDc, lblWorkAc, lblWorkDc,
            lblAway, _awayAc, _awayDc, lblAwayAc, lblAwayDc,
            lblHotkey, _hotkeyBox,
            _autoStartCheck,
            _okBtn, _cancelBtn
        });

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;
        _okBtn.Click += OnOkClick;
    }

    private void LoadValues()
    {
        _workAc.Value = _initial.Work.AcMinutes;
        _workDc.Value = _initial.Work.DcMinutes;
        _awayAc.Value = _initial.Away.AcMinutes;
        _awayDc.Value = _initial.Away.DcMinutes;
        _hotkeyBox.Text = $"{_initial.Hotkey.Modifiers}+{_initial.Hotkey.Key}";
        _autoStartCheck.Checked = _autoStartSvc.IsEnabled();
    }

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

        _capturedHotkey = new HotkeyConfig
        {
            Modifiers = string.Join("+", mods),
            Key = key.ToString()
        };
        _hotkeyBox.Text = string.IsNullOrEmpty(_capturedHotkey.Modifiers)
            ? _capturedHotkey.Key
            : $"{_capturedHotkey.Modifiers}+{_capturedHotkey.Key}";
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        Result = _initial with
        {
            Work = new TimeoutConfig { AcMinutes = (int)_workAc.Value, DcMinutes = (int)_workDc.Value },
            Away = new TimeoutConfig { AcMinutes = (int)_awayAc.Value, DcMinutes = (int)_awayDc.Value },
            Hotkey = _capturedHotkey,
            AutoStart = _autoStartCheck.Checked
        };
        DialogResult = DialogResult.OK;
    }
}
