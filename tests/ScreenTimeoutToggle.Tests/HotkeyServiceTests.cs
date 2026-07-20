using OBDim.Models;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

public class HotkeyServiceTests
{
    [Theory]
    [InlineData("", 0u)]
    [InlineData("Ctrl", 0x0002u)]
    [InlineData("Alt", 0x0001u)]
    [InlineData("Shift", 0x0004u)]
    [InlineData("Win", 0x0008u)]
    [InlineData("Ctrl+Alt", 0x0003u)]
    [InlineData("Ctrl+Shift+Alt", 0x0007u)]
    public void ParseModifiers_ParsesCombinations(string input, uint expected)
    {
        Assert.Equal(expected, HotkeyService.ParseModifiers(input));
    }

    [Fact]
    public void ParseModifiers_HandlesWhitespaceAndCase()
    {
        Assert.Equal(0x0003u, HotkeyService.ParseModifiers("ctrl + alt"));
        Assert.Equal(0x0003u, HotkeyService.ParseModifiers("CTRL+ALT"));
    }

    [Fact]
    public void HotkeyPressed_FiresWhenWndProcReceivesWmHotkey_WithMatchingId()
    {
        var svc = new HotkeyService(IntPtr.Zero);
        var fired = false;
        svc.HotkeyPressed += () => fired = true;

        // WM_HOTKEY = 0x0312; WParam holds the hotkey id, LParam holds modifiers+vk
        var msg = Message.Create(IntPtr.Zero, 0x0312, (IntPtr)1, IntPtr.Zero);
        var handled = svc.WndProc(msg);

        Assert.True(handled);
        Assert.True(fired);
    }

    [Fact]
    public void WndProc_IgnoresOtherMessages()
    {
        var svc = new HotkeyService(IntPtr.Zero);
        var fired = false;
        svc.HotkeyPressed += () => fired = true;

        var msg = Message.Create(IntPtr.Zero, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        var handled = svc.WndProc(msg);

        Assert.False(handled);
        Assert.False(fired);
    }

    [Fact]
    public void WndProc_IgnoresHotkeyWithWrongId()
    {
        var svc = new HotkeyService(IntPtr.Zero);
        var fired = false;
        svc.HotkeyPressed += () => fired = true;

        var msg = Message.Create(IntPtr.Zero, 0x0312, (IntPtr)99, IntPtr.Zero); // WParam = wrong id
        var handled = svc.WndProc(msg);

        Assert.False(handled);
        Assert.False(fired);
    }

    /// <summary>
    /// New test: KeyStringToVk recognizes single letters.
    /// </summary>
    [Theory]
    [InlineData("S", 'S')]
    [InlineData("A", 'A')]
    [InlineData("Z", 'Z')]
    public void KeyStringToVk_RecognizesSingleLetters(string key, char expectedVk)
    {
        Assert.Equal((uint)expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// New test: KeyStringToVk recognizes single digits.
    /// </summary>
    [Theory]
    [InlineData("1", '1')]
    [InlineData("0", '0')]
    [InlineData("9", '9')]
    public void KeyStringToVk_RecognizesSingleDigits(string key, char expectedVk)
    {
        Assert.Equal((uint)expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// New test: KeyStringToVk recognizes function keys F1-F24.
    /// </summary>
    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F5", 0x74u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("F24", 0x87u)]
    public void KeyStringToVk_RecognizesFunctionKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// New test: KeyStringToVk returns 0 for invalid keys (e.g., "Space", "Tab", empty, null).
    /// </summary>
    [Theory]
    [InlineData("Space")]
    [InlineData("Tab")]
    [InlineData("Enter")]
    [InlineData("")]
    [InlineData("  ")]
    public void KeyStringToVk_ReturnsZero_ForInvalidKeys(string key)
    {
        Assert.Equal(0u, HotkeyService.KeyStringToVk(key));
    }
}
