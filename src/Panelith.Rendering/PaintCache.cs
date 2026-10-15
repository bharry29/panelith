using SkiaSharp;

namespace Panelith.Rendering;

/// <summary>
/// Reusable <see cref="SKPaint"/> / <see cref="SKFont"/> instances for the
/// render loop. Allocating paints per widget per frame is the classic path to
/// pegged CPU on a 60 fps panel.
/// </summary>
internal sealed class PaintCache : IDisposable
{
    private bool _disposed;

    public SKPaint Fill { get; } = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
    };

    public SKPaint Stroke { get; } = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
    };

    public SKPaint Image { get; } = new()
    {
        IsAntialias = true,
    };

    public SKFont Font { get; } = new()
    {
        Edging = SKFontEdging.SubpixelAntialias,
        Hinting = SKFontHinting.Normal,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Fill.Dispose();
        Stroke.Dispose();
        Image.Dispose();
        Font.Dispose();
    }
}
