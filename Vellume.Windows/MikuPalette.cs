using Color = System.Windows.Media.Color;

namespace Vellume.Windows;

/// <summary>
/// Shared Hatsune Miku-inspired palette used by native desktop overlays.
/// </summary>
internal static class MikuPalette
{
    internal static readonly Color Graphite =
        Color.FromRgb(55, 59, 62);       // #373B3E
    internal static readonly Color Silver =
        Color.FromRgb(190, 200, 209);    // #BEC8D1
    internal static readonly Color Mint =
        Color.FromRgb(134, 206, 203);    // #86CECB
    internal static readonly Color Teal =
        Color.FromRgb(19, 122, 127);     // #137A7F
    internal static readonly Color Pink =
        Color.FromRgb(225, 40, 133);     // #E12885

    internal static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(
            alpha,
            color.R,
            color.G,
            color.B);
}
