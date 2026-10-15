using System.Diagnostics;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Providers;

/// <summary>
/// Reports OmniPanel's own CPU, memory and frame rate so themes can surface
/// <c>@panel.fps</c> and operators can see the tool's cost on the panel itself.
/// </summary>
public sealed class ProcessSensorSource : ISensorSource
{
    private readonly Process _process;
    private readonly Func<double?> _fpsProvider;
    private TimeSpan _previousCpu;
    private DateTimeOffset _previousSampleAt;
    private bool _hasPrevious;

    /// <summary>
    /// Creates a source for the current process. <paramref name="fpsProvider"/>
    /// supplies the most recent render FPS; return <see langword="null"/> when
    /// the renderer has not produced a frame yet.
    /// </summary>
    public ProcessSensorSource(Func<double?>? fpsProvider = null)
    {
        _process = Process.GetCurrentProcess();
        _fpsProvider = fpsProvider ?? (() => null);
        _previousCpu = _process.TotalProcessorTime;
        _previousSampleAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public string Id => "process";

    /// <inheritdoc />
    public string DisplayName => "OmniPanel Process";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        _process.Refresh();
        var now = DateTimeOffset.UtcNow;
        var cpu = _process.TotalProcessorTime;
        double? cpuLoad = 0d;

        if (_hasPrevious)
        {
            var elapsed = (now - _previousSampleAt).TotalMilliseconds;
            if (elapsed > 0d)
            {
                var cpuDelta = (cpu - _previousCpu).TotalMilliseconds;
                cpuLoad = Math.Clamp(cpuDelta / (elapsed * Environment.ProcessorCount) * 100d, 0d, 100d);
            }
        }

        _previousCpu = cpu;
        _previousSampleAt = now;
        _hasPrevious = true;

        var workingSet = (double)_process.WorkingSet64;
        var fps = _fpsProvider();

        var readings = new List<SensorReading>
        {
            SensorReading.Numeric(
                SensorId.From("panel", "0", "load", "cpu"),
                "OmniPanel CPU",
                SensorKind.Load,
                cpuLoad,
                0d,
                100d,
                Id),
            SensorReading.Numeric(
                SensorId.From("panel", "0", "data", "memory"),
                "OmniPanel Memory",
                SensorKind.Data,
                workingSet,
                sourceId: Id),
            SensorReading.Numeric(
                SensorId.From("panel", "0", "framerate", "fps"),
                "OmniPanel FPS",
                SensorKind.FrameRate,
                fps,
                0d,
                240d,
                Id),
        };

        return ValueTask.FromResult<IReadOnlyList<SensorReading>>(readings);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
