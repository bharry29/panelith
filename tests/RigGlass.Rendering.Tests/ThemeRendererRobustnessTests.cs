using RigGlass.Expressions;
using RigGlass.Sensors;
using RigGlass.Themes;
using Xunit;

namespace RigGlass.Rendering;

public sealed class ThemeRendererRobustnessTests
{
    [Fact]
    public void MissingFont_DoesNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(64, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new TextWidget
                {
                    X = 0, Y = 0, Width = 64, Height = 32,
                    Text = "hello",
                    FontFamily = "DefinitelyNotARealFont_XYZ_12345",
                    Color = "#ffffff",
                },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(64, 32));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.Equal(64, frame.Width);
    }

    [Fact]
    public void MissingAsset_RendersPlaceholderWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigglass-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var theme = new Theme
            {
                Canvas = new ThemeCanvas(32, 32, ThemeScaleMode.Stretch),
                Background = new ThemeBackground("#000000"),
                BasePath = dir,
                Widgets =
                [
                    new ImageWidget
                    {
                        X = 0, Y = 0, Width = 32, Height = 32,
                        Source = "missing.png",
                    },
                ],
            };

            using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(32, 32, ShowErrorMarkers: true));
            using var frame = renderer.Render(EmptyBindingContext.Instance);
            Assert.Equal(32, frame.Width);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EmptyGraphHistory_DoesNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(64, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new GraphWidget
                {
                    X = 0, Y = 0, Width = 64, Height = 32,
                    Series = [new GraphSeries("@cpu.load", "#00ff00")],
                    WindowSeconds = 30,
                },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(64, 32));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.NotNull(frame);
    }

    [Fact]
    public void AllMissingBindings_DoNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(80, 40, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new TextWidget { X = 0, Y = 0, Width = 80, Height = 20, Text = "{@cpu.temp:0}" },
                new BarWidget { X = 0, Y = 20, Width = 80, Height = 10, Value = "@cpu.load" },
                new GaugeWidget { X = 0, Y = 0, Width = 40, Height = 40, Value = "@gpu.temp" },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(80, 40));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.Equal(80, frame.Width);
    }

    [Fact]
    public void ZeroSizeWidget_DoesNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(32, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new ShapeWidget { X = 5, Y = 5, Width = 0, Height = 0, Fill = "#ff0000" },
                new TextWidget { X = 0, Y = 0, Width = 0, Height = 10, Text = "x" },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(32, 32));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.NotNull(frame);
    }

    [Fact]
    public void WidgetLargerThanCanvas_DoesNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(32, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new ShapeWidget
                {
                    X = -10, Y = -10, Width = 200, Height = 200,
                    Shape = ShapeKind.Ellipse,
                    Fill = "#00ff00",
                },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(32, 32));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.NotNull(frame);
    }

    [Fact]
    public void DeeplyNestedGroups_DoNotThrow()
    {
        ThemeWidget inner = new ShapeWidget
        {
            X = 1, Y = 1, Width = 4, Height = 4,
            Fill = "#ffffff",
        };

        for (var i = 0; i < 8; i++)
        {
            inner = new GroupWidget
            {
                X = 1, Y = 1, Width = 30 - i, Height = 30 - i,
                Clip = i % 2 == 0,
                Opacity = 0.95d,
                Items = [inner],
            };
        }

        var theme = new Theme
        {
            Canvas = new ThemeCanvas(32, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets = [inner],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(32, 32));
        using var frame = renderer.Render(EmptyBindingContext.Instance);
        Assert.NotNull(frame);
    }

    [Fact]
    public void VisibleWhen_HidesWidget()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(8, 8, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new ShapeWidget
                {
                    X = 0, Y = 0, Width = 8, Height = 8,
                    Fill = "#ff0000",
                    VisibleWhen = "@cpu.temp > 100",
                },
            ],
        };

        var ctx = new TestBindingContext().WithNumber("@cpu.temp", 40, SensorKind.Temperature);
        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(8, 8));
        using var frame = renderer.Render(ctx);

        // Background only — widget hidden.
        var i = 0;
        Assert.Equal(0, frame.Span[i]);
        Assert.Equal(0, frame.Span[i + 1]);
        Assert.Equal(0, frame.Span[i + 2]);
    }

    [Fact]
    public void BarAndGauge_RenderWithBindings()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(64, 64, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new BarWidget
                {
                    X = 4, Y = 4, Width = 56, Height = 8,
                    Value = "@cpu.load",
                    Minimum = 0, Maximum = 100,
                    Color = "#00ff00",
                    TrackColor = "#333333",
                    Segments = 8,
                },
                new GaugeWidget
                {
                    X = 8, Y = 16, Width = 48, Height = 48,
                    Value = "@cpu.temp",
                    Minimum = 0, Maximum = 100,
                    Color = "#36c5f0",
                    ColorStops =
                    [
                        new(0, "#00ff00"),
                        new(80, "#ff0000"),
                    ],
                },
            ],
        };

        var ctx = new TestBindingContext()
            .WithNumber("@cpu.load", 50, SensorKind.Load)
            .WithNumber("@cpu.temp", 90, SensorKind.Temperature);

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(64, 64));
        using var frame = renderer.Render(ctx);
        Assert.Equal(64, frame.Width);
        Assert.Equal(0, renderer.Stats.SkippedWidgets);
    }

    [Fact]
    public void GraphWithHistory_DoesNotThrow()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(64, 32, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#000000"),
            Widgets =
            [
                new GraphWidget
                {
                    X = 0, Y = 0, Width = 64, Height = 32,
                    BackgroundColor = "#111111",
                    GridColor = "#222222",
                    Series =
                    [
                        new GraphSeries("@cpu.load", "#00ff00", Thickness: 2, Fill: true, Smooth: true),
                    ],
                },
            ],
        };

        var ctx = new TestBindingContext()
            .WithNumber("@cpu.load", 40, SensorKind.Load)
            .WithHistory("@cpu.load", 10, 20, 30, 40, 50, double.NaN, 60);

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(64, 32));
        using var frame = renderer.Render(ctx);
        Assert.NotNull(frame);
    }
}
