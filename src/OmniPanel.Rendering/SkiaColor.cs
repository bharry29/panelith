using OmniPanel.Themes;
using SkiaSharp;

namespace OmniPanel.Rendering;

/// <summary>Conversions between <see cref="ThemeColor"/> and Skia colours.</summary>
/// <remarks>
/// Theme colours are straight (non-premultiplied) RGBA. Skia paints must be told
/// <see cref="SKAlphaType.Unpremul"/> when sampling bitmaps or colours shift —
/// especially visible on semi-transparent reds.
/// </remarks>
internal static class SkiaColor
{
    public static SKColor ToSk(this ThemeColor color) =>
        new(color.R, color.G, color.B, color.A);

    public static ThemeColor ToTheme(this SKColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);

    public static SKColor ParseOr(string? text, ThemeColor fallback) =>
        ThemeColor.ParseOrDefault(text, fallback).ToSk();

    public static SKColor ParseOr(string? text, SKColor fallback) =>
        ThemeColor.TryParse(text, out var color) ? color.ToSk() : fallback;

    public static SKColor WithOpacity(this SKColor color, double opacity)
    {
        opacity = Math.Clamp(opacity, 0d, 1d);
        return color.WithAlpha((byte)Math.Clamp(Math.Round(color.Alpha * opacity), 0d, 255d));
    }
}
