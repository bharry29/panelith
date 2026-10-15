using Microsoft.Extensions.Logging.Abstractions;
using Panelith.Devices.Drivers;
using Panelith.Engine;
using Panelith.Hosting;
using Panelith.Rendering;

namespace Panelith.Cli.Tests;

public sealed class VirtualPanelRunTests
{
    [Fact]
    public async Task RunLoopPaintsTheRigThemeOntoTheVirtualPanel()
    {
        var root = Directory.CreateTempSubdirectory("panelith-virtual-");
        var frames = Path.Combine(root.FullName, "frames");
        try
        {
            await using var runtime = await PanelithRuntime.StartAsync(
                demoSensors: true,
                loggers: NullLoggerFactory.Instance,
                virtualPanel: true,
                virtualOptions: new VirtualPanelOptions { OutputDirectory = frames },
                paths: new AppPaths(root.FullName),
                cancellationToken: TestContext.Current.CancellationToken);

            var options = runtime.Options with
            {
                Defaults = runtime.Options.Defaults with
                {
                    Theme = "rig",
                    FramesPerSecond = 10,
                },
            };

            await using var host = new PanelHost(
                runtime.Discovery,
                new ThemePanelRendererFactory(new ThemeLibrary(runtime.Paths)),
                runtime.Bindings,
                options,
                runtime.Loggers);

            await host.StartAsync(TestContext.Current.CancellationToken);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            PanelRuntimeStatus? status = null;
            while (DateTime.UtcNow < deadline)
            {
                status = host.Statuses.FirstOrDefault(s => s.Key.StartsWith("virtual:", StringComparison.Ordinal));
                if (status is { State: PanelRuntimeState.Running, FramesSent: > 0 }
                    or { State: PanelRuntimeState.Failed })
                {
                    break;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(status);
            Assert.True(
                status.State == PanelRuntimeState.Running && status.FramesSent > 0,
                status.Describe() + (status.LastError is null ? "" : " " + status.LastError));

            var pngPath = Path.Combine(frames, "frame.png");
            Assert.True(File.Exists(pngPath), "The virtual panel did not write a frame.");

            using var decoded = new SkiaImageCodec().Decode(await File.ReadAllBytesAsync(pngPath, TestContext.Current.CancellationToken));
            Assert.Equal(1280, decoded.Width);
            Assert.Equal(480, decoded.Height);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
