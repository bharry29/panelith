using RigGlass.Monitoring.Providers;
using RigGlass.Sensors;
using Xunit;

namespace RigGlass.Monitoring;

public sealed class ProcessSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReportsPanelCpuMemoryAndFps()
    {
        var source = new ProcessSensorSource(() => 72.5);
        _ = await source.ReadAsync(Ct);
        var readings = await source.ReadAsync(Ct);

        Assert.Contains(readings, r => r.Id.Value == "panel/0/load/cpu" && r.Kind == SensorKind.Load);
        Assert.Contains(readings, r => r.Id.Value == "panel/0/data/memory" && r.Kind == SensorKind.Data);
        var fps = Assert.Single(readings, r => r.Id.Value == "panel/0/framerate/fps");
        Assert.Equal(72.5d, fps.Value);
        await source.DisposeAsync();
    }
}
