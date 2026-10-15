using System.Buffers.Binary;
using Panelith.Media;
using Panelith.Themes;

namespace Panelith.Tests.Media;

public sealed class ClipFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "panelith-clip-" + Guid.NewGuid().ToString("N"));

    public ClipFileTests() => Directory.CreateDirectory(_folder);

    private string WriteClip(string name, params byte[][] frames)
    {
        var path = Path.Combine(_folder, name);
        using var stream = File.Create(path);
        ClipFile.Write(stream, 64, 16, 15, frames.Select(f => (ReadOnlyMemory<byte>)f).ToArray());
        return path;
    }

    [Fact]
    public void FramesRoundTripInOrder()
    {
        var path = WriteClip("a.ptclip", [1, 2, 3], [4, 5], [6]);

        using var reader = ClipReader.Open(path);
        Assert.Equal(new ClipInfo(64, 16, 15, 3), reader.Info);
        Assert.Equal([1, 2, 3], reader.ReadFrame(0));
        Assert.Equal([4, 5], reader.ReadFrame(1));
        Assert.Equal([6], reader.ReadFrame(2));
    }

    [Fact]
    public void AnUnfinishedClipIsRejected()
    {
        var path = Path.Combine(_folder, "partial.ptclip");
        using (var stream = File.Create(path))
        using (var writer = new ClipWriter(stream, 64, 16, 15))
        {
            writer.Add([1, 2, 3]);
        }

        Assert.Throws<InvalidDataException>(() => ClipReader.Open(path));
    }

    [Fact]
    public void ATruncatedClipIsRejected()
    {
        var path = WriteClip("cut.ptclip", [1, 2, 3], [4, 5]);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^4]);

        Assert.Throws<InvalidDataException>(() => ClipReader.Open(path));
    }

    [Fact]
    public void AFrameTablePointingBackwardsIsRejected()
    {
        var path = WriteClip("bad.ptclip", [1, 2, 3], [4, 5]);
        var bytes = File.ReadAllBytes(path);
        var table = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(table + 8), 2);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => ClipReader.Open(path));
    }

    [Fact]
    public void AHeaderClaimingTooManyFramesIsRejected()
    {
        var path = WriteClip("many.ptclip", [1]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), ClipLimits.MaxFrames + 1);
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => ClipReader.Open(path));
    }

    [Fact]
    public void OtherFilesAreNotClips()
    {
        var path = Path.Combine(_folder, "x.ptclip");
        File.WriteAllBytes(path, new byte[64]);
        Assert.Throws<InvalidDataException>(() => ClipReader.Open(path));
    }

    [Theory]
    [InlineData(0, 16, 15)]
    [InlineData(64, ClipLimits.MaxDimension + 1, 15)]
    [InlineData(64, 16, 0)]
    [InlineData(64, 16, ClipLimits.MaxFps + 1)]
    public void SizesAndRatesOutsideTheLimitsAreRefused(int width, int height, int fps)
    {
        using var stream = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => new ClipWriter(stream, width, height, fps));
    }

    [Fact]
    public void AnOversizedFrameIsRefused()
    {
        using var stream = new MemoryStream();
        using var writer = new ClipWriter(stream, 64, 16, 15);
        Assert.Throws<InvalidDataException>(() => writer.Add(new byte[ClipLimits.MaxFrameBytes + 1]));
    }

    [Theory]
    [InlineData(0d, 1d, 0)]
    [InlineData(0.99d, 1d, 14)]
    [InlineData(1d, 1d, 15)]
    [InlineData(2d, 1d, 0)]
    [InlineData(2.5d, 1d, 7)]
    [InlineData(0.5d, 2d, 15)]
    [InlineData(1d, 0d, 0)]
    public void FramesAreChosenByTimeAndLoop(double seconds, double speed, int expected)
    {
        var info = new ClipInfo(64, 16, 15, 30);
        Assert.Equal(expected, info.FrameAt(TimeSpan.FromSeconds(seconds), speed));
    }

    [Fact]
    public void NegativeSpeedPlaysBackwardsWithinTheLoop()
    {
        var info = new ClipInfo(64, 16, 15, 30);
        Assert.Equal(29, info.FrameAt(TimeSpan.FromSeconds(1d / 15), -1d));
    }

    [Fact]
    public void LeftOutJsonValuesKeepTheirDefaults()
    {
        const string json = """
            {
              "id": "d",
              "canvas": { "width": 100, "height": 50 },
              "background": { "color": "#000000", "video": "clip.ptclip" },
              "widgets": [ { "type": "graph", "x": 0, "y": 0, "width": 10, "height": 10, "series": [ { "value": "@cpu.load" } ] } ]
            }
            """;

        var theme = ThemeSerializer.Deserialize(json);

        Assert.Equal(1d, theme.Background.VideoSpeed);
        Assert.Equal(ImageFit.Cover, theme.Background.Fit);
        Assert.Equal(ThemeScaleMode.Fit, theme.Canvas.Scaling);
        var series = Assert.Single(Assert.IsType<GraphWidget>(Assert.Single(theme.Widgets)).Series);
        Assert.Equal(2d, series.Thickness);
        Assert.Equal(0.25d, series.FillOpacity);
        Assert.True(series.Smooth);
    }

    [Fact]
    public void VideoWidgetsAndBackgroundsSurviveJson()
    {
        var theme = new Theme
        {
            Id = "v",
            Background = new ThemeBackground("#000000", Video: "clip.ptclip", Scrim: "#00000066", VideoSpeed: 0.5d),
            Widgets = [new VideoWidget { X = 1, Y = 2, Width = 30, Height = 40, Source = "loop.ptclip", Fit = ImageFit.Contain, Speed = 2d }],
        };

        var json = ThemeSerializer.Serialize(theme);
        Assert.Contains("\"video\"", json, StringComparison.Ordinal);
        var back = ThemeSerializer.Deserialize(json);

        Assert.Equal(theme.Background, back.Background);
        var widget = Assert.IsType<VideoWidget>(Assert.Single(back.Widgets));
        Assert.Equal("loop.ptclip", widget.Source);
        Assert.Equal(ImageFit.Contain, widget.Fit);
        Assert.Equal(2d, widget.Speed);
    }

    [Fact]
    public void TheValidatorReportsMissingAndForeignClips()
    {
        WriteClip("ok.ptclip", [1]);
        File.WriteAllBytes(Path.Combine(_folder, "broken.ptclip"), new byte[40]);
        var theme = new Theme
        {
            Id = "v",
            BasePath = _folder,
            Background = new ThemeBackground(Video: "ok.ptclip"),
            Widgets =
            [
                new VideoWidget { Id = "missing", Width = 1, Height = 1, Source = "none.ptclip" },
                new VideoWidget { Id = "mp4", Width = 1, Height = 1, Source = "movie.mp4" },
                new VideoWidget { Id = "broken", Width = 1, Height = 1, Source = "broken.ptclip" },
            ],
        };

        var errors = ThemeValidator.Validate(theme).Where(d => d.Severity == ThemeDiagnosticSeverity.Error).ToList();

        Assert.Equal(["missing", "mp4", "broken"], errors.Select(e => e.WidgetId));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
