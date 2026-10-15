using OmniPanel.Imaging;

namespace OmniPanel.Devices.Encoding;

/// <summary>
/// Rotates and mirrors a frame into the panel's native scan order.
/// </summary>
/// <remarks>
/// <para>
/// Most of these panels cannot rotate in hardware, and the ones that can
/// disagree about which way is "up". Rotating in software keeps the renderer
/// free of device quirks: it always draws at the logical size the user chose,
/// and this step puts the result into whatever order the hardware wants.
/// </para>
/// <para>
/// The 180-degree case is handled by a reverse copy rather than by the general
/// transform, because it is the common one -- a panel mounted upside down in a
/// case -- and the fast path matters at 20 frames per second.
/// </para>
/// </remarks>
public static class FrameOrientation
{
    /// <summary>
    /// The size a frame becomes after being rotated by <paramref name="orientation"/>.
    /// </summary>
    public static (int Width, int Height) Transform(int width, int height, PanelOrientation orientation) =>
        orientation is PanelOrientation.Landscape or PanelOrientation.ReverseLandscape
            ? (height, width)
            : (width, height);

    /// <summary>
    /// Whether <paramref name="orientation"/> and <paramref name="mirror"/>
    /// together leave the frame unchanged, so the copy can be skipped.
    /// </summary>
    public static bool IsIdentity(PanelOrientation orientation, bool mirror) =>
        orientation == PanelOrientation.Portrait && !mirror;

    /// <summary>
    /// Writes <paramref name="source"/> into <paramref name="destination"/>,
    /// applying a rotation and an optional horizontal mirror.
    /// </summary>
    /// <param name="source">The rendered frame.</param>
    /// <param name="destination">
    /// Receives the transformed frame. Its dimensions must match
    /// <see cref="Transform"/> applied to the source.
    /// </param>
    /// <param name="orientation">Clockwise rotation to apply.</param>
    /// <param name="mirror">Whether to mirror horizontally after rotating.</param>
    /// <exception cref="ArgumentException">The destination is the wrong size.</exception>
    public static void Apply(
        FrameBuffer source,
        FrameBuffer destination,
        PanelOrientation orientation,
        bool mirror = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var (expectedWidth, expectedHeight) = Transform(source.Width, source.Height, orientation);
        if (destination.Width != expectedWidth || destination.Height != expectedHeight)
        {
            throw new ArgumentException(
                $"A {source.Width}x{source.Height} frame rotated by {(int)orientation} degrees needs a "
                + $"{expectedWidth}x{expectedHeight} destination, not {destination.Width}x{destination.Height}.",
                nameof(destination));
        }

        if (!mirror && orientation == PanelOrientation.Portrait)
        {
            source.CopyTo(destination);
            return;
        }

        if (!mirror && orientation == PanelOrientation.ReversePortrait)
        {
            ReverseCopy(source, destination);
            return;
        }

        TransformPixels(source, destination, orientation, mirror);
    }

    /// <summary>
    /// Rotates into a freshly rented buffer, returning the source unchanged when
    /// no transform is needed.
    /// </summary>
    /// <param name="source">The rendered frame.</param>
    /// <param name="orientation">Clockwise rotation to apply.</param>
    /// <param name="mirror">Whether to mirror horizontally.</param>
    /// <param name="rented">
    /// <see langword="true"/> when the returned buffer is new and the caller owns
    /// it, <see langword="false"/> when it is <paramref name="source"/> itself.
    /// </param>
    public static FrameBuffer ApplyOrPassThrough(
        FrameBuffer source,
        PanelOrientation orientation,
        bool mirror,
        out bool rented)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (IsIdentity(orientation, mirror))
        {
            rented = false;
            return source;
        }

        var (width, height) = Transform(source.Width, source.Height, orientation);
        var destination = FrameBuffer.Rent(width, height);
        Apply(source, destination, orientation, mirror);
        rented = true;
        return destination;
    }

    /// <summary>A 180-degree rotation is the source read backwards, one pixel at a time.</summary>
    private static void ReverseCopy(FrameBuffer source, FrameBuffer destination)
    {
        var src = source.ReadOnlySpan;
        var dst = destination.Span;
        var pixels = source.Width * source.Height;

        for (var i = 0; i < pixels; i++)
        {
            var from = i * FrameBuffer.BytesPerPixel;
            var to = (pixels - 1 - i) * FrameBuffer.BytesPerPixel;
            dst[to] = src[from];
            dst[to + 1] = src[from + 1];
            dst[to + 2] = src[from + 2];
            dst[to + 3] = src[from + 3];
        }
    }

    private static void TransformPixels(
        FrameBuffer source,
        FrameBuffer destination,
        PanelOrientation orientation,
        bool mirror)
    {
        var src = source.ReadOnlySpan;
        var dst = destination.Span;
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var destinationWidth = destination.Width;

        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                var (dx, dy) = orientation switch
                {
                    PanelOrientation.Landscape => (sourceHeight - 1 - y, x),
                    PanelOrientation.ReversePortrait => (sourceWidth - 1 - x, sourceHeight - 1 - y),
                    PanelOrientation.ReverseLandscape => (y, sourceWidth - 1 - x),
                    _ => (x, y),
                };

                if (mirror)
                {
                    dx = destinationWidth - 1 - dx;
                }

                var from = ((y * sourceWidth) + x) * FrameBuffer.BytesPerPixel;
                var to = ((dy * destinationWidth) + dx) * FrameBuffer.BytesPerPixel;
                dst[to] = src[from];
                dst[to + 1] = src[from + 1];
                dst[to + 2] = src[from + 2];
                dst[to + 3] = src[from + 3];
            }
        }
    }
}
