using OBDim.Models;

namespace OBDim.Services;

/// <summary>
/// Pure decision logic for "what exactly gets persisted after the settings dialog closes".
/// </summary>
/// <remarks>
/// Extracted from <c>TrayApp.OpenSettings</c> so the hotkey rollback path can be unit
/// tested without instantiating any WinForms type. Keeping it free of UI and I/O is what
/// makes the "rollback must not clobber the user's other changes" rule testable.
/// </remarks>
public static class ConfigResolver
{
    /// <summary>
    /// Decides the configuration that should be persisted after a settings dialog commit.
    /// </summary>
    /// <param name="oldCfg">The configuration in effect before the dialog was opened.</param>
    /// <param name="newCfg">The configuration the user submitted from the dialog.</param>
    /// <param name="hotkeyRegistered">
    /// Result of attempting to register <paramref name="newCfg"/>'s hotkey. Only meaningful
    /// when the hotkey actually changed; ignored otherwise.
    /// </param>
    /// <returns>
    /// <paramref name="newCfg"/> as-is when the new hotkey took effect (or when the hotkey
    /// was not changed at all); otherwise <paramref name="newCfg"/> with only the hotkey
    /// rolled back to <paramref name="oldCfg"/>'s, preserving every other user edit.
    /// </returns>
    public static AppConfig ResolveEffectiveConfig(AppConfig oldCfg, AppConfig newCfg, bool hotkeyRegistered)
    {
        ArgumentNullException.ThrowIfNull(oldCfg);
        ArgumentNullException.ThrowIfNull(newCfg);

        // The hotkey was not touched, so no registration was attempted and there is
        // nothing to roll back — the user's other changes must survive untouched.
        if (Equals(oldCfg.Hotkey, newCfg.Hotkey))
            return newCfg;

        // Registration failed: keep the old hotkey, keep everything else the user chose.
        return hotkeyRegistered
            ? newCfg
            : newCfg with { Hotkey = oldCfg.Hotkey };
    }
}
