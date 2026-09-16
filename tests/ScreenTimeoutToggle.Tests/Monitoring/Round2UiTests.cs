using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using OBDim.Models;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Services;
using OBDim.Services;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests.Monitoring;

[CollectionDefinition("NativeUi", DisableParallelization = true)]
public sealed class NativeUiCollection { }

internal static class Round2Ui
{
    public static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "native UI test timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    public static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(c => new[] { c }.Concat(Descendants(c)));
    public static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "UI did not settle");
            Application.DoEvents(); Thread.Sleep(2);
        }
        Application.DoEvents();
    }
    public static void Invoke(object target, string method) => target.GetType().GetMethod(method,
        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, method == "OnDeactivate" ? [EventArgs.Empty] : null);
    public static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
}

[Collection("NativeUi")]
public class Round2UiTests
{
    [Fact]
    public void QuotaNumericAndStaleChanges_KeepRowsAndHandles_AndRepaintSameColorBar()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            var next = env.Good();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(next)));
            coordinator.RequestManualRefresh(ProviderId.Codex);
            Round2Ui.PumpUntil(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
            using var form = new MonitorForm(coordinator);
            form.Show(); Application.DoEvents();
            var rows = Round2Ui.Field<Dictionary<ProviderId, List<Control>>>(form, "_cardRowControls")[ProviderId.Codex];
            var row = Assert.Single(rows); var handle = row.Handle;
            var bar = Assert.Single(Round2Ui.Descendants(row).OfType<MonitorForm.QuotaBar>());
            var invalidates = 0; bar.Invalidated += (_, _) => invalidates++;
            var created = form.RowsCreated;
            env.Clock.Advance(TimeSpan.FromSeconds(16)); next = env.Good(remaining: 64);
            coordinator.RequestManualRefresh(ProviderId.Codex);
            Round2Ui.PumpUntil(() => !coordinator.GetDisplayState(ProviderId.Codex).Refreshing);
            Assert.Same(row, Assert.Single(rows)); Assert.Equal(handle, row.Handle);
            Assert.Equal(created, form.RowsCreated); Assert.Equal(0, form.RowsDisposed);
            Assert.Equal(.64, bar.Fraction, 5); Assert.True(invalidates > 0);
            env.Clock.Advance(TimeSpan.FromMinutes(16)); coordinator.RaiseQuotaStateChanged(); Application.DoEvents();
            Assert.Equal(created, form.RowsCreated);
            Assert.Equal(Color.FromArgb(0x85, 0x86, 0x8B), bar.FillColor);
            var beforeWidth = row.Width; form.Width += 100;
            Round2Ui.Invoke(form, "UpdateQuotaView");
            Assert.True(row.Width > beforeWidth); Assert.Equal(handle, row.Handle);
        });
    }

    [Fact]
    public void FractionChangeAlone_InvalidatesProgressBar()
    {
        Round2Ui.Sta(() =>
        {
            using var bar = new MonitorForm.QuotaBar { Size = new Size(120, 7), Fraction = .65 };
            _ = bar.Handle;
            var invalidates = 0; bar.Invalidated += (_, _) => invalidates++;
            bar.Fraction = .64;
            Assert.True(invalidates > 0, "a same-color value change must request repaint");
        });
    }

    [Fact]
    public void HiddenQuota_IgnoresTwelveMemorySamples_AndDismissalReusesWindow()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
            using var form = new MonitorForm(coordinator);
            form.Show(); form.SetView(MonitorForm.View.Memory); Application.DoEvents();
            var count = form.QuotaRefreshCount;
            for (var i = 0; i < 12; i++) { Round2Ui.Invoke(coordinator, "SampleMemory"); Application.DoEvents(); }
            Assert.Equal(count, form.QuotaRefreshCount);
            var handle = form.Handle;
            Round2Ui.Invoke(form, "OnDeactivate");
            Assert.False(form.Visible); Assert.False(form.IsDisposed);
            Assert.True(TrayApp.ShouldSuppressReopenAfterDeactivate(form.LastDeactivateClosedAtUtc, DateTimeOffset.UtcNow));
            form.Show(); Assert.Equal(handle, form.Handle);
            Assert.Equal(0, form.RowsDisposed);
        });
    }

    [Fact]
    public void SettingsNavigationAndDeactivation_PreserveDraft_CloseCanBeCancelled()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
            using var settings = new MonitoringSettingsForm(coordinator);
            settings.Show(); Application.DoEvents();
            var work = Round2Ui.Descendants(settings).OfType<NumericUpDown>().Single(c => c.Name == "workAc");
            work.Value = 42; Assert.True(settings.HasUnsavedChanges);
            settings.SelectCategory(1); settings.SelectCategory(3); settings.SelectCategory(0);
            Round2Ui.Invoke(settings, "OnDeactivate");
            coordinator.RaiseQuotaStateChanged(); Application.DoEvents();
            Assert.True(settings.Visible); Assert.Equal(42, work.Value);
            settings.ConfirmDiscard = () => false; settings.Close();
            Assert.False(settings.IsDisposed); Assert.Equal(42, work.Value);
            settings.ConfirmDiscard = () => true; settings.Close(); Assert.True(settings.IsDisposed);
        });
    }

    [Fact]
    public void ZeroTimeout_ShowsNever_AndStillAcceptsNumericEdits()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
            using var form = new MonitoringSettingsForm(coordinator);
            form.Show(); Application.DoEvents();
            var work = Round2Ui.Descendants(form).OfType<NumericUpDown>().Single(c => c.Name == "workAc");
            Assert.Equal(0, work.Value); Assert.Equal("从不", work.Text);
            work.Text = "12345"; Assert.Equal(12345, work.Value);
            work.Text = "从不"; Assert.Equal(0, work.Value);
            form.Size = new Size(680, 520);
            var save = Round2Ui.Descendants(form).OfType<Button>().Single(c => c.Name == "save");
            for (var i = 0; i < 5; i++)
            {
                form.SelectCategory(i); Application.DoEvents();
                Assert.True(form.ClientRectangle.Contains(form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle))));
            }
        });
    }

    [Fact]
    public void Save_BindsCadenceAndPartialFailure_KeepsDraftAndReportsSpecificFailure()
    {
        Round2Ui.Sta(() =>
        {
            using var env = new Round2Environment();
            using var coordinator = env.Create(new Round2Adapter((_, _) => Task.FromResult(env.Good())));
            MonitoringSettings? submitted = null;
            using var settings = new MonitoringSettingsForm(coordinator, () => AppConfig.CreateDefault(), (_, monitoring) =>
            { submitted = monitoring; return Task.FromResult(new SettingsApplyResult(true, false, PowerApplied: false)); });
            settings.Show(); settings.SelectCategory(1);
            var cadence = Round2Ui.Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "interval_Codex");
            cadence.SelectedIndex = 4; // explicit 30 minutes, after inherit/1/5/15
            settings.SaveDraftAsync().GetAwaiter().GetResult();
            Assert.Equal(30, submitted?.Provider(ProviderId.Codex).RefreshIntervalMinutes);
            Assert.True(settings.HasUnsavedChanges);
            var status = Round2Ui.Field<Label>(settings, "_status").Text;
            Assert.Contains("监控配置保存失败", status); Assert.Contains("系统息屏时间未生效", status);
        });
    }
}
