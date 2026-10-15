using SkiaSharp;
using Xunit;

namespace Panelith.Rendering;

public sealed class SkiaSmokeTests
{
    [Fact]
    public void SkiaSharpCreatesSurfaceAndReadsUnpremulPixels()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.NotNull(surface);

        surface.Canvas.Clear(new SKColor(255, 0, 0, 128));
        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var px = pixmap.GetPixelColor(0, 0);

        Assert.Equal(255, px.Red);
        Assert.Equal(0, px.Green);
        Assert.Equal(0, px.Blue);
        Assert.Equal(128, px.Alpha);
        Assert.Equal(SKAlphaType.Unpremul, pixmap.AlphaType);
    }
}
