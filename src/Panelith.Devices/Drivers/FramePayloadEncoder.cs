using System.Buffers;
using Panelith.Devices.Encoding;
using Panelith.Devices.Profiles;
using Panelith.Imaging;

namespace Panelith.Devices.Drivers;

/// <summary>
/// Turns a rendered frame into the exact payload one panel expects.
/// </summary>
/// <remarks>
/// <para>
/// Every driver needs the same three steps - rotate, pack into the panel's
/// pixel format, and respect its payload ceiling - so they live here rather
/// than once per protocol. Buffers are rented once and reused for the life of
/// the connection: a panel at 30 fps would otherwise hand the collector several
/// megabytes a second, which is exactly the behaviour that gives competing
/// panel software its reputation for burning a core.
/// </para>
/// <para>
/// JPEG is the exception. Encoders allocate their own output, so a JPEG panel
/// still allocates once per frame; that cost is small next to the encode itself
/// and is not worth an API that pretends otherwise.
/// </para>
/// </remarks>
internal sealed class FramePayloadEncoder : IDisposable
{
    private readonly IImageCodec _codec;
    private readonly PanelPixelFormat _format;
    private readonly int _jpegQuality;
    private readonly int? _maxPayloadBytes;
    private readonly PanelOrientation _orientation;
    private readonly bool _mirror;
    private byte[]? _packed;

    internal FramePayloadEncoder(
        IImageCodec codec,
        PanelProfile profile,
        PanelOpenOptions options,
        PanelOrientation orientation)
    {
        _codec = codec;
        _format = profile.PixelFormat;
        _jpegQuality = options.JpegQuality is >= 1 and <= 100 ? options.JpegQuality : profile.JpegQuality;
        _maxPayloadBytes = profile.MaxPayloadBytes;
        _orientation = orientation;
        _mirror = options.FlipHorizontally;
    }

    /// <summary>The payload for one frame, valid until the next call.</summary>
    /// <exception cref="PanelConnectionException">The frame cannot be expressed in the panel's format.</exception>
    internal ReadOnlyMemory<byte> Encode(FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var oriented = FrameOrientation.ApplyOrPassThrough(frame, _orientation, _mirror, out var rented);
        try
        {
            return _format switch
            {
                PanelPixelFormat.Jpeg => EncodeJpeg(oriented),
                PanelPixelFormat.Png => _codec.EncodePng(oriented),
                _ => Pack(oriented),
            };
        }
        finally
        {
            if (rented)
            {
                oriented.Dispose();
            }
        }
    }

    public void Dispose()
    {
        var packed = Interlocked.Exchange(ref _packed, null);
        if (packed is not null)
        {
            ArrayPool<byte>.Shared.Return(packed);
        }
    }

    private byte[] EncodeJpeg(FrameBuffer frame) => _maxPayloadBytes is { } budget and > 0
        ? _codec.EncodeJpegWithinBudget(frame, budget, _jpegQuality)
        : _codec.EncodeJpeg(frame, _jpegQuality);

    private ReadOnlyMemory<byte> Pack(FrameBuffer frame)
    {
        var size = PanelFrameEncoder.PayloadSize(_format, frame.Width, frame.Height)
            ?? throw new PanelConnectionException(
                $"{_format} frames have no fixed payload size, so they cannot be packed.",
                PanelFailureReason.Unsupported);

        if (_maxPayloadBytes is { } max and > 0 && size > max)
        {
            throw new PanelConnectionException(
                $"A {frame.Width}x{frame.Height} {_format} frame is {size} bytes, which exceeds the "
                + $"{max} bytes this panel accepts.",
                PanelFailureReason.Unsupported);
        }

        var buffer = _packed;
        if (buffer is null || buffer.Length < size)
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            buffer = ArrayPool<byte>.Shared.Rent(size);
            _packed = buffer;
        }

        var written = PanelFrameEncoder.Encode(frame, _format, buffer.AsSpan(0, size));
        return buffer.AsMemory(0, written);
    }
}
