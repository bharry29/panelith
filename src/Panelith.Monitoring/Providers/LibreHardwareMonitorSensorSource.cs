using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using Panelith.Sensors;

namespace Panelith.Monitoring.Providers;

/// <summary>
/// Windows sensor backend that wraps LibreHardwareMonitor. Needs elevation for
/// the kernel driver that unlocks most motherboard sensors, but degrades to
/// whatever is readable when not elevated rather than failing the poll loop.
/// Disposes the <see cref="Computer"/> so the LHM driver handle is released —
/// leaking it breaks the next process that tries to open the driver.
/// </summary>
/// <remarks>
/// LHM is refreshed on its own thread and reads return the latest result. A
/// full refresh walks every chip and can take longer than the hub's per-source
/// budget, which would otherwise get the whole backend quarantined on a busy
/// machine. Networking is left to <see cref="SystemInfoSensorSource"/>: LHM
/// reports every Windows filter driver as its own adapter, dozens per machine.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LibreHardwareMonitorSensorSource : ISensorSource
{
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;
    private const double BytesPerMegabyte = 1024d * 1024d;

    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly Dictionary<string, string> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextInstance = new(StringComparer.Ordinal);
    private readonly TimeSpan _refreshInterval;
    private IReadOnlyList<SensorReading> _latest = [];
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshLoop;
    private bool _initialized;

    /// <summary>Creates the source.</summary>
    /// <param name="refreshInterval">How often LHM is refreshed in the background.</param>
    public LibreHardwareMonitorSensorSource(TimeSpan? refreshInterval = null) =>
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public string Id => "lhm";

    /// <inheritdoc />
    public string DisplayName => "LibreHardwareMonitor";

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public bool RequiresElevation => true;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || _initialized)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = false,

                // Fan controllers are found by probing serial ports and HID
                // devices, which can disturb the serial and HID panels Panelith
                // itself drives. No alias reads them.
                IsControllerEnabled = false,
            };
            computer.Open();
            _computer = computer;
            _initialized = true;
            Refresh();
            _refreshCancellation = new CancellationTokenSource();
            var token = _refreshCancellation.Token;
            _refreshLoop = Task.Run(() => RefreshLoopAsync(token), CancellationToken.None);
        }
        catch (Exception)
        {
            // Degrade: leave IsAvailable true but ReadAsync returns empty until
            // a later InitializeAsync retry succeeds. Elevation-related failures
            // are the common case and must not take the hub down.
            _computer = null;
            _initialized = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Volatile.Read(ref _latest));

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_refreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Refresh()
    {
        if (_computer is not { } computer)
        {
            return;
        }

        try
        {
            computer.Accept(_visitor);
        }
        catch (Exception)
        {
            // One chip failing to update keeps the previous readings on screen.
            return;
        }

        var deduplicator = new SensorIdDeduplicator();
        var readings = new List<SensorReading>();
        foreach (var hardware in OrderForInstances(computer.Hardware))
        {
            try
            {
                Collect(hardware, InstanceFor(hardware), deduplicator, readings);
            }
            catch (Exception)
            {
                // A device that vanished mid-walk is skipped for this refresh.
            }
        }

        Volatile.Write(ref _latest, readings);
    }

    /// <summary>
    /// Discrete GPUs are numbered before integrated ones, so <c>gpu/0</c> is the
    /// card a gamer means when they say "my GPU".
    /// </summary>
    private static IEnumerable<IHardware> OrderForInstances(IEnumerable<IHardware> hardware) =>
        hardware
            .OrderBy(h => MapHardwareType(h.HardwareType), StringComparer.Ordinal)
            .ThenBy(h => IsIntegratedGpu(h) ? 1 : 0)
            .ThenBy(h => h.Identifier?.ToString(), StringComparer.Ordinal);

    private string InstanceFor(IHardware hardware)
    {
        var key = hardware.Identifier?.ToString() ?? hardware.Name;
        if (_instances.TryGetValue(key, out var instance))
        {
            return instance;
        }

        var deviceClass = MapHardwareType(hardware.HardwareType);
        if (hardware.HardwareType == HardwareType.Memory)
        {
            instance = hardware.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ? "virtual" : "0";
        }
        else
        {
            var next = _nextInstance.GetValueOrDefault(deviceClass);
            _nextInstance[deviceClass] = next + 1;
            instance = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _instances[key] = instance;
        return instance;
    }

    internal static bool IsIntegratedGpu(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel
        && IsIntegratedGpuName(hardware.Name);

    /// <summary>Recognises integrated graphics by the names AMD and Intel give them.</summary>
    internal static bool IsIntegratedGpuName(string name)
    {
        if (name.Contains("Arc", StringComparison.Ordinal) || name.Contains(" RX ", StringComparison.Ordinal))
        {
            return false;
        }

        return name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && name.EndsWith("M Graphics", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _refreshCancellation, null) is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (_refreshLoop is { } loop)
            {
                await loop.ConfigureAwait(false);
            }

            cancellation.Dispose();
        }

        if (_computer is { } computer)
        {
            _computer = null;
            try
            {
                computer.Close();
            }
            catch (Exception)
            {
                // Close can throw if the driver was already torn down.
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private void Collect(IHardware hardware, string instance, SensorIdDeduplicator deduplicator, List<SensorReading> readings)
    {
        var deviceClass = MapHardwareType(hardware.HardwareType);
        double? memoryUsed = null;
        double? memoryAvailable = null;

        foreach (var sensor in hardware.Sensors)
        {
            var kind = MapSensorType(sensor.SensorType);
            if (kind == SensorKind.Unknown)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(sensor.Name) ? sensor.SensorType.ToString() : sensor.Name;
            var id = deduplicator.Unique(SensorId.From(
                deviceClass,
                instance,
                KindSegment(kind),
                label));

            var scale = sensor.SensorType switch
            {
                SensorType.Data => BytesPerGigabyte,
                SensorType.SmallData => BytesPerMegabyte,
                _ => 1d,
            };

            var value = Scale(sensor.Value, scale);

            // Without the kernel driver LHM reports locked sensors as exactly
            // zero, which a theme would otherwise show as a frozen 0° or 0 MHz.
            if (value is 0d
                && (kind is SensorKind.Temperature or SensorKind.Clock
                    || (kind == SensorKind.Power && hardware.HardwareType == HardwareType.Cpu)))
            {
                value = null;
            }

            if (hardware.HardwareType == HardwareType.Memory && sensor.SensorType == SensorType.Data)
            {
                if (label.Contains("used", StringComparison.OrdinalIgnoreCase))
                {
                    memoryUsed = value;
                }
                else if (label.Contains("available", StringComparison.OrdinalIgnoreCase))
                {
                    memoryAvailable = value;
                }
            }

            readings.Add(SensorReading.Numeric(
                id,
                $"{hardware.Name} {label}",
                kind,
                value,
                Scale(sensor.Min, scale),
                Scale(sensor.Max, scale),
                Id));
        }

        if (memoryUsed is { } used && memoryAvailable is { } available)
        {
            readings.Add(SensorReading.Numeric(
                deduplicator.Unique(SensorId.From(deviceClass, instance, "data", "total")),
                $"{hardware.Name} Total",
                SensorKind.Data,
                used + available,
                sourceId: Id));
        }

        // Name sensors help alias resolution pick the discrete GPU over iGPU.
        if (hardware.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            var nameId = deduplicator.Unique(SensorId.From(deviceClass, instance, "name"));
            readings.Add(SensorReading.Textual(nameId, hardware.Name, hardware.Name, Id));
        }

        foreach (var sub in hardware.SubHardware)
        {
            Collect(sub, instance, deduplicator, readings);
        }
    }

    private static double? Scale(float? value, double factor) =>
        value is { } v ? v * factor : null;

    private static string MapHardwareType(HardwareType type) => type switch
    {
        HardwareType.Cpu => "cpu",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
        HardwareType.Memory => "memory",
        HardwareType.Storage => "disk",
        HardwareType.Network => "network",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "motherboard",
        HardwareType.Cooler => "cooler",
        HardwareType.Battery => "battery",
        HardwareType.Psu => "psu",
        _ => "lhm",
    };

    private static SensorKind MapSensorType(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load or SensorType.Level => SensorKind.Load,
        SensorType.Clock or SensorType.Frequency => SensorKind.Clock,
        SensorType.Power => SensorKind.Power,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Current => SensorKind.Current,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Control => SensorKind.Control,
        SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Throughput => SensorKind.Throughput,
        SensorType.TimeSpan => SensorKind.Duration,
        _ => SensorKind.Unknown,
    };

    private static string KindSegment(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "temperature",
        SensorKind.Load => "load",
        SensorKind.Clock => "clock",
        SensorKind.Power => "power",
        SensorKind.Voltage => "voltage",
        SensorKind.Current => "current",
        SensorKind.Fan => "fan",
        SensorKind.Control => "control",
        SensorKind.Data => "data",
        SensorKind.Throughput => "throughput",
        SensorKind.Duration => "duration",
        _ => "other",
    };

    [SupportedOSPlatform("windows")]
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}

/// <summary>
/// Cross-platform façade around <see cref="LibreHardwareMonitorSensorSource"/>
/// that is safe to construct on Linux without CA1416 warnings.
/// </summary>
public sealed class LibreHardwareMonitorFacade : ISensorSource
{
    private readonly ISensorSource _inner;

    /// <summary>Creates the Windows implementation when running on Windows.</summary>
    public LibreHardwareMonitorFacade()
    {
        if (OperatingSystem.IsWindows())
        {
            _inner = new LibreHardwareMonitorSensorSource();
        }
        else
        {
            _inner = new Unavailable("lhm", "LibreHardwareMonitor");
        }
    }

    /// <inheritdoc />
    public string Id => _inner.Id;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public bool IsAvailable => _inner.IsAvailable;

    /// <inheritdoc />
    public bool RequiresElevation => true;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class Unavailable(string id, string displayName) : ISensorSource
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public bool IsAvailable => false;
        public bool RequiresElevation => true;
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
