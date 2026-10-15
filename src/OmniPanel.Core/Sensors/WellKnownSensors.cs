namespace OmniPanel.Sensors;

/// <summary>
/// Canonical aliases for the handful of sensors every theme wants.
/// </summary>
/// <remarks>
/// <para>
/// Sensor ids are necessarily machine-specific -- a Ryzen reports
/// <c>cpu/0/temperature/tctl_tdie</c> where an Intel part reports
/// <c>cpu/0/temperature/package</c> -- which makes a theme written against raw
/// ids non-portable. That is the single biggest reason panel themes do not move
/// between machines today.
/// </para>
/// <para>
/// OmniPanel solves it with aliases: the sensor registry resolves each alias to
/// the best available concrete sensor on this machine, and themes bind to the
/// alias. A theme that uses only aliases renders correctly on any supported
/// machine, and a theme that needs something exotic can still bind to a raw id.
/// </para>
/// </remarks>
public static class WellKnownSensors
{
    /// <summary>Alias namespace prefix. Aliases look like <c>@cpu.temp</c>.</summary>
    public const char AliasPrefix = '@';

    /// <summary>Overall CPU package temperature.</summary>
    public const string CpuTemperature = "@cpu.temp";

    /// <summary>Total CPU utilisation.</summary>
    public const string CpuLoad = "@cpu.load";

    /// <summary>Highest current core clock.</summary>
    public const string CpuClock = "@cpu.clock";

    /// <summary>CPU package power draw.</summary>
    public const string CpuPower = "@cpu.power";

    /// <summary>CPU fan speed.</summary>
    public const string CpuFan = "@cpu.fan";

    /// <summary>Marketing name of the CPU.</summary>
    public const string CpuName = "@cpu.name";

    /// <summary>GPU core temperature.</summary>
    public const string GpuTemperature = "@gpu.temp";

    /// <summary>GPU core utilisation.</summary>
    public const string GpuLoad = "@gpu.load";

    /// <summary>GPU core clock.</summary>
    public const string GpuClock = "@gpu.clock";

    /// <summary>GPU board power draw.</summary>
    public const string GpuPower = "@gpu.power";

    /// <summary>GPU fan speed.</summary>
    public const string GpuFan = "@gpu.fan";

    /// <summary>Video memory in use.</summary>
    public const string GpuMemoryUsed = "@gpu.vram.used";

    /// <summary>Total video memory.</summary>
    public const string GpuMemoryTotal = "@gpu.vram.total";

    /// <summary>Video memory utilisation.</summary>
    public const string GpuMemoryLoad = "@gpu.vram.load";

    /// <summary>Marketing name of the GPU.</summary>
    public const string GpuName = "@gpu.name";

    /// <summary>System memory in use.</summary>
    public const string MemoryUsed = "@mem.used";

    /// <summary>Total system memory.</summary>
    public const string MemoryTotal = "@mem.total";

    /// <summary>System memory utilisation.</summary>
    public const string MemoryLoad = "@mem.load";

    /// <summary>Aggregate network download rate.</summary>
    public const string NetworkDown = "@net.down";

    /// <summary>Aggregate network upload rate.</summary>
    public const string NetworkUp = "@net.up";

    /// <summary>Busiest disk's utilisation.</summary>
    public const string DiskLoad = "@disk.load";

    /// <summary>Hottest drive temperature.</summary>
    public const string DiskTemperature = "@disk.temp";

    /// <summary>Time since boot.</summary>
    public const string Uptime = "@sys.uptime";

    /// <summary>Host name.</summary>
    public const string HostName = "@sys.host";

    /// <summary>Rendered frames per second, reported by OmniPanel itself.</summary>
    public const string PanelFps = "@panel.fps";

    /// <summary>Every alias OmniPanel defines, with the kind it resolves to.</summary>
    public static IReadOnlyDictionary<string, SensorKind> All { get; } =
        new Dictionary<string, SensorKind>(StringComparer.OrdinalIgnoreCase)
        {
            [CpuTemperature] = SensorKind.Temperature,
            [CpuLoad] = SensorKind.Load,
            [CpuClock] = SensorKind.Clock,
            [CpuPower] = SensorKind.Power,
            [CpuFan] = SensorKind.Fan,
            [CpuName] = SensorKind.Text,
            [GpuTemperature] = SensorKind.Temperature,
            [GpuLoad] = SensorKind.Load,
            [GpuClock] = SensorKind.Clock,
            [GpuPower] = SensorKind.Power,
            [GpuFan] = SensorKind.Fan,
            [GpuMemoryUsed] = SensorKind.Data,
            [GpuMemoryTotal] = SensorKind.Data,
            [GpuMemoryLoad] = SensorKind.Load,
            [GpuName] = SensorKind.Text,
            [MemoryUsed] = SensorKind.Data,
            [MemoryTotal] = SensorKind.Data,
            [MemoryLoad] = SensorKind.Load,
            [NetworkDown] = SensorKind.Throughput,
            [NetworkUp] = SensorKind.Throughput,
            [DiskLoad] = SensorKind.Load,
            [DiskTemperature] = SensorKind.Temperature,
            [Uptime] = SensorKind.Duration,
            [HostName] = SensorKind.Text,
            [PanelFps] = SensorKind.FrameRate,
        };

    /// <summary>Whether <paramref name="binding"/> names an alias rather than a raw id.</summary>
    public static bool IsAlias(string? binding) =>
        !string.IsNullOrEmpty(binding) && binding[0] == AliasPrefix;
}
