using RigGlass.Themes;
using SkiaSharp;

namespace RigGlass.Rendering;

/// <summary>
/// Interpolates <see cref="ColorStop"/> lists for value-driven colouring.
/// </summary>
/// <remarks>
/// Theme validation treats stops as ascending thresholds. Rendering interpolates
/// between neighbouring stops so a temperature bar can fade from green through
/// amber into red rather than hard-switching at each boundary.
/// </remarks>
public static class ColorStopResolver
{
    /// <summary>
    /// Resolves the colour for <paramref name="value"/> against
    /// <paramref name="stops"/>. Returns <paramref name="fallback"/> when the
    /// list is empty or no stop colour parses.
    /// </summary>
    public static ThemeColor Resolve(
        IReadOnlyList<ColorStop>? stops,
        double value,
        ThemeColor fallback)
    {
        if (stops is null || stops.Count == 0)
        {
            return fallback;
        }

        // Ensure we walk ascending even if a theme author drifted.
        ColorStop? first = null;
        ColorStop? last = null;
        ColorStop? lower = null;
        ColorStop? upper = null;

        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            first ??= stop;
            last = stop;

            if (stop.At <= value)
            {
                lower = stop;
            }
            else if (upper is null)
            {
                upper = stop;
            }
        }

        if (lower is null)
        {
            // Below every stop — use the first colour solidly.
            return ThemeColor.ParseOrDefault(first!.Value.Color, fallback);
        }

        if (upper is null)
        {
            // At or above the last stop.
            return ThemeColor.ParseOrDefault(lower.Value.Color, fallback);
        }

        var lo = lower.Value;
        var hi = upper.Value;
        var span = hi.At - lo.At;
        var t = span <= 0d ? 0d : (value - lo.At) / span;
        var loColor = ThemeColor.ParseOrDefault(lo.Color, fallback);
        var hiColor = ThemeColor.ParseOrDefault(hi.Color, fallback);
        return loColor.LerpTo(hiColor, t);
    }

    /// <summary>Skia-coloured variant of <see cref="Resolve"/>.</summary>
    public static SKColor ResolveSk(
        IReadOnlyList<ColorStop>? stops,
        double value,
        SKColor fallback) =>
        Resolve(stops, value, fallback.ToTheme()).ToSk();
}
