using System.Diagnostics;
using Panelith.Rendering.Widgets;
using SkiaSharp;

namespace Panelith.Rendering;

public sealed class WidgetSmoothingTests
{
    [Fact]
    public void SegmentTheValueEndsInIsPartlyLit()
    {
        var track = new SKColor(0, 0, 0, 255);
        var fill = new SKColor(200, 100, 50, 255);

        Assert.Equal(fill, BarWidgetRenderer.SegmentColor(0, 2.5, track, fill));
        Assert.Equal(fill, BarWidgetRenderer.SegmentColor(1, 2.5, track, fill));
        Assert.Equal(new SKColor(100, 50, 25, 255), BarWidgetRenderer.SegmentColor(2, 2.5, track, fill));
        Assert.Equal(track, BarWidgetRenderer.SegmentColor(3, 2.5, track, fill));
    }

    [Fact]
    public void GraphPointsArePlacedByTime()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromSeconds(60);

        Assert.Equal(1d, GraphWidgetRenderer.TimeToX(now, now, window));
        Assert.Equal(0.5d, GraphWidgetRenderer.TimeToX(now.AddSeconds(-30), now, window), 9);
        Assert.Equal(0d, GraphWidgetRenderer.TimeToX(now.AddSeconds(-60), now, window), 9);
        Assert.True(GraphWidgetRenderer.TimeToX(now.AddSeconds(-61), now, window) < 0d);
    }

    [Fact]
    public void AutoScaleGrowsAtOnceAndShrinksGradually()
    {
        var state = new GraphWidgetRenderer.RangeState();
        var start = 1_000_000L;
        Assert.Equal((0d, 50d), GraphWidgetRenderer.EaseRange(state, 0, 50, start));

        // A higher peak widens the range in the same frame.
        Assert.Equal((0d, 80d), GraphWidgetRenderer.EaseRange(state, 0, 80, start + Ticks(0.1)));

        // When the peak scrolls away, the range settles back over a second or so.
        var (_, max) = GraphWidgetRenderer.EaseRange(state, 0, 40, start + Ticks(0.4));
        Assert.InRange(max, 50d, 79d);
        (_, max) = GraphWidgetRenderer.EaseRange(state, 0, 40, start + Ticks(5));
        Assert.Equal(40d, max, 1);
    }

    [Fact]
    public void ShrinkToFitShrinksAtOnceButGrowsOnlyAfterAPause()
    {
        var state = new TextWidgetRenderer.FitState();
        var start = 1_000_000L;

        Assert.Equal(40f, TextWidgetRenderer.StickySize(state, 40f, start));
        Assert.Equal(36f, TextWidgetRenderer.StickySize(state, 36f, start + Ticks(1)));

        // The larger size fits again, but not for long enough yet.
        Assert.Equal(36f, TextWidgetRenderer.StickySize(state, 40f, start + Ticks(2)));
        Assert.Equal(36f, TextWidgetRenderer.StickySize(state, 40f, start + Ticks(4)));
        Assert.Equal(40f, TextWidgetRenderer.StickySize(state, 40f, start + Ticks(5.1)));
    }

    [Fact]
    public void ShrinkToFitRestartsTheWaitWhenTheLargerSizeStopsFitting()
    {
        var state = new TextWidgetRenderer.FitState();
        var start = 1_000_000L;
        TextWidgetRenderer.StickySize(state, 30f, start);
        TextWidgetRenderer.StickySize(state, 40f, start + Ticks(1));
        TextWidgetRenderer.StickySize(state, 30f, start + Ticks(2));

        Assert.Equal(30f, TextWidgetRenderer.StickySize(state, 40f, start + Ticks(4.5)));
    }

    [Fact]
    public void TabularDigitsKeepNumbersTheSameWidth()
    {
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 40);
        var tabular = new TextWidgetRenderer.TextLayout(0d, Tabular: true);

        var ones = TextWidgetRenderer.MeasureWidth(font, "CPU 11%", tabular);
        var eights = TextWidgetRenderer.MeasureWidth(font, "CPU 88%", tabular);
        Assert.Equal(ones, eights, 3);

        var spaced = new TextWidgetRenderer.TextLayout(2d, Tabular: true);
        Assert.Equal(
            TextWidgetRenderer.MeasureWidth(font, "11", spaced),
            TextWidgetRenderer.MeasureWidth(font, "88", spaced),
            3);
    }

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
}
