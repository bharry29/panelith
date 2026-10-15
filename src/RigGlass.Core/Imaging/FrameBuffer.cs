using System.Buffers;

namespace RigGlass.Imaging;

/// <summary>
/// A rented, reusable 32-bit RGBA image buffer: the hand-off format between the
/// renderer and the device encoders.
/// </summary>
/// <remarks>
/// <para>
/// Panels are small but are driven continuously, so a naive pipeline allocates a
/// multi-megabyte bitmap many times a second and keeps the garbage collector
/// permanently busy. That shows up directly as the "high CPU usage" complaint
/// levelled at most panel software. <see cref="FrameBuffer"/> instead rents from
/// <see cref="ArrayPool{T}"/> and is returned to the pool on
/// <see cref="Dispose"/>.
/// </para>
/// <para>
/// Pixels are stored as straight (non-premultiplied) RGBA bytes in row-major
/// order with no padding between rows, because every device encoder needs
/// per-channel access and premultiplied alpha would have to be undone first.
/// </para>
/// </remarks>
public sealed class FrameBuffer : IDisposable
{
    /// <summary>Bytes per pixel.</summary>
    public const int BytesPerPixel = 4;

    private byte[]? _pixels;

    private FrameBuffer(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>Number of meaningful bytes in <see cref="Span"/>.</summary>
    public int ByteLength => Stride * Height;

    /// <summary>Whether this buffer has been returned to the pool.</summary>
    public bool IsDisposed => _pixels is null;

    /// <summary>
    /// Rents a buffer of the given size. The contents are not cleared; callers
    /// are expected to paint every pixel, which the renderer always does.
    /// </summary>
    public static FrameBuffer Rent(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixels = ArrayPool<byte>.Shared.Rent(width * height * BytesPerPixel);
        return new FrameBuffer(width, height, pixels);
    }

    /// <summary>Rents a buffer and clears it to fully transparent black.</summary>
    public static FrameBuffer RentCleared(int width, int height)
    {
        var buffer = Rent(width, height);
        buffer.Span.Clear();
        return buffer;
    }

    /// <summary>
    /// Wraps an existing array without renting. Used by tests and by decoders
    /// that already own a correctly-sized array.
    /// </summary>
    /// <exception cref="ArgumentException">The array is too small.</exception>
    public static FrameBuffer Wrap(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        var required = width * height * BytesPerPixel;
        if (pixels.Length < required)
        {
            throw new ArgumentException(
                $"Buffer of {pixels.Length} bytes is too small for {width}x{height} RGBA ({required} bytes).",
                nameof(pixels));
        }

        return new FrameBuffer(width, height, pixels);
    }

    /// <summary>The writable pixel data.</summary>
    /// <exception cref="ObjectDisposedException">The buffer was already disposed.</exception>
    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            return _pixels.AsSpan(0, ByteLength);
        }
    }

    /// <summary>The pixel data, read-only.</summary>
    /// <exception cref="ObjectDisposedException">The buffer was already disposed.</exception>
    public ReadOnlySpan<byte> ReadOnlySpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            return _pixels.AsSpan(0, ByteLength);
        }
    }

    /// <summary>
    /// The backing array, which may be longer than <see cref="ByteLength"/>
    /// because it came from a pool. Needed by interop that takes an array.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The buffer was already disposed.</exception>
    public byte[] Array
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            return _pixels;
        }
    }

    /// <summary>The bytes of one row.</summary>
    public Span<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Span.Slice(y * Stride, Stride);
    }

    /// <summary>Copies the pixels into a new array sized exactly to the image.</summary>
    public byte[] ToArray() => ReadOnlySpan.ToArray();

    /// <summary>Copies these pixels into <paramref name="destination"/>, which must match in size.</summary>
    /// <exception cref="ArgumentException">The destination has different dimensions.</exception>
    public void CopyTo(FrameBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Width != Width || destination.Height != Height)
        {
            throw new ArgumentException(
                $"Cannot copy a {Width}x{Height} frame into a {destination.Width}x{destination.Height} frame.",
                nameof(destination));
        }

        ReadOnlySpan.CopyTo(destination.Span);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
