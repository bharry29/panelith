using RigGlass.Expressions;
using RigGlass.Rendering;
using RigGlass.Themes;

namespace RigGlass.Cli.Rendering;

/// <summary>Renders one theme frame to a PNG, for previews and tests.</summary>
public static class ThemePreview
{
    /// <summary>Renders <paramref name="theme"/> and returns a PNG file.</summary>
    public static byte[] RenderPng(
        Theme theme,
        IBindingContext bindings,
        SkiaImageCodec codec,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        using var renderer = new ThemeRenderer(
            theme,
            new ThemeRenderOptions(width, height, ShowErrorMarkers: true, ThemeFolder: theme.BasePath));
        using var frame = renderer.Render(bindings);
        return codec.EncodePng(frame);
    }
}
