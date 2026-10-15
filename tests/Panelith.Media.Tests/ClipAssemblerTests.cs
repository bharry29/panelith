using SkiaSharp;

namespace Panelith.Media;

public sealed class ClipAssemblerTests
{
    /// <summary>A source frame whose picture is a solid shade encoding its number.</summary>
    private sealed class FakeFrame(int number, double fps) : IDecodedFrame
    {
        public static int Scaled { get; set; }

        public TimeSpan Timestamp { get; } = TimeSpan.FromSeconds(number / fps);

        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(1d / fps);

        public void CopyTo(SKBitmap destination)
        {
            Scaled++;
            destination.Erase(new SKColor((byte)(number * 4), 0, 0));
        }

        public void Dispose()
        {
        }
    }

    private static List<int> Bake(double sourceFps, int sourceFrames, TimeSpan start, TimeSpan? end, bool crossfade)
    {
        using var stream = new MemoryStream();
        using (var writer = new ClipWriter(stream, 4, 4, 15))
        using (var scaler = new FrameScaler(4, 4, CropRect.Full, 4, 4))
        using (var assembler = new ClipAssembler(writer, scaler, 15, start, end, crossfade, 100))
        {
            for (var i = 0; i < sourceFrames && assembler.Add(new FakeFrame(i, sourceFps)); i++)
            {
            }

            assembler.Finish();
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, stream.ToArray());
            using var reader = ClipReader.Open(path);
            return Enumerable.Range(0, reader.Info.FrameCount)
                .Select(i => (int)Math.Round(SKBitmap.Decode(reader.ReadFrame(i)).GetPixel(2, 2).Red / 4d))
                .ToList();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A60FpsSourceKeepsEveryFourthFrame()
    {
        FakeFrame.Scaled = 0;
        var frames = Bake(60, 60, TimeSpan.Zero, null, crossfade: false);

        Assert.Equal(15, frames.Count);
        Assert.Equal(Enumerable.Range(0, 15).Select(i => i * 4), frames);
        Assert.Equal(15, FakeFrame.Scaled);
    }

    [Fact]
    public void A10FpsSourceRepeatsFramesToReach15()
    {
        var frames = Bake(10, 10, TimeSpan.Zero, null, crossfade: false);

        Assert.Equal(15, frames.Count);
        Assert.Equal([0, 0, 1, 2, 2, 3, 4, 4, 5, 6, 6, 7, 8, 8, 9], frames);
    }

    [Fact]
    public void TheTrimmedRangeIsAllThatIsKept()
    {
        var frames = Bake(30, 60, TimeSpan.FromSeconds(0.5d), TimeSpan.FromSeconds(1d), crossfade: false);

        Assert.Equal(8, frames.Count);
        Assert.Equal(15, frames[0]);
        Assert.Equal(29, frames[^1]);
    }

    [Fact]
    public void TheCrossfadeDropsTheStartAndBlendsTheEndIntoIt()
    {
        var plain = Bake(15, 45, TimeSpan.Zero, null, crossfade: false);
        var looped = Bake(15, 45, TimeSpan.Zero, null, crossfade: true);

        Assert.Equal(45, plain.Count);
        Assert.Equal(37, looped.Count);
        Assert.Equal(plain[8..29], looped[..21]);

        // The last frame is mostly the frame the loop restarts on.
        Assert.InRange(looped[^1], 7, 12);
    }

    [Fact]
    public void AClipTooShortToCrossfadeKeepsEveryFrame()
    {
        Assert.Equal(10, Bake(15, 10, TimeSpan.Zero, null, crossfade: true).Count);
    }

    [Fact]
    public void CoverKeepsThePanelShapeCentred()
    {
        var wide = CropRect.Cover(1920, 1080, 1920, 480);
        Assert.Equal(0d, wide.X, 6);
        Assert.Equal(1d, wide.Width, 6);
        Assert.Equal(480d / 1080d, wide.Height, 6);
        Assert.Equal((1d - (480d / 1080d)) / 2d, wide.Y, 6);

        var square = CropRect.Cover(1920, 1080, 480, 480);
        Assert.Equal(0.5625d, square.Width, 6);
        Assert.Equal(0.21875d, square.X, 6);
    }

    [Fact]
    public void ACropSmallerThanThePanelIsFlagged()
    {
        var crop = CropRect.Cover(1920, 1080, 1920, 480);
        Assert.False(crop.IsSmallerThan(1920, 1080, 1920, 480));
        Assert.True(crop.IsSmallerThan(1280, 720, 1920, 480));
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)]
    [InlineData(0.6, 0, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    public void CropsOutsideThePictureAreInvalid(double x, double y, double width, double height) =>
        Assert.False(new CropRect(x, y, width, height).IsValid);
}
