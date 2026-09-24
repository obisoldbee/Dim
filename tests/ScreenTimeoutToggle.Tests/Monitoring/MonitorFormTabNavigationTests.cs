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
    public void QueuedTabSelectsHeaderPagesWithoutClickAndCanLeaveHeader() => Sta(form =>
    {
        var quota = Field<Button>(form, "_quotaSegment");
        var memory = Field<Button>(form, "_memorySegment");
        var network = Field<Button>(form, "_networkSegment");
        var clicks = 0;
        foreach (var tab in new[] { quota, memory, network }) tab.Click += (_, _) => clicks++;
        Assert.True(quota.Focus());
        Tab(quota);
        Assert.True(memory.Focused);
        Assert.Equal(MonitorForm.View.Memory, form.CurrentView);
        Assert.True(Field<Panel>(form, "_memoryView").Visible);
        Tab(memory);
        Assert.True(network.Focused);
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        Assert.True(Field<NetworkPage>(form, "_networkView").Visible);
        Assert.Equal(0, clicks); // selection must not require Enter, Space or a synthetic click

        var refresh = Field<Button>(form, "_refreshButton");
        var settings = Field<Button>(form, "_settingsButton");
        var actions = 0;
        refresh.Click += (_, _) => actions++;
        form.SettingsRequested += () => actions++;
        Tab(network);
        if (refresh.Enabled)
        {
            Assert.True(refresh.Focused);
            Tab(refresh);
        }
        Assert.True(settings.Focused);
        Tab(settings);
        Assert.True(Field<NetworkPage>(form, "_networkView").ContainsFocus);
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        Assert.Equal(0, actions);
    });

    [Fact]
    public void ShiftTabSelectsPreviousHeaderPageThroughControlPreprocessing() => Sta(form =>
    {
        var quota = Field<Button>(form, "_quotaSegment");
        var memory = Field<Button>(form, "_memorySegment");
        var network = Field<Button>(form, "_networkSegment");
        form.SetView(MonitorForm.View.Network);
        Assert.True(network.Focus());
        ModifiedTab(network, Keys.ShiftKey);
        Assert.True(memory.Focused);
        Assert.Equal(MonitorForm.View.Memory, form.CurrentView);
        ModifiedTab(memory, Keys.ShiftKey);
        Assert.True(quota.Focused);
        Assert.Equal(MonitorForm.View.Quota, form.CurrentView);
    });

    [Fact]
    public void BodyTabAndProgrammaticFocusDoNotSelectUnrelatedPages() => Sta(form =>
    {
        form.SetView(MonitorForm.View.Network);
        var page = Field<NetworkPage>(form, "_networkView");
        var range = page.Chart.RangeButtons[0];
        Assert.True(range.Focus());
        Tab(range);
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        Assert.True(page.ContainsFocus);

        // Focus can also be restored by WinForms when a page is hidden or reopened.
        Assert.True(Field<Button>(form, "_quotaSegment").Focus());
        Assert.Equal(MonitorForm.View.Network, form.CurrentView);
        Assert.True(range.Focus());
        form.SetView(MonitorForm.View.Memory);
        Assert.Equal(MonitorForm.View.Memory, form.CurrentView);
        Assert.True(Field<Panel>(form, "_memoryView").Visible);
    });

    [Fact]
    public void CtrlTabStillCyclesPagesImmediately() => Sta(form =>
    {
        var quota = Field<Button>(form, "_quotaSegment");
        Assert.True(quota.Focus());
        foreach (var expected in new[] { MonitorForm.View.Memory, MonitorForm.View.Network, MonitorForm.View.Quota })
        {
            ModifiedTab(quota, Keys.ControlKey);
            Assert.Equal(expected, form.CurrentView);
        }
    });

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    private static void Tab(Control focused)
    {
        Assert.True(focused.Focused);
        Assert.True(PostMessage(focused.Handle, 0x100, (IntPtr)Keys.Tab, IntPtr.Zero));
        Assert.True(PostMessage(focused.Handle, 0x101, (IntPtr)Keys.Tab, IntPtr.Zero));
        Application.DoEvents();
    }

    private static void ModifiedTab(Control focused, Keys modifier)
    {
        Assert.True(focused.Focused);
        var original = new byte[256];
        Assert.True(GetKeyboardState(original));
        try
        {
            // Thread-local state exercises the real control -> parent -> form route
            // without sending keystrokes to any other application on the desktop.
            var state = new byte[256]; state[(int)modifier] = 0x80;
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
