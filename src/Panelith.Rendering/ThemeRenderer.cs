using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Panelith.Expressions;
using Panelith.Imaging;
using Panelith.Rendering.Widgets;
using Panelith.Themes;
using SkiaSharp;

namespace Panelith.Rendering;

/// <summary>
/// Renders a <see cref="Theme"/> into a straight-RGBA <see cref="FrameBuffer"/>.
/// </summary>
/// <remarks>
/// Designed for a 15–60 fps panel loop: the surface, paints, typefaces, decoded
/// images and layout rectangles are cached and only rebuilt when the target size
/// changes. A widget that throws is skipped and logged once — a bad theme must
/// never take down the render thread.
/// </remarks>
public sealed class ThemeRenderer : IDisposable
{
    private readonly Theme _theme;
    private readonly ThemeRenderOptions _options;
    private readonly ILogger? _logger;
    private readonly FontResolver _fonts;
    private readonly ThemeAssetCache _assets;
    private readonly PaintCache _paints = new();
    private readonly ColorCache _colors = new();
    private readonly RenderDiagnostics _diagnostics = new();
    private readonly Dictionary<ThemeWidget, ThemeRect> _layout = new(ReferenceEqualityComparer.Instance);
    private readonly ThemeBindingContext _bindings;
    private readonly WidgetRenderContext _widgetContext = new();
    private readonly Dictionary<ThemeWidget, object> _widgetState = new(ReferenceEqualityComparer.Instance);
    private readonly SKPixmap _readback = new();

    private LayoutTransform _transform;
    private int _width;
    private int _height;
    private SKSurface? _surface;
    private SKImageInfo _info;
    private SKColor _letterboxColor;
    private SKColor _backgroundColor;
    private SKColor? _backgroundGradientTo;
    private bool _disposed;

    /// <summary>Creates a renderer for <paramref name="theme"/>.</summary>
    public ThemeRenderer(Theme theme, ThemeRenderOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Height);

        _theme = theme;
        _options = options;
        _logger = logger;
        _bindings = new ThemeBindingContext(theme);

        var folder = options.ThemeFolder ?? theme.BasePath;
        _fonts = new FontResolver(folder, logger);
        _assets = new ThemeAssetCache(folder, capacity: options.MaxCachedImages, logger: logger);

        _letterboxColor = ThemeColor.ParseOrDefault(theme.Canvas.Background, ThemeColor.Black).ToSk();
        _backgroundColor = ThemeColor.ParseOrDefault(theme.Background.Color, ThemeColor.Black).ToSk();
        _backgroundGradientTo = theme.Background.GradientTo is { } g
            ? ThemeColor.ParseOrDefault(g, ThemeColor.Black).ToSk()
            : null;

        EnsureSurface(options.Width, options.Height);
        Relayout();
    }

    /// <summary>Per-frame timing and skip counters.</summary>
    public RenderStats Stats => _diagnostics.Stats;

    /// <summary>Rents a new frame and renders into it. The caller owns the buffer.</summary>
    public FrameBuffer Render(IBindingContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = FrameBuffer.Rent(_width, _height);
        try
        {
            RenderInto(buffer, context);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renders into an existing buffer. Preferred for the hot loop because it
    /// avoids renting from the array pool every frame.
    /// </summary>
    public void RenderInto(FrameBuffer target, IBindingContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        if (target.Width != _width || target.Height != _height)
        {
            EnsureSurface(target.Width, target.Height);
            Relayout();
        }

        var started = Stopwatch.GetTimestamp();
        _diagnostics.BeginFrame();

        _bindings.Reset(context);
        var canvas = _surface!.Canvas;
        canvas.Clear(SKColors.Transparent);

        DrawLetterbox(canvas);
        DrawBackground(canvas);

        var ctx = _widgetContext;
        ctx.Canvas = canvas;
        ctx.Bindings = _bindings;
        ctx.Defaults = _theme.Defaults;
        ctx.Fonts = _fonts;
        ctx.Assets = _assets;
        ctx.Paints = _paints;
        ctx.Colors = _colors;
        ctx.Diagnostics = _diagnostics;
        ctx.Logger = _logger;
        ctx.ShowErrorMarkers = _options.ShowErrorMarkers;
        ctx.Now = context.Now;
        ctx.Ticks = started;
        ctx.State = _widgetState;
        ctx.Opacity = 1d;

        // Index by Count rather than foreach: collection-expression backends such
        // as ReadOnlySingleElementList allocate an enumerator on foreach, which
        // shows up as steady-state GC on a 60 fps panel loop.
        var widgets = _theme.Widgets;
        for (var i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            if (!_layout.TryGetValue(widget, out var rect))
            {
                rect = ThemeLayout.MapWidget(_transform, widget.Bounds, widget.Anchor);
            }

            RenderWidget(widget, rect, ctx);
        }

        FlushTo(target);
        _diagnostics.EndFrame(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Resizes the renderer for a different panel.</summary>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == _width && height == _height)
        {
            return;
        }

        EnsureSurface(width, height);
        Relayout();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface?.Dispose();
        _surface = null;
        _readback.Dispose();
        _paints.Dispose();
        _fonts.Dispose();
        _assets.Dispose();
    }

    private void RenderWidget(ThemeWidget widget, ThemeRect rect, WidgetRenderContext ctx)
    {
        try
        {
            _bindings.UseRaw = _theme.Defaults.Motion == false || !widget.Animate;
            if (!IsVisible(widget, ctx.Bindings))
            {
                return;
            }

            if (widget.Opacity <= 0d)
            {
                return;
            }

            var previousOpacity = ctx.Opacity;
            var needsRotate = widget.Rotation != 0d && !rect.IsEmpty;
            if (needsRotate)
            {
                ctx.Canvas.Save();
                ctx.Canvas.RotateDegrees((float)widget.Rotation, (float)rect.CenterX, (float)rect.CenterY);
            }

            // Groups multiply opacity internally so children inherit it; leaf
            // widgets apply their own factor here.
            if (widget is not GroupWidget && widget.Opacity < 1d)
            {
                ctx.Opacity = Math.Clamp(previousOpacity * widget.Opacity, 0d, 1d);
            }

            switch (widget)
            {
                case TextWidget text:
                    TextWidgetRenderer.Render(text, rect, ctx);
                    break;
                case BarWidget bar:
                    BarWidgetRenderer.Render(bar, rect, ctx);
                    break;
                case GaugeWidget gauge:
                    GaugeWidgetRenderer.Render(gauge, rect, ctx);
                    break;
                case GraphWidget graph:
                    GraphWidgetRenderer.Render(graph, rect, ctx);
                    break;
                case ImageWidget image:
                    ImageWidgetRenderer.Render(image, rect, ctx);
                    break;
                case ShapeWidget shape:
                    ShapeWidgetRenderer.Render(shape, rect, ctx);
                    break;
                case GroupWidget group:
                    GroupWidgetRenderer.Render(group, rect, ctx, RenderWidget);
                    break;
            }

            ctx.Opacity = previousOpacity;
            if (needsRotate)
            {
                ctx.Canvas.Restore();
            }
        }
        catch (Exception ex)
        {
            ctx.ReportWidgetFailure(widget, ex);
            ctx.DrawErrorMarker(rect);
        }
    }

    private static bool IsVisible(ThemeWidget widget, IBindingContext bindings)
    {
        if (string.IsNullOrWhiteSpace(widget.VisibleWhen))
        {
            return true;
        }

        if (!BindingExpression.TryParse(widget.VisibleWhen, out var expression, out _))
        {
            return true;
        }

        return expression!.Evaluate(bindings).AsBoolean();
    }

    private void DrawLetterbox(SKCanvas canvas)
    {
        // Under Fit, the letterbox bars use the canvas background colour so the
        // panel never flashes an undefined colour around the content.
        if (_theme.Canvas.Scaling != ThemeScaleMode.Fit && _theme.Canvas.Scaling != ThemeScaleMode.None)
        {
            return;
        }

        canvas.Clear(_letterboxColor);
    }

    private void DrawBackground(SKCanvas canvas)
    {
        var bg = _theme.Background;
        var content = ThemeLayout.CanvasDeviceRect(_transform);
        var dest = new SKRect(
            (float)content.X,
            (float)content.Y,
            (float)content.Right,
            (float)content.Bottom);

        // Under Fill/Stretch the content covers the panel; under Fit/None we
        // paint only inside the design canvas rectangle.
        if (_theme.Canvas.Scaling is ThemeScaleMode.Fill or ThemeScaleMode.Stretch)
        {
            dest = new SKRect(0, 0, _width, _height);
        }

        if (_backgroundGradientTo is { } to && bg.Color is not null)
        {
            var angle = bg.GradientAngle * Math.PI / 180d;
            var cx = dest.MidX;
            var cy = dest.MidY;
            var dx = (float)(Math.Cos(angle) * dest.Width / 2d);
            var dy = (float)(Math.Sin(angle) * dest.Height / 2d);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(cx - dx, cy - dy),
                new SKPoint(cx + dx, cy + dy),
                [_backgroundColor, to],
                SKShaderTileMode.Clamp);
            var paint = _paints.Fill;
            paint.Shader = shader;
            paint.Color = SKColors.White;
            canvas.DrawRect(dest, paint);
            paint.Shader = null;
        }
        else
        {
            var paint = _paints.Fill;
            paint.Color = _backgroundColor;
            paint.Shader = null;
            canvas.DrawRect(dest, paint);
        }

        if (!string.IsNullOrWhiteSpace(bg.Image))
        {
            var bitmap = _assets.GetBitmap(bg.Image);
            var paint = _paints.Image;
            paint.Color = SKColors.White;
            paint.ColorFilter = null;
            ImageFitHelper.Draw(canvas, bitmap, dest, bg.Fit, paint);
        }
    }

    private void FlushTo(FrameBuffer target)
    {
        // Reuse a single SKPixmap so the hot loop does not allocate a wrapper
        // object every frame.
        if (!_surface!.PeekPixels(_readback))
        {
            throw new InvalidOperationException("Skia surface has no pixel backing.");
        }

        _readback.GetPixelSpan()[..target.ByteLength].CopyTo(target.Span);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_surface is not null && _width == width && _height == height)
        {
            return;
        }

        _surface?.Dispose();
        _width = width;
        _height = height;
        _info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        _surface = SKSurface.Create(_info)
            ?? throw new InvalidOperationException($"Failed to create SKSurface ({width}x{height}).");
    }

    private void Relayout()
    {
        _transform = ThemeLayout.ComputeTransform(
            _theme.Canvas.Width,
            _theme.Canvas.Height,
            _width,
            _height,
            _theme.Canvas.Scaling);

        _layout.Clear();
        var widgets = _theme.Widgets;
        for (var i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            _layout[widget] = ThemeLayout.MapWidget(_transform, widget.Bounds, widget.Anchor);
        }
    }
}
