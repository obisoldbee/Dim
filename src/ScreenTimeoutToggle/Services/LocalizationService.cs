namespace OBDim.Services;

/// <summary>
/// Lightweight internationalization service using in-memory dictionaries.
/// Supports zh-CN (default) and en-US. No .resx files needed for this small project.
/// </summary>
public static class LocalizationService
{
    /// <summary>
    /// The currently active language code. Set by TrayApp at startup from config.
    /// </summary>
    public static string CurrentLanguage { get; set; } = "zh-CN";

    /// <summary>
    /// All translation dictionaries, keyed by language code.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        ["zh-CN"] = new()
        {
            // Menu
            ["menu.switch_to_away"] = "切换到离开",
            ["menu.switch_to_work"] = "切换到工作",
            ["menu.settings"] = "设置...",
            ["menu.exit"] = "退出",

            // Bubble notifications
            ["bubble.mode_changed_title"] = "模式已切换",
            ["bubble.mode_changed"] = "已切换到{0}模式",
            ["bubble.switch_failed_title"] = "切换失败",
            ["bubble.hotkey_unavailable_title"] = "热键不可用",
            ["bubble.hotkey_unavailable"] = "热键 {0}+{1} 无法注册。",
            ["bubble.hotkey_change_failed_title"] = "热键修改失败",
            ["bubble.hotkey_change_failed"] = "热键 {0}+{1} 已被占用，已恢复之前的设置。",
            ["bubble.hotkey_rollback_failed_title"] = "热键恢复失败",
            ["bubble.hotkey_rollback_failed"] = "新热键 {0}+{1} 不可用，且旧热键 {2}+{3} 也无法重新注册。请在设置中重新选择热键，或重启应用。",
            ["bubble.language_changed_title"] = "语言已切换",
            ["bubble.language_changed"] = "语言已切换为{0}，部分界面将在重启后完全生效。",

            // Tooltip (tray icon hover)
            ["tooltip.work"] = "工作 · 电源 {0} / 电池 {1}",
            ["tooltip.away"] = "离开 · 电源 {0} / 电池 {1}",
            ["tooltip.unknown"] = "未知 · 电源 {0} / 电池 {1}",

            // Common values
            ["common.never"] = "从不",
            ["common.minutes"] = "{0}分钟",

            // Mode display names
            ["mode.work"] = "工作",
            ["mode.away"] = "离开",
            ["mode.unknown"] = "未知",

            // Settings form
            ["settings.title"] = "OB Dim — 设置",
            ["settings.work_mode"] = "工作模式（分钟，0 = 从不）",
            ["settings.away_mode"] = "离开模式（分钟，0 = 从不）",
            ["settings.plugged"] = "电源",
            ["settings.battery"] = "电池",
            ["settings.hotkey_label"] = "热键（点击输入框，然后按键）",
            ["settings.win_note"] = "不支持 Win 键",
            ["settings.autostart"] = "开机自启动",
            ["settings.language"] = "语言",
            ["settings.ok"] = "确定",
            ["settings.cancel"] = "取消",
            ["settings.large_value_warning"] = "确定吗？这超过 3 小时了。",

            // Hotkey validation
            ["hotkey.invalid_title"] = "无效热键",
            ["hotkey.invalid_key"] = "键 '{0}' 不是有效的热键。请按字母、数字或功能键。",
            ["hotkey.single_key_warning_title"] = "警告",
            ["hotkey.single_key_warning"] = "单键热键可能与打字冲突。确定使用？",

            // Error messages
            ["error.startup_failed"] = "OB Dim 启动失败：{0}",
            ["error.already_running"] = "OB Dim 已经在运行。",
            ["error.app_title"] = "OB Dim",

            // powercfg failure messages (v1.0.5) — keyed by PowerConfigErrorKind.
            // Each message must stay actionable: tell the user what to check next.
            ["error.powercfg_timeout"] = "系统电源设置响应超时。请检查安全软件是否拦截了 powercfg.exe，或系统是否处于高负载 / 省电模式，然后重试。",
            ["error.powercfg_invocation_failed"] = "无法调用系统电源设置工具 powercfg.exe。请检查安全软件是否拦截了该程序。",
            ["error.powercfg_rejected"] = "系统拒绝修改电源设置，通常是权限不足或组策略锁定了电源方案。请重试，或联系系统管理员。",
            ["error.powercfg_parse_failed"] = "无法解析系统电源设置的返回值。请重启应用后重试；若持续出现，请回传日志排查。",

            // Language display names (always shown in their own script)
            ["language.zh_cn"] = "中文",
            ["language.en_us"] = "English",
        },

        ["en-US"] = new()
        {
            // Menu
            ["menu.switch_to_away"] = "Switch to Away",
            ["menu.switch_to_work"] = "Switch to Work",
            ["menu.settings"] = "Settings...",
            ["menu.exit"] = "Exit",

            // Bubble notifications
            ["bubble.mode_changed_title"] = "Mode Changed",
            ["bubble.mode_changed"] = "Switched to {0} mode",
            ["bubble.switch_failed_title"] = "Switch Failed",
            ["bubble.hotkey_unavailable_title"] = "Hotkey Unavailable",
            ["bubble.hotkey_unavailable"] = "Hotkey {0}+{1} could not be registered.",
            ["bubble.hotkey_change_failed_title"] = "Hotkey Change Failed",
            ["bubble.hotkey_change_failed"] = "Hotkey {0}+{1} is in use. Reverted to previous.",
            ["bubble.hotkey_rollback_failed_title"] = "Hotkey Rollback Failed",
            ["bubble.hotkey_rollback_failed"] = "New hotkey {0}+{1} is unavailable, and the previous hotkey {2}+{3} could not be re-registered. Please choose a different hotkey in Settings or restart the app.",
            ["bubble.language_changed_title"] = "Language Changed",
            ["bubble.language_changed"] = "Language changed to {0}. Some UI will fully apply after restart.",

            // Tooltip (tray icon hover)
            ["tooltip.work"] = "Work · AC {0} / DC {1}",
            ["tooltip.away"] = "Away · AC {0} / DC {1}",
            ["tooltip.unknown"] = "Unknown · AC {0} / DC {1}",

            // Common values
            ["common.never"] = "Never",
            ["common.minutes"] = "{0}min",

            // Mode display names
            ["mode.work"] = "Work",
            ["mode.away"] = "Away",
            ["mode.unknown"] = "Unknown",

            // Settings form
            ["settings.title"] = "OB Dim — Settings",
            ["settings.work_mode"] = "Work mode (minutes, 0 = never)",
            ["settings.away_mode"] = "Away mode (minutes, 0 = never)",
            ["settings.plugged"] = "Plugged",
            ["settings.battery"] = "Battery",
            ["settings.hotkey_label"] = "Hotkey (click box, then press keys)",
            ["settings.win_note"] = "Win key not supported",
            ["settings.autostart"] = "Start with Windows",
            ["settings.language"] = "Language",
            ["settings.ok"] = "OK",
            ["settings.cancel"] = "Cancel",
            ["settings.large_value_warning"] = "Are you sure? This is more than 3 hours.",

            // Hotkey validation
            ["hotkey.invalid_title"] = "Invalid Hotkey",
            ["hotkey.invalid_key"] = "Key '{0}' is not a valid hotkey key. Please press a letter, digit, or function key.",
            ["hotkey.single_key_warning_title"] = "Warning",
            ["hotkey.single_key_warning"] = "Single-key hotkeys may conflict with typing. Use anyway?",

            // Error messages
            ["error.startup_failed"] = "OB Dim failed to start: {0}",
            ["error.already_running"] = "OB Dim is already running.",
            ["error.app_title"] = "OB Dim",

            // powercfg failure messages (v1.0.5) — keyed by PowerConfigErrorKind.
            // Each message must stay actionable: tell the user what to check next.
            ["error.powercfg_timeout"] = "The system power settings timed out. Check whether security software is blocking powercfg.exe, or whether the system is under heavy load / in power saving mode, then try again.",
            ["error.powercfg_invocation_failed"] = "Could not start the system power tool powercfg.exe. Check whether security software is blocking it.",
            ["error.powercfg_rejected"] = "The system rejected the power setting change — usually insufficient permissions or a group policy that locks the power plan. Try again, or contact your system administrator.",
            ["error.powercfg_parse_failed"] = "Could not read the system power settings output. Restart the app and try again; if it keeps happening, send us the log for diagnosis.",

            // Language display names (always shown in their own script)
            ["language.zh_cn"] = "中文",
            ["language.en_us"] = "English",
        }
    };

    /// <summary>
    /// Returns the localized string for the given key in the current language.
    /// If the current language is unsupported, falls back to zh-CN (default).
    /// If the key is not found in any language, returns the key itself as a fallback.
    /// </summary>
    /// <param name="key">The translation key (e.g., "menu.exit").</param>
    /// <returns>The localized string, or the key itself if not found.</returns>
    public static string Get(string key)
    {
        // Try current language first
        if (Translations.TryGetValue(CurrentLanguage, out var lang) &&
            lang.TryGetValue(key, out var value))
        {
            return value;
        }
        // Fallback to default language (zh-CN) if current language is unsupported
        if (CurrentLanguage != "zh-CN" &&
            Translations.TryGetValue("zh-CN", out var defaultLang) &&
            defaultLang.TryGetValue(key, out var defaultValue))
        {
            return defaultValue;
        }
        return key; // Last resort: raw key for genuinely missing keys
    }

    /// <summary>
    /// Returns the localized string for the given key, formatted with the provided arguments.
    /// Uses <see cref="string.Format(string, object[])"/> for substitution.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted localized string.</returns>
    public static string Get(string key, params object[] args)
    {
        return string.Format(Get(key), args);
    }

    /// <summary>Language codes this build ships translations for.</summary>
    public static IReadOnlyCollection<string> SupportedLanguages => Translations.Keys.ToList();

    /// <summary>
    /// Every translation key defined in the reference language (zh-CN).
    /// Used to verify no language is missing a key.
    /// </summary>
    public static IReadOnlyCollection<string> AllKeys => Translations["zh-CN"].Keys.ToList();

    /// <summary>
    /// Returns true when the given language defines <paramref name="key"/>.
    /// Lets a test prove that every key exists in every supported language — a missing
    /// key silently degrades to the raw key name (or English) in the UI.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <param name="languageCode">Language code, e.g. "zh-CN".</param>
    /// <returns>True if the language has an entry for the key.</returns>
    public static bool HasKey(string key, string languageCode) =>
        Translations.TryGetValue(languageCode, out var lang) && lang.ContainsKey(key);

    /// <summary>
    /// Returns a localized, user-facing description for a powercfg failure category.
    /// Raw exception messages are English and full of internal detail, so the UI maps
    /// <see cref="PowerConfigErrorKind"/> to an actionable localized sentence instead.
    /// </summary>
    /// <param name="kind">The failure category carried by the exception.</param>
    /// <param name="fallbackMessage">
    /// Used when the category is <see cref="PowerConfigErrorKind.Unknown"/> — typically the
    /// exception message, which is fine for the log but shown as a last resort in the UI.
    /// </param>
    /// <returns>A localized message, or <paramref name="fallbackMessage"/> for unknown kinds.</returns>
    public static string GetPowerConfigError(PowerConfigErrorKind kind, string fallbackMessage)
    {
        return kind switch
        {
            PowerConfigErrorKind.Timeout => Get("error.powercfg_timeout"),
            PowerConfigErrorKind.InvocationFailed => Get("error.powercfg_invocation_failed"),
            PowerConfigErrorKind.NonZeroExit => Get("error.powercfg_rejected"),
            PowerConfigErrorKind.ParseFailed => Get("error.powercfg_parse_failed"),
            _ => fallbackMessage
        };
    }

    /// <summary>
    /// Gets the display name for a language code (e.g., "zh-CN" → "中文").
    /// </summary>
    /// <param name="languageCode">The language code ("zh-CN" or "en-US").</param>
    /// <returns>The display name, or the code itself if unknown.</returns>
    public static string GetLanguageDisplayName(string languageCode)
    {
        return languageCode switch
        {
            "zh-CN" => Get("language.zh_cn"),
            "en-US" => Get("language.en_us"),
            _ => languageCode
        };
    }
}
