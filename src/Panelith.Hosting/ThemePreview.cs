using Panelith.Expressions;
using Panelith.Rendering;
using Panelith.Themes;

namespace Panelith.Hosting;

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
        using var frame = renderer.Render(new StillBindingContext(bindings));
        return codec.EncodePng(frame);
    }

    /// <summary>
    /// Hides sample timestamps. A still frame has no clock to scroll graphs
    /// along, so they spread whatever history there is across their width.
    /// </summary>
    private sealed class StillBindingContext(IBindingContext inner) : IBindingContext, ISensorKindProvider
    {
        public DateTimeOffset Now => inner.Now;

        public BindingValue ResolveSensor(string reference) => inner.ResolveSensor(reference);

        public BindingValue ResolveVariable(string name) => inner.ResolveVariable(name);

        public IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) =>
            inner.ResolveHistory(reference, window);

        public Sensors.SensorKind KindOf(string reference) =>
            inner is ISensorKindProvider provider ? provider.KindOf(reference) : Sensors.SensorKind.Unknown;
    }
}
