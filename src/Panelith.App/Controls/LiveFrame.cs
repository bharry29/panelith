using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Panelith.Imaging;

namespace Panelith.App.Controls;

/// <summary>
/// A bitmap that is repainted in place, for views that show a panel frame many
/// times a second without allocating a new image each time.
/// </summary>
/// <remarks>Must be updated on the UI thread.</remarks>
public sealed class LiveFrame : IDisposable
{
    /// <summary>The current image, or null before the first update.</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Raised after the pixels change.</summary>
    public event EventHandler? Changed;

    /// <summary>Copies a rendered frame in.</summary>
    public void Update(FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Update(frame.Width, frame.Height, (destination, stride) =>
        {
            var rowBytes = frame.Stride;
            var source = frame.ReadOnlySpan;
            for (var y = 0; y < frame.Height; y++)
            {
                source.Slice(y * rowBytes, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
            }

            return true;
        });
    }

    /// <summary>
    /// Resizes if needed and lets <paramref name="copy"/> write straight RGBA
    /// rows into the bitmap. <paramref name="copy"/> returns false to leave the
    /// image untouched.
    /// </summary>
    public unsafe void Update(int width, int height, CopyPixels copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Bitmap is null || Bitmap.PixelSize.Width != width || Bitmap.PixelSize.Height != height)
        {
            Bitmap?.Dispose();
            Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        }

        bool copied;
        using (var locked = Bitmap.Lock())
        {
            var span = new Span<byte>((void*)locked.Address, locked.RowBytes * height);
            copied = copy(span, locked.RowBytes);
        }

        if (copied)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Bitmap?.Dispose();
        Bitmap = null;
    }

    /// <summary>Writes pixels into a destination with the given row stride.</summary>
    public delegate bool CopyPixels(Span<byte> destination, int stride);
}
