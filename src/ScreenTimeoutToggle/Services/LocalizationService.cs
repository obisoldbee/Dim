namespace OBDim.Services;

/// <summary>
/// Lightweight internationalization service using in-memory dictionaries.
/// Supports zh-CN (default) and en-US. No .resx files needed for this small project.
/// </summary>
public static class LocalizationService
{
    private static volatile string _currentLanguage = "zh-CN";

    /// <summary>
    /// The currently active language code. Set by TrayApp at startup from config.
    /// </summary>
    /// <remarks>
    /// v1.0.7: the switch happens on the UI thread while background threads (powercfg
    /// continuations, hotkey handlers) may be formatting a balloon, so the backing field
    /// is volatile — a plain static string may be cached in a register and never re-read.
    /// </remarks>
    public static string CurrentLanguage
    {
        get => _currentLanguage;
        set => _currentLanguage = value ?? "zh-CN";
    }

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
            ["bubble.hotkey_change_failed_title"] = "热键已被占用",
            ["bubble.hotkey_change_failed"] = "热键 {0}+{1} 已被其它程序占用，已恢复之前的设置。",
            ["bubble.hotkey_change_invalid_title"] = "热键无效",
            ["bubble.hotkey_change_invalid"] = "热键 {0}+{1} 不是有效的按键，已恢复之前的设置。请在设置中改用字母、数字、功能键或方向键等。",
            ["bubble.hotkey_rollback_failed_title"] = "热键恢复失败",
            ["bubble.hotkey_rollback_failed"] = "新热键 {0}+{1} 不可用，且旧热键 {2}+{3} 也无法重新注册。请在设置中重新选择热键，或重启应用。",
            ["bubble.language_changed_title"] = "语言已切换",
            ["bubble.language_changed"] = "语言已切换为{0}，部分界面将在重启后完全生效。",

            // v1.0.7: config persistence failures are no longer silent
            ["bubble.save_failed_title"] = "设置未能保存",
            ["bubble.save_failed"] = "本次修改已经生效，但写入配置文件失败（磁盘已满或文件被占用）。重启后可能回到旧设置。",
            ["bubble.config_reset_title"] = "设置已重置",
            ["bubble.config_reset"] = "配置文件无法解析，已自动备份并恢复默认设置。请重新设置超时值与热键。",

            // Tooltip (tray icon hover)
            ["tooltip.work"] = "工作 · 电源 {0} / 电池 {1}",
            ["tooltip.away"] = "离开 · 电源 {0} / 电池 {1}",
            ["tooltip.unknown"] = "未知 · 电源 {0} / 电池 {1}",
            // v1.0.7: shown while the startup powercfg read is still in flight. Better
            // "don't know yet" than a number the system is not running.
            ["tooltip.detecting"] = "正在读取系统当前设置…",
            // v1.0.8: shown when the startup read already failed. Nothing is in flight;
            // saying "reading…" forever would be a lie.
            ["tooltip.read_failed"] = "读取系统设置失败 · 详见日志",

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

            // ---- Monitoring: quota & memory panel (2026-09) ----
            ["menu.monitor"] = "额度与内存…",
            ["monitor.title"] = "OB Dim — 额度与内存",
            ["monitor.tab_quota"] = "额度",
            ["monitor.tab_memory"] = "内存",
            ["monitor.refresh_all"] = "全部刷新",
            ["monitor.settings"] = "监控设置",
            ["monitor.col_window"] = "窗口",
            ["monitor.col_quota"] = "额度",
            ["monitor.col_reset"] = "重置时间",
            ["monitor.provider.codex"] = "Codex",
            ["monitor.provider.minimax"] = "MiniMax Token Plan",
            ["monitor.provider.ark"] = "方舟",
            ["monitor.window.primary"] = "主窗口",
            ["monitor.window.secondary"] = "次窗口",
            ["monitor.window.interval"] = "当前时段",
            ["monitor.window.weekly"] = "本周",
            ["monitor.window.monthly"] = "本月",
            ["monitor.window.session"] = "会话",
            ["monitor.window.5h"] = "5 小时",
            ["monitor.window.daily"] = "每日",
            ["monitor.window.individual_limit"] = "个人限额",
            ["monitor.state_disabled"] = "未启用",
            ["monitor.state_refreshing"] = "刷新中…",
            ["monitor.state_ok"] = "有效",
            ["monitor.state_partial"] = "部分有效",
            ["monitor.state_error"] = "查询失败",
            ["monitor.state_stale"] = "数据过期",
            ["monitor.state_no_data_yet"] = "尚未获取数据",
            ["monitor.state_disabled_hint"] = "在设置页开启后开始查询",
            ["monitor.paused_short"] = "自动刷新已暂停",
            ["monitor.updated"] = "更新于 {0}",
            ["monitor.percent_remaining"] = "剩余 {0}%",
            ["monitor.reset_suffix"] = "重置",
            ["monitor.badge.reset_credit"] = "重置权益",
            ["monitor.open_task_manager"] = "打开任务管理器",
            ["monitor.settings.save"] = "保存",
            ["monitor.saved"] = "已保存",
            ["monitor.hotkey_hint"] = "快捷键：{0} 唤起/收起面板 · Tab 或 ←/→ 切换额度与内存 · 1/2/3 直达页面 · Esc 关闭",
            ["monitor.last_success"] = "最近成功 {0}",
            ["monitor.paused"] = "已暂停自动刷新（登录或修正配置后可手动重试）",
            ["monitor.not_returned"] = "未返回额度",
            ["monitor.no_subscription"] = "无订阅",
            ["monitor.bucket_error"] = "此套餐查询失败，详见日志",
            ["monitor.percent_used"] = "已用 {0}% · 剩余 {1}%",
            ["monitor.percent_out_of_range"] = "数值异常（{0}%）",
            ["monitor.not_provided"] = "未提供",
            ["monitor.counts"] = "{0}/{1} 次",
            ["monitor.reset_pending"] = "待刷新确认",
            ["monitor.reset_unknown"] = "未知",
            ["monitor.reset_credits"] = "重置权益：可用 {0} 张",
            ["monitor.reset_credits_count_only"] = "明细未返回",
            ["monitor.reset_credits_empty_details"] = "无可用明细",
            ["monitor.reset_credit_expires"] = "至 {0} 到期",
            ["monitor.memory_physical"] = "物理内存",
            ["monitor.memory_used_of"] = "已用 {0} / {1}",
            ["monitor.memory_available"] = "可用 {0}",
            ["monitor.memory_commit"] = "已提交",
            ["monitor.memory_commit_values"] = "{0} / {1}（提交内存 ≠ 物理 RAM 使用量）",
            ["monitor.memory_low_signal"] = "系统低内存信号",
            ["monitor.low_triggered"] = "触发",
            ["monitor.low_not_triggered"] = "未触发",
            ["monitor.low_unknown"] = "未知",
            ["monitor.memory_sampled_label"] = "采样时间",
            ["monitor.memory_sampled"] = "{0}",
            ["monitor.memory_state_label"] = "状态",
            ["monitor.memory_disabled"] = "内存监控已关闭",
            ["monitor.memory_no_data"] = "暂无采样数据",
            ["monitor.memory_error"] = "采样失败：{0}",
            ["monitor.memory_stale"] = "数据过期（超过 30 秒无新样本）",
            ["monitor.memory_live"] = "正常",
            ["monitor.trend_title"] = "最近一小时趋势",
            ["monitor.trend_physical"] = "物理可用",
            ["monitor.trend_commit"] = "已提交",
            ["monitor.trend_no_data"] = "暂无趋势数据",
            ["monitor.error.cli_not_installed"] = "未找到 CLI，请安装或在监控设置中指定路径",
            ["monitor.error.unsupported_entry"] = "无法安全启动该 CLI 入口，请改用官方可执行文件",
            ["monitor.error.not_signed_in"] = "需要登录：请用对应 CLI 完成登录后手动重试",
            ["monitor.error.api_error"] = "服务返回错误",
            ["monitor.error.timeout"] = "查询超时",
            ["monitor.error.output_limit"] = "输出超出上限",
            ["monitor.error.parse_failed"] = "返回数据无法解析",
            ["monitor.error.cancelled"] = "已取消",
            ["monitor.error.execution_failed"] = "查询失败",
            ["monitor.settings.title"] = "OB Dim — 监控设置",
            ["monitor.settings.memory"] = "内存监控（每 5 秒采样）",
            ["monitor.settings.reminders"] = "额度提醒（剩余 ≤20% / ≤5% 各提醒一次，默认关闭）",
            ["monitor.settings.cli_path"] = "CLI 路径（可选，留空自动查找）",
            ["monitor.settings.popover_hotkey"] = "弹窗热键（点击输入框后按键）",
            ["monitor.settings.left_click"] = "左键单击托盘打开面板（关闭 = 左键切换工作/离开）",
            ["monitor.settings.group_screen"] = "息屏模式",
            ["monitor.settings.group_hotkeys"] = "热键",
            ["monitor.settings.group_general"] = "通用",
            ["monitor.settings.group_monitoring"] = "监控",
            ["monitor.memory.stat_physical"] = "物理内存",
            ["monitor.memory.stat_used"] = "已用",
            ["monitor.memory.stat_available"] = "可用",
            ["monitor.memory.stat_commit"] = "已提交",
            ["monitor.memory.stat_commit_limit"] = "提交上限",
            ["monitor.memory.stat_low_signal"] = "低内存信号",
            ["monitor.settings.save_failed"] = "监控设置未能写入文件（磁盘已满或文件被占用）。本次修改仍会生效。",
            ["monitor.bubble.reminder_title"] = "额度提醒",
            ["monitor.bubble.reminder"] = "{0} {1}：剩余 {2}%，将于 {3} 重置",
            ["monitor.bubble.unavailable_title"] = "监控不可用",
            ["monitor.bubble.unavailable"] = "监控模块未能启动，详见日志。",
            ["common.duration_minutes"] = "{0} 分钟",
            ["common.duration_hours"] = "{0} 小时",
            ["common.duration_days"] = "{0} 天",
            ["common.countdown_days_hours"] = "{0}天{1}小时",
            ["common.countdown_hours_minutes"] = "{0}小时{1}分",
            ["common.countdown_minutes"] = "{0} 分钟",
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
            ["bubble.hotkey_change_failed_title"] = "Hotkey Already In Use",
            ["bubble.hotkey_change_failed"] = "Hotkey {0}+{1} is already in use by another program. Reverted to previous.",
            ["bubble.hotkey_change_invalid_title"] = "Invalid Hotkey",
            ["bubble.hotkey_change_invalid"] = "Hotkey {0}+{1} is not a valid key. Reverted to previous. Please choose a letter, digit, function key or arrow key instead.",
            ["bubble.hotkey_rollback_failed_title"] = "Hotkey Rollback Failed",
            ["bubble.hotkey_rollback_failed"] = "New hotkey {0}+{1} is unavailable, and the previous hotkey {2}+{3} could not be re-registered. Please choose a different hotkey in Settings or restart the app.",
            ["bubble.language_changed_title"] = "Language Changed",
            ["bubble.language_changed"] = "Language changed to {0}. Some UI will fully apply after restart.",

            // v1.0.7: config persistence failures are no longer silent
            ["bubble.save_failed_title"] = "Settings Not Saved",
            ["bubble.save_failed"] = "The change took effect, but the config file could not be written (disk full or file in use). It may be lost after a restart.",
            ["bubble.config_reset_title"] = "Settings Reset",
            ["bubble.config_reset"] = "The config file could not be parsed. It has been backed up and default settings restored. Please re-enter your timeout values and hotkey.",

            // Tooltip (tray icon hover)
            ["tooltip.work"] = "Work · AC {0} / DC {1}",
            ["tooltip.away"] = "Away · AC {0} / DC {1}",
            ["tooltip.unknown"] = "Unknown · AC {0} / DC {1}",
            // v1.0.7: shown while the startup powercfg read is still in flight.
            ["tooltip.detecting"] = "Reading current system settings…",
            // v1.0.8: shown when the startup read already failed. Nothing is in flight.
            ["tooltip.read_failed"] = "Could not read system settings · see the log",

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

            // ---- Monitoring: quota & memory panel (2026-09) ----
            ["menu.monitor"] = "Usage && Memory…",
            ["monitor.title"] = "OB Dim — Usage & Memory",
            ["monitor.tab_quota"] = "Quota",
            ["monitor.tab_memory"] = "Memory",
            ["monitor.refresh_all"] = "Refresh All",
            ["monitor.settings"] = "Monitoring Settings",
            ["monitor.col_window"] = "Window",
            ["monitor.col_quota"] = "Quota",
            ["monitor.col_reset"] = "Resets",
            ["monitor.provider.codex"] = "Codex",
            ["monitor.provider.minimax"] = "MiniMax Token Plan",
            ["monitor.provider.ark"] = "Ark",
            ["monitor.window.primary"] = "Primary window",
            ["monitor.window.secondary"] = "Secondary window",
            ["monitor.window.interval"] = "Current interval",
            ["monitor.window.weekly"] = "This week",
            ["monitor.window.monthly"] = "This month",
            ["monitor.window.session"] = "Session",
            ["monitor.window.5h"] = "5 hours",
            ["monitor.window.daily"] = "Daily",
            ["monitor.window.individual_limit"] = "Individual limit",
            ["monitor.state_disabled"] = "Not enabled",
            ["monitor.state_refreshing"] = "Refreshing…",
            ["monitor.state_ok"] = "OK",
            ["monitor.state_partial"] = "Partially valid",
            ["monitor.state_error"] = "Query failed",
            ["monitor.state_stale"] = "Expired data",
            ["monitor.state_no_data_yet"] = "No data yet",
            ["monitor.state_disabled_hint"] = "Enable it on the settings page to start querying",
            ["monitor.paused_short"] = "Auto-refresh paused",
            ["monitor.updated"] = "Updated {0}",
            ["monitor.percent_remaining"] = "{0}% left",
            ["monitor.reset_suffix"] = "resets",
            ["monitor.badge.reset_credit"] = "reset credits",
            ["monitor.open_task_manager"] = "Open Task Manager",
            ["monitor.settings.save"] = "Save",
            ["monitor.saved"] = "Saved",
            ["monitor.hotkey_hint"] = "Hotkeys: {0} toggles this panel · Tab or ←/→ switches quota/memory · 1/2/3 jump to a page · Esc closes",
            ["monitor.last_success"] = "Last success {0}",
            ["monitor.paused"] = "Auto-refresh paused (retry manually after signing in or fixing the config)",
            ["monitor.not_returned"] = "No quota returned",
            ["monitor.no_subscription"] = "No subscription",
            ["monitor.bucket_error"] = "This plan's query failed — see the log",
            ["monitor.percent_used"] = "Used {0}% · {1}% left",
            ["monitor.percent_out_of_range"] = "Invalid value ({0}%)",
            ["monitor.not_provided"] = "Not provided",
            ["monitor.counts"] = "{0}/{1} calls",
            ["monitor.reset_pending"] = "Awaiting refresh",
            ["monitor.reset_unknown"] = "Unknown",
            ["monitor.reset_credits"] = "Reset credits: {0} available",
            ["monitor.reset_credits_count_only"] = "Details not returned",
            ["monitor.reset_credits_empty_details"] = "No details available",
            ["monitor.reset_credit_expires"] = "expires {0}",
            ["monitor.memory_physical"] = "Physical memory",
            ["monitor.memory_used_of"] = "{0} used / {1}",
            ["monitor.memory_available"] = "{0} available",
            ["monitor.memory_commit"] = "Committed",
            ["monitor.memory_commit_values"] = "{0} / {1} (commit is not physical RAM usage)",
            ["monitor.memory_low_signal"] = "System low-memory signal",
            ["monitor.low_triggered"] = "Triggered",
            ["monitor.low_not_triggered"] = "Not triggered",
            ["monitor.low_unknown"] = "Unknown",
            ["monitor.memory_sampled_label"] = "Sampled",
            ["monitor.memory_sampled"] = "{0}",
            ["monitor.memory_state_label"] = "State",
            ["monitor.memory_disabled"] = "Memory monitoring is off",
            ["monitor.memory_no_data"] = "No samples yet",
            ["monitor.memory_error"] = "Sampling failed: {0}",
            ["monitor.memory_stale"] = "Expired (no sample for over 30 seconds)",
            ["monitor.memory_live"] = "Live",
            ["monitor.trend_title"] = "Last-hour trend",
            ["monitor.trend_physical"] = "Physical available",
            ["monitor.trend_commit"] = "Committed",
            ["monitor.trend_no_data"] = "No trend data yet",
            ["monitor.error.cli_not_installed"] = "CLI not found — install it or set the path in monitoring settings",
            ["monitor.error.unsupported_entry"] = "This CLI entry cannot be launched safely — use the official executable",
            ["monitor.error.not_signed_in"] = "Sign-in required — sign in with the provider CLI, then retry manually",
            ["monitor.error.api_error"] = "The provider returned an error",
            ["monitor.error.timeout"] = "Query timed out",
            ["monitor.error.output_limit"] = "Output exceeded the limit",
            ["monitor.error.parse_failed"] = "Could not parse the response",
            ["monitor.error.cancelled"] = "Cancelled",
            ["monitor.error.execution_failed"] = "Query failed",
            ["monitor.settings.title"] = "OB Dim — Monitoring Settings",
            ["monitor.settings.memory"] = "Memory monitoring (samples every 5 seconds)",
            ["monitor.settings.reminders"] = "Quota reminders (once each at ≤20% / ≤5% left, off by default)",
            ["monitor.settings.cli_path"] = "CLI path (optional; leave empty to search automatically)",
            ["monitor.settings.popover_hotkey"] = "Popover hotkey (click the box, then press keys)",
            ["monitor.settings.left_click"] = "Left-click tray opens the panel (off = left-click toggles Work/Away)",
            ["monitor.settings.group_screen"] = "Screen timeout",
            ["monitor.settings.group_hotkeys"] = "Hotkeys",
            ["monitor.settings.group_general"] = "General",
            ["monitor.settings.group_monitoring"] = "Monitoring",
            ["monitor.memory.stat_physical"] = "Physical",
            ["monitor.memory.stat_used"] = "Used",
            ["monitor.memory.stat_available"] = "Available",
            ["monitor.memory.stat_commit"] = "Committed",
            ["monitor.memory.stat_commit_limit"] = "Commit limit",
            ["monitor.memory.stat_low_signal"] = "Low-memory signal",
            ["monitor.settings.save_failed"] = "Monitoring settings could not be written (disk full or file in use). The change still applies this session.",
            ["monitor.bubble.reminder_title"] = "Quota reminder",
            ["monitor.bubble.reminder"] = "{0} {1}: {2}% left, resets at {3}",
            ["monitor.bubble.unavailable_title"] = "Monitoring unavailable",
            ["monitor.bubble.unavailable"] = "The monitoring module failed to start — see the log.",
            ["common.duration_minutes"] = "{0} min",
            ["common.duration_hours"] = "{0} h",
            ["common.duration_days"] = "{0} d",
            ["common.countdown_days_hours"] = "{0}d {1}h",
            ["common.countdown_hours_minutes"] = "{0}h {1}m",
            ["common.countdown_minutes"] = "{0} min",
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

    /// <summary>
    /// Built once: <see cref="SupportedLanguages"/> is enumerated by tests on every run and
    /// every call used to allocate a fresh copy of the whole key list.
    /// </summary>
    private static readonly IReadOnlyCollection<string> CachedSupportedLanguages =
        Translations.Keys.ToArray();

    /// <summary>
    /// Built once, same reason as <see cref="CachedSupportedLanguages"/>.
    /// </summary>
    private static readonly IReadOnlyCollection<string> CachedAllKeys =
        Translations["zh-CN"].Keys.ToArray();

    /// <summary>Language codes this build ships translations for.</summary>
    public static IReadOnlyCollection<string> SupportedLanguages => CachedSupportedLanguages;

    /// <summary>
    /// Every translation key defined in the reference language (zh-CN).
    /// Used to verify no language is missing a key.
    /// </summary>
    public static IReadOnlyCollection<string> AllKeys => CachedAllKeys;

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
