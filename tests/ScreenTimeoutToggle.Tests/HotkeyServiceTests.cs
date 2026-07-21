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

    // ===== v1.0.2: Named key support tests =====

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes PrintScreen (VK_SNAPSHOT = 0x2C).
    /// </summary>
    [Theory]
    [InlineData("PrintScreen", 0x2Cu)]
    [InlineData("Snapshot", 0x2Cu)]   // alias
    public void KeyStringToVk_RecognizesPrintScreen(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes Pause (VK_PAUSE = 0x13).
    /// </summary>
    [Fact]
    public void KeyStringToVk_RecognizesPause()
    {
        Assert.Equal(0x13u, HotkeyService.KeyStringToVk("Pause"));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes ScrollLock (VK_SCROLL = 0x91).
    /// </summary>
    [Theory]
    [InlineData("Scroll", 0x91u)]
    [InlineData("ScrollLock", 0x91u)]  // alias
    public void KeyStringToVk_RecognizesScrollLock(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes CapsLock (VK_CAPITAL = 0x14).
    /// </summary>
    [Theory]
    [InlineData("Capital", 0x14u)]
    [InlineData("CapsLock", 0x14u)]  // alias
    public void KeyStringToVk_RecognizesCapsLock(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes NumLock (VK_NUMLOCK = 0x90).
    /// </summary>
    [Fact]
    public void KeyStringToVk_RecognizesNumLock()
    {
        Assert.Equal(0x90u, HotkeyService.KeyStringToVk("NumLock"));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes navigation keys (Insert, Delete, Home, End, etc.).
    /// </summary>
    [Theory]
    [InlineData("Insert", 0x2Du)]
    [InlineData("Delete", 0x2Eu)]
    [InlineData("Home", 0x24u)]
    [InlineData("End", 0x23u)]
    [InlineData("PageUp", 0x21u)]
    [InlineData("Prior", 0x21u)]      // alias for PageUp
    [InlineData("PageDown", 0x22u)]
    [InlineData("Next", 0x22u)]       // alias for PageDown
    public void KeyStringToVk_RecognizesNavigationKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk recognizes arrow keys.
    /// </summary>
    [Theory]
    [InlineData("Left", 0x25u)]
    [InlineData("Up", 0x26u)]
    [InlineData("Right", 0x27u)]
    [InlineData("Down", 0x28u)]
    public void KeyStringToVk_RecognizesArrowKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.2: KeyStringToVk is case-insensitive for named keys.
    /// </summary>
    [Fact]
    public void KeyStringToVk_NamedKeys_CaseInsensitive()
    {
        Assert.Equal(0x2Cu, HotkeyService.KeyStringToVk("printscreen"));
        Assert.Equal(0x2Cu, HotkeyService.KeyStringToVk("PRINTSCREEN"));
        Assert.Equal(0x13u, HotkeyService.KeyStringToVk("pause"));
        Assert.Equal(0x91u, HotkeyService.KeyStringToVk("SCROLLLOCK"));
    }

    /// <summary>
    /// v1.0.2: F13-F24 should still work (already supported in v1.0.1, verify regression).
    /// </summary>
    [Theory]
    [InlineData("F13", 0x7Cu)]
    [InlineData("F16", 0x7Fu)]
    [InlineData("F20", 0x83u)]
    [InlineData("F24", 0x87u)]
    public void KeyStringToVk_RecognizesF13ToF24(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }
}
