using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Panelith.Monitoring.Providers.Rtss;
using Panelith.Sensors;

namespace Panelith.Monitoring.Providers;

/// <summary>
/// In-game frame rate, frame time and 1% low, read from RivaTuner Statistics
/// Server's shared memory.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no public API for another program's frame rate. RTSS (bundled
/// with MSI Afterburner, or installed alone) already hooks games to draw its
/// overlay and publishes what it measures, so reading it costs nothing and needs
/// no injection of our own. When RTSS is not running this source reports
/// nothing and themes fall back to their hint text.
/// </para>
/// <para>
/// The game measured is the foreground program if RTSS is tracking it,
/// otherwise the one RTSS updated most recently. Frame times are sampled on a
/// background thread about 60 times a second to build the 1% low.
/// </para>
/// </remarks>
public sealed partial class RtssSensorSource : ISensorSource
{
    /// <summary>The frame-rate sensor id.</summary>
    public static readonly SensorId FpsId = SensorId.From("game", "0", "framerate", "fps");

    /// <summary>The 1% low sensor id.</summary>
    public static readonly SensorId Low1Id = SensorId.From("game", "0", "framerate", "low1");

    /// <summary>The frame-time sensor id, in milliseconds.</summary>
    public static readonly SensorId FrameTimeId = SensorId.From("game", "0", "frametime", "last");

    /// <summary>The game-name sensor id.</summary>
    public static readonly SensorId NameId = SensorId.From("game", "0", "name", "process");

    /// <summary>The game's process id, for matching against running programs.</summary>
    public static readonly SensorId ProcessIdId = SensorId.From("game", "0", "count", "pid");

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private const uint FreshMilliseconds = 3000;

    private readonly Lock _gate = new();
    private readonly Queue<(long At, double FrameTimeMs)> _samples = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private RtssApp? _target;

    /// <inheritdoc />
    public string Id => "rtss";

    /// <inheritdoc />
    public string DisplayName => "RivaTuner Statistics Server";

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>Whether RTSS's shared memory was found on the last check.</summary>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() && _thread is null)
        {
            _thread = new Thread(Sample) { IsBackground = true, Name = "Panelith RTSS sampler", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        RtssApp? target;
        double? low;
        lock (_gate)
        {
            target = _target;
            low = RtssSharedMemory.OnePercentLow(_samples.Select(s => s.FrameTimeMs).ToArray());
        }

        if (target is not { } app)
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        IReadOnlyList<SensorReading> readings =
        [
            SensorReading.Numeric(FpsId, "Game FPS", SensorKind.FrameRate, app.FramesPerSecond, 0d, null, Id),
            SensorReading.Numeric(Low1Id, "Game 1% Low", SensorKind.FrameRate, low, 0d, null, Id),
            SensorReading.Numeric(FrameTimeId, "Game Frame Time (ms)", SensorKind.Unknown, app.FrameTimeMilliseconds, 0d, null, Id),
            SensorReading.Textual(NameId, "Game", app.Name, Id),
            SensorReading.Numeric(ProcessIdId, "Game Process Id", SensorKind.Count, app.ProcessId, sourceId: Id),
        ];
        return ValueTask.FromResult(readings);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Sample()
    {
        var token = _stop.Token;
        var header = new byte[RtssSharedMemory.HeaderSize];
        var entry = new byte[RtssSharedMemory.EntryPrefixSize];
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        var foreground = 0;
        long foregroundCheckedAt = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (view is null)
                {
                    (mapping, view) = TryOpen();
                    IsRunning = view is not null;
                    if (view is null)
                    {
                        Clear();
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                        continue;
                    }
                }

                var now = Environment.TickCount64;
                if (now - foregroundCheckedAt > 250)
                {
                    foreground = ForegroundProcessId();
                    foregroundCheckedAt = now;
                }

                if (!TryPick(view, header, entry, foreground, out var picked))
                {
                    view.Dispose();
                    mapping?.Dispose();
                    view = null;
                    mapping = null;
                    continue;
                }

                Record(picked, now);
                token.WaitHandle.WaitOne(picked is null ? 500 : 15);
            }
        }
        finally
        {
            view?.Dispose();
            mapping?.Dispose();
        }
    }

    private static (MemoryMappedFile?, MemoryMappedViewAccessor?) TryOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        try
        {
            var mapping = MemoryMappedFile.OpenExisting(RtssSharedMemory.MappingName, MemoryMappedFileRights.Read);
            return (mapping, mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    /// <returns>False when the mapping no longer looks like RTSS's and must be reopened.</returns>
    private static bool TryPick(MemoryMappedViewAccessor view, byte[] header, byte[] entry, int foreground, out RtssApp? picked)
    {
        picked = null;
        if (view.Capacity < RtssSharedMemory.HeaderSize)
        {
            return false;
        }

        view.ReadArray(0, header, 0, header.Length);
        if (RtssSharedMemory.ParseHeader(header) is not { } layout)
        {
            return false;
        }

        var tick = (uint)Environment.TickCount;
        RtssApp? newest = null;
        for (var i = 0; i < layout.Count; i++)
        {
            var start = (long)layout.Offset + ((long)i * layout.EntrySize);
            if (start + entry.Length > view.Capacity)
            {
                break;
            }

            view.ReadArray(start, entry, 0, entry.Length);
            if (RtssSharedMemory.ParseEntry(entry) is not { } app || unchecked(tick - app.Time1) > FreshMilliseconds)
            {
                continue;
            }

            if (app.ProcessId == foreground)
            {
                picked = app;
                return true;
            }

            if (newest is null || unchecked(app.Time1 - newest.Value.Time1) < int.MaxValue)
            {
                newest = app;
            }
        }

        picked = newest;
        return true;
    }

    private void Record(RtssApp? picked, long now)
    {
        lock (_gate)
        {
            if (picked?.ProcessId != _target?.ProcessId)
            {
                _samples.Clear();
            }

            _target = picked;
            if (picked?.FrameTimeMilliseconds is { } frameTime)
            {
                _samples.Enqueue((now, frameTime));
            }

            while (_samples.Count > 0 && now - _samples.Peek().At > Window.TotalMilliseconds)
            {
                _samples.Dequeue();
            }
        }
    }

    private void Clear()
    {
        lock (_gate)
        {
            _target = null;
            _samples.Clear();
        }
    }

    private static int ForegroundProcessId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var window = GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(window, out var id);
        return id;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint window, out int processId);
}
