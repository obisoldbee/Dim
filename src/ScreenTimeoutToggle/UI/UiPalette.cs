using System.Drawing;

namespace OBDim.UI;

/// <summary>
/// The one place these colors are defined. MonitorForm reaches them unqualified through
/// <c>using static</c>, and the memory page's extracted controls reach them the same way — the
/// alternative was duplicating the palette into each control, which is how a theme drifts.
/// </summary>
internal static class UiPalette
{
    internal static readonly Color Accent = Color.FromArgb(0x14, 0x79, 0xFA);       // #1479fa
    internal static readonly Color BarGreen = Color.FromArgb(0x2A, 0xBD, 0x51);     // #2abd51
    internal static readonly Color BarAmber = Color.FromArgb(0xED, 0xB7, 0x28);     // #edb728
    internal static readonly Color BarRed = Color.FromArgb(0xFB, 0x30, 0x41);       // #fb3041
    internal static readonly Color CardBorder = Color.FromArgb(0xEB, 0xEB, 0xED);   // #ebebed
    internal static readonly Color TextStrong = Color.FromArgb(0x37, 0x39, 0x3D);   // #37393d
    internal static readonly Color TextPrimary = Color.FromArgb(0x49, 0x4B, 0x50);  // #494b50
    internal static readonly Color TextSecondary = Color.FromArgb(0x85, 0x86, 0x8B);// #85868b
    internal static readonly Color Hairline = Color.FromArgb(0xE9, 0xE9, 0xEB);     // #e9e9eb
    internal static readonly Color BadgeBack = Color.FromArgb(0xE9, 0xE9, 0xEB);    // #e9e9eb
    internal static readonly Color BadgeText = Color.FromArgb(0x77, 0x7A, 0x80);    // #777a80
    internal static readonly Color TrackBack = Color.FromArgb(0xF0, 0xF1, 0xF2);    // #f0f1f2
    internal static readonly Color SectionBack = Color.FromArgb(0xF4, 0xF4, 0xF5);  // #f4f4f5
    internal static readonly Color StaleText = Color.FromArgb(0x91, 0x67, 0x1D);    // #91671d
    internal static readonly Color UnlimitedBlue = Color.FromArgb(0x06, 0x73, 0xFF);// #0673ff
    internal static readonly Color PageBack = Color.White;

    /// <summary>The greyed series used when the newest sample is older than the freshness bound.</summary>
    internal static readonly Color StaleSeries = Color.FromArgb(0xB5, 0xB7, 0xBB);

    /// <summary>Tooltip surface/backdrop — the popover has no window chrome of its own.</summary>
    internal static readonly Color TooltipBack = Color.FromArgb(0xF7, 0xF8, 0xFA);
}
