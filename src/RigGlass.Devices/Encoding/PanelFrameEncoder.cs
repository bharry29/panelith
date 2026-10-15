using RigGlass.Imaging;

namespace RigGlass.Devices.Encoding;

/// <summary>
/// Converts a rendered RGBA frame into the exact bytes a panel expects.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place where orientation, mirroring and pixel packing
/// happen, so that every driver gets the same behaviour and only has to
/// describe <em>what</em> its panel wants rather than reimplement <em>how</em>.
/// </para>
/// <para>
/// Everything here is pure and allocation-light: the hot path writes into a
/// caller-supplied span so that a steady-state render loop allocates nothing per
/// frame.
/// </para>
/// </remarks>
public static class PanelFrameEncoder
{
    /// <summary>
    /// The number of bytes <see cref="Encode(FrameBuffer, PanelPixelFormat, Span{byte})"/>
    /// will write for an uncompressed format, or <see langword="null"/> for a
    /// compressed one.
    /// </summary>
    public static int? PayloadSize(PanelPixelFormat format, int width, int height) =>
        format.PayloadSize(width, height);

    /// <summary>
    /// Packs <paramref name="frame"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="frame">Source pixels, already at the panel's resolution.</param>
    /// <param name="format">Target layout. Must not be a compressed format.</param>
    /// <param name="destination">Receives the packed bytes.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">
    /// The format is compressed, or the destination is too small.
    /// </exception>
    public static int Encode(FrameBuffer frame, PanelPixelFormat format, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (format.IsCompressed())
        {
            throw new ArgumentException(
                $"{format} is a compressed format; use IImageCodec to produce its payload.",
                nameof(format));
        }

        var required = format.PayloadSize(frame.Width, frame.Height)!.Value;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Need {required} bytes for a {frame.Width}x{frame.Height} {format} payload "
                + $"but the destination holds {destination.Length}.",
                nameof(destination));
        }

        var source = frame.ReadOnlySpan;
        switch (format)
        {
            case PanelPixelFormat.Rgb565BigEndian:
                PackRgb565RowMajor(source, destination, frame.Width, frame.Height, bigEndian: true);
                break;
            case PanelPixelFormat.Rgb565LittleEndian:
                PackRgb565RowMajor(source, destination, frame.Width, frame.Height, bigEndian: false);
                break;
            case PanelPixelFormat.Rgb565LittleEndianColumns:
                PackRgb565ColumnMajor(source, destination, frame.Width, frame.Height);
                break;
            case PanelPixelFormat.Rgb888:
                PackThreeChannel(source, destination, frame.Width, frame.Height, swapRedBlue: false);
                break;
            case PanelPixelFormat.Bgr888:
                PackThreeChannel(source, destination, frame.Width, frame.Height, swapRedBlue: true);
                break;
            case PanelPixelFormat.Bgra8888:
                PackFourChannel(source, destination, frame.Width, frame.Height, swapRedBlue: true);
                break;
            case PanelPixelFormat.Rgba8888:
                source.CopyTo(destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unhandled pixel format.");
        }

        return required;
    }

    /// <summary>Packs a frame into a newly allocated array.</summary>
    public static byte[] Encode(FrameBuffer frame, PanelPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffer = new byte[format.PayloadSize(frame.Width, frame.Height)
            ?? throw new ArgumentException($"{format} is compressed.", nameof(format))];
        Encode(frame, format, buffer);
        return buffer;
    }

    /// <summary>
    /// Row-major RGB565. Each pixel becomes one 16-bit word holding the top 5
    /// bits of red, top 6 of green and top 5 of blue.
    /// </summary>
    private static void PackRgb565RowMajor(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        bool bigEndian)
    {
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * FrameBuffer.BytesPerPixel;
            var word = ToRgb565(source[offset], source[offset + 1], source[offset + 2]);
            var target = i * 2;
            if (bigEndian)
            {
                destination[target] = (byte)(word >> 8);
                destination[target + 1] = (byte)(word & 0xFF);
            }
            else
            {
                destination[target] = (byte)(word & 0xFF);
                destination[target + 1] = (byte)(word >> 8);
            }
        }
    }

    /// <summary>
    /// Column-major RGB565 after a vertical flip, with the last word of each
    /// column zeroed.
    /// </summary>
    /// <remarks>
    /// The Winbond controller behind the Thermalright HID panels scans bottom-up
    /// in columns and treats the final word of each column as a terminator; real
    /// pixel data there leaves a bright stripe down the panel.
    /// </remarks>
    private static void PackRgb565ColumnMajor(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        var target = 0;
        for (var x = 0; x < width; x++)
        {
            for (var y = height - 1; y >= 0; y--)
            {
                var offset = ((y * width) + x) * FrameBuffer.BytesPerPixel;
                var word = ToRgb565(source[offset], source[offset + 1], source[offset + 2]);
                destination[target] = (byte)(word & 0xFF);
                destination[target + 1] = (byte)(word >> 8);
                target += 2;
            }

            destination[target - 2] = 0;
            destination[target - 1] = 0;
        }
    }

    private static void PackThreeChannel(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        bool swapRedBlue)
    {
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * FrameBuffer.BytesPerPixel;
            var target = i * 3;
            if (swapRedBlue)
            {
                destination[target] = source[offset + 2];
                destination[target + 1] = source[offset + 1];
                destination[target + 2] = source[offset];
            }
            else
            {
                destination[target] = source[offset];
                destination[target + 1] = source[offset + 1];
                destination[target + 2] = source[offset + 2];
            }
        }
    }

    private static void PackFourChannel(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        bool swapRedBlue)
    {
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * FrameBuffer.BytesPerPixel;
            if (swapRedBlue)
            {
                destination[offset] = source[offset + 2];
                destination[offset + 1] = source[offset + 1];
                destination[offset + 2] = source[offset];
                destination[offset + 3] = source[offset + 3];
            }
            else
            {
                destination[offset] = source[offset];
                destination[offset + 1] = source[offset + 1];
                destination[offset + 2] = source[offset + 2];
                destination[offset + 3] = source[offset + 3];
            }
        }
    }

    private static ushort ToRgb565(byte r, byte g, byte b) =>
        (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
}
