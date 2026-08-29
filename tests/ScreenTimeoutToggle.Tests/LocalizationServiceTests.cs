using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

public class LocalizationServiceTests
{
    /// <summary>
    /// Default language should be zh-CN.
    /// </summary>
    [Fact]
    public void DefaultLanguage_IsZhCn()
    {
        // Save and restore to avoid affecting other tests
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            Assert.Equal("zh-CN", LocalizationService.CurrentLanguage);
            Assert.Equal("退出", LocalizationService.Get("menu.exit"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Switching to en-US should return English text.
    /// </summary>
    [Fact]
    public void SwitchToEnUs_ReturnsEnglishText()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "en-US";
            Assert.Equal("Exit", LocalizationService.Get("menu.exit"));
            Assert.Equal("Settings...", LocalizationService.Get("menu.settings"));
            Assert.Equal("Switch to Away", LocalizationService.Get("menu.switch_to_away"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Switching to zh-CN should return Chinese text.
    /// </summary>
    [Fact]
    public void SwitchToZhCn_ReturnsChineseText()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            Assert.Equal("退出", LocalizationService.Get("menu.exit"));
            Assert.Equal("设置...", LocalizationService.Get("menu.settings"));
            Assert.Equal("切换到离开", LocalizationService.Get("menu.switch_to_away"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Missing key should return the key itself as fallback.
    /// </summary>
    [Fact]
    public void Get_MissingKey_ReturnsKeyItself()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            Assert.Equal("nonexistent.key", LocalizationService.Get("nonexistent.key"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Format string with arguments should substitute correctly.
    /// </summary>
    [Fact]
    public void Get_WithArgs_FormatsCorrectly()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "en-US";
            var result = LocalizationService.Get("bubble.mode_changed", "Work");
            Assert.Equal("Switched to Work mode", result);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Format string with arguments in Chinese should substitute correctly.
    /// </summary>
    [Fact]
    public void Get_WithArgs_FormatsCorrectly_ZhCn()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            var result = LocalizationService.Get("bubble.mode_changed", "工作");
            Assert.Equal("已切换到工作模式", result);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// GetLanguageDisplayName returns the display name for known codes.
    /// </summary>
    [Fact]
    public void GetLanguageDisplayName_ReturnsDisplayName()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            Assert.Equal("中文", LocalizationService.GetLanguageDisplayName("zh-CN"));
            Assert.Equal("English", LocalizationService.GetLanguageDisplayName("en-US"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// GetLanguageDisplayName returns the code itself for unknown languages.
    /// </summary>
    [Fact]
    public void GetLanguageDisplayName_UnknownCode_ReturnsCodeItself()
    {
        Assert.Equal("fr-FR", LocalizationService.GetLanguageDisplayName("fr-FR"));
    }

    /// <summary>
    /// Tooltip format strings should substitute timeout values correctly.
    /// </summary>
    [Fact]
    public void Get_TooltipFormat_SubstitutesValues()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "en-US";
            var result = LocalizationService.Get("tooltip.work", "Never", "30min");
            Assert.Equal("Work · AC Never / DC 30min", result);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// Tooltip format strings in Chinese should substitute timeout values correctly.
    /// </summary>
    [Fact]
    public void Get_TooltipFormat_SubstitutesValues_ZhCn()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            var result = LocalizationService.Get("tooltip.work", "从不", "30分钟");
            Assert.Equal("工作 · 电源 从不 / 电池 30分钟", result);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// v1.0.5: every translation key must exist in every supported language. A missing key
    /// degrades silently — Chinese users would see either the raw key or English text —
    /// so the gap is invisible until someone actually hits that code path.
    /// </summary>
    [Fact]
    public void EveryKey_IsDefinedInEveryLanguage()
    {
        var languages = LocalizationService.SupportedLanguages;
        Assert.Contains("zh-CN", languages);
        Assert.Contains("en-US", languages);

        foreach (var key in LocalizationService.AllKeys)
        {
            foreach (var language in languages)
            {
                Assert.True(LocalizationService.HasKey(key, language),
                    $"Translation key '{key}' is missing for language '{language}'.");
            }
        }
    }

    /// <summary>
    /// v1.0.5: powercfg failures are shown to the user through PowerConfigErrorKind.
    /// Every kind needs a real translation in BOTH languages — a missing key would leak
    /// the raw key name (or an English sentence) into a Chinese user's error bubble.
    /// </summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void GetPowerConfigError_AllKinds_AreTranslated(string language)
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = language;

            foreach (var kind in new[]
                     {
                         PowerConfigErrorKind.Timeout,
                         PowerConfigErrorKind.InvocationFailed,
                         PowerConfigErrorKind.NonZeroExit,
                         PowerConfigErrorKind.ParseFailed
                     })
            {
                var message = LocalizationService.GetPowerConfigError(kind, "RAW_FALLBACK");

                Assert.NotEqual("RAW_FALLBACK", message);
                Assert.DoesNotContain("error.powercfg", message);
                Assert.False(string.IsNullOrWhiteSpace(message));
            }
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// v1.0.5: the timeout message must be actionable — it should tell the user to check
    /// security software, which is the most common real cause of a powercfg stall.
    /// </summary>
    [Fact]
    public void GetPowerConfigError_Timeout_IsActionable()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "zh-CN";
            var zh = LocalizationService.GetPowerConfigError(PowerConfigErrorKind.Timeout, "raw");
            Assert.Contains("powercfg", zh);
            Assert.Contains("安全软件", zh);

            LocalizationService.CurrentLanguage = "en-US";
            var en = LocalizationService.GetPowerConfigError(PowerConfigErrorKind.Timeout, "raw");
            Assert.Contains("powercfg", en);
            Assert.Contains("security software", en);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// v1.0.5: an unclassified failure falls back to the raw message rather than showing
    /// the user an empty bubble.
    /// </summary>
    [Fact]
    public void GetPowerConfigError_UnknownKind_ReturnsFallbackMessage()
    {
        Assert.Equal("raw message",
            LocalizationService.GetPowerConfigError(PowerConfigErrorKind.Unknown, "raw message"));
    }

    /// <summary>
    /// v1.0.6: "key is invalid" and "key is already taken" are now different messages.
    /// They must be translated in both languages AND must not collapse back into the
    /// same wording, otherwise the split is cosmetic.
    /// </summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void HotkeyChangeFailure_InvalidAndInUse_AreDistinctAndTranslated(string language)
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = language;

            var invalidTitle = LocalizationService.Get("bubble.hotkey_change_invalid_title");
            var invalidText = LocalizationService.Get("bubble.hotkey_change_invalid", "Ctrl+Alt", "D1");
            var inUseTitle = LocalizationService.Get("bubble.hotkey_change_failed_title");
            var inUseText = LocalizationService.Get("bubble.hotkey_change_failed", "Ctrl+Alt", "F5");

            foreach (var s in new[] { invalidTitle, invalidText, inUseTitle, inUseText })
            {
                Assert.DoesNotContain("bubble.hotkey", s);
                Assert.False(string.IsNullOrWhiteSpace(s));
            }

            Assert.NotEqual(invalidTitle, inUseTitle);
            Assert.NotEqual(invalidText, inUseText);

            // The message must name the key the user tried, so it is actionable.
            Assert.Contains("D1", invalidText);
            Assert.Contains("F5", inUseText);
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// v1.0.5: the hotkey rollback-failure bubble needs both languages too.
    /// </summary>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void HotkeyRollbackFailed_IsTranslated(string language)
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = language;

            var title = LocalizationService.Get("bubble.hotkey_rollback_failed_title");
            var text = LocalizationService.Get("bubble.hotkey_rollback_failed", "Ctrl+Alt", "F5", "Ctrl+Alt", "S");

            Assert.DoesNotContain("bubble.hotkey_rollback", title);
            Assert.DoesNotContain("bubble.hotkey_rollback", text);
            Assert.Contains("F5", text); // new key
            Assert.Contains("S", text);  // old key
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }

    /// <summary>
    /// QA: Unsupported language code (e.g., "fr-FR") should fall back to the default
    /// language (zh-CN), NOT return the raw key string. Without this fallback, manually
    /// editing config.json with an unsupported language would cause ALL UI text to show
    /// as raw keys (e.g., "menu.exit" instead of "退出"), rendering the app unusable.
    /// </summary>
    [Fact]
    public void Get_UnsupportedLanguage_FallsBackToZhCn()
    {
        var saved = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.CurrentLanguage = "fr-FR";
            // Should fall back to zh-CN (default), not return the raw key
            Assert.Equal("退出", LocalizationService.Get("menu.exit"));
            Assert.Equal("设置...", LocalizationService.Get("menu.settings"));
        }
        finally
        {
            LocalizationService.CurrentLanguage = saved;
        }
    }
}
