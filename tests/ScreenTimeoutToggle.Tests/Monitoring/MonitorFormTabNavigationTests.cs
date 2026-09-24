using System.Reflection;
using System.Runtime.InteropServices;
using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using OBDim.Monitoring.Services;
using OBDim.UI;
using OBDim.UI.Network;
using Xunit;

namespace OBDim.Tests.Monitoring;

[Collection("NativeUi")]
public class MonitorFormTabNavigationTests
{
    [Fact]
    public void QueuedTabCyclesAllThreePagesRepeatedlyWithoutResettingFocus() => Sta(form =>
    {
        var clicks = 0;
        foreach (var tab in Tabs(form)) tab.Click += (_, _) => clicks++;
        var actions = 0;
        Field<Button>(form, "_refreshButton").Click += (_, _) => actions++;
        form.SettingsRequested += () => actions++;
        Assert.True(Tabs(form)[0].Focused); // actual initial focus, not a test-only Focus call
        for (var i = 0; i < 12; i++)
        {
            var previous = form.CurrentView;
            Tab(Tabs(form)[(int)previous]);
            var expected = (MonitorForm.View)((i + 1) % 3);
            Assert.Equal(expected, form.CurrentView);
            Assert.True(Tabs(form)[(int)expected].Focused);
            Assert.True(Field<Control>(form, new[] { "_quotaView", "_memoryView", "_networkView" }[(int)expected]).Visible);
        }
        Assert.Equal(0, clicks);
        Assert.Equal(0, actions);
    });

    [Fact]
    public void ShiftTabCyclesBackwardsIncludingQuotaToNetwork() => Sta(form =>
    {
        for (var i = 0; i < 12; i++)
        {
            ModifiedTab(Tabs(form)[(int)form.CurrentView], Keys.Shift);
            var expected = (MonitorForm.View)((2 * (i + 1)) % 3);
            Assert.Equal(expected, form.CurrentView);
            Assert.True(Tabs(form)[(int)expected].Focused);
        }
    });

    [Fact]
    public void F6LeavesPageCycleForControlsAndReturnsWithoutActivatingActions() => Sta(form =>
    {
        Tab(Tabs(form)[0]); Tab(Tabs(form)[1]);
        var actions = 0;
        var refresh = Field<Button>(form, "_refreshButton");
        var settings = Field<Button>(form, "_settingsButton");
        refresh.Click += (_, _) => actions++;
        form.SettingsRequested += () => actions++;
        Key(Tabs(form)[2], Keys.F6);
        if (refresh.Enabled) { Assert.True(refresh.Focused); Tab(refresh); }
        Assert.True(settings.Focused);
        Tab(settings);
        var page = Field<NetworkPage>(form, "_networkView");
        Assert.True(page.ContainsFocus);
        var range = page.Chart.RangeButtons[0];
        Assert.True(range.Focus());
        Tab(range);
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        var focused = Descendants(form).Single(c => c.Focused);
        Key(focused, Keys.F6);
        Assert.True(Tabs(form)[2].Focused);
        Tab(Tabs(form)[2]);
        Assert.Equal(MonitorForm.View.Quota, form.CurrentView);
        Assert.Equal(0, actions);
    });

    [Fact]
    public void FocusedInterfaceInputKeepsNormalTabTraversal() => Sta(form =>
    {
        form.SetView(MonitorForm.View.Network);
        var page = Field<NetworkPage>(form, "_networkView");
        var combo = Descendants(page).OfType<ComboBox>().Single();
        Assert.True(combo.Focus());
        Tab(combo);
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        Assert.True(page.ContainsFocus);
    });

    [Fact]
    public void ProgrammaticFocusDoesNotSelectUnrelatedPages() => Sta(form =>
    {
        form.SetView(MonitorForm.View.Network);
        Assert.True(Tabs(form)[0].Focus());
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        var range = Field<NetworkPage>(form, "_networkView").Chart.RangeButtons[0];
        Assert.True(range.Focus());
        form.SetView(MonitorForm.View.Memory);
        Assert.Equal(MonitorForm.View.Memory, form.CurrentView);
        Assert.True(Field<Panel>(form, "_memoryView").Visible);
    });

    [Fact]
    public void ReopeningFromBodyFocusReturnsToSelectedPageCycle() => Sta(form =>
    {
        form.SetView(MonitorForm.View.Network);
        Assert.True(Field<NetworkPage>(form, "_networkView").Chart.RangeButtons[1].Focus());
        form.Hide();
        using var tray = new NotifyIcon();
        form.ShowAnchoredToTray(tray);
        Assert.True(Tabs(form)[2].Focused);
        Tab(Tabs(form)[2]);
        Assert.Equal(MonitorForm.View.Quota, form.CurrentView);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtrlTabCyclesAndAlignsFocusWithSelection(bool backwards) => Sta(form =>
    {
        for (var i = 0; i < 9; i++)
        {
            ModifiedTab(Tabs(form)[(int)form.CurrentView], Keys.Control | (backwards ? Keys.Shift : Keys.None));
            var expected = (MonitorForm.View)(((backwards ? 2 : 1) * (i + 1)) % 3);
            Assert.Equal(expected, form.CurrentView);
            Assert.True(Tabs(form)[(int)expected].Focused);
        }
    });

    private static Button[] Tabs(MonitorForm form) => [Field<Button>(form, "_quotaSegment"), Field<Button>(form, "_memorySegment"), Field<Button>(form, "_networkSegment")];
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    private static void Tab(Control focused) => Key(focused, Keys.Tab);

    private static void Key(Control focused, Keys key)
    {
        Assert.True(focused.Focused);
        Assert.True(PostMessage(focused.Handle, 0x100, (IntPtr)key, IntPtr.Zero));
        Assert.True(PostMessage(focused.Handle, 0x101, (IntPtr)key, IntPtr.Zero));
        Application.DoEvents();
    }

    private static void ModifiedTab(Control focused, Keys modifiers)
    {
        Assert.True(focused.Focused);
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            // Thread-local state exercises the real control -> parent -> form route
            // without sending keystrokes to any other application on the desktop.
            var state = new byte[256];
            if (modifiers.HasFlag(Keys.Shift)) state[(int)Keys.ShiftKey] = 0x80;
            if (modifiers.HasFlag(Keys.Control)) state[(int)Keys.ControlKey] = 0x80;
            Assert.True(SetKeyboardState(state));
            var message = Message.Create(focused.Handle, 0x100, (IntPtr)Keys.Tab, IntPtr.Zero);
            Assert.True(focused.PreProcessMessage(ref message));
        }
        finally { Assert.True(SetKeyboardState(original)); }
        Application.DoEvents();
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;

    private sealed class Reader : IMemoryReader
    {
        public MemorySample? Read(out string? error) { error = null; return null; }
        public void Dispose() { }
    }

    private static void Sta(Action<MonitorForm> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "obdim-tab-nav-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var coordinator = new MonitoringCoordinator(SystemClock.Instance, new Reader(),
                    new Dictionary<ProviderId, IProviderAdapter>(),
                    new MonitoringSettingsService(Path.Combine(dir, "settings.json")),
                    new MonitoringCacheService(Path.Combine(dir, "cache")));
                using var form = new MonitorForm(coordinator);
                form.Shown += (_, _) => form.BeginInvoke(() =>
                {
                    try { action(form); }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                });
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally { Directory.Delete(dir, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA timeout");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
