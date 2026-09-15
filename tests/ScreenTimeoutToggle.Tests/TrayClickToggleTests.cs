using System.Windows.Forms;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// Guard for the tray-icon click bug: the tray used to subscribe to
/// <c>NotifyIcon.Click</c>, which fires for ANY mouse button, so right-clicking the
/// icon to open the context menu also toggled Work/Away. The fix subscribes to
/// <c>MouseClick</c> and consults <see cref="TrayApp.ShouldToggleOnClick"/> — these
/// tests pin down that only the left button toggles.
/// </summary>
/// <remarks>
/// The method under test is pure (no NotifyIcon, no message loop), so unlike
/// <c>UiMarshallingTests</c> these need no STA thread. Per AGENTS.md, this guard was
/// verified to actually go red: with <c>ShouldToggleOnClick</c> temporarily returning
/// <c>true</c> unconditionally, the Right/Middle cases fail.
/// </remarks>
public class TrayClickToggleTests
{
    [Fact]
    public void ShouldToggleOnClick_LeftButton_ReturnsTrue()
    {
        Assert.True(TrayApp.ShouldToggleOnClick(MouseButtons.Left),
            "a left click on the tray icon is the documented way to toggle Work/Away (spec §2.1(1))");
    }

    [Theory]
    [InlineData(MouseButtons.Right)]
    [InlineData(MouseButtons.Middle)]
    [InlineData(MouseButtons.XButton1)]
    [InlineData(MouseButtons.XButton2)]
    [InlineData(MouseButtons.None)]
    public void ShouldToggleOnClick_NonLeftButton_ReturnsFalse(MouseButtons button)
    {
        Assert.False(TrayApp.ShouldToggleOnClick(button),
            $"{button} must NOT toggle — right-click only opens the context menu; " +
            "NotifyIcon.Click fires for every button, which is exactly the bug being guarded against");
    }

    // ---- v1.1.2：托盘左键开关弹窗与 OnDeactivate→Close 的时序竞态守卫 ----
    // 面板打开时点托盘：失活先关掉面板，MouseClick 随后到达，若不抑制就会"点托盘关不掉"。

    private static readonly DateTimeOffset Base = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SuppressReopen_NullTimestamp_ReturnsFalse()
    {
        // 面板从未因失活关闭过（首次打开 / Esc 关闭）——必须照常打开。
        Assert.False(TrayApp.ShouldSuppressReopenAfterDeactivate(null, Base));
    }

    [Theory]
    [InlineData(0)]    // 同一击：失活关闭后立即到达的 MouseClick
    [InlineData(100)]
    [InlineData(400)]  // 窗口边界（含）
    public void SuppressReopen_JustClosedByDeactivation_ReturnsTrue(int msAgo)
    {
        Assert.True(TrayApp.ShouldSuppressReopenAfterDeactivate(Base.AddMilliseconds(-msAgo), Base),
            $"{msAgo}ms 前刚因失活关闭——同一击托盘点击不应把面板重新弹出来");
    }

    [Theory]
    [InlineData(401)]  // 窗口外：这是用户的下一次真实交互
    [InlineData(5000)]
    public void SuppressReopen_OldDeactivateClose_ReturnsFalse(int msAgo)
    {
        Assert.False(TrayApp.ShouldSuppressReopenAfterDeactivate(Base.AddMilliseconds(-msAgo), Base),
            $"{msAgo}ms 前的失活关闭不该吞掉一次新的打开请求");
    }

    [Fact]
    public void SuppressReopen_FutureTimestamp_ClockSkew_ReturnsFalse()
    {
        // 时间戳在未来只可能是时钟漂移/乱序——宁可多开一次，也不吞掉真实点击。
        Assert.False(TrayApp.ShouldSuppressReopenAfterDeactivate(Base.AddSeconds(1), Base));
    }
}
