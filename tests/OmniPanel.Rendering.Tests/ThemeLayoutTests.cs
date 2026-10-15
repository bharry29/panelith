using OmniPanel.Themes;
using Xunit;

namespace OmniPanel.Rendering;

public sealed class ThemeLayoutTests
{
    public static TheoryData<ThemeScaleMode, int, int, int, int, double, double> ScaleCases => new()
    {
        // Fit: 480x480 on 1280x480 → scale 1, letterbox horizontally
        { ThemeScaleMode.Fit, 480, 480, 1280, 480, 1d, 1d },
        // Fit: 480x320 on 960x640 → scale 2
        { ThemeScaleMode.Fit, 480, 320, 960, 640, 2d, 2d },
        // Fill: 480x480 on 1280x480 → scale 1280/480
        { ThemeScaleMode.Fill, 480, 480, 1280, 480, 1280d / 480d, 1280d / 480d },
        // Stretch: independent axes
        { ThemeScaleMode.Stretch, 480, 480, 1280, 480, 1280d / 480d, 1d },
        // None: identity scale
        { ThemeScaleMode.None, 480, 480, 1280, 480, 1d, 1d },
    };

    [Theory]
    [MemberData(nameof(ScaleCases))]
    public void ComputeTransform_ProducesExpectedScale(
        ThemeScaleMode mode,
        int cw, int ch, int tw, int th,
        double expectedSx, double expectedSy)
    {
        var t = ThemeLayout.ComputeTransform(cw, ch, tw, th, mode);

        Assert.Equal(expectedSx, t.ScaleX, 6);
        Assert.Equal(expectedSy, t.ScaleY, 6);
        Assert.Equal(tw, t.TargetWidth);
        Assert.Equal(th, t.TargetHeight);
    }

    [Fact]
    public void Fit_CentresLetterboxedCanvas_OnUltrawide()
    {
        // 480x480 theme on 1280x480 panel: content is 480x480 centred with
        // 400px bars on each side.
        var t = ThemeLayout.ComputeTransform(480, 480, 1280, 480, ThemeScaleMode.Fit);

        Assert.Equal(1d, t.ScaleX);
        Assert.Equal(400d, t.OffsetX);
        Assert.Equal(0d, t.OffsetY);

        var canvas = ThemeLayout.CanvasDeviceRect(t);
        Assert.Equal(400d, canvas.X);
        Assert.Equal(480d, canvas.Width);
        Assert.Equal(480d, canvas.Height);
    }

    [Fact]
    public void Fill_CropsOverflow_OnUltrawide()
    {
        var t = ThemeLayout.ComputeTransform(480, 480, 1280, 480, ThemeScaleMode.Fill);
        var scale = 1280d / 480d;

        Assert.Equal(scale, t.ScaleX);
        // Vertically the scaled canvas is taller than the panel and centred.
        Assert.Equal((480 - (480 * scale)) / 2d, t.OffsetY, 6);
    }

    [Theory]
    [InlineData(AnchorEdges.None)]
    [InlineData(AnchorEdges.Left)]
    [InlineData(AnchorEdges.Right)]
    [InlineData(AnchorEdges.Top)]
    [InlineData(AnchorEdges.Bottom)]
    [InlineData(AnchorEdges.LeftRight)]
    [InlineData(AnchorEdges.TopBottom)]
    [InlineData(AnchorEdges.All)]
    [InlineData(AnchorEdges.Left | AnchorEdges.Top)]
    [InlineData(AnchorEdges.Right | AnchorEdges.Bottom)]
    public void MapWidget_AllAnchorCombinations_OnUltrawideFit(AnchorEdges anchors)
    {
        var t = ThemeLayout.ComputeTransform(480, 480, 1280, 480, ThemeScaleMode.Fit);
        var design = new ThemeRect(40, 40, 100, 50);

        var mapped = ThemeLayout.MapWidget(t, design, anchors);

        Assert.True(mapped.Width > 0, $"width for {anchors}");
        Assert.True(mapped.Height > 0, $"height for {anchors}");

        if ((anchors & AnchorEdges.Left) != 0)
        {
            Assert.Equal(40d, mapped.X, 6);
        }

        if ((anchors & AnchorEdges.Right) != 0)
        {
            // distRight = 480 - 140 = 340 → right edge at 1280 - 340 = 940
            Assert.Equal(940d, mapped.Right, 6);
        }

        if ((anchors & AnchorEdges.LeftRight) == AnchorEdges.LeftRight)
        {
            Assert.Equal(900d, mapped.Width, 6);
        }

        if (anchors == AnchorEdges.None)
        {
            Assert.Equal(440d, mapped.X, 6); // 400 offset + 40
            Assert.Equal(100d, mapped.Width, 6);
        }
    }

    [Fact]
    public void OppositeAnchors_StretchWidgetAcrossUltrawide()
    {
        // A header bar authored 20px from left and right on a square canvas
        // should span nearly the full ultrawide panel when LeftRight-anchored.
        var t = ThemeLayout.ComputeTransform(480, 480, 1280, 480, ThemeScaleMode.Fit);
        var design = new ThemeRect(20, 10, 440, 30);

        var mapped = ThemeLayout.MapWidget(t, design, AnchorEdges.LeftRight);

        Assert.Equal(20d, mapped.X, 6);
        Assert.Equal(1260d, mapped.Right, 6); // 1280 - 20
        Assert.Equal(1240d, mapped.Width, 6);
        // Vertical still uses the Fit transform (centred content).
        Assert.Equal(10d, mapped.Y, 6);
        Assert.Equal(30d, mapped.Height, 6);
    }

    [Theory]
    [InlineData(ThemeScaleMode.Fit)]
    [InlineData(ThemeScaleMode.Fill)]
    [InlineData(ThemeScaleMode.Stretch)]
    [InlineData(ThemeScaleMode.None)]
    public void MapWidget_WithoutAnchors_PreservesAspectUnderUniformModes(ThemeScaleMode mode)
    {
        var t = ThemeLayout.ComputeTransform(400, 300, 800, 600, mode);
        var design = new ThemeRect(10, 20, 100, 50);
        var mapped = ThemeLayout.MapWidget(t, design, AnchorEdges.None);

        if (mode is ThemeScaleMode.Fit or ThemeScaleMode.Fill or ThemeScaleMode.None)
        {
            Assert.Equal(mapped.Width / design.Width, mapped.Height / design.Height, 6);
        }

        if (mode == ThemeScaleMode.Stretch)
        {
            Assert.Equal(design.Width * t.ScaleX, mapped.Width, 6);
            Assert.Equal(design.Height * t.ScaleY, mapped.Height, 6);
        }
    }

    [Fact]
    public void SingleEdgeAnchor_KeepsAuthoredSize()
    {
        var t = ThemeLayout.ComputeTransform(480, 480, 1280, 480, ThemeScaleMode.Fit);
        var design = new ThemeRect(10, 10, 80, 40);

        var leftOnly = ThemeLayout.MapWidget(t, design, AnchorEdges.Left);
        Assert.Equal(10d, leftOnly.X, 6);
        Assert.Equal(80d, leftOnly.Width, 6); // scale is 1 under Fit here

        var rightOnly = ThemeLayout.MapWidget(t, design, AnchorEdges.Right);
        Assert.Equal(80d, rightOnly.Width, 6);
        Assert.Equal(1280d - (480d - design.Right), rightOnly.Right, 6);
    }

    [Theory]
    [InlineData(100, 100, 200, 100)] // wider target
    [InlineData(100, 100, 100, 200)] // taller target
    [InlineData(100, 100, 50, 50)]   // smaller target
    [InlineData(480, 320, 1280, 480)]
    [InlineData(480, 480, 480, 480)] // identical
    public void Fit_NeverExceedsTarget(int cw, int ch, int tw, int th)
    {
        var t = ThemeLayout.ComputeTransform(cw, ch, tw, th, ThemeScaleMode.Fit);
        var canvas = ThemeLayout.CanvasDeviceRect(t);

        Assert.True(canvas.Width <= tw + 0.001);
        Assert.True(canvas.Height <= th + 0.001);
    }

    [Fact]
    public void ConvenienceOverload_UsesCanvasScaling()
    {
        var canvas = new ThemeCanvas(480, 480, ThemeScaleMode.Stretch);
        var mapped = ThemeLayout.MapWidget(
            canvas, 1280, 480,
            new ThemeRect(0, 0, 480, 480),
            AnchorEdges.None);

        Assert.Equal(1280d, mapped.Width, 6);
        Assert.Equal(480d, mapped.Height, 6);
    }
}
