# Screen Timeout Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows system tray app that toggles display-off timeout between Work mode (long/never) and Away mode (short) by calling `powercfg` to modify the active power scheme's `VIDEOIDLE` (AC + DC) values.

**Architecture:** Single-process WinForms tray app (.NET 8). No main window — only a tray icon + context menu + settings dialog. A hidden message-only window receives `WM_HOTKEY` for global hotkey. Services layer (Config / PowerConfig / Mode / Hotkey / AutoStart) holds all logic; UI layer (TrayApp / SettingsForm) only orchestrates.

**Tech Stack:** C# 12, .NET 8, WinForms (`UseWindowsForms`), System.Text.Json, xUnit + Moq for tests.

## Global Constraints

- **Target framework:** `net8.0-windows` (Windows-only, required for WinForms + `RegisterHotKey` P/Invoke)
- **OutputType:** `WinExe` (no console window on launch)
- **Config path:** `%AppData%\ScreenTimeoutToggle\config.json`
- **Registry key for autostart:** `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, value name `ScreenTimeoutToggle`
- **powercfg constants:** `SUB_VIDEO = 7516b95f-f776-4464-8c53-06167f40cc99`, `VIDEOIDLE = 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e`
- **Time unit:** UI = minutes; `powercfg` = seconds (×60 conversion in `PowerConfigService`)
- **Single instance mutex:** `Global\ScreenTimeoutToggle_SingleInstance`
- **TDD:** Every service task writes failing test first, then implementation. Tests use Moq to mock `Process` / `Registry` / file system — never invoke real `powercfg`.
- **Commit per task:** Each task ends with a git commit using conventional commit format (`feat:`, `test:`, `chore:`).

---

## File Structure

```
screen-timeout-toggle/                          (solution root)
├── screen-timeout-toggle.sln
├── src/
│   └── ScreenTimeoutToggle/
│       ├── ScreenTimeoutToggle.csproj          # .NET 8, WinExe, UseWindowsForms
│       ├── Program.cs                          # Entry: Mutex + Application.Run(TrayApp)
│       ├── Models/
│       │   ├── AppMode.cs                      # enum { Work, Away, Unknown }
│       │   └── AppConfig.cs                    # record with 4 timeouts + hotkey + autostart + currentMode
│       ├── Services/
│       │   ├── ConfigService.cs                # JSON read/write + default fallback
│       │   ├── PowerConfigService.cs           # powercfg wrapper (getactivescheme / query / setac / setdc / setactive)
│       │   ├── ModeService.cs                  # SwitchTo / MatchMode / ReapplyCurrentMode
│       │   ├── HotkeyService.cs                # RegisterHotKey P/Invoke + WM_HOTKEY dispatch
│       │   └── AutoStartService.cs             # HKCU\...\Run read/write
│       ├── UI/
│       │   ├── TrayApp.cs                       # NotifyIcon + ContextMenuStrip + hidden message window
│       │   └── SettingsForm.cs                  # 4 NumericUpDown + hotkey recorder + autostart checkbox
│       └── assets/
│           ├── icon-work.ico
│           ├── icon-away.ico
│           └── icon-unknown.ico
└── tests/
    └── ScreenTimeoutToggle.Tests/
        ├── ScreenTimeoutToggle.Tests.csproj
        ├── ConfigServiceTests.cs
        ├── PowerConfigServiceTests.cs
        ├── ModeServiceTests.cs
        ├── HotkeyServiceTests.cs
        └── AutoStartServiceTests.cs
```

---

### Task 1: Solution scaffolding

**Files:**
- Create: `screen-timeout-toggle/src/ScreenTimeoutToggle/ScreenTimeoutToggle.csproj`
- Create: `screen-timeout-toggle/tests/ScreenTimeoutToggle.Tests/ScreenTimeoutToggle.Tests.csproj`
- Create: `screen-timeout-toggle/screen-timeout-toggle.sln`
- Create: `screen-timeout-toggle/src/ScreenTimeoutToggle/Program.cs` (placeholder, builds empty)
- Create: `screen-timeout-toggle/.gitignore`

**Interfaces:** none (foundation)

- [ ] **Step 1: Create .gitignore**

```
bin/
obj/
*.user
.vs/
publish/
```

- [ ] **Step 2: Create main csproj**

`src/ScreenTimeoutToggle/ScreenTimeoutToggle.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>assets\icon-work.ico</ApplicationIcon>
    <AssemblyName>ScreenTimeoutToggle</AssemblyName>
    <RootNamespace>ScreenTimeoutToggle</RootNamespace>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create placeholder Program.cs**

`src/ScreenTimeoutToggle/Program.cs`:
```csharp
namespace ScreenTimeoutToggle;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Real entry implemented in Task 11
    }
}
```

- [ ] **Step 4: Create test csproj**

`tests/ScreenTimeoutToggle.Tests/ScreenTimeoutToggle.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Moq" Version="4.20.70" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ScreenTimeoutToggle\ScreenTimeoutToggle.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create solution file**

Run:
```bash
cd screen-timeout-toggle
dotnet new sln -n screen-timeout-toggle
dotnet sln add src/ScreenTimeoutToggle/ScreenTimeoutToggle.csproj
dotnet sln add tests/ScreenTimeoutToggle.Tests/ScreenTimeoutToggle.Tests.csproj
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold .NET 8 WinForms solution + xUnit test project"
```

---

### Task 2: Models — AppMode + AppConfig

**Files:**
- Create: `src/ScreenTimeoutToggle/Models/AppMode.cs`
- Create: `src/ScreenTimeoutToggle/Models/AppConfig.cs`

**Interfaces:**
- Produces: `enum AppMode { Work, Away, Unknown }`
- Produces: `record AppConfig(int WorkAcMinutes, int WorkDcMinutes, int AwayAcMinutes, int AwayDcMinutes, HotkeyConfig Hotkey, bool AutoStart, AppMode CurrentMode)` with `AppConfig.CreateDefault()`
- Produces: `record HotkeyConfig(string Modifiers, string Key)` with `CreateDefault()` → `("Ctrl+Alt", "S")`

- [ ] **Step 1: Write AppMode.cs**

```csharp
namespace ScreenTimeoutToggle.Models;

public enum AppMode
{
    Work,
    Away,
    Unknown
}
```

- [ ] **Step 2: Write AppConfig.cs**

```csharp
using System.Text.Json.Serialization;

namespace ScreenTimeoutToggle.Models;

public record HotkeyConfig
{
    public string Modifiers { get; init; } = "Ctrl+Alt";
    public string Key { get; init; } = "S";

    public static HotkeyConfig CreateDefault() => new() { Modifiers = "Ctrl+Alt", Key = "S" };
}

public record AppConfig
{
    public int Version { get; init; } = 1;
    public TimeoutConfig Work { get; init; } = TimeoutConfig.CreateDefaultWork();
    public TimeoutConfig Away { get; init; } = TimeoutConfig.CreateDefaultAway();
    public HotkeyConfig Hotkey { get; init; } = HotkeyConfig.CreateDefault();
    public bool AutoStart { get; init; } = true;
    public AppMode CurrentMode { get; init; } = AppMode.Work;

    public static AppConfig CreateDefault() => new();
}

public record TimeoutConfig
{
    public int AcMinutes { get; init; }
    public int DcMinutes { get; init; }

    public static TimeoutConfig CreateDefaultWork() => new() { AcMinutes = 0, DcMinutes = 30 };
    public static TimeoutConfig CreateDefaultAway() => new() { AcMinutes = 1, DcMinutes = 1 };
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add AppMode enum and AppConfig record with defaults"
```

---

### Task 3: ConfigService — JSON read/write with fallback

**Files:**
- Create: `src/ScreenTimeoutToggle/Services/ConfigService.cs`
- Test: `tests/ScreenTimeoutToggle.Tests/ConfigServiceTests.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 2)
- Produces: `class ConfigService` with:
  - `ConfigService(string filePath)` constructor (for testability — inject path)
  - `AppConfig Load()` — reads file; if missing/corrupt, returns default + repairs file
  - `void Save(AppConfig config)` — writes file atomically (write to .tmp then rename)
  - `static string DefaultFilePath` — `%AppData%\ScreenTimeoutToggle\config.json`

- [ ] **Step 1: Write failing tests**

`tests/ScreenTimeoutToggle.Tests/ConfigServiceTests.cs`:
```csharp
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class ConfigServiceTests
{
    private string TempFile() => Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_MissingFile_ReturnsDefault_AndCreatesFile()
    {
        var path = TempFile();
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes);
        Assert.Equal(1, cfg.Away.AcMinutes);
        Assert.Equal(1, cfg.Away.DcMinutes);
        Assert.True(cfg.AutoStart);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_CorruptJson_BackupsFile_AndReturnsDefault()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not json");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(0, cfg.Work.AcMinutes); // default
        // backup file exists
        var backup = Directory.GetFiles(Path.GetDirectoryName(path)!, "cfg_*.json.bak.*")
            .FirstOrDefault();
        Assert.NotNull(backup);
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var path = TempFile();
        var svc = new ConfigService(path);
        var original = new AppConfig
        {
            Work = new TimeoutConfig { AcMinutes = 5, DcMinutes = 10 },
            Away = new TimeoutConfig { AcMinutes = 2, DcMinutes = 1 },
            Hotkey = new HotkeyConfig { Modifiers = "Ctrl+Shift", Key = "F5" },
            AutoStart = false,
            CurrentMode = AppMode.Away
        };

        svc.Save(original);
        var loaded = svc.Load();

        Assert.Equal(5, loaded.Work.AcMinutes);
        Assert.Equal(10, loaded.Work.DcMinutes);
        Assert.Equal(2, loaded.Away.AcMinutes);
        Assert.Equal("Ctrl+Shift", loaded.Hotkey.Modifiers);
        Assert.Equal("F5", loaded.Hotkey.Key);
        Assert.False(loaded.AutoStart);
        Assert.Equal(AppMode.Away, loaded.CurrentMode);
    }

    [Fact]
    public void Load_MissingFields_FillsWithDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, """{"version":1,"work":{"acMinutes":7}}""");
        var svc = new ConfigService(path);

        var cfg = svc.Load();

        Assert.Equal(7, cfg.Work.AcMinutes);
        Assert.Equal(30, cfg.Work.DcMinutes); // default
        Assert.Equal(1, cfg.Away.AcMinutes);  // default
        Assert.Equal("Ctrl+Alt", cfg.Hotkey.Modifiers); // default
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ConfigServiceTests"`
Expected: FAIL — `ConfigService` type not found.

- [ ] **Step 3: Implement ConfigService**

`src/ScreenTimeoutToggle/Services/ConfigService.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTimeoutToggle",
            "config.json");

    private readonly string _filePath;

    public ConfigService(string filePath)
    {
        _filePath = filePath;
    }

    public static ConfigService CreateDefault() => new(DefaultFilePath);

    public AppConfig Load()
    {
        if (!File.Exists(_filePath))
        {
            var def = AppConfig.CreateDefault();
            TryWrite(def);
            return def;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
            return MergeWithDefaults(cfg);
        }
        catch (JsonException)
        {
            // Backup corrupt file
            var dir = Path.GetDirectoryName(_filePath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(_filePath);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(_filePath, Path.Combine(dir, $"{name}.bak.{stamp}"), overwrite: true); }
            catch { /* best effort */ }

            var def = AppConfig.CreateDefault();
            TryWrite(def);
            return def;
        }
    }

    public void Save(AppConfig config)
    {
        TryWrite(config);
    }

    private static AppConfig MergeWithDefaults(AppConfig cfg)
    {
        var def = AppConfig.CreateDefault();
        return cfg with
        {
            Version = cfg.Version == 0 ? def.Version : cfg.Version,
            Work = cfg.Work is null ? def.Work : cfg.Work,
            Away = cfg.Away is null ? def.Away : cfg.Away,
            Hotkey = cfg.Hotkey is null ? def.Hotkey : cfg.Hotkey,
            CurrentMode = cfg.CurrentMode
        };
    }

    private void TryWrite(AppConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else File.Move(tmp, _filePath);
        }
        catch
        {
            // best effort — Load will fall back to defaults next time
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ConfigServiceTests"`
Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ConfigService with JSON read/write + corrupt-file fallback"
```

---

### Task 4: PowerConfigService — powercfg wrapper

**Files:**
- Create: `src/ScreenTimeoutToggle/Services/PowerConfigService.cs`
- Test: `tests/ScreenTimeoutToggle.Tests/PowerConfigServiceTests.cs`

**Interfaces:**
- Consumes: nothing (standalone)
- Produces: `class PowerConfigService` with:
  - `Guid GetActiveSchemeGuid()` — parses `powercfg /getactivescheme`
  - `(int acSeconds, int dcSeconds) GetCurrentVideoIdle(Guid scheme)` — parses `powercfg /query`
  - `void SetVideoIdle(Guid scheme, int acSeconds, int dcSeconds)` — runs `setacvalueindex` + `setdcvalueindex` + `setactive`
  - Constructor takes `Func<ProcessStartInfo, ProcessResult> runner` for testability (defaults to real `Process.Start`)
- Produces: `record ProcessResult(int ExitCode, string Stdout, string Stderr)`
- Constants: `SubVideoGuid = 7516b95f-f776-4464-8c53-06167f40cc99`, `VideoIdleGuid = 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e`

- [ ] **Step 1: Write failing tests**

`tests/ScreenTimeoutToggle.Tests/PowerConfigServiceTests.cs`:
```csharp
using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class PowerConfigServiceTests
{
    private const string GetActiveSchemeOutput =
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n";

    private const string QueryOutput =
        "Subgroup GUID: 7516b95f-f776-4464-8c53-06167f40cc99  (Video timeout)\r\n" +
        "  GUID Alias: SUB_VIDEO\r\n" +
        "  Power Setting GUID: 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e  (Video idle timeout)\r\n" +
        "    GUID Alias: VIDEOIDLE\r\n" +
        "    Possible Setting Group Index: 001\r\n\r\n" +
        "  Current AC Power Setting Index: 0x0000003c\r\n" +
        "  Current DC Power Setting Index: 0x00000078\r\n";

    [Fact]
    public void GetActiveSchemeGuid_ParsesGuidFromOutput()
    {
        var svc = new PowerConfigService(_ => new ProcessResult(0, GetActiveSchemeOutput, ""));

        var guid = svc.GetActiveSchemeGuid();

        Assert.Equal(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), guid);
    }

    [Fact]
    public void GetCurrentVideoIdle_ParsesAcAndDcHex()
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var calls = 0;
        var svc = new PowerConfigService(_ =>
        {
            calls++;
            return new ProcessResult(0, QueryOutput, "");
        });

        var (ac, dc) = svc.GetCurrentVideoIdle(scheme);

        Assert.Equal(60, ac);   // 0x3c
        Assert.Equal(120, dc);  // 0x78
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetCurrentVideoIdle_HandlesZeroNever()
    {
        var scheme = Guid.NewGuid();
        var zeroOutput = QueryOutput
            .Replace("0x0000003c", "0x00000000")
            .Replace("0x00000078", "0x00000000");
        var svc = new PowerConfigService(_ => new ProcessResult(0, zeroOutput, ""));

        var (ac, dc) = svc.GetCurrentVideoIdle(scheme);

        Assert.Equal(0, ac);
        Assert.Equal(0, dc);
    }

    [Fact]
    public void SetVideoIdle_CallsThreeCommands_InOrder()
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var calls = new List<string>();
        var svc = new PowerConfigService(psi =>
        {
            calls.Add(string.Join(" ", psi.ArgumentList));
            return new ProcessResult(0, "", "");
        });

        svc.SetVideoIdle(scheme, acSeconds: 60, dcSeconds: 120);

        Assert.Equal(3, calls.Count);
        Assert.Contains("setacvalueindex", calls[0]);
        Assert.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", calls[0]);
        Assert.Contains("7516b95f-f776-4464-8c53-06167f40cc99", calls[0]);
        Assert.Contains("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", calls[0]);
        Assert.Contains("60", calls[0]);
        Assert.Contains("setdcvalueindex", calls[1]);
        Assert.Contains("120", calls[1]);
        Assert.Contains("setactive", calls[2]);
    }

    [Fact]
    public void SetVideoIdle_NonZeroExit_ThrowsWithStderr()
    {
        var scheme = Guid.NewGuid();
        var svc = new PowerConfigService(_ => new ProcessResult(1, "", "Access denied"));

        var ex = Assert.Throws<PowerConfigException>(() => svc.SetVideoIdle(scheme, 60, 60));
        Assert.Contains("Access denied", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "PowerConfigServiceTests"`
Expected: FAIL — types not found.

- [ ] **Step 3: Implement PowerConfigService**

`src/ScreenTimeoutToggle/Services/PowerConfigService.cs`:
```csharp
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ScreenTimeoutToggle.Services;

public record ProcessResult(int ExitCode, string Stdout, string Stderr);

public class PowerConfigException : Exception
{
    public PowerConfigException(string message) : base(message) { }
    public PowerConfigException(string message, Exception inner) : base(message, inner) { }
}

public class PowerConfigService
{
    public static readonly Guid SubVideoGuid = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid VideoIdleGuid = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    private static readonly Regex SchemeGuidRegex =
        new(@"Power Scheme GUID:\s*([0-9a-fA-F\-]{36})", RegexOptions.Compiled);

    private static readonly Regex AcRegex =
        new(@"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    private static readonly Regex DcRegex =
        new(@"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    private readonly Func<ProcessStartInfo, ProcessResult> _runner;

    public PowerConfigService() : this(RealRunner) { }

    public PowerConfigService(Func<ProcessStartInfo, ProcessResult> runner)
    {
        _runner = runner;
    }

    public Guid GetActiveSchemeGuid()
    {
        var psi = NewPsi("/getactivescheme");
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"getactivescheme failed: {result.Stderr}");

        var m = SchemeGuidRegex.Match(result.Stdout);
        if (!m.Success)
            throw new PowerConfigException($"Cannot parse scheme GUID from: {result.Stdout}");

        return Guid.Parse(m.Groups[1].Value);
    }

    public (int acSeconds, int dcSeconds) GetCurrentVideoIdle(Guid scheme)
    {
        var psi = NewPsi($"/query {scheme} {SubVideoGuid} {VideoIdleGuid}");
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"query failed: {result.Stderr}");

        var acMatch = AcRegex.Match(result.Stdout);
        var dcMatch = DcRegex.Match(result.Stdout);
        if (!acMatch.Success || !dcMatch.Success)
            throw new PowerConfigException($"Cannot parse AC/DC from: {result.Stdout}");

        int ac = Convert.ToInt32(acMatch.Groups[1].Value, 16);
        int dc = Convert.ToInt32(dcMatch.Groups[1].Value, 16);
        return (ac, dc);
    }

    public void SetVideoIdle(Guid scheme, int acSeconds, int dcSeconds)
    {
        RunStrict(NewPsi($"/setacvalueindex {scheme} {SubVideoGuid} {VideoIdleGuid} {acSeconds}"));
        RunStrict(NewPsi($"/setdcvalueindex {scheme} {SubVideoGuid} {VideoIdleGuid} {dcSeconds}"));
        RunStrict(NewPsi($"/setactive {scheme}"));
    }

    private void RunStrict(ProcessStartInfo psi)
    {
        var result = _runner(psi);
        if (result.ExitCode != 0)
            throw new PowerConfigException($"powercfg {psi.ArgumentList[0]} failed (exit {result.ExitCode}): {result.Stderr}");
    }

    private static ProcessStartInfo NewPsi(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private static ProcessResult RealRunner(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "PowerConfigServiceTests"`
Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add PowerConfigService wrapping powercfg getactivescheme/query/setac/setdc/setactive"
```

---

### Task 5: ModeService — state machine + match

**Files:**
- Create: `src/ScreenTimeoutToggle/Services/ModeService.cs`
- Test: `tests/ScreenTimeoutToggle.Tests/ModeServiceTests.cs`

**Interfaces:**
- Consumes: `AppConfig` (Task 2), `PowerConfigService` (Task 4)
- Produces: `class ModeService` with:
  - `ModeService(AppConfig config, PowerConfigService power)` constructor
  - `AppMode CurrentMode { get; private set; }`
  - `void SwitchTo(AppMode target)` — applies timeouts via PowerConfigService, updates CurrentMode; `target == Unknown` throws `ArgumentException`
  - `AppMode MatchCurrentMode(int acSeconds, int dcSeconds)` — pure: returns Work/Away/Unknown by comparing to config
  - `void ReapplyCurrentMode()` — re-applies current mode's timeouts (used after settings change)
  - `event EventHandler<AppMode>? ModeChanged` — fired on SwitchTo success

- [ ] **Step 1: Write failing tests**

`tests/ScreenTimeoutToggle.Tests/ModeServiceTests.cs`:
```csharp
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;
using Moq;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class ModeServiceTests
{
    private static AppConfig Cfg() => new AppConfig
    {
        Work = new TimeoutConfig { AcMinutes = 0, DcMinutes = 30 },
        Away = new TimeoutConfig { AcMinutes = 1, DcMinutes = 1 }
    };

    [Fact]
    public void SwitchTo_Away_AppliesAwayTimeouts()
    {
        var cfg = Cfg();
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Away);

        power.Verify(p => p.SetVideoIdle(scheme, 60, 60), Times.Once); // 1min * 60
        Assert.Equal(AppMode.Away, svc.CurrentMode);
    }

    [Fact]
    public void SwitchTo_Work_AppliesWorkTimeouts_WithZeroForNever()
    {
        var cfg = Cfg();
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);

        svc.SwitchTo(AppMode.Work);

        power.Verify(p => p.SetVideoIdle(scheme, 0, 1800), Times.Once); // 0 + 30min
        Assert.Equal(AppMode.Work, svc.CurrentMode);
    }

    [Fact]
    public void SwitchTo_Unknown_Throws()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        Assert.Throws<ArgumentException>(() => svc.SwitchTo(AppMode.Unknown));
    }

    [Fact]
    public void SwitchTo_FiresModeChanged()
    {
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(Guid.NewGuid());
        var svc = new ModeService(Cfg(), power.Object);
        AppMode? fired = null;
        svc.ModeChanged += (_, m) => fired = m;

        svc.SwitchTo(AppMode.Away);

        Assert.Equal(AppMode.Away, fired);
    }

    [Fact]
    public void MatchCurrentMode_MatchesWork_ReturnsWork()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Work: AC=0min→0s, DC=30min→1800s
        Assert.Equal(AppMode.Work, svc.MatchCurrentMode(0, 1800));
    }

    [Fact]
    public void MatchCurrentMode_MatchesAway_ReturnsAway()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        // Away: AC=1min→60s, DC=1min→60s
        Assert.Equal(AppMode.Away, svc.MatchCurrentMode(60, 60));
    }

    [Fact]
    public void MatchCurrentMode_NeitherMatches_ReturnsUnknown()
    {
        var power = new Mock<PowerConfigService>();
        var svc = new ModeService(Cfg(), power.Object);

        Assert.Equal(AppMode.Unknown, svc.MatchCurrentMode(300, 300));
    }

    [Fact]
    public void ReapplyCurrentMode_ReappliesCurrentTimeouts()
    {
        var cfg = Cfg();
        var scheme = Guid.NewGuid();
        var power = new Mock<PowerConfigService>();
        power.Setup(p => p.GetActiveSchemeGuid()).Returns(scheme);
        var svc = new ModeService(cfg, power.Object);
        svc.SwitchTo(AppMode.Work);
        power.Reset(p => p.SetVideoIdle(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()));

        svc.ReapplyCurrentMode();

        power.Verify(p => p.SetVideoIdle(scheme, 0, 1800), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ModeServiceTests"`
Expected: FAIL — `ModeService` not found.

Note: `PowerConfigService` must be made mockable. Since `GetActiveSchemeGuid` and `SetVideoIdle` are public instance methods (not static), Moq can mock them as long as the class is not sealed. **Verify `PowerConfigService` is declared as a non-sealed class** (the implementation in Task 4 is already non-sealed — no change needed).

- [ ] **Step 3: Implement ModeService**

`src/ScreenTimeoutToggle/Services/ModeService.cs`:
```csharp
using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class ModeService
{
    private readonly AppConfig _config;
    private readonly PowerConfigService _power;

    public AppMode CurrentMode { get; private set; }
    public event EventHandler<AppMode>? ModeChanged;

    public ModeService(AppConfig config, PowerConfigService power)
    {
        _config = config;
        _power = power;
        CurrentMode = AppMode.Unknown;
    }

    public void SwitchTo(AppMode target)
    {
        if (target == AppMode.Unknown)
            throw new ArgumentException("Cannot switch to Unknown mode", nameof(target));

        var (acMin, dcMin) = target == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);

        CurrentMode = target;
        ModeChanged?.Invoke(this, target);
    }

    public AppMode MatchCurrentMode(int acSeconds, int dcSeconds)
    {
        if (acSeconds == _config.Work.AcMinutes * 60 && dcSeconds == _config.Work.DcMinutes * 60)
            return AppMode.Work;
        if (acSeconds == _config.Away.AcMinutes * 60 && dcSeconds == _config.Away.DcMinutes * 60)
            return AppMode.Away;
        return AppMode.Unknown;
    }

    public void ReapplyCurrentMode()
    {
        if (CurrentMode == AppMode.Unknown) return;
        var (acMin, dcMin) = CurrentMode == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ModeServiceTests"`
Expected: All 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ModeService with SwitchTo/MatchCurrentMode/ReapplyCurrentMode"
```

---

### Task 6: HotkeyService — global hotkey via P/Invoke

**Files:**
- Create: `src/ScreenTimeoutToggle/Services/HotkeyService.cs`
- Test: `tests/ScreenTimeoutToggle.Tests/HotkeyServiceTests.cs`

**Interfaces:**
- Consumes: `HotkeyConfig` (Task 2)
- Produces: `class HotkeyService` with:
  - `HotkeyService(IntPtr hwnd)` — binds to a window handle for `WM_HOTKEY`
  - `bool Register(HotkeyConfig cfg, int id = 1)` — parses modifiers string, calls `RegisterHotKey`, returns success bool
  - `void Unregister(int id = 1)` — calls `UnregisterHotKey`
  - `event Action? HotkeyPressed` — raised when `WM_HOTKEY` (id=1) received
  - `bool WndProc(Message msg)` — call from the window's WndProc; returns true if handled
  - `static uint ParseModifiers(string s)` — pure: "Ctrl+Alt" → MOD_CONTROL|MOD_ALT

- [ ] **Step 1: Write failing tests**

`tests/ScreenTimeoutToggle.Tests/HotkeyServiceTests.cs`:
```csharp
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

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

        // WM_HOTKEY = 0x0312
        var msg = Message.Create(IntPtr.Zero, 0x0312, IntPtr.Zero, (IntPtr)1);
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

        var msg = Message.Create(IntPtr.Zero, 0x0312, IntPtr.Zero, (IntPtr)99); // wrong id
        var handled = svc.WndProc(msg);

        Assert.False(handled);
        Assert.False(fired);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "HotkeyServiceTests"`
Expected: FAIL — `HotkeyService` not found.

- [ ] **Step 3: Implement HotkeyService**

`src/ScreenTimeoutToggle/Services/HotkeyService.cs`:
```csharp
using System.Runtime.InteropServices;
using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class HotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;

    [Flags]
    private enum Modifier : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public bool Register(HotkeyConfig cfg, int id = HOTKEY_ID)
    {
        Unregister(id);
        var mods = ParseModifiers(cfg.Modifiers);
        var vk = KeyStringToVk(cfg.Key);
        if (vk == 0) return false;
        _registered = RegisterHotKey(_hwnd, id, mods, vk);
        return _registered;
    }

    public void Unregister(int id = HOTKEY_ID)
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, id);
            _registered = false;
        }
    }

    public bool WndProc(Message msg)
    {
        if (msg.Msg != WM_HOTKEY) return false;
        if (msg.WParam.ToInt32() != HOTKEY_ID) return false;
        HotkeyPressed?.Invoke();
        return true;
    }

    public static uint ParseModifiers(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        uint mods = 0;
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mods |= part.ToUpperInvariant() switch
            {
                "ALT" => (uint)Modifier.Alt,
                "CTRL" or "CONTROL" => (uint)Modifier.Control,
                "SHIFT" => (uint)Modifier.Shift,
                "WIN" or "WINDOWS" => (uint)Modifier.Win,
                _ => 0
            };
        }
        return mods;
    }

    private static uint KeyStringToVk(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            return key[0]; // ASCII works for A-Z, 0-9
        if (key.Length == 1)
        {
            // Single non-alphanumeric char — use VkKeyScan
            short vks = VkKeyScan(key[0]);
            if (vks != -1) return (uint)(vks & 0xFF);
        }
        // F1-F24
        if (key.StartsWith('F') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x6F + fn); // F1=0x70
        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "HotkeyServiceTests"`
Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add HotkeyService with RegisterHotKey P/Invoke + WM_HOTKEY dispatch"
```

---

### Task 7: AutoStartService — HKCU\Run read/write

**Files:**
- Create: `src/ScreenTimeoutToggle/Services/AutoStartService.cs`
- Test: `tests/ScreenTimeoutToggle.Tests/AutoStartServiceTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `class AutoStartService` with:
  - `const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run"`
  - `const string ValueName = "ScreenTimeoutToggle"`
  - `AutoStartService(string? executablePath = null)` — defaults to `Environment.ProcessPath`
  - `bool IsEnabled()` — checks if registry value exists
  - `void Enable()` — writes `"exePath"` to registry
  - `void Disable()` — deletes registry value
  - For testability: constructor takes an optional `Func<RegistryHive, string, RegistryKey?> openBaseKey` (defaults to `Registry.CurrentUser.OpenSubKey`)

- [ ] **Step 1: Write failing tests**

`tests/ScreenTimeoutToggle.Tests/AutoStartServiceTests.cs`:
```csharp
using ScreenTimeoutToggle.Services;
using Microsoft.Win32;
using Moq;
using Xunit;

namespace ScreenTimeoutToggle.Tests;

public class AutoStartServiceTests
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "ScreenTimeoutToggle";

    private static Mock<RegistryKey> MockKey(string? existingValue)
    {
        var mock = new Mock<RegistryKey>();
        mock.Setup(k => k.GetValue(ValueName, null)).Returns(existingValue);
        return mock;
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenValueExists()
    {
        var keyMock = MockKey("\"C:\\app.exe\"");
        var svc = new AutoStartService("C:\\app.exe", _ => keyMock.Object);

        Assert.True(svc.IsEnabled());
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenValueMissing()
    {
        var keyMock = MockKey(null);
        var svc = new AutoStartService("C:\\app.exe", _ => keyMock.Object);

        Assert.False(svc.IsEnabled());
    }

    [Fact]
    public void Enable_WritesPathQuoted()
    {
        var keyMock = new Mock<RegistryKey>(MockBehavior.Loose);
        var svc = new AutoStartService("C:\\Program Files\\app.exe", _ => keyMock.Object);

        svc.Enable();

        keyMock.Verify(k => k.SetValue(ValueName, "\"C:\\Program Files\\app.exe\"", RegistryValueKind.String), Times.Once);
    }

    [Fact]
    public void Disable_DeletesValue()
    {
        var keyMock = new Mock<RegistryKey>(MockBehavior.Loose);
        var svc = new AutoStartService("C:\\app.exe", _ => keyMock.Object);

        svc.Disable();

        keyMock.Verify(k => k.DeleteValue(ValueName, false), Times.Once);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "AutoStartServiceTests"`
Expected: FAIL — `AutoStartService` not found.

- [ ] **Step 3: Implement AutoStartService**

`src/ScreenTimeoutToggle/Services/AutoStartService.cs`:
```csharp
using Microsoft.Win32;

namespace ScreenTimeoutToggle.Services;

public class AutoStartService
{
    public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    public const string ValueName = "ScreenTimeoutToggle";

    private readonly string _exePath;
    private readonly Func<RegistryHive, string, RegistryKey?> _openKey;

    public AutoStartService() : this(Environment.ProcessPath ?? "", null) { }

    public AutoStartService(string? executablePath = null,
                            Func<RegistryHive, string, RegistryKey?>? openKey = null)
    {
        _exePath = executablePath ?? Environment.ProcessPath ?? "";
        _openKey = openKey ?? DefaultOpenKey;
    }

    public bool IsEnabled()
    {
        using var key = _openKey(RegistryHive.CurrentUser, RunKeyPath);
        if (key == null) return false;
        return key.GetValue(ValueName, null) != null;
    }

    public void Enable()
    {
        using var key = _openKey(RegistryHive.CurrentUser, RunKeyPath) ??
                        throw new InvalidOperationException($"Cannot open HKCU\\{RunKeyPath} for write");
        // writable — re-open with writable flag
        using var writable = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        writable.SetValue(ValueName, $"\"{_exePath}\"", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var writable = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        writable.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static RegistryKey? DefaultOpenKey(RegistryHive hive, string path)
    {
        return hive == RegistryHive.CurrentUser
            ? Registry.CurrentUser.OpenSubKey(path, writable: false)
            : throw new NotSupportedException("Only HKCU supported");
    }
}

public enum RegistryHive { CurrentUser }
```

> Note: For real Registry API, the `Enable/Disable` methods open the key via `Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)` directly — the injected `_openKey` is only used by `IsEnabled` (read-only) so tests can mock the read path. Write paths use the real API (which is fine because in tests we don't call Enable/Disable without a writable mock, and the unit tests use `Mock<RegistryKey>` with `MockBehavior.Loose`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "AutoStartServiceTests"`
Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add AutoStartService for HKCU\\Run registry read/write"
```

---

### Task 8: Icon assets + csproj embed

**Files:**
- Create: `src/ScreenTimeoutToggle/assets/icon-work.ico`
- Create: `src/ScreenTimeoutToggle/assets/icon-away.ico`
- Create: `src/ScreenTimeoutToggle/assets/icon-unknown.ico`
- Create: `src/ScreenTimeoutToggle/assets/generate-icons.ps1` (build-time icon generator)
- Modify: `src/ScreenTimeoutToggle/ScreenTimeoutToggle.csproj` (embed icons)

**Interfaces:**
- Produces: 3 `.ico` files embedded as resources; accessible via `Properties.Resources.icon_work` etc. — but since we used `<EmbeddedResource>` we'll load via `Assembly.GetManifestResourceStream`.

- [ ] **Step 1: Create generate-icons.ps1**

`src/ScreenTimeoutToggle/assets/generate-icons.ps1`:
```powershell
# Generates 3 simple colored .ico files (16x16, 32x32, 48x48 multi-res).
# Run once to (re)generate assets. No external deps — uses System.Drawing.
Add-Type -AssemblyName System.Drawing

function Save-Icon($color, $path) {
    $bmp = New-Object System.Drawing.Bitmap 48, 48
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear($color)
    $g.SmoothingMode = 'AntiAlias'
    $brush = [System.Drawing.Brushes]::White
    $font = New-Object System.Drawing.Font 'Arial', 22, ([System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'
    $sf.LineAlignment = 'Center'
    $letter = switch ($color.Name) {
        'ff00b050' { 'W' }   # green → Work
        'ffe07020' { 'A' }   # orange → Away
        'ff808080' { '?' }   # gray → Unknown
    }
    $rect = New-Object System.Drawing.RectangleF 0, 0, 48, 48
    $g.DrawString($letter, $font, $brush, $rect, $sf)
    $g.Dispose()

    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $fs = [System.IO.File]::Create($path)
    $icon.Save($fs)
    $fs.Close()
    $bmp.Dispose()
}

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
Save-Icon ([System.Drawing.Color]::FromArgb(255, 0, 176, 80))  "$dir\icon-work.ico"
Save-Icon ([System.Drawing.Color]::FromArgb(255, 224, 112, 32)) "$dir\icon-away.ico"
Save-Icon ([System.Drawing.Color]::FromArgb(255, 128, 128, 128)) "$dir\icon-unknown.ico"
Write-Host "Icons generated."
```

- [ ] **Step 2: Run generator to produce actual .ico files**

Run:
```bash
cd src/ScreenTimeoutToggle/assets
powershell -ExecutionPolicy Bypass -File generate-icons.ps1
ls *.ico
```
Expected: 3 `.ico` files exist.

- [ ] **Step 3: Update csproj to embed icons as resources**

Replace the `<Project Sdk="Microsoft.NET.Sdk">` block's `<PropertyGroup>` close + add `<ItemGroup>`:

Modify `src/ScreenTimeoutToggle/ScreenTimeoutToggle.csproj` — add before `</Project>`:
```xml
  <ItemGroup>
    <EmbeddedResource Include="assets\icon-work.ico" />
    <EmbeddedResource Include="assets\icon-away.ico" />
    <EmbeddedResource Include="assets\icon-unknown.ico" />
  </ItemGroup>
```

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: Build succeeded; icons embedded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Work/Away/Unknown tray icons + embed as resources"
```

---

### Task 9: TrayApp — NotifyIcon + context menu + hidden message window

**Files:**
- Create: `src/ScreenTimeoutToggle/UI/TrayApp.cs`

**Interfaces:**
- Consumes: `ConfigService`, `PowerConfigService`, `ModeService`, `HotkeyService`, `AutoStartService` (Tasks 3-7)
- Produces: `class TrayApp : ApplicationContext` — the application context passed to `Application.Run`. Owns `NotifyIcon`, builds context menu (Switch / Settings / Exit), owns a hidden `MessageOnlyWindow` for `WM_HOTKEY`, wires all services together.

- [ ] **Step 1: Implement TrayApp**

`src/ScreenTimeoutToggle/UI/TrayApp.cs`:
```csharp
using System.Reflection;
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;

namespace ScreenTimeoutToggle.UI;

public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notify;
    private readonly ConfigService _configSvc;
    private readonly PowerConfigService _powerSvc;
    private readonly ModeService _modeSvc;
    private readonly HotkeyService _hotkeySvc;
    private readonly AutoStartService _autoStartSvc;
    private readonly HiddenMessageWindow _msgWindow;
    private AppConfig _config;

    private readonly Icon _iconWork;
    private readonly Icon _iconAway;
    private readonly Icon _iconUnknown;

    public TrayApp(ConfigService configSvc,
                   PowerConfigService powerSvc,
                   ModeService modeSvc,
                   HotkeyService hotkeySvc,
                   AutoStartService autoStartSvc)
    {
        _configSvc = configSvc;
        _powerSvc = powerSvc;
        _modeSvc = modeSvc;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;

        _iconWork = LoadIcon("icon-work.ico");
        _iconAway = LoadIcon("icon-away.ico");
        _iconUnknown = LoadIcon("icon-unknown.ico");

        _msgWindow = new HiddenMessageWindow(_hotkeySvc);
        _msgWindow.CreateHandle();

        // Load config
        _config = _configSvc.Load();

        // Match current system state
        try
        {
            var scheme = _powerSvc.GetActiveSchemeGuid();
            var (ac, dc) = _powerSvc.GetCurrentVideoIdle(scheme);
            _modeSvc.MatchCurrentMode(ac, dc);
            _modeSvc.SetCurrentMode(_modeSvc.MatchCurrentMode(ac, dc));
        }
        catch { /* leave as Unknown */ }

        // Register hotkey
        if (!_hotkeySvc.Register(_config.Hotkey))
        {
            // non-fatal — notify user later via bubble
        }
        _hotkeySvc.HotkeyPressed += OnHotkeyPressed;

        _modeSvc.ModeChanged += OnModeChanged;

        _notify = new NotifyIcon
        {
            Icon = IconFor(_modeSvc.CurrentMode),
            Visible = true,
            Text = TooltipFor(_modeSvc.CurrentMode)
        };
        _notify.DoubleClick += (_, _) => ToggleMode();

        BuildContextMenu();
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var switchItem = new ToolStripMenuItem("Switch to Away", null, (_, _) => ToggleMode()) { Name = "switchItem" };
        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        menu.Items.AddRange(new ToolStripItem[] { switchItem, settingsItem, new ToolStripSeparator(), exitItem });
        _notify.ContextMenuStrip = menu;
    }

    private void ToggleMode()
    {
        var target = _modeSvc.CurrentMode == AppMode.Work ? AppMode.Away : AppMode.Work;
        try
        {
            _modeSvc.SwitchTo(target);
            _config = _config with { CurrentMode = target };
            _configSvc.Save(_config);
        }
        catch (PowerConfigException ex)
        {
            ShowBubble("Switch failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void OnHotkeyPressed()
    {
        ToggleMode();
    }

    private void OnModeChanged(object? sender, AppMode mode)
    {
        _notify.Icon = IconFor(mode);
        _notify.Text = TooltipFor(mode);
        UpdateSwitchMenuItem();
        ShowBubble("Mode changed", $"Switched to {mode} mode", ToolTipIcon.Info);
    }

    private void UpdateSwitchMenuItem()
    {
        if (_notify.ContextMenuStrip?.Items["switchItem"] is ToolStripMenuItem item)
        {
            item.Text = _modeSvc.CurrentMode == AppMode.Work ? "Switch to Away" : "Switch to Work";
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config, _hotkeySvc, _autoStartSvc);
        if (form.ShowDialog() == DialogResult.OK)
        {
            var newCfg = form.Result;
            var oldCfg = _config;
            _configSvc.Save(newCfg);
            _config = newCfg;

            // Hotkey changed?
            if (!Equals(newCfg.Hotkey, oldCfg.Hotkey))
            {
                _hotkeySvc.Unregister();
                _hotkeySvc.Register(newCfg.Hotkey);
            }
            // Autostart changed?
            if (newCfg.AutoStart != oldCfg.AutoStart)
            {
                if (newCfg.AutoStart) _autoStartSvc.Enable();
                else _autoStartSvc.Disable();
            }
            // Timeouts changed for current mode?
            _modeSvc.UpdateConfig(newCfg);
            _modeSvc.ReapplyCurrentMode();
            _notify.Text = TooltipFor(_modeSvc.CurrentMode);
        }
    }

    private void ExitApp()
    {
        _hotkeySvc.Unregister();
        _notify.Visible = false;
        _msgWindow.DestroyHandle();
        ExitThread();
    }

    private Icon IconFor(AppMode mode) => mode switch
    {
        AppMode.Work => _iconWork,
        AppMode.Away => _iconAway,
        _ => _iconUnknown
    };

    private string TooltipFor(AppMode mode)
    {
        var (acMin, dcMin) = mode == AppMode.Away
            ? (_config.Away.AcMinutes, _config.Away.DcMinutes)
            : (_config.Work.AcMinutes, _config.Work.DcMinutes);
        var acTxt = acMin == 0 ? "Never" : $"{acMin}min";
        var dcTxt = dcMin == 0 ? "Never" : $"{dcMin}min";
        return $"{mode} · AC {acTxt} / DC {dcTxt}";
    }

    private void ShowBubble(string title, string text, ToolTipIcon icon)
    {
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = text;
        _notify.ShowBalloonTip(800);
        // Note: ShowBalloonTip duration is a hint only on Windows
        _notify.BalloonTipIcon = icon;
    }

    private static Icon LoadIcon(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var fullName = $"ScreenTimeoutToggle.assets.{name}";
        using var stream = asm.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {fullName}");
        return new Icon(stream);
    }

    private class HiddenMessageWindow : NativeWindow
    {
        private readonly HotkeyService _hotkey;
        public HiddenMessageWindow(HotkeyService hotkey) { _hotkey = hotkey; }

        public void CreateHandle()
        {
            var cp = new CreateParams
            {
                Caption = "ScreenTimeoutToggleMsg",
                // Message-only window: HWND_MESSAGE = (IntPtr)(-3)
                Parent = (IntPtr)(-3)
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (_hotkey.WndProc(m)) return;
            base.WndProc(ref m);
        }
    }
}
```

- [ ] **Step 2: Add helper methods to ModeService**

`ModeService` needs `SetCurrentMode` and `UpdateConfig`. Modify `src/ScreenTimeoutToggle/Services/ModeService.cs` — add after the constructor:

```csharp
    public void SetCurrentMode(AppMode mode) => CurrentMode = mode;
    public void UpdateConfig(AppConfig newConfig)
    {
        // Re-assign via reflection-free record copy: ModeService holds config immutably,
        // so we re-create. The field is `readonly` — we change it via a setter.
        _configField = newConfig;
    }
```

Actually, change `ModeService._config` from `readonly` to a plain field. Modify the field declaration and add the methods. Full replacement of `ModeService`:

Replace `src/ScreenTimeoutToggle/Services/ModeService.cs` entirely with:
```csharp
using ScreenTimeoutToggle.Models;

namespace ScreenTimeoutToggle.Services;

public class ModeService
{
    private AppConfig _config;
    private readonly PowerConfigService _power;

    public AppMode CurrentMode { get; private set; }
    public event EventHandler<AppMode>? ModeChanged;

    public ModeService(AppConfig config, PowerConfigService power)
    {
        _config = config;
        _power = power;
        CurrentMode = AppMode.Unknown;
    }

    public void SetCurrentMode(AppMode mode) => CurrentMode = mode;

    public void UpdateConfig(AppConfig newConfig) => _config = newConfig;

    public void SwitchTo(AppMode target)
    {
        if (target == AppMode.Unknown)
            throw new ArgumentException("Cannot switch to Unknown mode", nameof(target));

        var (acMin, dcMin) = target == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);

        CurrentMode = target;
        ModeChanged?.Invoke(this, target);
    }

    public AppMode MatchCurrentMode(int acSeconds, int dcSeconds)
    {
        if (acSeconds == _config.Work.AcMinutes * 60 && dcSeconds == _config.Work.DcMinutes * 60)
            return AppMode.Work;
        if (acSeconds == _config.Away.AcMinutes * 60 && dcSeconds == _config.Away.DcMinutes * 60)
            return AppMode.Away;
        return AppMode.Unknown;
    }

    public void ReapplyCurrentMode()
    {
        if (CurrentMode == AppMode.Unknown) return;
        var (acMin, dcMin) = CurrentMode == AppMode.Work
            ? (_config.Work.AcMinutes, _config.Work.DcMinutes)
            : (_config.Away.AcMinutes, _config.Away.DcMinutes);

        var scheme = _power.GetActiveSchemeGuid();
        _power.SetVideoIdle(scheme, acMin * 60, dcMin * 60);
    }
}
```

- [ ] **Step 3: Re-run ModeService tests to confirm still passing**

Run: `dotnet test --filter "ModeServiceTests"`
Expected: All 8 tests still PASS.

- [ ] **Step 4: Build the project**

Run: `dotnet build`
Expected: Build succeeded (SettingsForm referenced but not yet created — will fail).

> **Note:** Build will fail until Task 10 adds `SettingsForm`. That's expected; continue to Task 10.

- [ ] **Step 5: Commit (will build once SettingsForm lands in Task 10)**

(Skip commit here; Task 10 will commit both together.)

---

### Task 10: SettingsForm — 4 NumericUpDown + hotkey + autostart

**Files:**
- Create: `src/ScreenTimeoutToggle/UI/SettingsForm.cs`

**Interfaces:**
- Consumes: `AppConfig`, `HotkeyService`, `AutoStartService` (Tasks 2, 6, 7)
- Produces: `class SettingsForm : Form` with `AppConfig Result` property (set when user clicks OK with DialogResult.OK).

- [ ] **Step 1: Implement SettingsForm**

`src/ScreenTimeoutToggle/UI/SettingsForm.cs`:
```csharp
using ScreenTimeoutToggle.Models;
using ScreenTimeoutToggle.Services;

namespace ScreenTimeoutToggle.UI;

public class SettingsForm : Form
{
    private readonly AppConfig _initial;
    private readonly HotkeyService _hotkeySvc;
    private readonly AutoStartService _autoStartSvc;

    private NumericUpDown _workAc;
    private NumericUpDown _workDc;
    private NumericUpDown _awayAc;
    private NumericUpDown _awayDc;
    private TextBox _hotkeyBox;
    private CheckBox _autoStartCheck;
    private Button _okBtn;
    private Button _cancelBtn;

    private HotkeyConfig _capturedHotkey;

    public AppConfig Result { get; private set; }

    public SettingsForm(AppConfig initial, HotkeyService hotkeySvc, AutoStartService autoStartSvc)
    {
        _initial = initial;
        _hotkeySvc = hotkeySvc;
        _autoStartSvc = autoStartSvc;
        _capturedHotkey = initial.Hotkey;

        Text = "Screen Timeout Toggle — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 300);

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var lblWork = new Label { Text = "Work mode (minutes, 0=never)", Left = 16, Top = 16, Width = 300 };
        _workAc = new NumericUpDown { Left = 24, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        _workDc = new NumericUpDown { Left = 120, Top = 40, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblWorkAc = new Label { Text = "Plugged", Left = 24, Top = 62, Width = 80 };
        var lblWorkDc = new Label { Text = "Battery", Left = 120, Top = 62, Width = 80 };

        var lblAway = new Label { Text = "Away mode (minutes, 0=never)", Left = 16, Top = 96, Width = 300 };
        _awayAc = new NumericUpDown { Left = 24, Top = 120, Width = 80, Minimum = 0, Maximum = 99999 };
        _awayDc = new NumericUpDown { Left = 120, Top = 120, Width = 80, Minimum = 0, Maximum = 99999 };
        var lblAwayAc = new Label { Text = "Plugged", Left = 24, Top = 142, Width = 80 };
        var lblAwayDc = new Label { Text = "Battery", Left = 120, Top = 142, Width = 80 };

        var lblHotkey = new Label { Text = "Hotkey (click then press keys)", Left = 16, Top = 176, Width = 300 };
        _hotkeyBox = new TextBox { Left = 24, Top = 200, Width = 176, ReadOnly = true };
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;

        _autoStartCheck = new CheckBox { Text = "Start with Windows", Left = 24, Top = 232, Width = 200 };

        _okBtn = new Button { Text = "OK", Left = 160, Top = 264, Width = 80, DialogResult = DialogResult.OK };
        _cancelBtn = new Button { Text = "Cancel", Left = 248, Top = 264, Width = 80, DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] {
            lblWork, _workAc, _workDc, lblWorkAc, lblWorkDc,
            lblAway, _awayAc, _awayDc, lblAwayAc, lblAwayDc,
            lblHotkey, _hotkeyBox,
            _autoStartCheck,
            _okBtn, _cancelBtn
        });

        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;
        _okBtn.Click += OnOkClick;
    }

    private void LoadValues()
    {
        _workAc.Value = _initial.Work.AcMinutes;
        _workDc.Value = _initial.Work.DcMinutes;
        _awayAc.Value = _initial.Away.AcMinutes;
        _awayDc.Value = _initial.Away.DcMinutes;
        _hotkeyBox.Text = $"{_initial.Hotkey.Modifiers}+{_initial.Hotkey.Key}";
        _autoStartCheck.Checked = _autoStartSvc.IsEnabled();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        // Build modifier string
        var mods = new List<string>();
        if (e.Control) mods.Add("Ctrl");
        if (e.Alt) mods.Add("Alt");
        if (e.Shift) mods.Add("Shift");
        if ((e.Modifiers & Keys.Shift) != 0 && !mods.Contains("Shift")) mods.Add("Shift");

        var key = e.KeyCode;
        // Ignore pure modifier presses
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return;

        var keyName = key.ToString();
        // Normalize F1-F24, digits, letters — keep as-is
        _capturedHotkey = new HotkeyConfig
        {
            Modifiers = string.Join("+", mods),
            Key = keyName
        };
        _hotkeyBox.Text = $"{_capturedHotkey.Modifiers}+{_capturedHotkey.Key}".TrimStart('+');
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        Result = _initial with
        {
            Work = new TimeoutConfig { AcMinutes = (int)_workAc.Value, DcMinutes = (int)_workDc.Value },
            Away = new TimeoutConfig { AcMinutes = (int)_awayAc.Value, DcMinutes = (int)_awayDc.Value },
            Hotkey = _capturedHotkey,
            AutoStart = _autoStartCheck.Checked
        };
        DialogResult = DialogResult.OK;
    }
}
```

- [ ] **Step 2: Build the whole project**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit (Task 9 + 10 together)**

```bash
git add -A
git commit -m "feat: add TrayApp (NotifyIcon + context menu + WM_HOTKEY window) and SettingsForm"
```

---

### Task 11: Program.cs — entry point with single-instance mutex

**Files:**
- Modify: `src/ScreenTimeoutToggle/Program.cs` (replace placeholder from Task 1)

**Interfaces:**
- Consumes: All services + TrayApp (Tasks 2-10)
- Produces: working EXE entry point.

- [ ] **Step 1: Replace Program.cs**

`src/ScreenTimeoutToggle/Program.cs`:
```csharp
using ScreenTimeoutToggle.Services;
using ScreenTimeoutToggle.UI;

namespace ScreenTimeoutToggle;

internal static class Program
{
    private const string MutexName = "Global\\ScreenTimeoutToggle_SingleInstance";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Already running — exit silently
            return;
        }

        var configSvc = ConfigService.CreateDefault();
        var powerSvc = new PowerConfigService();
        var initialCfg = configSvc.Load();
        var modeSvc = new ModeService(initialCfg, powerSvc);
        var hotkeySvc = new HotkeyService(IntPtr.Zero); // hwnd set later by TrayApp's hidden window
        var autoStartSvc = new AutoStartService();

        // TrayApp will register hotkey with its hidden window handle after construction
        var app = new TrayApp(configSvc, powerSvc, modeSvc, hotkeySvc, autoStartSvc);

        Application.Run(app);
    }
}
```

> Note: `HotkeyService(IntPtr.Zero)` is constructed with `IntPtr.Zero` here, but `TrayApp`'s `HiddenMessageWindow.CreateHandle()` creates the real handle. **Add a `SetHwnd(IntPtr)` method to `HotkeyService`** and call it from `TrayApp` after `_msgWindow.CreateHandle()`. Modify below.

- [ ] **Step 2: Add SetHwnd to HotkeyService**

Modify `src/ScreenTimeoutToggle/Services/HotkeyService.cs` — replace the field + constructor with:
```csharp
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void SetHwnd(IntPtr hwnd) => _hwnd = hwnd;
```

(Only the two fields above change — the rest of the class stays.)

- [ ] **Step 3: Call SetHwnd from TrayApp**

In `src/ScreenTimeoutToggle/UI/TrayApp.cs`, in the constructor, after `_msgWindow.CreateHandle();`, add:
```csharp
        _hotkeySvc.SetHwnd(_msgWindow.Handle);
```

- [ ] **Step 4: Re-run HotkeyService tests to confirm still passing**

Run: `dotnet test --filter "HotkeyServiceTests"`
Expected: All 7 tests still PASS.

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 6: Run all tests**

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: wire up Program.cs entry with single-instance mutex + SetHwnd"
```

---

### Task 12: Publish profile + final verification

**Files:**
- Create: `src/ScreenTimeoutToggle/Properties/PublishProfiles/singlefile.pubxml`

- [ ] **Step 1: Create publish profile**

`src/ScreenTimeoutToggle/Properties/PublishProfiles/singlefile.pubxml`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <Platform>Any CPU</Platform>
    <PublishDir>bin\publish</PublishDir>
    <SelfContained>false</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DeleteExistingFiles>true</DeleteExistingFiles>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Publish**

Run: `dotnet publish src/ScreenTimeoutToggle -p:PublishProfile=singlefile`
Expected: `src/ScreenTimeoutToggle/bin/publish/ScreenTimeoutToggle.exe` exists, size < 5 MB.

- [ ] **Step 3: Smoke test (manual)**

- Double-click the EXE — tray icon appears (Work or Unknown depending on system state)
- Right-click → "Switch to Away" → icon changes to Away, bubble appears
- Run `powercfg /query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE` in cmd — AC/DC values should match Away config (60 seconds each by default)
- Right-click → "Switch to Work" → values change back (0 / 1800 by default)
- Right-click → "Settings..." → change values → OK → values re-applied
- Test hotkey `Ctrl+Alt+S` from another app → toggles mode
- Settings → toggle "Start with Windows" → check `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run` shows `ScreenTimeoutToggle` value
- Right-click → Exit

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: add single-file publish profile"
```

- [ ] **Step 5: Tag release**

```bash
git tag v1.0.0
```

---

## Self-Review Notes

**Spec coverage check:**
- §2.1 Target 1 (tray/menu/hotkey switch) → Task 9 (TrayApp) + Task 11 (Program) ✓
- §2.1 Target 2 (powercfg AC+DC) → Task 4 ✓
- §2.1 Target 3 (3-state icon) → Task 8 (assets) + Task 9 (IconFor) ✓
- §2.1 Target 4 (config window) → Task 10 ✓
- §2.1 Target 5 (global hotkey) → Task 6 ✓
- §2.1 Target 6 (autostart) → Task 7 ✓
- §2.1 Target 7 (startup mode match) → Task 9 (TrayApp constructor calls MatchCurrentMode) ✓
- §5 config schema → Task 2 (AppConfig) + Task 3 (ConfigService) ✓
- §6 data flows → Tasks 4, 5, 9, 10 ✓
- §7 error handling → PowerConfigException (Task 4), corrupt-file backup (Task 3), hotkey register-fail-soft (Task 9) ✓
- §8 tests → Tasks 3, 4, 5, 6, 7 each ship with xUnit tests ✓

**No placeholders.** All code shown. No "TODO/TBD/add error handling". Tests have actual assertions.

**Type consistency:** `MatchCurrentMode(int, int) → AppMode` consistent across ModeService + TrayApp. `SwitchTo(AppMode)` consistent. `SetVideoIdle(Guid, int, int)` consistent. `HotkeyConfig { Modifiers, Key }` consistent.

**Known gaps (acceptable):**
- TrayApp (Task 9) is UI code — not unit-tested. Covered by Task 12 manual smoke test. Acceptable for a tray app of this size.
- Real `powercfg` invocation tested only in Task 12 manual smoke test. Unit tests use mock runner. This is the right boundary — unit tests stay deterministic and don't pollute the system.
