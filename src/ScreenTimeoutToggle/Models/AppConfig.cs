namespace OBDim.Models;

public record HotkeyConfig
{
    public string Modifiers { get; init; } = "Ctrl+Alt";
    public string Key { get; init; } = "S";

    public static HotkeyConfig CreateDefault() => new() { Modifiers = "Ctrl+Alt", Key = "S" };
}

public record TimeoutConfig
{
    public int AcMinutes { get; init; }
    public int DcMinutes { get; init; }

    public static TimeoutConfig CreateDefaultWork() => new() { AcMinutes = 0, DcMinutes = 30 };
    public static TimeoutConfig CreateDefaultAway() => new() { AcMinutes = 1, DcMinutes = 1 };
}

public record AppConfig
{
    public int Version { get; init; } = 1;
    public TimeoutConfig Work { get; init; } = TimeoutConfig.CreateDefaultWork();
    public TimeoutConfig Away { get; init; } = TimeoutConfig.CreateDefaultAway();
    public HotkeyConfig Hotkey { get; init; } = HotkeyConfig.CreateDefault();
    public bool AutoStart { get; init; } = true;
    public AppMode CurrentMode { get; init; } = AppMode.Work;
    public string Language { get; init; } = "zh-CN";

    public static AppConfig CreateDefault() => new();
}
