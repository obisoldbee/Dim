using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;

namespace OBDim.UI;

/// <summary>
/// Independent monitoring settings dialog (spec §8(2)): writes monitoring.json only — it
/// never touches the original config.json. Shows per-provider enable switches and optional
/// CLI path overrides, plus the memory-monitoring and reminder switches.
/// </summary>
public sealed class MonitoringSettingsForm : Form
{
    private readonly MonitoringCoordinator _coordinator;
    private readonly Dictionary<ProviderId, CheckBox> _enableChecks = [];
    private readonly Dictionary<ProviderId, TextBox> _pathBoxes = [];
    private readonly CheckBox _memoryCheck = new();
    private readonly CheckBox _remindersCheck = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();

    public MonitoringSettingsForm(MonitoringCoordinator coordinator)
    {
        _coordinator = coordinator;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 330);
        KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();

        Text = LocalizationService.Get("monitor.settings.title");
        _okButton.Text = LocalizationService.Get("settings.ok");
        _cancelButton.Text = LocalizationService.Get("settings.cancel");
        _memoryCheck.Text = LocalizationService.Get("monitor.settings.memory");
        _remindersCheck.Text = LocalizationService.Get("monitor.settings.reminders");

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        LoadFromCoordinator();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            AutoScroll = true,
        };

        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var enable = new CheckBox
            {
                Name = $"enable_{id}",
                AutoSize = true,
                Text = LocalizationService.Get($"monitor.provider.{id.ToString().ToLowerInvariant()}"),
            };
            var pathLabel = new Label
            {
                AutoSize = true,
                Text = LocalizationService.Get("monitor.settings.cli_path"),
                Margin = new Padding(24, 3, 3, 0),
            };
            var path = new TextBox
            {
                Name = $"cliPath_{id}",
                Dock = DockStyle.Fill,
                Margin = new Padding(24, 0, 3, 6),
            };
            _enableChecks[id] = enable;
            _pathBoxes[id] = path;
            layout.Controls.Add(enable);
            layout.Controls.Add(pathLabel);
            layout.Controls.Add(path);
        }

        _memoryCheck.Name = "memoryEnabledCheck";
        _memoryCheck.AutoSize = true;
        _remindersCheck.Name = "remindersCheck";
        _remindersCheck.AutoSize = true;
        layout.Controls.Add(_memoryCheck);
        layout.Controls.Add(_remindersCheck);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8),
        };
        _okButton.Name = "okButton";
        _okButton.DialogResult = DialogResult.None; // applied manually — save can fail
        _okButton.Click += (_, _) => ApplyAndClose();
        _cancelButton.Name = "cancelButton";
        _cancelButton.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(_okButton);
        buttons.Controls.Add(_cancelButton);

        Controls.Add(layout);
        Controls.Add(buttons);
    }

    private void LoadFromCoordinator()
    {
        var settings = _coordinator.Settings;
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            var provider = settings.Provider(id);
            _enableChecks[id].Checked = provider.Enabled;
            _pathBoxes[id].Text = provider.CliPath ?? "";
        }
        _memoryCheck.Checked = settings.MemoryEnabled;
        _remindersCheck.Checked = settings.RemindersEnabled;
    }

    private void ApplyAndClose()
    {
        var settings = _coordinator.Settings with
        {
            MemoryEnabled = _memoryCheck.Checked,
            RemindersEnabled = _remindersCheck.Checked,
            Providers = Enum.GetValues<ProviderId>().Select(id => new ProviderSettings
            {
                Id = id,
                Enabled = _enableChecks[id].Checked,
                CliPath = string.IsNullOrWhiteSpace(_pathBoxes[id].Text) ? null : _pathBoxes[id].Text.Trim(),
            }).ToList(),
        };

        // The MonitoringSettingsService owns persistence; the coordinator owns runtime
        // behavior. Apply first so the panel updates even if the save fails (a failed save
        // is reported, never faked — spec §8(6)).
        _coordinator.ApplySettings(settings);
        var saved = _coordinator.SaveSettings(settings);
        if (!saved)
        {
            MessageBox.Show(
                this,
                LocalizationService.Get("monitor.settings.save_failed"),
                LocalizationService.Get("bubble.save_failed_title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
