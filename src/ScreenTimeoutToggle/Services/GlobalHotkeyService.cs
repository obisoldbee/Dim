using System.Runtime.InteropServices;
using OBDim.Models;

namespace OBDim.Services;

/// <summary>
/// Registers ONE additional global hotkey, independent from the mode-switch hotkey owned
/// by <see cref="HotkeyService"/> (which deliberately owns exactly one). Used for the
/// monitoring popover toggle (default Ctrl+Alt+D). Routing: the tray's hidden message
/// window consults this service's <see cref="WndProc"/> after <see cref="HotkeyService"/>.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

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
    private readonly int _id;
    private bool _registered;

    public event Action? HotkeyPressed;

    public bool IsRegistered => _registered;

    public GlobalHotkeyService(IntPtr hwnd, int id)
    {
        _hwnd = hwnd;
        _id = id;
    }

    /// <summary>Registers the combination; false when invalid or taken (nothing is torn down).</summary>
    public bool Register(HotkeyConfig cfg)
    {
        var vk = HotkeyService.KeyStringToVk(cfg.Key);
        if (vk == 0) return false;
        var mods = HotkeyService.ParseModifiers(cfg.Modifiers);
        if (RegisterHotKey(_hwnd, _id, mods, vk))
        {
            _registered = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a single-string hotkey like "Ctrl+Alt+D" into a HotkeyConfig
    /// (last segment = key, the rest = modifiers). Null when nothing usable.
    /// </summary>
    public static HotkeyConfig? ParseHotkeyString(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var key = parts[^1];
        var modifiers = parts.Length == 1 ? "" : string.Join("+", parts[..^1]);
        if (HotkeyService.KeyStringToVk(key) == 0) return null;
        return new HotkeyConfig { Modifiers = modifiers, Key = key };
    }

    public bool WndProc(Message msg)
    {
        if (msg.Msg != WM_HOTKEY) return false;
        if (msg.WParam.ToInt32() != _id) return false;
        HotkeyPressed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { UnregisterHotKey(_hwnd, _id); } catch { /* best effort */ }
            _registered = false;
        }
    }
}
