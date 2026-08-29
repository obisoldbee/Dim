using System.Windows.Forms;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// v1.0.6: exhaustive check that every <see cref="Keys"/> name a user could plausibly
/// press resolves to a real virtual-key code through
/// <see cref="HotkeyService.KeyStringToVk"/>.
/// </summary>
/// <remarks>
/// Before v1.0.6, 94 of the enum's names returned 0, so pressing a top-row digit in the
/// settings dialog produced the string "D1" and was rejected as an invalid hotkey.
/// The gap is closed for everything that is a genuine key; the remainder is excluded
/// deliberately and enumerated here so any future gap shows up as a test failure.
/// </remarks>
public class KeysEnumCoverageTests
{
    /// <summary>
    /// Keys enum names intentionally left unresolvable, with the reason.
    /// Anything NOT in this set must resolve to a non-zero virtual-key code.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyExcluded =
        new(StringComparer.Ordinal)
        {
            // v1.0.5 decision: pressing Backspace in the hotkey capture box is far more
            // likely to mean "clear the field" than "bind Backspace". Accepting the string
            // "Back" is what silently turned Ctrl+Alt+Backspace into Ctrl+Alt+BrowserBack.
            // "Backspace" is the accepted spelling; ConfigService migrates stored "Back".
            ["Back"] = "v1.0.5: rejected on purpose — Backspace collided with BrowserBack; use \"Backspace\"",

            // Not keys at all.
            ["None"] = "sentinel value, not a key",
            ["KeyCode"] = "mask constant used to strip modifiers",
            ["Modifiers"] = "mask constant used to isolate modifiers",

            // Mouse buttons — this app binds keyboard hotkeys only.
            ["LButton"] = "mouse button",
            ["RButton"] = "mouse button",
            ["MButton"] = "mouse button",
            ["XButton1"] = "mouse button",
            ["XButton2"] = "mouse button",

            // Modifiers. RegisterHotKey takes these as modifier flags, not as the key.
            ["Shift"] = "modifier flag, not a virtual key",
            ["Control"] = "modifier flag, not a virtual key",
            ["Alt"] = "modifier flag, not a virtual key",
            ["ShiftKey"] = "modifier key — handled by ParseModifiers",
            ["ControlKey"] = "modifier key — handled by ParseModifiers",
            ["Menu"] = "modifier key (Alt) — handled by ParseModifiers",
            ["LShiftKey"] = "modifier key — handled by ParseModifiers",
            ["RShiftKey"] = "modifier key — handled by ParseModifiers",
            ["LControlKey"] = "modifier key — handled by ParseModifiers",
            ["RControlKey"] = "modifier key — handled by ParseModifiers",
            ["LMenu"] = "modifier key — handled by ParseModifiers",
            ["RMenu"] = "modifier key — handled by ParseModifiers",

            // Pseudo-keys and reserved codes that no physical key produces.
            ["Cancel"] = "Ctrl+Break pseudo-key, not a physical key",
            ["LineFeed"] = "control character, not a virtual key",
            ["FinalMode"] = "IME-internal mode constant",
            ["ProcessKey"] = "reserved for IME processing",
            ["Packet"] = "reserved for passing Unicode characters",
            ["Attn"] = "IBM 3270 reserved code",
            ["Crsel"] = "IBM 3270 reserved code",
            ["Exsel"] = "IBM 3270 reserved code",
            ["EraseEof"] = "IBM 3270 reserved code",
            ["Play"] = "IBM 3270 reserved code",
            ["Zoom"] = "reserved code",
            ["NoName"] = "reserved code",
            ["Pa1"] = "IBM 3270 reserved code",
        };

    /// <summary>
    /// Exhaustive guard: any Keys enum name that is not on the exclusion list must
    /// resolve to a non-zero virtual-key code.
    /// </summary>
    [Fact]
    public void KeysEnum_EveryNonExcludedName_ResolvesToVirtualKey()
    {
        var unresolved = Enum.GetNames<Keys>()
            .Where(name => !DeliberatelyExcluded.ContainsKey(name))
            .Where(name => HotkeyService.KeyStringToVk(name) == 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unresolved);
    }

    /// <summary>
    /// The resolved code must equal the enum's own value — catches a mapping that
    /// resolves (non-zero) but to the wrong key, the exact shape of the v1.0.3
    /// Back/BrowserBack defect.
    /// </summary>
    [Fact]
    public void KeysEnum_ResolvedCode_MatchesEnumValue()
    {
        var mismatches = Enum.GetNames<Keys>()
            .Where(name => !DeliberatelyExcluded.ContainsKey(name))
            .Select(name => (Name: name,
                             Expected: (uint)(int)Enum.Parse<Keys>(name),
                             Actual: HotkeyService.KeyStringToVk(name)))
            .Where(x => x.Expected != x.Actual)
            .Select(x => $"{x.Name}: enum=0x{x.Expected:X2} dict=0x{x.Actual:X2}")
            .ToList();

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The exclusion list must stay honest: every name on it must really be unresolved,
    /// otherwise a stale entry would hide a key that is now supported.
    /// </summary>
    [Fact]
    public void ExclusionList_ContainsOnlyNamesThatAreStillUnresolved()
    {
        var stale = DeliberatelyExcluded.Keys
            .Where(name => HotkeyService.KeyStringToVk(name) != 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(stale);
    }

    /// <summary>
    /// Every name on the exclusion list must actually exist in the Keys enum, so a typo
    /// cannot silently disable the guard for a real key.
    /// </summary>
    [Fact]
    public void ExclusionList_ContainsOnlyRealKeysEnumNames()
    {
        var bogus = DeliberatelyExcluded.Keys
            .Where(name => !Enum.IsDefined(typeof(Keys), name))
            .ToList();

        Assert.Empty(bogus);
    }

    /// <summary>
    /// Documents the size of the gap that v1.0.6 closed, and keeps it closed.
    /// v1.0.5 resolved 0 of the excluded set and 94 enum names overall were missing.
    /// </summary>
    [Fact]
    public void KeysEnum_MissingCount_IsWithinExpectedBounds()
    {
        var missing = Enum.GetNames<Keys>()
            .Count(name => HotkeyService.KeyStringToVk(name) == 0);

        // Exactly the documented exclusions, no more.
        Assert.Equal(DeliberatelyExcluded.Count, missing);
    }

    /// <summary>
    /// The user's own hotkey (NumPad0) sat inside the v1.0.5 collision-guard gap.
    /// Pin it explicitly.
    /// </summary>
    [Theory]
    [InlineData("NumPad0", 0x60u)]
    [InlineData("CapsLock", 0x14u)]
    [InlineData("PageUp", 0x21u)]
    [InlineData("PageDown", 0x22u)]
    [InlineData("Snapshot", 0x2Cu)]
    public void KeyStringToVk_ResolvesNamesThatMissedTheOldHardcodedGuard(string name, uint expectedVk)
    {
        Assert.Equal(expectedVk, HotkeyService.KeyStringToVk(name));
    }
}
