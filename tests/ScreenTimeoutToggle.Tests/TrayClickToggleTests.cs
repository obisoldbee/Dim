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
}
