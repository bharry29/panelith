using Microsoft.Extensions.Logging;
using Panelith.Expressions;
using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering;

/// <summary>Mutable per-frame state handed to widget renderers — reused across frames.</summary>
internal sealed class WidgetRenderContext
{
    public SKCanvas Canvas { get; set; } = null!;
    public IBindingContext Bindings { get; set; } = EmptyBindingContext.Instance;
    public ThemeDefaults Defaults { get; set; }
    public FontResolver Fonts { get; set; } = null!;
    public ThemeAssetCache Assets { get; set; } = null!;
    public VideoClipCache Clips { get; set; } = null!;
    public PaintCache Paints { get; set; } = null!;
    public ColorCache Colors { get; set; } = null!;
    public RenderDiagnostics Diagnostics { get; set; } = null!;
    public ILogger? Logger { get; set; }
    public bool ShowErrorMarkers { get; set; }
    public DateTimeOffset Now { get; set; }
    public double Opacity { get; set; } = 1d;

    /// <summary>A monotonic timestamp for this frame, in <see cref="System.Diagnostics.Stopwatch"/> ticks, for widget easing.</summary>
    public long Ticks { get; set; }

    /// <summary>State widgets keep between frames, such as an eased graph range.</summary>
    public Dictionary<ThemeWidget, object> State { get; set; } = new(ReferenceEqualityComparer.Instance);

    public T StateFor<T>(ThemeWidget widget)
        where T : class, new()
    {
        if (State.TryGetValue(widget, out var existing) && existing is T typed)
        {
            return typed;
        }

        var created = new T();
        State[widget] = created;
        return created;
    }

    /// <summary>Seconds between two values of <see cref="Ticks"/>.</summary>
    public static double SecondsBetween(long from, long to) =>
        (to - from) / (double)System.Diagnostics.Stopwatch.Frequency;

    public SKColor ResolveColor(string? text, string? fallbackHex) =>
        Colors.Get(text, fallbackHex, ThemeColor.White).WithOpacity(Opacity);

    public void DrawErrorMarker(ThemeRect rect)
    {
        if (!ShowErrorMarkers || rect.IsEmpty)
        {
            return;
        }

        var paint = Paints.Stroke;
        paint.Color = new SKColor(220, 64, 160, (byte)Math.Clamp(Math.Round(160 * Opacity), 0, 255));
        paint.StrokeWidth = 1f;
        Canvas.DrawRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom, paint);
        Canvas.DrawLine((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom, paint);
    }

    public void ReportWidgetFailure(ThemeWidget widget, Exception ex)
    {
        Diagnostics.NoteSkip();
        var key = widget.Id ?? widget.GetType().Name + ":" + widget.X + "," + widget.Y;
        if (!Diagnostics.ShouldReport(key))
        {
            return;
        }

        Logger?.LogWarning(ex, "Widget '{WidgetId}' failed to render and will be skipped.", widget.Id ?? widget.GetType().Name);
    }
}
