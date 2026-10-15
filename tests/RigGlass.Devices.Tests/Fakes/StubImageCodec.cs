using RigGlass.Imaging;

namespace RigGlass.Devices.Tests.Fakes;

/// <summary>
/// A codec that fabricates payloads of a predictable size.
/// </summary>
/// <remarks>
/// The device layer only cares how many bytes a compressed frame is and that
/// the bytes reach the wire unchanged, so a real encoder would add a graphics
/// dependency and a lot of time to these tests without testing anything more.
/// </remarks>
internal sealed class StubImageCodec : IImageCodec
{
    private int _nextSize = 1000;

    /// <summary>The size the next encode will produce.</summary>
    internal int NextSize
    {
        get => _nextSize;
        set => _nextSize = value;
    }

    /// <summary>Quality values passed to <see cref="EncodeJpeg"/>, in order.</summary>
    internal List<int> RequestedQualities { get; } = [];

    public byte[] EncodeJpeg(FrameBuffer frame, int quality = 85)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RequestedQualities.Add(quality);

        // Fill with a recognisable, position-dependent pattern so a driver that
        // reorders or drops payload bytes is caught rather than merely producing
        // the right length.
        var payload = new byte[_nextSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        return payload;
    }

    public byte[] EncodePng(FrameBuffer frame) => EncodeJpeg(frame);

    public FrameBuffer Decode(ReadOnlySpan<byte> data, int? width = null, int? height = null) =>
        FrameBuffer.RentCleared(width ?? 1, height ?? 1);
}
