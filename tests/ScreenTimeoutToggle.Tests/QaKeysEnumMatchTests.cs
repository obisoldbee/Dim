using System.Windows.Forms;
using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// QA verification: ensures that .NET Keys enum ToString() output matches
/// the NamedKeys dictionary keys in HotkeyService. This catches the real-world
/// bug where a dictionary key uses an abbreviated name that doesn't match the
/// actual Keys enum member name returned by e.KeyCode.ToString() in SettingsForm.
/// </summary>
public class QaKeysEnumMatchTests
{
    /// <summary>
    /// Verifies that Keys.MediaPreviousTrack.ToString() returns the FULL name
    /// "MediaPreviousTrack", not the abbreviated "MediaPrevTrack".
    /// </summary>
    [Fact]
    public void KeysEnum_MediaPreviousTrack_FullName()
    {
        var name = Keys.MediaPreviousTrack.ToString();
        Assert.Equal("MediaPreviousTrack", name);
    }

    /// <summary>
    /// CRITICAL: KeyStringToVk must accept the ACTUAL Keys enum name for the
    /// Media Previous Track key (0xB1). The user presses the key, SettingsForm
    /// calls e.KeyCode.ToString() → "MediaPreviousTrack", then KeyStringToVk.
    /// If the dictionary only has "MediaPrevTrack" (abbreviated), this returns 0
    /// and the user sees "not a valid hotkey" — the SAME bug class as v1.0.2 Subtract.
    /// </summary>
    [Fact]
    public void KeyStringToVk_AcceptsActualKeysEnumName_MediaPreviousTrack()
    {
        var keyStr = Keys.MediaPreviousTrack.ToString();
        var vk = HotkeyService.KeyStringToVk(keyStr);
        Assert.Equal(0xB1u, vk);
    }

    /// <summary>
    /// v1.0.5 REGRESSION: Keys.Back is BACKSPACE (VK_BACK = 8), while Keys.BrowserBack is
    /// VK_BROWSER_BACK (0xA6). SettingsForm feeds e.KeyCode.ToString() straight into
    /// KeyStringToVk, so Backspace produces the string "Back" — which v1.0.3 mapped to the
    /// browser key, silently registering a hotkey that never fired.
    /// Verified here because this file owns the "dictionary key vs Keys enum name" checks.
    /// </summary>
    [Fact]
    public void KeysEnum_Back_IsBackspace_NotBrowserBack()
    {
        Assert.Equal("Back", Keys.Back.ToString());
        Assert.Equal(0x08, (int)Keys.Back);
        Assert.Equal(0xA6, (int)Keys.BrowserBack);

        // Pressing Backspace must be reported as an invalid hotkey, not silently remapped.
        Assert.Equal(0u, HotkeyService.KeyStringToVk(Keys.Back.ToString()));
        // The explicit alias still works for hand-edited config files.
        Assert.Equal(0x08u, HotkeyService.KeyStringToVk("Backspace"));
        // The browser key stays reachable under its own name.
        Assert.Equal(0xA6u, HotkeyService.KeyStringToVk(Keys.BrowserBack.ToString()));
    }
}
