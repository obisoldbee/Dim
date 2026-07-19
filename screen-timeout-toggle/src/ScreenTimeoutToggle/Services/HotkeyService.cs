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
            short vks = VkKeyScan(key[0]);
            if (vks != -1) return (uint)(vks & 0xFF);
        }
        // F1-F24
        if (key.StartsWith('F') && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x6F + fn); // F1=0x70
        return 0;
    }
}
