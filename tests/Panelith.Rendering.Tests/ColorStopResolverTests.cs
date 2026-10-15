using Panelith.Themes;
using Xunit;

namespace Panelith.Rendering;

public sealed class ColorStopResolverTests
{
    private static readonly ColorStop[] Stops =
    [
        new(0, "#00ff00"),
        new(50, "#ffff00"),
        new(100, "#ff0000"),
    ];

    [Fact]
    public void BelowFirstStop_ReturnsFirstColor()
    {
        var color = ColorStopResolver.Resolve(Stops, -10, ThemeColor.White);
        Assert.Equal(0, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void AtStop_ReturnsExactColor()
    {
        var color = ColorStopResolver.Resolve(Stops, 50, ThemeColor.White);
        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void BetweenStops_Interpolates()
    {
        // Halfway between green (0) and yellow (50) → mid at 25.
        var color = ColorStopResolver.Resolve(Stops, 25, ThemeColor.White);
        Assert.Equal(128, color.R); // 0 → 255
        Assert.Equal(255, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void AboveLastStop_ReturnsLastColor()
    {
        var color = ColorStopResolver.Resolve(Stops, 150, ThemeColor.White);
        Assert.Equal(255, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void EmptyStops_ReturnsFallback()
    {
        var color = ColorStopResolver.Resolve(null, 50, ThemeColor.White);
        Assert.Equal(ThemeColor.White, color);
    }
}
