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
