using Panelith.Expressions;
using Panelith.Imaging;
using Panelith.Media;
using Panelith.Themes;

namespace Panelith.Rendering;

public sealed class VideoRenderingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "panelith-video-" + Guid.NewGuid().ToString("N"));

    public VideoRenderingTests()
    {
        Directory.CreateDirectory(_folder);

        // Every frame is the same solid colour, so the test does not depend on the clock.
        var codec = new SkiaImageCodec();
        using var frame = FrameBuffer.RentCleared(8, 8);
        var pixels = frame.Span;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }

        var jpeg = codec.EncodeJpeg(frame, quality: 100);
        using var stream = File.Create(Path.Combine(_folder, "red.ptclip"));
        ClipFile.Write(stream, 8, 8, 15, [jpeg, jpeg, jpeg]);
    }

    private static (byte R, byte G, byte B) Pixel(FrameBuffer frame, int x, int y)
    {
        var i = ((y * frame.Width) + x) * 4;
        var span = frame.ReadOnlySpan;
        return (span[i], span[i + 1], span[i + 2]);
    }

    [Fact]
    public void ABackgroundVideoFillsTheCanvas()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(8, 8, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#0000ff", Video: "red.ptclip"),
            BasePath = _folder,
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(8, 8));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        var (r, g, b) = Pixel(frame, 4, 4);
        Assert.True(r > 230 && g < 25 && b < 25, $"expected red, got {r},{g},{b}");
    }

    [Fact]
    public void TheScrimDarkensTheVideo()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(8, 8, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000", Video: "red.ptclip", Scrim: "#00000080"),
            BasePath = _folder,
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(8, 8));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        var (r, _, _) = Pixel(frame, 4, 4);
        Assert.InRange(r, 110, 145);
    }

    [Fact]
    public void AVideoWidgetDrawsInsideItsBox()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(16, 8, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            BasePath = _folder,
            Widgets = [new VideoWidget { X = 8, Y = 0, Width = 8, Height = 8, Source = "red.ptclip", Fit = ImageFit.Stretch }],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(16, 8));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        Assert.Equal((0, 0, 0), Pixel(frame, 2, 4));
        Assert.True(Pixel(frame, 12, 4).R > 230);
    }

    [Fact]
    public void AMissingOrEscapingClipLeavesTheBackgroundAlone()
    {
        foreach (var source in new[] { "none.ptclip", @"..\red.ptclip" })
        {
            var theme = new Theme
            {
                Canvas = new ThemeCanvas(8, 8, ThemeScaleMode.Stretch),
                Background = new ThemeBackground("#0000ff", Video: source),
                BasePath = _folder,
            };

            using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(8, 8));
            using var frame = renderer.Render(EmptyBindingContext.Instance);
            Assert.Equal((0, 0, 255), Pixel(frame, 4, 4));
        }
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
