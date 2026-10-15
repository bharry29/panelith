using RigGlass.Themes;
using SkiaSharp;

namespace RigGlass.Rendering;

/// <summary>
/// Memoises <see cref="ThemeColor.TryParse"/> results.
/// </summary>
/// <remarks>
/// Parsing allocates (the hex slice <c>value[1..]</c>), and a 60 fps loop that
/// re-parses every widget colour every frame shows up as steady-state GC pressure.
/// Themes are immutable for the life of a renderer, so caching by string is safe.
/// </remarks>
internal sealed class ColorCache
{
    private readonly Dictionary<string, SKColor> _colors = new(StringComparer.OrdinalIgnoreCase);

    public SKColor Get(string? text, ThemeColor fallback)
    {
        if (string.IsNullOrEmpty(text))
        {
            return fallback.ToSk();
        }

        if (_colors.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var color = ThemeColor.ParseOrDefault(text, fallback).ToSk();
        _colors[text] = color;
        return color;
    }

    public SKColor Get(string? text, string? fallbackHex, ThemeColor ultimate)
    {
        if (!string.IsNullOrEmpty(text))
        {
            return Get(text, ultimate);
        }

        return Get(fallbackHex, ultimate);
    }
}
