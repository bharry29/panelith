using Panelith.Expressions;
using Panelith.Imaging;
using Panelith.Themes;
using Xunit;

namespace Panelith.Rendering;

public sealed class AllocationTests
{
    [Fact]
    public void RenderInto_SteadyState_DoesNotAllocatePerFrame()
    {
        var theme = new Theme
        {
            Canvas = new ThemeCanvas(64, 64, ThemeScaleMode.Stretch),
            Background = new ThemeBackground("#102030"),
            Widgets =
            [
                new ShapeWidget
                {
                    X = 8, Y = 8, Width = 48, Height = 48,
                    Shape = ShapeKind.Rectangle,
                    Fill = "#ff6600",
                    CornerRadius = 4,
                },
                new ShapeWidget
                {
                    X = 16, Y = 16, Width = 32, Height = 32,
                    Shape = ShapeKind.Ellipse,
                    Stroke = "#ffffff",
                    StrokeWidth = 2,
                },
            ],
        };

        using var renderer = new ThemeRenderer(theme, new ThemeRenderOptions(64, 64));
        using var target = FrameBuffer.Rent(64, 64);
        var context = EmptyBindingContext.Instance;

        // Warm up: surface, paints, layout, JIT.
        for (var i = 0; i < 20; i++)
        {
            renderer.RenderInto(target, context);
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 100;
        for (var i = 0; i < iterations; i++)
        {
            renderer.RenderInto(target, context);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Shape-only frames must stay essentially allocation-free. Allow a tiny
        // slack for runtime bookkeeping so the assertion is not flaky across
        // GC implementations, but catch a regression that rents a frame buffer
        // or allocates paints each frame (those are kilobytes each).
        const long budgetBytes = 2_048;
        Assert.True(
            allocated <= budgetBytes,
            $"Steady-state RenderInto allocated {allocated} bytes over {iterations} frames (budget {budgetBytes}).");
    }
}
