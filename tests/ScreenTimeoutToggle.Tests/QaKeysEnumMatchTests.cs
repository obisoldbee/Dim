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
    /// Verify ALL newly-added keys: Keys enum ToString() must resolve via KeyStringToVk.
    /// This is the definitive end-to-end check for the SettingsForm capture chain.
    /// </summary>
    [Theory]
    [InlineData(Keys.NumPad0, 0x60u)]
    [InlineData(Keys.NumPad1, 0x61u)]
    [InlineData(Keys.NumPad2, 0x62u)]
    [InlineData(Keys.NumPad3, 0x63u)]
    [InlineData(Keys.NumPad4, 0x64u)]
    [InlineData(Keys.NumPad5, 0x65u)]
    [InlineData(Keys.NumPad6, 0x66u)]
    [InlineData(Keys.NumPad7, 0x67u)]
    [InlineData(Keys.NumPad8, 0x68u)]
    [InlineData(Keys.NumPad9, 0x69u)]
    [InlineData(Keys.Multiply, 0x6Au)]
    [InlineData(Keys.Add, 0x6Bu)]
    [InlineData(Keys.Separator, 0x6Cu)]
    [InlineData(Keys.Subtract, 0x6Du)]
    [InlineData(Keys.Decimal, 0x6Eu)]
    [InlineData(Keys.Divide, 0x6Fu)]
    [InlineData(Keys.BrowserBack, 0xA6u)]
    [InlineData(Keys.BrowserForward, 0xA7u)]
    [InlineData(Keys.BrowserRefresh, 0xA8u)]
    [InlineData(Keys.BrowserStop, 0xA9u)]
    [InlineData(Keys.BrowserSearch, 0xAAu)]
    [InlineData(Keys.BrowserFavorites, 0xABu)]
    [InlineData(Keys.BrowserHome, 0xACu)]
    [InlineData(Keys.VolumeMute, 0xADu)]
    [InlineData(Keys.VolumeDown, 0xAEu)]
    [InlineData(Keys.VolumeUp, 0xAFu)]
    [InlineData(Keys.MediaNextTrack, 0xB0u)]
    [InlineData(Keys.MediaPreviousTrack, 0xB1u)]
    [InlineData(Keys.MediaStop, 0xB2u)]
    [InlineData(Keys.MediaPlayPause, 0xB3u)]
    [InlineData(Keys.LaunchMail, 0xB4u)]
    public void KeyStringToVk_AcceptsActualKeysEnumName(Keys keysEnum, uint expectedVk)
    {
        var keyStr = keysEnum.ToString();
        var vk = HotkeyService.KeyStringToVk(keyStr);
        Assert.Equal(expectedVk, vk);
    }
}
