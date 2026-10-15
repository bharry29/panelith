using OmniPanel.Expressions;
using OmniPanel.Imaging;
using OmniPanel.Themes;
using Xunit;

namespace OmniPanel.Rendering;

public sealed class ThemeRendererPixelTests
{
    [Fact]
    public void SolidShape_FillsExactPixels()
    {
        var theme = new Theme
        {
            Id = "pixel",
            Name = "pixel",
            Canvas = new ThemeCanvas(16, 16, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new ShapeWidget
                {
                    X = 4, Y = 4, Width = 8, Height = 8,
                    Shape = ShapeKind.Rectangle,
                    Fill = "#ff0000",
                },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(16, 16));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        AssertPixel(frame, 0, 0, 0, 0, 0, 255);
        AssertPixel(frame, 4, 4, 255, 0, 0, 255);
        AssertPixel(frame, 7, 7, 255, 0, 0, 255);
        AssertPixel(frame, 11, 11, 255, 0, 0, 255);
        // Just outside the rect
        AssertPixel(frame, 3, 4, 0, 0, 0, 255);
        AssertPixel(frame, 12, 4, 0, 0, 0, 255);
    }

    [Fact]
    public void BackgroundColor_FillsEntirePanel_UnderStretch()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(10, 10, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#112233"),
            Widgets = [],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(10, 10));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        AssertPixel(frame, 0, 0, 0x11, 0x22, 0x33, 255);
        AssertPixel(frame, 9, 9, 0x11, 0x22, 0x33, 255);
    }

    [Fact]
    public void FitLetterbox_UsesCanvasBackground()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(10, 10, ThemeScaleMode.Fit, Background: "#ff00ff"),
            Background = new ThemeBackground("#0000ff"),
            Widgets = [],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(20, 10));
        using var frame = renderer.Render(EmptyBindingContext.Instance);

        // Side letterbox bars are magenta; centre content is blue.
        AssertPixel(frame, 0, 0, 255, 0, 255, 255);
        AssertPixel(frame, 5, 5, 0, 0, 255, 255);
        AssertPixel(frame, 19, 0, 255, 0, 255, 255);
    }

    [Fact]
    public void RenderInto_ReusesTargetBuffer()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(8, 8, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#010203"),
            Widgets = [],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(8, 8));
        using var target = FrameBuffer.Rent(8, 8);
        renderer.RenderInto(target, EmptyBindingContext.Instance);
        AssertPixel(target, 0, 0, 1, 2, 3, 255);
        Assert.Equal(1, renderer.Stats.FrameCount);
    }

    private static void AssertPixel(FrameBuffer frame, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = ((y * frame.Width) + x) * 4;
        var pr = frame.Span[i];
        var pg = frame.Span[i + 1];
        var pb = frame.Span[i + 2];
        var pa = frame.Span[i + 3];
        Assert.True(pr == r && pg == g && pb == b && pa == a,
            $"pixel ({x},{y}) expected rgba({r},{g},{b},{a}) but was ({pr},{pg},{pb},{pa})");
    }
}
