using OBDim.Monitoring.Models;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

[Collection("NativeUi")]
public class MonitorFormSettingsLayoutTests
{
    [Fact]
    public void SettingsWindow_HasAcDcLabelsAndPersistentClickableSaveAcrossCategories()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
            using var form = new MonitoringSettingsForm(coordinator);
            form.Show(); Application.DoEvents();
            var controls = Round2Ui.Descendants(form).ToList();
            Assert.Contains(controls, c => c is Label && c.Visible && c.Text == "接通电源（分钟）");
            Assert.Contains(controls, c => c is Label && c.Visible && c.Text == "使用电池（分钟）");
            Assert.Equal(4, controls.Count(c => c is NumericUpDown && c.Visible));
            var save = controls.OfType<Button>().Single(c => c.Name == "save");
            for (var page = 0; page < 5; page++)
            {
                form.SelectCategory(page); Application.DoEvents();
                Assert.True(save.Visible && save.Enabled);
                var bounds = form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
                Assert.True(form.ClientRectangle.Contains(bounds), $"save clipped on category {page}: {bounds}");
                Assert.True(save.Width >= TextRenderer.MeasureText(save.Text, save.Font).Width + 8 * form.DeviceDpi / 96);
            }
            form.SelectCategory(1);
            Assert.Equal(3, controls.Count(c => c is CheckBox && c.Name.StartsWith("enable_")));
            Assert.Equal(3, controls.Count(c => c is TextBox && c.Name.StartsWith("cliPath_")));
            foreach (var expand in controls.OfType<Button>().Where(c => c.Text == "CLI 路径…")) expand.PerformClick();
            Application.DoEvents();
            Assert.All(controls.OfType<TextBox>().Where(c => c.Name.StartsWith("cliPath_")), c => Assert.True(c.Visible));
            Assert.Contains(controls, c => c is Label && c.Text.Contains("CLI 程序路径（可选）"));
        });
    }
}
