using System.Runtime.InteropServices;
using OBDim.Models;

namespace OBDim.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// The one and only hotkey id this service ever uses.
    /// </summary>
    /// <remarks>
    /// v1.0.7 (M5): <c>Register</c> and <c>Unregister</c> used to accept an id parameter
    /// that no caller ever supplied, while <see cref="WndProc"/> only ever recognised
    /// <c>HOTKEY_ID</c>. Registering under a custom id therefore produced a hotkey that
    /// was live (per <see cref="IsRegistered"/>) but could never fire, and could not even
    /// be released by the parameterless <c>Unregister()</c>. The service owns exactly one
    /// hotkey, so the parameter was removed rather than papered over.
    /// </remarks>
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

    /// <summary>
    /// Modifiers and virtual key of the live registration. Remembered so a refused swap
    /// can put the previous hotkey back: releasing needs only an id, but re-registering
    /// needs the whole combination.
    /// </summary>
    private uint _registeredMods;
    private uint _registeredVk;

    public event Action? HotkeyPressed;

    /// <summary>
    /// True while a hotkey is currently registered with the window (or thread, when the
    /// handle is null). Exposed so callers and tests can distinguish "no hotkey is live"
    /// from "a WM_HOTKEY message was consumed".
    /// </summary>
    public bool IsRegistered => _registered;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void SetHwnd(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Registers a global hotkey, replacing any hotkey this service already owns.
    /// </summary>
    /// <param name="cfg">Key combination to register.</param>
    /// <returns>
    /// False if the key combination is invalid or already in use by another application.
    /// In that case the previously registered hotkey is restored whenever Windows still
    /// lets us have it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// v1.0.5: The key string is validated and resolved to a virtual-key code BEFORE the
    /// previously registered hotkey is unregistered. Previously the order was
    /// "unregister → parse", so an invalid new key left the service in a dangling state
    /// (old key gone, no new key registered) with no way to roll back atomically.
    /// </para>
    /// <para>
    /// v1.0.7 (M6): that atomicity only ever covered the invalid-key branch. "Combination
    /// already in use" — by far the more common failure — still went
    /// <c>Unregister(old) → Register(new)</c>, so a refusal from Windows left the app with
    /// no hotkey at all. There is no atomic swap in the Win32 API, so the next best thing
    /// is a recorded rollback: the live combination is remembered and re-registered when
    /// the new one is refused. Restoring it can fail too (something else may have taken
    /// the old combination during the gap), which is logged as an error rather than
    /// reported as a successful revert.
    /// </para>
    /// </remarks>
    public bool Register(HotkeyConfig cfg)
    {
        // Resolve first — never tear down the working hotkey for a key we cannot register.
        var vk = KeyStringToVk(cfg.Key);
        if (vk == 0)
        {
            LogService.Warn($"Invalid hotkey key, keeping existing registration: {cfg.Modifiers}+{cfg.Key}");
            return false;
        }

        var mods = ParseModifiers(cfg.Modifiers);

        // Snapshot what is live, so a refused swap can be undone.
        var previousMods = _registeredMods;
        var previousVk = _registeredVk;
        var hadPrevious = _registered;

        if (hadPrevious)
            UnregisterHotKey(_hwnd, HOTKEY_ID);

        if (RegisterHotKey(_hwnd, HOTKEY_ID, mods, vk))
        {
            SetLive(mods, vk);
            return true;
        }

        LogService.Warn($"Failed to register hotkey: {cfg.Modifiers}+{cfg.Key}");

        if (!hadPrevious)
        {
            ClearLive();
            return false;
        }

        // Roll back. Re-registering the old combination can fail as well, in which case no
        // hotkey is live — the caller reads IsRegistered to find out.
        if (RegisterHotKey(_hwnd, HOTKEY_ID, previousMods, previousVk))
        {
            SetLive(previousMods, previousVk);
            LogService.Info("Hotkey change refused; the previous registration is live again");
        }
        else
        {
            ClearLive();
            LogService.Error("Hotkey change was refused and the previous hotkey could not be restored; no hotkey is registered");
        }

        return false;
    }

    /// <summary>
    /// Releases the hotkey owned by this service, if any.
    /// </summary>
    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            ClearLive();
        }
    }

    /// <summary>
    /// Releases the hotkey. Same effect as <see cref="Unregister"/>.
    /// </summary>
    /// <remarks>
    /// v1.0.7 (M12): the service used to depend on every caller remembering to call
    /// <c>Unregister()</c> on every exit path; an exception in between left a global
    /// hotkey registered to a dead window.
    /// </remarks>
    public void Dispose()
    {
        Unregister();
        GC.SuppressFinalize(this);
    }

    private void SetLive(uint mods, uint vk)
    {
        _registered = true;
        _registeredMods = mods;
        _registeredVk = vk;
    }

    private void ClearLive()
    {
        _registered = false;
        _registeredMods = 0;
        _registeredVk = 0;
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
    /// <remarks>
    /// <para>
    /// Declared <c>internal</c> (with <c>InternalsVisibleTo</c> for the test assembly) so
    /// the collision guard can enumerate it by reflection instead of relying on a
    /// hand-maintained list that silently drifts out of sync.
    /// </para>
    /// <para>
    /// Deliberately absent, because they are not usable as a hotkey <em>key</em>:
    /// <list type="bullet">
    /// <item><description><c>Back</c> — rejected since v1.0.5; use <c>Backspace</c>.</description></item>
    /// <item><description>Modifier keys (<c>ShiftKey</c>, <c>ControlKey</c>, <c>Menu</c>,
    /// <c>LShiftKey</c>, <c>RShiftKey</c>, <c>LControlKey</c>, <c>RControlKey</c>,
    /// <c>LMenu</c>, <c>RMenu</c>) — handled as modifiers by <see cref="ParseModifiers"/>.</description></item>
    /// <item><description>Mouse buttons (<c>LButton</c>, <c>RButton</c>, <c>MButton</c>,
    /// <c>XButton1</c>, <c>XButton2</c>) and <c>None</c> — not keyboard keys.</description></item>
    /// <item><description>Mask constants <c>KeyCode</c> and <c>Modifiers</c>, and the
    /// modifier flags <c>Shift</c>, <c>Control</c>, <c>Alt</c> — not virtual keys.</description></item>
    /// <item><description>Reserved / non-physical codes: <c>Cancel</c> (Ctrl+Break
    /// pseudo-key), <c>LineFeed</c> (control character), <c>FinalMode</c> (IME-internal),
    /// <c>ProcessKey</c>, <c>Packet</c>, and the IBM 3270 block <c>Attn</c>,
    /// <c>Crsel</c>, <c>Exsel</c>, <c>EraseEof</c>, <c>Play</c>, <c>Zoom</c>,
    /// <c>NoName</c>, <c>Pa1</c>.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    internal static readonly Dictionary<string, uint> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // --- v1.0.6: main-keyboard digits (VK_0 – VK_9, 0x30–0x39) ---
            // Keys.D0..D9 are what SettingsForm receives (e.KeyCode.ToString()) when the
            // user presses a top-row digit. Without these, pressing "1" produced "D1",
            // which resolved to 0 and was rejected as an invalid hotkey.
            ["D0"] = 0x30,          // VK_0
            ["D1"] = 0x31,          // VK_1
            ["D2"] = 0x32,          // VK_2
            ["D3"] = 0x33,          // VK_3
            ["D4"] = 0x34,          // VK_4
            ["D5"] = 0x35,          // VK_5
            ["D6"] = 0x36,          // VK_6
            ["D7"] = 0x37,          // VK_7
            ["D8"] = 0x38,          // VK_8
            ["D9"] = 0x39,          // VK_9

            // --- v1.0.6: whitespace / editing keys ---
            ["Tab"] = 0x09,         // VK_TAB
            ["Clear"] = 0x0C,       // VK_CLEAR (numpad 5 with NumLock off)
            ["Return"] = 0x0D,      // VK_RETURN — Keys.Return alias
            ["Enter"] = 0x0D,       // VK_RETURN — Keys.Enter (same VK, both names accepted)
            ["Escape"] = 0x1B,      // VK_ESCAPE
            ["Space"] = 0x20,       // VK_SPACE

            // --- v1.0.6: IME keys (real VKs on JP / KR keyboards) ---
            ["KanaMode"] = 0x15,    // VK_KANA
            ["HangulMode"] = 0x15,  // VK_HANGUL (same VK, alternative name)
            ["HanguelMode"] = 0x15, // VK_HANGUEL (same VK, alternative name)
            ["JunjaMode"] = 0x17,   // VK_JUNJA
            ["KanjiMode"] = 0x19,   // VK_KANJI
            ["HanjaMode"] = 0x19,   // VK_HANJA (same VK, alternative name)
            ["IMEConvert"] = 0x1C,    // VK_CONVERT
            ["IMENonconvert"] = 0x1D, // VK_NONCONVERT
            ["IMEAccept"] = 0x1E,     // VK_ACCEPT
            ["IMEAceept"] = 0x1E,     // VK_ACCEPT (note: .NET's own misspelled alias)
            ["IMEModeChange"] = 0x1F, // VK_MODECHANGE

            // --- v1.0.6: miscellaneous real keys ---
            ["Select"] = 0x29,      // VK_SELECT
            ["Print"] = 0x2A,       // VK_PRINT
            ["Execute"] = 0x2B,     // VK_EXECUTE
            ["Help"] = 0x2F,        // VK_HELP
            ["LWin"] = 0x5B,        // VK_LWIN
            ["RWin"] = 0x5C,        // VK_RWIN
            ["Apps"] = 0x5D,        // VK_APPS (context-menu key)
            ["Sleep"] = 0x5F,       // VK_SLEEP

            // --- v1.0.6: OEM symbol keys (VK_OEM_*, 0xBA–0xE2) ---
            // Both the descriptive .NET names and the numeric Oem1..Oem102 names are
            // accepted; some pairs share one virtual-key code.
            ["OemSemicolon"] = 0xBA,      // VK_OEM_1  ;:
            ["Oem1"] = 0xBA,              // alias
            ["Oemplus"] = 0xBB,           // VK_OEM_PLUS  =+
            ["Oemcomma"] = 0xBC,          // VK_OEM_COMMA ,<
            ["OemMinus"] = 0xBD,          // VK_OEM_MINUS -_
            ["OemPeriod"] = 0xBE,         // VK_OEM_PERIOD .>
            ["OemQuestion"] = 0xBF,       // VK_OEM_2  /?
            ["Oem2"] = 0xBF,              // alias
            ["Oemtilde"] = 0xC0,          // VK_OEM_3  `~
            ["Oem3"] = 0xC0,              // alias
            ["OemOpenBrackets"] = 0xDB,   // VK_OEM_4  [{
            ["Oem4"] = 0xDB,              // alias
            ["OemPipe"] = 0xDC,           // VK_OEM_5  \|
            ["Oem5"] = 0xDC,              // alias
            ["OemCloseBrackets"] = 0xDD,  // VK_OEM_6  ]}
            ["Oem6"] = 0xDD,              // alias
            ["OemQuotes"] = 0xDE,         // VK_OEM_7  '"
            ["Oem7"] = 0xDE,              // alias
            ["Oem8"] = 0xDF,              // VK_OEM_8
            ["OemBackslash"] = 0xE2,      // VK_OEM_102 (102nd key, JP/BR layouts)
            ["Oem102"] = 0xE2,            // alias
            ["OemClear"] = 0xFE,          // VK_OEM_CLEAR

            ["PrintScreen"] = 0x2C, // VK_SNAPSHOT
            ["Snapshot"] = 0x2C,    // alias used by some WinForms versions
            ["Pause"] = 0x13,       // VK_PAUSE
            ["Scroll"] = 0x91,      // VK_SCROLL
            ["ScrollLock"] = 0x91,  // alias
            ["Capital"] = 0x14,     // VK_CAPITAL (CapsLock)
            ["CapsLock"] = 0x14,    // alias
            ["NumLock"] = 0x90,     // VK_NUMLOCK
            // v1.0.5: explicit alias for the Backspace key. Keys.Back (8) is what
            // WinForms reports, but "Backspace" is the unambiguous name used in
            // hand-edited config files, so accept it too.
            ["Backspace"] = 0x08,   // VK_BACK
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

            // --- v1.0.3: Numeric keypad keys (VK_NUMPAD0 – VK_DIVIDE, 0x60–0x6F) ---
            ["NumPad0"] = 0x60,     // VK_NUMPAD0
            ["Numpad0"] = 0x60,     // alias (matched case-insensitively)
            ["NumPad1"] = 0x61,     // VK_NUMPAD1
            ["Numpad1"] = 0x61,     // alias
            ["NumPad2"] = 0x62,     // VK_NUMPAD2
            ["Numpad2"] = 0x62,     // alias
            ["NumPad3"] = 0x63,     // VK_NUMPAD3
            ["Numpad3"] = 0x63,     // alias
            ["NumPad4"] = 0x64,     // VK_NUMPAD4
            ["Numpad4"] = 0x64,     // alias
            ["NumPad5"] = 0x65,     // VK_NUMPAD5
            ["Numpad5"] = 0x65,     // alias
            ["NumPad6"] = 0x66,     // VK_NUMPAD6
            ["Numpad6"] = 0x66,     // alias
            ["NumPad7"] = 0x67,     // VK_NUMPAD7
            ["Numpad7"] = 0x67,     // alias
            ["NumPad8"] = 0x68,     // VK_NUMPAD8
            ["Numpad8"] = 0x68,     // alias
            ["NumPad9"] = 0x69,     // VK_NUMPAD9
            ["Numpad9"] = 0x69,     // alias
            ["Multiply"] = 0x6A,    // VK_MULTIPLY
            ["Add"] = 0x6B,         // VK_ADD
            ["Separator"] = 0x6C,   // VK_SEPARATOR
            ["Subtract"] = 0x6D,    // VK_SUBTRACT
            ["Decimal"] = 0x6E,     // VK_DECIMAL
            ["Divide"] = 0x6F,      // VK_DIVIDE

            // --- v1.0.3: Browser keys (0xA6–0xAC) ---
            // Note: "Home" is NOT remapped here — it stays as VK_HOME (0x24) above.
            //       Use "BrowserHome" for VK_BROWSER_HOME (0xAC).
            //
            // v1.0.5 regression fix: a bare "Back" entry used to map to 0xA6 here.
            // But Keys.Back == 8 is the BACKSPACE key, and SettingsForm captures
            // e.KeyCode.ToString() verbatim, so pressing Backspace (typically to clear
            // the old value) produced the string "Back" and was silently registered as
            // Ctrl+Alt+BrowserBack — the hotkey appeared saved but never fired.
            // "Back" is intentionally left OUT of this dictionary so that Backspace is
            // reported as an invalid key (as it was before v1.0.3). Only the explicit
            // "BrowserBack" name maps to 0xA6.
            ["BrowserBack"] = 0xA6,       // VK_BROWSER_BACK
            ["Forward"] = 0xA7,           // VK_BROWSER_FORWARD
            ["BrowserForward"] = 0xA7,    // alias
            ["Refresh"] = 0xA8,           // VK_BROWSER_REFRESH
            ["BrowserRefresh"] = 0xA8,    // alias
            ["Stop"] = 0xA9,              // VK_BROWSER_STOP
            ["BrowserStop"] = 0xA9,       // alias
            ["Search"] = 0xAA,            // VK_BROWSER_SEARCH
            ["BrowserSearch"] = 0xAA,     // alias
            ["Favorites"] = 0xAB,         // VK_BROWSER_FAVORITES
            ["BrowserFavorites"] = 0xAB,  // alias
            ["BrowserHome"] = 0xAC,       // VK_BROWSER_HOME

            // --- v1.0.3: Volume / media / launch keys (0xAD–0xB4) ---
            ["VolumeMute"] = 0xAD,        // VK_VOLUME_MUTE
            ["VolumeDown"] = 0xAE,        // VK_VOLUME_DOWN
            ["VolumeUp"] = 0xAF,          // VK_VOLUME_UP
            ["MediaNextTrack"] = 0xB0,    // VK_MEDIA_NEXT_TRACK
            ["MediaPreviousTrack"] = 0xB1, // VK_MEDIA_PREV_TRACK (.NET Keys enum full name)
            ["MediaPrevTrack"] = 0xB1,     // alias (short form)
            ["MediaStop"] = 0xB2,         // VK_MEDIA_STOP
            ["MediaPlayPause"] = 0xB3,    // VK_MEDIA_PLAY_PAUSE
            ["LaunchMail"] = 0xB4,        // VK_LAUNCH_MAIL

            // --- v1.0.6: remaining launch / media keys (0xB5–0xB7) ---
            ["SelectMedia"] = 0xB5,          // VK_LAUNCH_MEDIA_SELECT
            ["LaunchApplication1"] = 0xB6,   // VK_LAUNCH_APP1 ("My Computer")
            ["LaunchApplication2"] = 0xB7,   // VK_LAUNCH_APP2 ("Calculator")
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
