using System.Runtime.InteropServices;
using OBDim.Models;

namespace OBDim.Services;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char ch);

    private IntPtr _hwnd;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void SetHwnd(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Registers a global hotkey. Internally unregisters any previously registered hotkey first.
    /// Returns false if the key combination is invalid or already in use by another application.
    /// </summary>
    public bool Register(HotkeyConfig cfg, int id = HOTKEY_ID)
    {
        Unregister(id);
        var mods = ParseModifiers(cfg.Modifiers);
        var vk = KeyStringToVk(cfg.Key);
        if (vk == 0) return false;
        _registered = RegisterHotKey(_hwnd, id, mods, vk);
        if (!_registered)
        {
            LogService.Warn($"Failed to register hotkey: {cfg.Modifiers}+{cfg.Key}");
        }
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

    /// <summary>
    /// Parses a modifier string like "Ctrl+Alt" into a combined Win32 modifier flag.
    /// </summary>
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

    /// <summary>
    /// Named virtual-key codes for keys that are not single letters/digits or F1-F24.
    /// Keys are matched case-insensitively. Includes both the Keys enum name and
    /// common aliases (e.g., "ScrollLock" for "Scroll").
    /// </summary>
    private static readonly Dictionary<string, uint> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PrintScreen"] = 0x2C, // VK_SNAPSHOT
            ["Snapshot"] = 0x2C,    // alias used by some WinForms versions
            ["Pause"] = 0x13,       // VK_PAUSE
            ["Scroll"] = 0x91,      // VK_SCROLL
            ["ScrollLock"] = 0x91,  // alias
            ["Capital"] = 0x14,     // VK_CAPITAL (CapsLock)
            ["CapsLock"] = 0x14,    // alias
            ["NumLock"] = 0x90,     // VK_NUMLOCK
            ["Insert"] = 0x2D,      // VK_INSERT
            ["Delete"] = 0x2E,      // VK_DELETE
            ["Home"] = 0x24,        // VK_HOME
            ["End"] = 0x23,         // VK_END
            ["PageUp"] = 0x21,      // VK_PRIOR
            ["Prior"] = 0x21,       // alias
            ["PageDown"] = 0x22,    // VK_NEXT
            ["Next"] = 0x22,        // alias
            ["Left"] = 0x25,        // VK_LEFT
            ["Up"] = 0x26,          // VK_UP
            ["Right"] = 0x27,       // VK_RIGHT
            ["Down"] = 0x28,        // VK_DOWN
        };

    /// <summary>
    /// Converts a key string (e.g., "S", "F5", "1", "PrintScreen") to a Win32 virtual-key code.
    /// Returns 0 for unrecognized keys.
    /// </summary>
    public static uint KeyStringToVk(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            return key[0]; // ASCII works for A-Z, 0-9
        if (key.Length == 1)
        {
            short vks = VkKeyScan(key[0]);
            if (vks != -1) return (uint)(vks & 0xFF);
        }
        // F1-F24
        if (key.StartsWith('F') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x6F + fn); // F1=0x70
        // Named keys (PrintScreen, Pause, ScrollLock, etc.)
        if (NamedKeys.TryGetValue(key, out uint namedVk))
            return namedVk;
        return 0;
    }
}
