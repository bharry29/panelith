using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Providers;

/// <summary>
/// Windows sensor backend that wraps LibreHardwareMonitor. Needs elevation for
/// the kernel driver that unlocks most motherboard sensors, but degrades to
/// whatever is readable when not elevated rather than failing the poll loop.
/// Disposes the <see cref="Computer"/> so the LHM driver handle is released —
/// leaking it breaks the next process that tries to open the driver.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LibreHardwareMonitorSensorSource : ISensorSource
{
    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _initialized;

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
                IsNetworkEnabled = true,
                IsControllerEnabled = true,
            };
            computer.Open();
            _computer = computer;
            _initialized = true;
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
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        if (_computer is null)
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        try
        {
            _computer.Accept(_visitor);
        }
        catch (Exception)
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        var deduplicator = new SensorIdDeduplicator();
        var readings = new List<SensorReading>();
        foreach (var hardware in _computer.Hardware)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(hardware, deduplicator, readings);
        }

        return ValueTask.FromResult<IReadOnlyList<SensorReading>>(readings);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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

    private void Collect(IHardware hardware, SensorIdDeduplicator deduplicator, List<SensorReading> readings)
    {
        var deviceClass = MapHardwareType(hardware.HardwareType);
        var instance = ExtractInstance(hardware.Identifier?.ToString());

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

            readings.Add(SensorReading.Numeric(
                id,
                $"{hardware.Name} {label}",
                kind,
                sensor.Value,
                sensor.Min,
                sensor.Max,
                Id));
        }

        // Name sensors help alias resolution pick the discrete GPU over iGPU.
        if (hardware.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
        {
            var nameId = deduplicator.Unique(SensorId.From(deviceClass, instance, "name"));
            readings.Add(SensorReading.Textual(nameId, hardware.Name, hardware.Name, Id));
        }

        foreach (var sub in hardware.SubHardware)
        {
            Collect(sub, deduplicator, readings);
        }
    }

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

    private static string ExtractInstance(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "0";
        }

        // LHM identifiers look like /cpu/0 or /gpu-nvidia/0 — take the trailing number.
        for (var i = identifier.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(identifier[i]))
            {
                var digits = identifier[(i + 1)..];
                return digits.Length > 0 ? digits : "0";
            }
        }

        return identifier;
    }

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
