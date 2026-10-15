using Panelith.Themes;

namespace Panelith.Rendering;

/// <summary>
/// Pure geometry that maps a theme's design canvas onto a panel of any size.
/// </summary>
/// <remarks>
/// Competing panel apps author themes in device pixels, so a 480×480 layout is
/// unusable on a 1280×480 ultrawide. Panelith keeps one design canvas and
/// applies <see cref="ThemeScaleMode"/> plus per-widget <see cref="AnchorEdges"/>
/// at render time. This type is deliberately free of Skia so the layout math can
/// be unit-tested without a graphics stack.
/// </remarks>
public readonly record struct LayoutTransform(
    double ScaleX,
    double ScaleY,
    double OffsetX,
    double OffsetY,
    int TargetWidth,
    int TargetHeight,
    int CanvasWidth,
    int CanvasHeight)
{
    /// <summary>Whether the two axes share a uniform scale.</summary>
    public bool IsUniform => Math.Abs(ScaleX - ScaleY) < 1e-9;

    /// <summary>Maps a design-space point into device pixels (ignoring anchors).</summary>
    public ThemeRect MapRect(ThemeRect design) =>
        new(
            OffsetX + (design.X * ScaleX),
            OffsetY + (design.Y * ScaleY),
            design.Width * ScaleX,
            design.Height * ScaleY);
}

/// <summary>
/// Computes canvas→panel transforms and per-widget device-space rectangles.
/// </summary>
public static class ThemeLayout
{
    /// <summary>
    /// Builds the base transform for a canvas onto a target panel under
    /// <paramref name="mode"/>. Letterbox / crop offsets are centred.
    /// </summary>
    public static LayoutTransform ComputeTransform(
        int canvasWidth,
        int canvasHeight,
        int targetWidth,
        int targetHeight,
        ThemeScaleMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        double scaleX;
        double scaleY;
        double offsetX;
        double offsetY;

        switch (mode)
        {
            case ThemeScaleMode.Stretch:
                scaleX = (double)targetWidth / canvasWidth;
                scaleY = (double)targetHeight / canvasHeight;
                offsetX = 0d;
                offsetY = 0d;
                break;

            case ThemeScaleMode.None:
                // 1:1 pixels; centre the design canvas inside a larger panel so
                // un-anchored content stays readable rather than stuck at (0,0).
                scaleX = 1d;
                scaleY = 1d;
                offsetX = (targetWidth - canvasWidth) / 2d;
                offsetY = (targetHeight - canvasHeight) / 2d;
                break;

            case ThemeScaleMode.Fill:
            {
                var scale = Math.Max(
                    (double)targetWidth / canvasWidth,
                    (double)targetHeight / canvasHeight);
                scaleX = scale;
                scaleY = scale;
                offsetX = (targetWidth - (canvasWidth * scale)) / 2d;
                offsetY = (targetHeight - (canvasHeight * scale)) / 2d;
                break;
            }

            default: // Fit
            {
                var scale = Math.Min(
                    (double)targetWidth / canvasWidth,
                    (double)targetHeight / canvasHeight);
                scaleX = scale;
                scaleY = scale;
                offsetX = (targetWidth - (canvasWidth * scale)) / 2d;
                offsetY = (targetHeight - (canvasHeight * scale)) / 2d;
                break;
            }
        }

        return new LayoutTransform(
            scaleX, scaleY, offsetX, offsetY,
            targetWidth, targetHeight, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// Maps a widget's design-space rectangle into device pixels, honouring
    /// <paramref name="anchors"/>.
    /// </summary>
    /// <remarks>
    /// An anchored edge keeps its authored distance from that edge of the
    /// <em>target</em>, not of the scaled canvas. Opposite anchors stretch the
    /// widget between the two pinned edges — that is what lets a square theme
    /// look right on an ultrawide panel without a second layout file.
    /// </remarks>
    public static ThemeRect MapWidget(
        LayoutTransform transform,
        ThemeRect design,
        AnchorEdges anchors)
    {
        if (anchors == AnchorEdges.None)
        {
            return transform.MapRect(design);
        }

        var left = transform.OffsetX + (design.X * transform.ScaleX);
        var top = transform.OffsetY + (design.Y * transform.ScaleY);
        var right = transform.OffsetX + (design.Right * transform.ScaleX);
        var bottom = transform.OffsetY + (design.Bottom * transform.ScaleY);

        // Distances from the design canvas edges, preserved in device pixels
        // when that edge is anchored.
        var distLeft = design.X;
        var distTop = design.Y;
        var distRight = transform.CanvasWidth - design.Right;
        var distBottom = transform.CanvasHeight - design.Bottom;

        if ((anchors & AnchorEdges.Left) != 0)
        {
            left = distLeft;
        }

        if ((anchors & AnchorEdges.Top) != 0)
        {
            top = distTop;
        }

        if ((anchors & AnchorEdges.Right) != 0)
        {
            right = transform.TargetWidth - distRight;
        }

        if ((anchors & AnchorEdges.Bottom) != 0)
        {
            bottom = transform.TargetHeight - distBottom;
        }

        // Single-edge horizontal anchor: keep authored width (in device space
        // under the current scale) rather than stretching to the far side.
        if ((anchors & AnchorEdges.LeftRight) == AnchorEdges.Left)
        {
            right = left + (design.Width * transform.ScaleX);
        }
        else if ((anchors & AnchorEdges.LeftRight) == AnchorEdges.Right)
        {
            left = right - (design.Width * transform.ScaleX);
        }

        if ((anchors & AnchorEdges.TopBottom) == AnchorEdges.Top)
        {
            bottom = top + (design.Height * transform.ScaleY);
        }
        else if ((anchors & AnchorEdges.TopBottom) == AnchorEdges.Bottom)
        {
            top = bottom - (design.Height * transform.ScaleY);
        }

        return new ThemeRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Convenience overload taking a <see cref="ThemeCanvas"/> and target size.
    /// </summary>
    public static ThemeRect MapWidget(
        ThemeCanvas canvas,
        int targetWidth,
        int targetHeight,
        ThemeRect design,
        AnchorEdges anchors,
        ThemeScaleMode? modeOverride = null)
    {
        var transform = ComputeTransform(
            canvas.Width,
            canvas.Height,
            targetWidth,
            targetHeight,
            modeOverride ?? canvas.Scaling);
        return MapWidget(transform, design, anchors);
    }

    /// <summary>
    /// The rectangle of the design canvas in device pixels (useful for clipping
    /// letterboxed content under <see cref="ThemeScaleMode.Fit"/>).
    /// </summary>
    public static ThemeRect CanvasDeviceRect(LayoutTransform transform) =>
        new(
            transform.OffsetX,
            transform.OffsetY,
            transform.CanvasWidth * transform.ScaleX,
            transform.CanvasHeight * transform.ScaleY);
}
