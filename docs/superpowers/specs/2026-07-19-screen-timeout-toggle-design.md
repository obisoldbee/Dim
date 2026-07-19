# Screen Timeout Toggle — 设计文档

- **状态**：Draft（待用户审查）
- **日期**：2026-07-19
- **作者**：齐活林（Delivery Director，软件开发团队主理人）
- **背景调研**：`C:\Users\34538\Downloads\deep-research-report.md`（ChatGPT 整理）

---

## 1. 概述

一个 Windows 系统托盘小程序，让用户在 **Work 模式**（长时间不熄屏）和 **Away 模式**（短时间就熄屏）之间一键切换显示器的"多久不动后熄屏"超时时间。

切换通过调用 Windows 内置的 `powercfg` 命令修改当前电源方案的 `VIDEOIDLE`（即"关闭显示器"超时）实现，**不需要常驻干预**，切换后程序可继续驻留托盘但系统行为已经生效。

### 1.1 目标用户场景

- 家用 Windows 电脑
- 用户在工位工作时希望长时间不熄屏（甚至永不）
- 离开工位时希望快速熄屏（省电 + 隐私）
- 息屏与锁屏是两套独立机制，本工具只管息屏

---

## 2. 目标与非目标

### 2.1 目标（MVP）

1. ✅ 托盘左键 / 右键菜单 / 全局快捷键，三种方式一键切换 Work ⇄ Away
2. ✅ 切换时调用 `powercfg` 修改 `VIDEOIDLE` 的 AC + DC 两个值（仅插电/电池两路）
3. ✅ 托盘图标视觉区分 Work / Away / Unknown 三态
4. ✅ 配置窗口：4 个超时值（分钟，0=永不）+ 快捷键录入 + 开机自启复选框
5. ✅ 全局快捷键（默认 `Ctrl+Alt+S`，可自定义）
6. ✅ 开机自启（注册表 `HKCU\...\Run`）
7. ✅ 启动时读系统当前 `VIDEOIDLE` 匹配 Work/Away 配置，匹配则恢复模式，不匹配则 Unknown

### 2.2 非目标（MVP 不做）

- ❌ 不修改 `VIDEOCONLOCK`（锁屏后息屏时间），由系统默认机制处理
- ❌ 不做"智能判断"（空闲检测、进程监听等），留作未来扩展
- ❌ 不做自定义时间段（如"工作日 9-18 自动 Work"），留作未来扩展
- ❌ 不做电源计划切换（仅改当前计划的 `VIDEOIDLE`）
- ❌ 不做主窗口（程序只在托盘 + 配置弹窗）

### 2.3 未来可扩展方向（明确告知用户）

- 增加自定义时间段（工作日/时段自动切换）
- 增加智能判断（基于 `GetLastInputInfo` 空闲检测）
- 增加 `VIDEOCONLOCK` 控制
- 增加多电源计划切换

---

## 3. 关键决策汇总

| 维度 | 决定 | 理由 |
|------|------|------|
| 应用形态 | 纯原生 Windows 桌面托盘应用 | 轻量、低内存、原生体验 |
| 技术栈 | C# WinForms + .NET 8 | 内存最小（10-20MB）、原生 `NotifyIcon`、未来扩展友好 |
| 改写对象 | 仅 `VIDEOIDLE`（AC + DC 两路） | 用户明确：息屏与锁屏是两套机制，只管息屏 |
| 配置项 | 4 个超时值（分钟，0=永不）+ 快捷键 + 自启开关 | 全部用户可配置，无预设档位限制 |
| 默认值 | Work AC=0（永不）/ DC=30min；Away AC=1min / DC=1min | 贴合用户"大部分时间永不熄屏"的倾向 |
| 增强功能 | 全局快捷键 + 开机自启 | 用户选 A，体验完整 |
| 启动模式判断 | 读系统当前 `VIDEOIDLE` 匹配 Work/Away 配置 | 最贴近系统真实状态，避免与控制面板手动修改失同步 |
| 单位 | 分钟（UI 输入，×60 转秒存到 powercfg） | 贴合 Windows 控制面板习惯，息屏时间秒级精度无意义 |

---

## 4. 技术架构

### 4.1 整体架构

```
单进程 · 常驻托盘 · 无主窗口
   │
   ├── 启动
   │     ├── 单实例检查（Mutex）—— 已运行则激活旧实例，新进程退出
   │     ├── 读 config.json（缺失/损坏则用默认值重建）
   │     ├── 读系统当前 VIDEOIDLE
   │     ├── 匹配 Work/Away 配置 → 匹配则恢复模式，不匹配则 Unknown
   │     ├── 注册全局快捷键（失败则通知，不阻塞）
   │     └── 显示托盘图标
   │
   ├── 用户触发切换（托盘左键 / 右键菜单 / 全局快捷键）
   │      ↓
   ├── ModeService.SwitchTo(targetMode)
   │      ↓
   ├── PowerConfigService 调用 powercfg 写 VIDEOIDLE (AC+DC) + setactive 生效
   │      ↓
   ├── 更新托盘图标 + 气泡通知 + 持久化当前模式
   │
   ├── 用户点右键菜单"设置" → 弹出 SettingsForm
   │      ↓
   ├── 修改配置 → 保存 config.json
   │      ↓
   ├── 若当前模式对应的超时值被改了 → 立即重新应用
   │
   └── 退出（释放快捷键、释放 Mutex、清理托盘图标）
```

### 4.2 文件结构

```
ScreenTimeoutToggle/
├── Program.cs                  # 入口：单实例 Mutex + 启动 TrayApp
├── Models/
│   ├── AppMode.cs              # enum: Work, Away, Unknown
│   └── AppConfig.cs            # 配置类（4 超时值 + 快捷键修饰/键 + 自启开关）
├── Services/
│   ├── ConfigService.cs        # 读写 %AppData%\ScreenTimeoutToggle\config.json
│   ├── PowerConfigService.cs   # 封装 powercfg：GetActiveScheme / Query VIDEOIDLE / SetAcDcValueIndex / SetActive
│   ├── ModeService.cs          # 切换逻辑 + 启动时模式匹配
│   ├── HotkeyService.cs        # RegisterHotKey/UnregisterHotKey（WM_HOTKEY 消息泵）
│   └── AutoStartService.cs     # 注册表 HKCU\...\Run 读写
├── UI/
│   ├── TrayApp.cs              # NotifyIcon + ContextMenuStrip 主控制器（隐藏消息窗接收 WM_HOTKEY）
│   └── SettingsForm.cs         # 设置弹窗：4 个 NumericUpDown(0=永不) + 快捷键录入 + 自启复选框
├── assets/
│   ├── icon-work.ico           # Work 图标（蓝绿色调）
│   ├── icon-away.ico           # Away 图标（橙红色调）
│   └── icon-unknown.ico        # Unknown 图标（灰色调，启动时系统值不匹配任何模式时显示）
└── ScreenTimeoutToggle.csproj  # .NET 8, OutputType WinExe, 单文件发布
```

### 4.3 关键技术点

1. **无主窗体的消息泵**：WinForms 托盘应用通常需要一个隐藏的 `Form` 来接收 Windows 消息（特别是 `WM_HOTKEY` 全局快捷键）。`TrayApp` 内部创建一个隐藏窗体作为消息接收器。
2. **单实例**：使用 `System.Threading.Mutex`，名字固定为 `Global\ScreenTimeoutToggle_SingleInstance`。
3. **powercfg 调用**：通过 `System.Diagnostics.Process` 启动子进程，捕获 stdout/stderr 和退出码。命令拼接时所有参数都来自内部常量或配置文件，不接受外部输入（避免注入）。
4. **图标资源**：通过 `.csproj` 的 `<EmbeddedResource>` 或 `<None Include="assets\*.ico" CopyToOutputDirectory>` 嵌入。打包成单文件时一并打包。

---

## 5. 配置文件 Schema

文件路径：`%AppData%\ScreenTimeoutToggle\config.json`

```json
{
  "version": 1,
  "work": {
    "acMinutes": 0,
    "dcMinutes": 30
  },
  "away": {
    "acMinutes": 1,
    "dcMinutes": 1
  },
  "hotkey": {
    "modifiers": "Ctrl+Alt",
    "key": "S"
  },
  "autoStart": true,
  "currentMode": "Work"
}
```

字段说明：

| 字段 | 类型 | 取值 | 说明 |
|------|------|------|------|
| `version` | int | 1 | 配置文件版本号，未来升级用 |
| `work.acMinutes` | int | 0 ~ 99999 | Work 模式插电熄屏分钟数，0=永不 |
| `work.dcMinutes` | int | 0 ~ 99999 | Work 模式电池熄屏分钟数，0=永不 |
| `away.acMinutes` | int | 0 ~ 99999 | Away 模式插电熄屏分钟数 |
| `away.dcMinutes` | int | 0 ~ 99999 | Away 模式电池熄屏分钟数 |
| `hotkey.modifiers` | string | `None`/`Alt`/`Ctrl`/`Shift`/`Win` 组合，`+` 连接 | 修饰键 |
| `hotkey.key` | string | 单字符或键名（如 `S`, `F5`） | 主键 |
| `autoStart` | bool | true/false | 开机自启 |
| `currentMode` | string | `Work`/`Away`/`Unknown` | 上次模式，用于持久化（启动时仍以系统真实值为准） |

**降级策略**：
- 文件缺失 → 用默认值创建
- 文件存在但 JSON 损坏 → 备份为 `config.json.bak.{timestamp}`，用默认值创建
- 字段缺失 → 用该字段的默认值补全，不报错
- 字段类型错误 → 用默认值覆盖，记录日志

---

## 6. 核心数据流

### 6.1 切换到 Away 模式（以默认值为例）

```
1. 用户点托盘左键（或按 Ctrl+Alt+S）
2. TrayApp.OnClick / OnHotKey → ModeService.SwitchTo(Away)
3. ModeService 调 PowerConfigService.ApplyTimeouts(acMinutes=1, dcMinutes=1)
4. PowerConfigService 执行：
     powercfg /setacvalueindex <scheme> SUB_VIDEO VIDEOIDLE 60
     powercfg /setdcvalueindex <scheme> SUB_VIDEO VIDEOIDLE 60
     powercfg /setactive <scheme>
5. ModeService 更新 CurrentMode = Away，写 config.json
6. TrayApp 更新托盘图标为 icon-away.ico，tooltip = "Away · AC 1min / DC 1min"
7. 气泡通知 800ms："已切到 Away 模式"
```

### 6.2 启动时模式匹配

```
1. Program.Main 启动 → ConfigService.Load() 读 config.json
2. PowerConfigService.GetCurrentVideoIdle() → 返回 (acSeconds, dcSeconds)
3. ModeService.MatchMode(acSeconds, dcSeconds):
     - 若 (acSeconds == work.acMinutes*60) && (dcSeconds == work.dcMinutes*60) → Work
     - 若 (acSeconds == away.acMinutes*60) && (dcSeconds == away.dcMinutes*60) → Away
     - 否则 → Unknown
4. TrayApp 按 MatchMode 结果显示对应图标
   - Unknown 时图标用灰色版本，tooltip = "未识别 · 当前 AC {x}min / DC {y}min"
   - Unknown 时不主动改系统，等用户主动切换
```

### 6.3 设置弹窗保存

```
1. 用户在 SettingsForm 修改 4 个数字 + 快捷键 + 自启
2. 点"确定"
3. ConfigService.Save(newConfig)
4. 若 autoStart 变了 → AutoStartService.Apply(newAutoStart)
5. 若 hotkey 变了 → HotkeyService.Unregister() + HotkeyService.Register(newHotkey)
6. 若当前模式对应的超时值变了 → ModeService.ReapplyCurrentMode()
   （例如当前是 Work，用户改了 work.acMinutes，立即重新 ApplyTimeouts）
7. 关闭弹窗，更新托盘 tooltip
```

---

## 7. 错误处理

| 场景 | 处理 |
|------|------|
| `powercfg` 退出码非 0 | 气泡通知"切换失败：{stderr}"，托盘图标叠加警告角标，不改变 CurrentMode |
| config.json 缺失 | 用默认值创建，正常启动 |
| config.json JSON 损坏 | 备份为 `config.json.bak.{timestamp}`，用默认值创建，正常启动 |
| config.json 字段缺失/类型错误 | 用该字段默认值补全，正常启动 |
| 快捷键已被其他程序占用 | `RegisterHotKey` 失败 → 通知"快捷键 {X} 注册失败，已被占用"，不阻塞启动，托盘仍可用 |
| 单实例检测到已运行 | 激活已有托盘图标（可选：发气泡"已在运行"），新进程退出 |
| UAC / 权限问题 | powercfg 改当前用户计划通常不需要管理员（实测可行），如遇失败通知用户"请以管理员身份运行" |
| 找不到 powercfg.exe | 极端情况，通知用户"系统缺失 powercfg，请联系管理员" |

---

## 8. 测试策略（QA 严过关负责）

### 8.1 单元测试（xUnit + Moq）

| 模块 | 测试点 |
|------|--------|
| `ConfigService` | 读写 JSON 往返、默认值降级、损坏文件备份恢复、字段缺失补全、字段类型错误覆盖 |
| `PowerConfigService` | mock `Process` 输出，验证 GUID 解析（`powercfg /getactivescheme` 输出格式）、AC/DC 值解析（`powercfg /query` 输出格式）、`setacvalueindex`/`setdcvalueindex`/`setactive` 命令拼接、退出码非 0 抛异常 |
| `ModeService` | 状态机切换（Work→Away→Work）、启动模式匹配（三种情况：匹配 Work/匹配 Away/Unknown）、ReapplyCurrentMode 触发条件 |
| `HotkeyService` | 修饰键字符串解析、注册成功/失败路径 |
| `AutoStartService` | 注册表读写（mock `Registry`） |

### 8.2 集成测试（手动 / QA 阶段）

- 真实调用 `powercfg /query` 验证 `VIDEOIDLE` 在切换前后值的变化
- 切到 Away 后等 2 分钟，确认显示器是否真的熄屏（手动观察）
- 切到 Work（AC=0）后长时间不动，确认不熄屏
- 托盘图标在 Work/Away/Unknown 三态下的视觉区分
- 气泡通知是否正常弹出
- 全局快捷键在焦点不在程序上时是否生效（如在浏览器中按 Ctrl+Alt+S）
- 开机自启：重启系统后程序是否自动启动到托盘
- 单实例：双击 EXE 两次，确认只有一个进程

### 8.3 测试约束

- 单元测试不依赖真实 `powercfg` 调用，避免污染系统电源设置
- 集成测试在隔离环境（VM 或独立测试机）运行，测试后恢复原始 `VIDEOIDLE` 值
- 单元测试覆盖率目标：核心 Services 模块 ≥ 80%

---

## 9. 发布与打包

### 9.1 单文件 EXE

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- `--self-contained false`：依赖用户机器的 .NET 8 Runtime（如无则需安装）
- 若要完全免依赖：`--self-contained true`（体积变大，~70MB）

**推荐**：`--self-contained false`，体积 <1MB，用户首次运行若无 .NET 8 会提示安装。

### 9.2 图标资源

- `icon-work.ico`：蓝绿色调，表示"工作中，长亮"
- `icon-away.ico`：橙红色调，表示"离开，将熄屏"
- `icon-unknown.ico`：灰色调，启动时系统当前 `VIDEOIDLE` 不匹配 Work/Away 任一配置时显示

---

## 10. 待明确事项（无）

所有关键决策点已在 brainstorming 阶段确认完毕，无遗留问题。

---

## 11. 下一步

1. ⏭️ 用户审查本 spec 文档
2. ⏭️ 审查通过后，调用 `writing-plans` skill 生成实现计划
3. ⏭️ 启动软件开发团队**快速模式**：TeamCreate → 工程师寇豆码实现全部代码 → QA 严过关验证
4. ⏭️ 交付：可运行的 EXE + 源码 + 测试报告
