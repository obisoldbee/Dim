using OBDim.UI;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// Direct coverage for <see cref="SettingsForm.IsFunctionKey"/> — the rule that lets a
/// function key be captured as a single-key hotkey without modifiers. It was previously
/// private and only exercised indirectly (if at all). Pure string logic, so no STA
/// thread or WinForms control creation is needed.
/// </summary>
public class SettingsFormTests
{
    [Theory]
    [InlineData("F1")]
    [InlineData("F12")]
    [InlineData("F24")]
    [InlineData("PrintScreen")]
    [InlineData("Snapshot")]   // alias
    [InlineData("Pause")]
    [InlineData("Scroll")]
    [InlineData("ScrollLock")] // alias
    [InlineData("Capital")]    // Keys enum name for CapsLock
    [InlineData("CapsLock")]
    [InlineData("NumLock")]
    public void IsFunctionKey_AcceptsFunctionFamily(string key)
    {
        Assert.True(SettingsForm.IsFunctionKey(key), $"{key} should count as a function key");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("D1")]
    [InlineData("Space")]
    [InlineData("Backspace")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("F123")]
    [InlineData("Escape")]
    [InlineData("BrowserBack")]
    [InlineData("")]
    [InlineData("  ")]
    public void IsFunctionKey_RejectsNonFunctionKeys(string key)
    {
        Assert.False(SettingsForm.IsFunctionKey(key), $"{key} should not count as a function key");
    }

    [Theory]
    [InlineData("f1")]
    [InlineData("printscreen")]
    [InlineData("capslock")]
    [InlineData(" numlock ")]
    public void IsFunctionKey_IsCaseAndWhitespaceInsensitive(string key)
    {
        Assert.True(SettingsForm.IsFunctionKey(key), "Keys enum names are matched case-insensitively");
    }
}
