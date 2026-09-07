using System.Windows.Forms;
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

    // --- TryParseFunctionKeyNumber: the shared F1-F24 parser behind KeyStringToVk and
    // SettingsForm.IsFunctionKey (it used to be a duplicated rule in each). ---

    [Theory]
    [InlineData("F1", 1)]
    [InlineData("F2", 2)]
    [InlineData("F9", 9)]
    [InlineData("F10", 10)]
    [InlineData("F13", 13)]
    [InlineData("F19", 19)]
    [InlineData("F24", 24)]
    public void TryParseFunctionKeyNumber_AcceptsF1ToF24(string key, int expectedFn)
    {
        Assert.True(HotkeyService.TryParseFunctionKeyNumber(key, out var fn));
        Assert.Equal(expectedFn, fn);
    }

    [Theory]
    [InlineData("F0")]        // function keys start at F1
    [InlineData("F25")]        // Keys enum stops at F24
    [InlineData("F26")]
    [InlineData("F99")]
    [InlineData("F")]         // no digits at all
    [InlineData("FA")]         // digit position is not a digit
    [InlineData("F1A")]        // trailing junk
    [InlineData("F123")]       // too long — F100+ are not keys
    [InlineData("F+1")]        // sign is not a digit
    [InlineData("F 1")]        // space is not a digit
    [InlineData("")]           // empty
    [InlineData("X1")]         // wrong letter
    [InlineData("SPACE")]
    [InlineData("F٥")]         // non-ASCII digit: never a Keys enum name, must not parse
    public void TryParseFunctionKeyNumber_RejectsEverythingElse(string key)
    {
        Assert.False(HotkeyService.TryParseFunctionKeyNumber(key, out _));
    }

    /// <summary>
    /// KeyStringToVk returns 0 for keys that cannot be used as a hotkey.
    /// v1.0.6: "Space"/"Tab"/"Enter" used to be listed here as invalid — that was the
    /// defect that made digit, space, enter, escape and symbol keys unsettable. They are
    /// now valid (see <see cref="KeyStringToVk_RecognizesMainKeyboardAndEditingKeys"/>);
    /// only junk and the deliberately excluded keys remain rejected.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("NotAKey")]
    [InlineData("Bogus")]
    [InlineData("Back")]     // v1.0.5: rejected on purpose — use "Backspace"
    [InlineData("ShiftKey")] // modifier, not a hotkey key
    [InlineData("LButton")]  // mouse button
    [InlineData("KeyCode")]  // mask constant, not a virtual key
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

    // ===== v1.0.3: Numeric keypad key support tests =====

    /// <summary>
    /// v1.0.3: KeyStringToVk recognizes numeric keypad digits NumPad0–NumPad9.
    /// </summary>
    [Theory]
    [InlineData("NumPad0", 0x60u)]
    [InlineData("Numpad0", 0x60u)]   // alias (case-insensitive)
    [InlineData("NumPad1", 0x61u)]
    [InlineData("Numpad1", 0x61u)]
    [InlineData("NumPad2", 0x62u)]
    [InlineData("Numpad2", 0x62u)]
    [InlineData("NumPad3", 0x63u)]
    [InlineData("Numpad3", 0x63u)]
    [InlineData("NumPad4", 0x64u)]
    [InlineData("Numpad4", 0x64u)]
    [InlineData("NumPad5", 0x65u)]
    [InlineData("Numpad5", 0x65u)]
    [InlineData("NumPad6", 0x66u)]
    [InlineData("Numpad6", 0x66u)]
    [InlineData("NumPad7", 0x67u)]
    [InlineData("Numpad7", 0x67u)]
    [InlineData("NumPad8", 0x68u)]
    [InlineData("Numpad8", 0x68u)]
    [InlineData("NumPad9", 0x69u)]
    [InlineData("Numpad9", 0x69u)]
    public void KeyStringToVk_RecognizesNumPadDigits(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.3: KeyStringToVk recognizes numeric keypad operator keys.
    /// </summary>
    [Theory]
    [InlineData("Multiply", 0x6Au)]
    [InlineData("Add", 0x6Bu)]
    [InlineData("Separator", 0x6Cu)]
    [InlineData("Subtract", 0x6Du)]
    [InlineData("Decimal", 0x6Eu)]
    [InlineData("Divide", 0x6Fu)]
    public void KeyStringToVk_RecognizesNumPadOperators(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.3: KeyStringToVk is case-insensitive for numpad keys.
    /// </summary>
    [Fact]
    public void KeyStringToVk_NumPadKeys_CaseInsensitive()
    {
        Assert.Equal(0x6Du, HotkeyService.KeyStringToVk("subtract"));
        Assert.Equal(0x6Du, HotkeyService.KeyStringToVk("SUBTRACT"));
        Assert.Equal(0x6Bu, HotkeyService.KeyStringToVk("add"));
        Assert.Equal(0x6Au, HotkeyService.KeyStringToVk("MULTIPLY"));
        Assert.Equal(0x60u, HotkeyService.KeyStringToVk("numpad0"));
        Assert.Equal(0x69u, HotkeyService.KeyStringToVk("NUMPAD9"));
    }

    // ===== v1.0.3: Browser key support tests =====

    /// <summary>
    /// v1.0.3: KeyStringToVk recognizes browser navigation keys.
    /// </summary>
    [Theory]
    [InlineData("BrowserBack", 0xA6u)]
    [InlineData("Forward", 0xA7u)]           // not a Keys member — safe alias only
    [InlineData("BrowserForward", 0xA7u)]    // alias
    [InlineData("Refresh", 0xA8u)]
    [InlineData("BrowserRefresh", 0xA8u)]    // alias
    [InlineData("Stop", 0xA9u)]
    [InlineData("BrowserStop", 0xA9u)]       // alias
    [InlineData("Search", 0xAAu)]
    [InlineData("BrowserSearch", 0xAAu)]     // alias
    [InlineData("Favorites", 0xABu)]
    [InlineData("BrowserFavorites", 0xABu)]  // alias
    [InlineData("BrowserHome", 0xACu)]
    public void KeyStringToVk_RecognizesBrowserKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.5 REGRESSION: Keys.Back is the BACKSPACE key (VK_BACK = 8), NOT
    /// Keys.BrowserBack (VK_BROWSER_BACK = 0xA6). SettingsForm captures
    /// e.KeyCode.ToString() verbatim, so pressing Backspace (most often to clear the old
    /// value) yields the string "Back". v1.0.3 mapped "Back" → 0xA6, which silently
    /// registered Ctrl+Alt+BrowserBack: the hotkey looked saved but never fired, with no
    /// error at all. "Back" must now be rejected so the user gets the invalid-key prompt.
    /// </summary>
    [Fact]
    public void KeyStringToVk_Back_IsRejected_NotMappedToBrowserBack()
    {
        // Sanity check on the enum name WinForms actually reports for Backspace.
        Assert.Equal("Back", Keys.Back.ToString());

        Assert.Equal(0u, HotkeyService.KeyStringToVk(Keys.Back.ToString()));
        Assert.Equal(0u, HotkeyService.KeyStringToVk("Back"));

        // The browser key is still reachable under its unambiguous name.
        Assert.Equal("BrowserBack", Keys.BrowserBack.ToString());
        Assert.Equal(0xA6u, HotkeyService.KeyStringToVk("BrowserBack"));
        Assert.Equal(0xA6u, HotkeyService.KeyStringToVk(Keys.BrowserBack.ToString()));
    }

    /// <summary>
    /// v1.0.5: "Backspace" is accepted as the explicit, unambiguous spelling of VK_BACK
    /// (useful for hand-edited config files). It must never collide with BrowserBack.
    /// </summary>
    [Theory]
    [InlineData("Backspace", 0x08u)]
    [InlineData("backspace", 0x08u)]   // case-insensitive
    [InlineData("BACKSPACE", 0x08u)]
    public void KeyStringToVk_RecognizesBackspaceAlias(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
        Assert.NotEqual(HotkeyService.KeyStringToVk("BrowserBack"), HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.6: every NamedKeys entry that is also a real <see cref="Keys"/> enum member
    /// must map to that member's own virtual-key code.
    /// </summary>
    /// <remarks>
    /// v1.0.5 shipped this as 36 hand-written <c>InlineData</c> rows against a 58-entry
    /// dictionary — CapsLock, NumPad0-9, PageDown, PageUp and Snapshot were silently
    /// unguarded (and the user's own hotkey, NumPad0, was in that gap). Data now comes
    /// from reflecting over the dictionary itself, so adding an entry automatically adds
    /// its guard.
    /// </remarks>
    /// <param name="name">Dictionary key.</param>
    /// <param name="dictVk">Virtual-key code the dictionary maps the name to.</param>
    /// <param name="enumVk">Virtual-key code of the same-named <see cref="Keys"/> member.</param>
    [Theory]
    [MemberData(nameof(NamedKeysThatAreKeysEnumMembers))]
    public void NamedKeys_MatchesKeysEnumValue(string name, uint dictVk, uint enumVk)
    {
        Assert.Equal(enumVk, dictVk);
        Assert.Equal(dictVk, HotkeyService.KeyStringToVk(name));
    }

    /// <summary>
    /// Generates one case per NamedKeys entry whose name is also a <see cref="Keys"/>
    /// enum member (pure aliases such as "Forward" or "CapsLock"-style spellings that are
    /// not enum members are skipped — there is nothing to collide with).
    /// </summary>
    /// <returns>Name, dictionary virtual-key code, enum virtual-key code.</returns>
    public static TheoryData<string, uint, uint> NamedKeysThatAreKeysEnumMembers()
    {
        var data = new TheoryData<string, uint, uint>();
        foreach (var (name, vk) in HotkeyService.NamedKeys)
        {
            if (Enum.TryParse<Keys>(name, ignoreCase: false, out var enumValue))
                data.Add(name, vk, (uint)(int)enumValue);
        }
        return data;
    }

    /// <summary>
    /// Guards the guard: a reflection bug or an emptied dictionary must not turn the
    /// collision Theory above into zero silently-passing cases.
    /// </summary>
    [Fact]
    public void NamedKeysCollisionGuard_CoversEveryEnumMemberEntry()
    {
        var covered = NamedKeysThatAreKeysEnumMembers().Count();
        var expected = HotkeyService.NamedKeys
            .Count(kv => Enum.TryParse<Keys>(kv.Key, ignoreCase: false, out _));

        Assert.True(expected > 0, "NamedKeys should contain at least one Keys enum member");
        Assert.Equal(expected, covered);
        // Sanity anchor: the dictionary grows as key coverage expands.
        Assert.True(HotkeyService.NamedKeys.Count >= 100,
            $"Expected the expanded key map, found {HotkeyService.NamedKeys.Count} entries");
    }

    /// <summary>
    /// v1.0.6: the whole point of the key-map expansion — digits, space, enter, escape,
    /// tab and symbol keys must now resolve from the exact string SettingsForm produces.
    /// </summary>
    [Theory]
    [InlineData(Keys.D0, 0x30u)]
    [InlineData(Keys.D1, 0x31u)]
    [InlineData(Keys.D5, 0x35u)]
    [InlineData(Keys.D9, 0x39u)]
    [InlineData(Keys.Space, 0x20u)]
    [InlineData(Keys.Enter, 0x0Du)]
    [InlineData(Keys.Tab, 0x09u)]
    [InlineData(Keys.Escape, 0x1Bu)]
    [InlineData(Keys.OemSemicolon, 0xBAu)]
    [InlineData(Keys.Oemplus, 0xBBu)]
    [InlineData(Keys.Oemcomma, 0xBCu)]
    [InlineData(Keys.OemMinus, 0xBDu)]
    [InlineData(Keys.OemPeriod, 0xBEu)]
    [InlineData(Keys.OemQuestion, 0xBFu)]
    [InlineData(Keys.Oemtilde, 0xC0u)]
    [InlineData(Keys.OemOpenBrackets, 0xDBu)]
    [InlineData(Keys.OemPipe, 0xDCu)]
    [InlineData(Keys.OemCloseBrackets, 0xDDu)]
    [InlineData(Keys.OemQuotes, 0xDEu)]
    [InlineData(Keys.OemBackslash, 0xE2u)]
    [InlineData(Keys.Apps, 0x5Du)]
    [InlineData(Keys.SelectMedia, 0xB5u)]
    [InlineData(Keys.LaunchApplication1, 0xB6u)]
    [InlineData(Keys.LaunchApplication2, 0xB7u)]
    public void KeyStringToVk_RecognizesMainKeyboardAndEditingKeys(Keys key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key.ToString()));
    }

    /// <summary>
    /// v1.0.6: Keys.Enter and Keys.Return are two names for the same virtual key (0x0D).
    /// Both spellings must be accepted — they cannot be expressed as two InlineData rows
    /// because they compile to identical arguments.
    /// </summary>
    [Fact]
    public void KeyStringToVk_AcceptsBothEnterAndReturnSpellings()
    {
        Assert.Equal(0x0Du, HotkeyService.KeyStringToVk("Enter"));
        Assert.Equal(0x0Du, HotkeyService.KeyStringToVk("Return"));
        Assert.Equal(Keys.Enter, Keys.Return); // documents why this is a Fact, not a Theory
    }

    /// <summary>
    /// v1.0.5: "Back" must not appear as a NamedKeys alias. Keeping it out is what makes
    /// Backspace report as invalid instead of silently becoming BrowserBack.
    /// </summary>
    [Fact]
    public void KeyStringToVk_BackspaceKeyName_DoesNotResolveToBrowserBack()
    {
        // Whatever the spelling, the Backspace key must never resolve to 0xA6.
        foreach (var spelling in new[] { "Back", "Backspace", "back", "BACK" })
        {
            var vk = HotkeyService.KeyStringToVk(spelling);
            Assert.True(vk == 0u || vk == 0x08u,
                $"'{spelling}' resolved to 0x{vk:X2}, which is not Backspace (0x08) or invalid (0x00)");
        }
    }

    /// <summary>
    /// v1.0.3: "Home" must still map to VK_HOME (0x24), NOT VK_BROWSER_HOME (0xAC).
    /// This is a regression guard — the browser Home key uses "BrowserHome" instead.
    /// </summary>
    [Fact]
    public void KeyStringToVk_Home_StaysNavigationHome_NotBrowserHome()
    {
        Assert.Equal(0x24u, HotkeyService.KeyStringToVk("Home"));
        Assert.Equal(0xACu, HotkeyService.KeyStringToVk("BrowserHome"));
    }

    // ===== v1.0.3: Volume / media / launch key support tests =====

    /// <summary>
    /// v1.0.3: KeyStringToVk recognizes volume control keys.
    /// </summary>
    [Theory]
    [InlineData("VolumeMute", 0xADu)]
    [InlineData("VolumeDown", 0xAEu)]
    [InlineData("VolumeUp", 0xAFu)]
    public void KeyStringToVk_RecognizesVolumeKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.3: KeyStringToVk recognizes media transport keys.
    /// </summary>
    [Theory]
    [InlineData("MediaNextTrack", 0xB0u)]
    [InlineData("MediaPreviousTrack", 0xB1u)]   // .NET Keys enum full name
    [InlineData("MediaPrevTrack", 0xB1u)]       // alias (short form)
    [InlineData("MediaStop", 0xB2u)]
    [InlineData("MediaPlayPause", 0xB3u)]
    public void KeyStringToVk_RecognizesMediaKeys(string key, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(key));
    }

    /// <summary>
    /// v1.0.5: an unresolvable key must be rejected outright and must not report a
    /// registration. WndProc is deliberately NOT asserted here — it answers "was this
    /// WM_HOTKEY message consumed", not "is a hotkey live"; <see cref="HotkeyService.IsRegistered"/>
    /// is the state under test.
    /// </summary>
    /// v1.0.6: "Space" is no longer an invalid key — it became settable in this release,
    /// so it can no longer stand in as an example of an unresolvable key.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAKey")]
    [InlineData("Back")]   // Backspace — the v1.0.3-era silent BrowserBack collision
    public void Register_InvalidKey_ReturnsFalse_AndStaysUnregistered(string key)
    {
        var svc = new HotkeyService(IntPtr.Zero);
        var cfg = new HotkeyConfig { Modifiers = "Ctrl+Alt", Key = key };

        Assert.False(svc.Register(cfg));
        Assert.False(svc.IsRegistered);
    }

    /// <summary>
    /// v1.0.5 REGRESSION (ordering): v1.0.3 unregistered the old hotkey BEFORE resolving
    /// the new key, so a bad new key left the app with no hotkey at all and no way to
    /// roll back atomically. Validation must now happen first, leaving the existing
    /// registration intact.
    /// </summary>
    /// <remarks>
    /// v1.0.7: the <c>return</c> on an unregisterable environment used to make this a
    /// silent no-op — the test reported green while asserting nothing. It now skips
    /// explicitly, with the reason recorded in the test output.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("NotAKey")]
    [InlineData("Back")]
    public void Register_InvalidKey_KeepsPreviousRegistration(string invalidKey)
    {
        var svc = new HotkeyService(IntPtr.Zero);
        try
        {
            // A null hwnd registers a thread-level hotkey, which succeeds on a normal
            // interactive thread and gives us a real "previously registered" state.
            var registered = svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F24" });
            if (!registered)
            {
                SkipTest(NoInteractiveDesktopReason);
            }
            Assert.True(svc.IsRegistered);

            Assert.False(svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt", Key = invalidKey }));

            // The working hotkey must have survived the failed change.
            Assert.True(svc.IsRegistered);
        }
        finally
        {
            svc.Unregister();
        }
    }

    /// <summary>
    /// v1.0.5: replacing one valid hotkey with another must end with exactly one live
    /// registration (the new key), not zero or two.
    /// </summary>
    [Fact]
    public void Register_ValidKeyReplacement_EndsRegistered()
    {
        var svc = new HotkeyService(IntPtr.Zero);
        try
        {
            if (!svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F24" }))
            {
                SkipTest(NoInteractiveDesktopReason);
            }

            Assert.True(svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F23" }));
            Assert.True(svc.IsRegistered);
        }
        finally
        {
            svc.Unregister();
        }
    }

    // ===== v1.0.7 =====

    /// <summary>
    /// v1.0.7: RegisterHotKey needs an interactive window station. When it is unavailable
    /// the affected tests skip with this reason rather than passing without asserting.
    /// </summary>
    private const string NoInteractiveDesktopReason =
        "This environment cannot register thread-level hotkeys (no interactive window " +
        "station — typical of a service account or a headless CI agent).";

    /// <summary>
    /// Skips the running test, recording <paramref name="reason"/> in the test output.
    /// </summary>
    /// <param name="reason">Why the test cannot run here.</param>
    /// <remarks>
    /// xunit 2.9.0 documents <c>Assert.Skip</c> but does not ship it in
    /// <c>xunit.assert</c>, so the exception it would have thrown is raised directly.
    /// </remarks>
    private static void SkipTest(string reason) => throw Xunit.Sdk.SkipException.ForSkip(reason);

    /// <summary>
    /// v1.0.7 (M6): "combination already in use" is far more common than "key name
    /// invalid", yet it was the branch that tore the working hotkey down and left nothing
    /// behind. Register must restore the previous combination when Windows refuses the new
    /// one, instead of reporting a revert it did not perform.
    /// </summary>
    /// <remarks>
    /// A thread-level hotkey (hwnd = <see cref="IntPtr.Zero"/>) is owned by the thread
    /// that registered it and is released when that thread exits, so the blocking
    /// registration has to happen on — and stay on — a separate thread for the refusal to
    /// be reproducible.
    /// </remarks>
    [Fact]
    public void Register_CombinationInUse_RestoresThePreviousRegistration()
    {
        var svc = new HotkeyService(IntPtr.Zero);

        var blockerReady = new ManualResetEventSlim(false);
        var releaseBlocker = new ManualResetEventSlim(false);
        // bool[] rather than a captured local: the value is written on the blocker thread.
        var blocked = new bool[1];

        var blockerThread = new Thread(() =>
        {
            // Owns Ctrl+Alt+Shift+F23 for this thread only, for the life of the thread.
            var blocker = new HotkeyService(IntPtr.Zero);
            blocked[0] = blocker.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F23" });
            blockerReady.Set();
            releaseBlocker.Wait();
            blocker.Dispose();
        })
        { IsBackground = true };
        blockerThread.SetApartmentState(ApartmentState.STA);
        blockerThread.Start();

        try
        {
            if (!blockerReady.Wait(TimeSpan.FromSeconds(10)))
            {
                SkipTest("The blocking thread did not start in time.");
            }
            if (!blocked[0])
            {
                SkipTest(NoInteractiveDesktopReason);
            }
            if (!svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F24" }))
            {
                SkipTest(NoInteractiveDesktopReason);
            }
            Assert.True(svc.IsRegistered);

            // The blocking thread owns this combination, so Windows must refuse it here.
            var accepted = svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F23" });

            Assert.False(accepted, "expected RegisterHotKey to refuse a combination owned by another thread");
            // The rollback is the point of the test: the previous hotkey is live again.
            Assert.True(svc.IsRegistered,
                "a refused hotkey change must restore the previous registration, not leave the app with none");
        }
        finally
        {
            releaseBlocker.Set();
            blockerThread.Join(TimeSpan.FromSeconds(10));
            svc.Unregister();
        }
    }

    /// <summary>
    /// v1.0.7 (M12): the service is IDisposable, so releasing the hotkey no longer depends
    /// on every caller remembering to call Unregister() on every exit path.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesTheHotkey_AndIsIdempotent()
    {
        var svc = new HotkeyService(IntPtr.Zero);

        if (!svc.Register(new HotkeyConfig { Modifiers = "Ctrl+Alt+Shift", Key = "F24" }))
        {
            SkipTest(NoInteractiveDesktopReason);
        }
        Assert.True(svc.IsRegistered);

        svc.Dispose();
        Assert.False(svc.IsRegistered);

        // A second Dispose must not throw — Dispose is called from TrayApp.Dispose and
        // from an exception path in the same run.
        svc.Dispose();
        Assert.False(svc.IsRegistered);
    }

    /// <summary>
    /// v1.0.7 (M5): the service owns exactly one hotkey id. A WM_HOTKEY carrying any other
    /// id must be ignored, and the id is no longer a caller-supplied parameter that could
    /// disagree with the one WndProc recognises.
    /// </summary>
    [Fact]
    public void Register_HasNoCustomIdParameter_ThatCouldDisagreeWithWndProc()
    {
        var register = typeof(HotkeyService).GetMethod(nameof(HotkeyService.Register));
        var unregister = typeof(HotkeyService).GetMethod(nameof(HotkeyService.Unregister));

        Assert.NotNull(register);
        Assert.NotNull(unregister);
        Assert.Single(register!.GetParameters());
        Assert.Empty(unregister!.GetParameters());
    }

    /// <summary>
    /// v1.0.3 regression guard, retained. The full enum-name coverage for these keys now
    /// lives in <see cref="QaKeysEnumMatchTests"/>; only the two targeted Facts remain
    /// there, so this duplicate Theory is intentionally not repeated in this file.
    /// </summary>
    [Fact]
    public void KeyStringToVk_RecognizesLaunchMail()
    {
        Assert.Equal(0xB4u, HotkeyService.KeyStringToVk("LaunchMail"));
    }
}
