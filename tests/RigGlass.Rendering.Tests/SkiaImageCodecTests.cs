using RigGlass.Imaging;
using Xunit;

namespace RigGlass.Rendering;

public sealed class SkiaImageCodecTests
{
    private readonly SkiaImageCodec _codec = new();

    [Fact]
    public void PngRoundTrip_IsLosslessIncludingAlpha()
    {
        using var frame = FrameBuffer.RentCleared(8, 4);
        SetPixel(frame, 0, 0, 255, 0, 0, 255);       // opaque red
        SetPixel(frame, 1, 0, 0, 255, 0, 128);       // half-alpha green
        SetPixel(frame, 2, 0, 0, 0, 255, 64);        // quarter-alpha blue
        SetPixel(frame, 3, 0, 255, 255, 255, 0);     // transparent white
        SetPixel(frame, 7, 3, 10, 20, 30, 200);

        var png = _codec.EncodePng(frame);
        using var decoded = _codec.Decode(png);

        Assert.Equal(8, decoded.Width);
        Assert.Equal(4, decoded.Height);
        AssertPixel(decoded, 0, 0, 255, 0, 0, 255);
        AssertPixel(decoded, 1, 0, 0, 255, 0, 128);
        AssertPixel(decoded, 2, 0, 0, 0, 255, 64);
        AssertPixel(decoded, 3, 0, 255, 255, 255, 0);
        AssertPixel(decoded, 7, 3, 10, 20, 30, 200);
    }

    [Fact]
    public void JpegRoundTrip_IsApproximatelyCorrect()
    {
        using var frame = FrameBuffer.RentCleared(32, 32);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                SetPixel(frame, x, y, 200, 40, 40, 255);
            }
        }

        var jpeg = _codec.EncodeJpeg(frame, quality: 95);
        using var decoded = _codec.Decode(jpeg);

        Assert.Equal(32, decoded.Width);
        var (r, g, b, a) = GetPixel(decoded, 16, 16);
        Assert.Equal(255, a);
        Assert.InRange(r, 180, 220);
        Assert.InRange(g, 20, 60);
        Assert.InRange(b, 20, 60);
    }

    [Fact]
    public void EncodeJpegWithinBudget_RespectsBudget()
    {
        using var frame = FrameBuffer.RentCleared(128, 128);
        for (var i = 0; i < frame.ByteLength; i += 4)
        {
            frame.Span[i] = (byte)(i % 256);
            frame.Span[i + 1] = (byte)((i * 3) % 256);
            frame.Span[i + 2] = (byte)((i * 7) % 256);
            frame.Span[i + 3] = 255;
        }

        const int budget = 4_000;
        var encoded = _codec.EncodeJpegWithinBudget(frame, budget, startQuality: 95);

        Assert.True(encoded.Length <= budget, $"encoded {encoded.Length} bytes exceeds budget {budget}");
        Assert.True(encoded.Length > 100);
    }

    [Fact]
    public void Decode_ResizesWhenRequested()
    {
        using var frame = FrameBuffer.RentCleared(16, 8);
        SetPixel(frame, 0, 0, 255, 0, 0, 255);
        var png = _codec.EncodePng(frame);

        using var decoded = _codec.Decode(png, width: 8, height: 4);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(4, decoded.Height);
    }

    [Fact]
    public void Decode_RejectsEmptyData()
    {
        Assert.Throws<ArgumentException>(() => _codec.Decode([]));
    }

    private static void SetPixel(FrameBuffer frame, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = ((y * frame.Width) + x) * 4;
        frame.Span[i] = r;
        frame.Span[i + 1] = g;
        frame.Span[i + 2] = b;
        frame.Span[i + 3] = a;
    }

    private static (byte R, byte G, byte B, byte A) GetPixel(FrameBuffer frame, int x, int y)
    {
        var i = ((y * frame.Width) + x) * 4;
        return (frame.Span[i], frame.Span[i + 1], frame.Span[i + 2], frame.Span[i + 3]);
    }

    private static void AssertPixel(FrameBuffer frame, int x, int y, byte r, byte g, byte b, byte a)
    {
        var (pr, pg, pb, pa) = GetPixel(frame, x, y);
        Assert.True(pr == r && pg == g && pb == b && pa == a,
            $"pixel ({x},{y}) expected rgba({r},{g},{b},{a}) but was ({pr},{pg},{pb},{pa})");
    }
}
