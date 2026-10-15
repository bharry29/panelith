using System.Globalization;
using RigGlass.Monitoring.IO;
using RigGlass.Sensors;

namespace RigGlass.Monitoring.Providers;

/// <summary>
/// Samples host-wide counters from <c>/proc</c> and <c>/sys</c>: CPU load,
/// memory, network and disk throughput, uptime, load average, battery and
/// filesystem usage. Rate sensors need a previous sample; the first poll
/// reports zeros (never a garbage spike) so graphs start cleanly.
/// </summary>
public sealed class LinuxSystemSensorSource : ISensorSource
{
    private static readonly string[] VirtualInterfacePrefixes =
    [
        "lo", "docker", "veth", "br-", "br0", "virbr", "tun", "tap", "wg",
        "vnet", "kube", "cni", "flannel", "cali", "nodelocal",
    ];

    private readonly IFileSystem _fs;
    private readonly IDriveProbe _drives;
    private readonly string _procRoot;
    private readonly string _sysRoot;

    private CpuSample? _previousCpu;
    private Dictionary<string, (ulong Rx, ulong Tx)>? _previousNet;
    private Dictionary<string, (ulong ReadSectors, ulong WriteSectors, ulong IoTicks)>? _previousBlock;
    private DateTimeOffset? _previousSampleAt;

    /// <summary>Creates a source against the live host filesystem.</summary>
    public LinuxSystemSensorSource(IFileSystem? fileSystem = null, IDriveProbe? driveProbe = null)
        : this(fileSystem ?? RealFileSystem.Instance, driveProbe ?? SystemDriveProbe.Instance, "/proc", "/sys")
    {
    }

    /// <summary>Creates a source against fixture roots — used by tests.</summary>
    public LinuxSystemSensorSource(
        IFileSystem fileSystem,
        IDriveProbe driveProbe,
        string procRoot,
        string sysRoot)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _drives = driveProbe ?? throw new ArgumentNullException(nameof(driveProbe));
        _procRoot = procRoot ?? throw new ArgumentNullException(nameof(procRoot));
        _sysRoot = sysRoot ?? throw new ArgumentNullException(nameof(sysRoot));
    }

    /// <inheritdoc />
    public string Id => "linux-system";

    /// <inheritdoc />
    public string DisplayName => "Linux system";

    /// <inheritdoc />
    /// <remarks>
    /// Availability follows the fixture/proc tree rather than the host OS so the
    /// same tests pass on Windows CI against an in-memory <c>/proc</c>.
    /// </remarks>
    public bool IsAvailable =>
        _fs.FileExists(Path.Combine(_procRoot, "stat"))
        && _fs.FileExists(Path.Combine(_procRoot, "meminfo"));

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        var now = DateTimeOffset.UtcNow;
        var readings = new List<SensorReading>(64);
        var elapsed = _previousSampleAt is { } previous
            ? (now - previous).TotalSeconds
            : 0d;

        ReadCpu(readings, elapsed);
        ReadMemory(readings);
        ReadNetwork(readings, elapsed);
        ReadBlockDevices(readings, elapsed);
        ReadUptime(readings);
        ReadLoadAverage(readings);
        ReadBattery(readings);
        ReadVolumes(readings);
        ReadHost(readings);

        _previousSampleAt = now;
        return ValueTask.FromResult<IReadOnlyList<SensorReading>>(readings);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ReadCpu(List<SensorReading> readings, double elapsed)
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "stat"));
        if (text is null)
        {
            return;
        }

        var samples = new List<(string Name, CpuSample Sample)>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            // Aggregate line is "cpu"; per-core lines are "cpu0", "cpu1", ...
            if (parts[0] != "cpu" && !char.IsAsciiDigit(parts[0][^1]))
            {
                continue;
            }

            if (!TryParseCpuSample(parts, out var sample))
            {
                continue;
            }

            samples.Add((parts[0], sample));
        }

        var previous = _previousCpu;
        var previousMap = _cpuCorePrevious;
        _previousCpu = samples.Count > 0 && samples[0].Name == "cpu" ? samples[0].Sample : null;
        _cpuCorePrevious = samples
            .Where(static s => s.Name != "cpu")
            .ToDictionary(static s => s.Name, static s => s.Sample, StringComparer.Ordinal);

        foreach (var (name, sample) in samples)
        {
            double? load = null;
            if (elapsed > 0d)
            {
                if (name == "cpu" && previous is { } prevAgg)
                {
                    load = ComputeCpuLoad(prevAgg, sample);
                }
                else if (name != "cpu" && previousMap is not null && previousMap.TryGetValue(name, out var prevCore))
                {
                    load = ComputeCpuLoad(prevCore, sample);
                }
                else if (elapsed > 0d)
                {
                    // First sample for this core after a topology change: report 0,
                    // never an uninitialised spike.
                    load = 0d;
                }
            }
            else
            {
                load = 0d;
            }

            if (name == "cpu")
            {
                readings.Add(SensorReading.Numeric(
                    SensorId.From("cpu", "0", "load", "total"),
                    "CPU Load",
                    SensorKind.Load,
                    load,
                    0d,
                    100d,
                    Id));
            }
            else
            {
                var coreIndex = name.Length > 3 ? name[3..] : name;
                readings.Add(SensorReading.Numeric(
                    SensorId.From("cpu", "0", "load", "core", coreIndex),
                    $"CPU Core {coreIndex} Load",
                    SensorKind.Load,
                    load,
                    0d,
                    100d,
                    Id));
            }
        }

        var cpuName = ReadCpuName();
        if (cpuName is not null)
        {
            readings.Add(SensorReading.Textual(
                SensorId.From("cpu", "0", "name"),
                "CPU Name",
                cpuName,
                Id));
        }
    }

    private Dictionary<string, CpuSample>? _cpuCorePrevious;

    private string? ReadCpuName()
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "cpuinfo"));
        if (text is null)
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    var name = line[(colon + 1)..].Trim();
                    return name.Length == 0 ? null : name;
                }
            }
        }

        return null;
    }

    private void ReadMemory(List<SensorReading> readings)
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "meminfo"));
        if (text is null)
        {
            return;
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();
            var space = rest.IndexOf(' ');
            var numberPart = space < 0 ? rest : rest[..space];
            if (long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
            {
                values[key] = kib * 1024L;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total) || total <= 0)
        {
            return;
        }

        var available = values.TryGetValue("MemAvailable", out var avail)
            ? avail
            : values.GetValueOrDefault("MemFree");
        var used = Math.Clamp(total - available, 0, total);
        var load = used * 100d / total;

        readings.Add(SensorReading.Numeric(SensorId.From("memory", "0", "data", "used"), "Memory Used", SensorKind.Data, used, 0d, total, Id));
        readings.Add(SensorReading.Numeric(SensorId.From("memory", "0", "data", "total"), "Memory Total", SensorKind.Data, total, sourceId: Id));
        readings.Add(SensorReading.Numeric(SensorId.From("memory", "0", "load", "used"), "Memory Load", SensorKind.Load, load, 0d, 100d, Id));

        if (values.TryGetValue("SwapTotal", out var swapTotal) && swapTotal > 0)
        {
            var swapFree = values.GetValueOrDefault("SwapFree");
            var swapUsed = Math.Clamp(swapTotal - swapFree, 0, swapTotal);
            readings.Add(SensorReading.Numeric(SensorId.From("memory", "0", "data", "swap_used"), "Swap Used", SensorKind.Data, swapUsed, 0d, swapTotal, Id));
            readings.Add(SensorReading.Numeric(SensorId.From("memory", "0", "data", "swap_total"), "Swap Total", SensorKind.Data, swapTotal, sourceId: Id));
        }
    }

    private void ReadNetwork(List<SensorReading> readings, double elapsed)
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "net", "dev"));
        if (text is null)
        {
            return;
        }

        var current = new Dictionary<string, (ulong Rx, ulong Tx)>(StringComparer.Ordinal);
        var previous = _previousNet;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var iface = line[..colon].Trim();
            if (IsVirtualInterface(iface))
            {
                continue;
            }

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9
                || !ulong.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx)
                || !ulong.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx))
            {
                continue;
            }

            current[iface] = (rx, tx);

            double? down;
            double? up;
            if (elapsed > 0d && previous is not null && previous.TryGetValue(iface, out var prev))
            {
                down = RateFromCounter(prev.Rx, rx, elapsed);
                up = RateFromCounter(prev.Tx, tx, elapsed);
            }
            else
            {
                down = 0d;
                up = 0d;
            }

            readings.Add(SensorReading.Numeric(
                SensorId.From("network", iface, "throughput", "down"),
                $"{iface} Download",
                SensorKind.Throughput,
                down,
                sourceId: Id));
            readings.Add(SensorReading.Numeric(
                SensorId.From("network", iface, "throughput", "up"),
                $"{iface} Upload",
                SensorKind.Throughput,
                up,
                sourceId: Id));
        }

        // Aggregate sums per-interface rates that already scrubbed wraparound,
        // so a single rolling counter cannot invent a multi-gigabyte spike.
        double? aggDown = elapsed > 0d && previous is not null ? SumRates(previous, current, elapsed, rx: true) : 0d;
        double? aggUp = elapsed > 0d && previous is not null ? SumRates(previous, current, elapsed, rx: false) : 0d;

        readings.Add(SensorReading.Numeric(
            SensorId.From("network", "0", "throughput", "down"),
            "Network Download",
            SensorKind.Throughput,
            aggDown,
            sourceId: Id));
        readings.Add(SensorReading.Numeric(
            SensorId.From("network", "0", "throughput", "up"),
            "Network Upload",
            SensorKind.Throughput,
            aggUp,
            sourceId: Id));

        _previousNet = current;
    }

    private static double SumRates(
        Dictionary<string, (ulong Rx, ulong Tx)> previous,
        Dictionary<string, (ulong Rx, ulong Tx)> current,
        double elapsed,
        bool rx)
    {
        double total = 0d;
        foreach (var (iface, sample) in current)
        {
            if (!previous.TryGetValue(iface, out var prev))
            {
                continue;
            }

            var rate = RateFromCounter(rx ? prev.Rx : prev.Tx, rx ? sample.Rx : sample.Tx, elapsed);
            if (rate is { } r)
            {
                total += r;
            }
        }

        return total;
    }

    private void ReadBlockDevices(List<SensorReading> readings, double elapsed)
    {
        var blockRoot = Path.Combine(_sysRoot, "block");
        if (!_fs.DirectoryExists(blockRoot))
        {
            return;
        }

        var current = new Dictionary<string, (ulong ReadSectors, ulong WriteSectors, ulong IoTicks)>(StringComparer.Ordinal);
        var previous = _previousBlock;
        double busiest = 0d;

        foreach (var deviceDir in _fs.EnumerateDirectories(blockRoot).OrderBy(static p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(deviceDir);
            // Skip partitions (sda1, nvme0n1p1); keep whole devices.
            if (name.Length == 0 || char.IsAsciiDigit(name[^1]) && !name.StartsWith("nvme", StringComparison.Ordinal))
            {
                // nvme0n1 ends with digit but is a device; nvme0n1p1 is a partition.
                if (name.Contains('p', StringComparison.Ordinal) && char.IsAsciiDigit(name[^1]))
                {
                    continue;
                }

                if (!name.StartsWith("nvme", StringComparison.Ordinal) && char.IsAsciiDigit(name[^1]))
                {
                    continue;
                }
            }

            if (name.Contains('p', StringComparison.Ordinal)
                && name.StartsWith("nvme", StringComparison.Ordinal)
                && char.IsAsciiDigit(name[^1]))
            {
                var pIndex = name.LastIndexOf('p');
                if (pIndex > 0 && char.IsAsciiDigit(name[pIndex - 1]))
                {
                    continue;
                }
            }

            var stat = _fs.ReadAllText(Path.Combine(deviceDir, "stat"));
            if (stat is null)
            {
                continue;
            }

            var fields = stat.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7
                || !ulong.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var readSectors)
                || !ulong.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var writeSectors))
            {
                continue;
            }

            ulong ioTicks = 0;
            if (fields.Length >= 10)
            {
                _ = ulong.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out ioTicks);
            }

            current[name] = (readSectors, writeSectors, ioTicks);

            double? readRate = 0d;
            double? writeRate = 0d;
            double? util = 0d;
            if (elapsed > 0d && previous is not null && previous.TryGetValue(name, out var prev))
            {
                readRate = RateFromCounter(prev.ReadSectors, readSectors, elapsed) is { } rr ? rr * 512d : null;
                writeRate = RateFromCounter(prev.WriteSectors, writeSectors, elapsed) is { } wr ? wr * 512d : null;
                if (RateFromCounter(prev.IoTicks, ioTicks, elapsed) is { } tickRate)
                {
                    // io_ticks is milliseconds of busy time; convert to percent.
                    util = Math.Clamp(tickRate / 10d, 0d, 100d);
                }
            }

            if (util is { } u && u > busiest)
            {
                busiest = u;
            }

            readings.Add(SensorReading.Numeric(
                SensorId.From("disk", name, "throughput", "read"),
                $"{name} Read",
                SensorKind.Throughput,
                readRate,
                sourceId: Id));
            readings.Add(SensorReading.Numeric(
                SensorId.From("disk", name, "throughput", "write"),
                $"{name} Write",
                SensorKind.Throughput,
                writeRate,
                sourceId: Id));
            readings.Add(SensorReading.Numeric(
                SensorId.From("disk", name, "load", "busy"),
                $"{name} Busy",
                SensorKind.Load,
                util,
                0d,
                100d,
                Id));
        }

        readings.Add(SensorReading.Numeric(
            SensorId.From("disk", "0", "load", "busy"),
            "Disk Load",
            SensorKind.Load,
            elapsed > 0d && previous is not null ? busiest : 0d,
            0d,
            100d,
            Id));

        _previousBlock = current;
    }

    private void ReadUptime(List<SensorReading> readings)
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "uptime"));
        if (text is null)
        {
            return;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return;
        }

        readings.Add(SensorReading.Numeric(
            SensorId.From("system", "0", "duration", "uptime"),
            "Uptime",
            SensorKind.Duration,
            seconds,
            sourceId: Id));
    }

    private void ReadLoadAverage(List<SensorReading> readings)
    {
        var text = _fs.ReadAllText(Path.Combine(_procRoot, "loadavg"));
        if (text is null)
        {
            return;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var load))
            {
                continue;
            }

            var label = i switch { 0 => "1m", 1 => "5m", _ => "15m" };
            readings.Add(SensorReading.Numeric(
                SensorId.From("system", "0", "load", "avg_" + label),
                $"Load Average ({label})",
                SensorKind.Load,
                load,
                sourceId: Id));
        }
    }

    private void ReadBattery(List<SensorReading> readings)
    {
        var root = Path.Combine(_sysRoot, "class", "power_supply");
        if (!_fs.DirectoryExists(root))
        {
            return;
        }

        var index = 0;
        foreach (var supply in _fs.EnumerateDirectories(root).OrderBy(static p => p, StringComparer.Ordinal))
        {
            var type = _fs.ReadAllText(Path.Combine(supply, "type"))?.Trim();
            if (!string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(supply);
            var capacityText = _fs.ReadAllText(Path.Combine(supply, "capacity"))?.Trim();
            if (capacityText is not null
                && double.TryParse(capacityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var capacity))
            {
                readings.Add(SensorReading.Numeric(
                    SensorId.From("battery", index.ToString(CultureInfo.InvariantCulture), "load", "capacity"),
                    $"{name} Capacity",
                    SensorKind.Load,
                    capacity,
                    0d,
                    100d,
                    Id));
            }

            var status = _fs.ReadAllText(Path.Combine(supply, "status"))?.Trim();
            if (!string.IsNullOrEmpty(status))
            {
                readings.Add(SensorReading.Textual(
                    SensorId.From("battery", index.ToString(CultureInfo.InvariantCulture), "status"),
                    $"{name} Status",
                    status,
                    Id));
            }

            index++;
        }
    }

    private void ReadVolumes(List<SensorReading> readings)
    {
        var index = 0;
        foreach (var volume in _drives.GetVolumes())
        {
            if (volume.TotalBytes <= 0)
            {
                continue;
            }

            var idIndex = index.ToString(CultureInfo.InvariantCulture);
            var load = volume.UsedBytes * 100d / volume.TotalBytes;
            readings.Add(SensorReading.Numeric(
                SensorId.From("volume", idIndex, "data", "used"),
                $"{volume.Name} Used",
                SensorKind.Data,
                volume.UsedBytes,
                0d,
                volume.TotalBytes,
                Id));
            readings.Add(SensorReading.Numeric(
                SensorId.From("volume", idIndex, "data", "total"),
                $"{volume.Name} Total",
                SensorKind.Data,
                volume.TotalBytes,
                sourceId: Id));
            readings.Add(SensorReading.Numeric(
                SensorId.From("volume", idIndex, "load", "used"),
                $"{volume.Name} Usage",
                SensorKind.Load,
                load,
                0d,
                100d,
                Id));
            index++;
        }
    }

    private static void ReadHost(List<SensorReading> readings)
    {
        var host = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(host))
        {
            readings.Add(SensorReading.Textual(
                SensorId.From("system", "0", "name", "host"),
                "Host Name",
                host,
                "linux-system"));
        }
    }

    /// <summary>
    /// Computes a byte-or-count rate from two counter samples. A decrease is
    /// treated as a reset or 32-bit wrap and yields <see langword="null"/> so
    /// the caller can skip the sample rather than invent a multi-gigabyte spike.
    /// </summary>
    internal static double? RateFromCounter(ulong previous, ulong current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
        {
            return null;
        }

        if (current < previous)
        {
            return null;
        }

        return (current - previous) / elapsedSeconds;
    }

    internal static double? ComputeCpuLoad(CpuSample previous, CpuSample current)
    {
        var totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
        {
            return 0d;
        }

        var idleDelta = current.Idle - previous.Idle;
        var busy = totalDelta - idleDelta;
        if (busy < 0)
        {
            return 0d;
        }

        return Math.Clamp(busy * 100d / totalDelta, 0d, 100d);
    }

    internal static bool IsVirtualInterface(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        foreach (var prefix in VirtualInterfacePrefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseCpuSample(string[] parts, out CpuSample sample)
    {
        sample = default;
        // user nice system idle [iowait irq softirq steal ...]
        Span<ulong> fields = stackalloc ulong[Math.Min(parts.Length - 1, 10)];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!ulong.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fields[i]))
            {
                return false;
            }
        }

        ulong total = 0;
        foreach (var field in fields)
        {
            total += field;
        }

        var idle = fields.Length > 3 ? fields[3] : 0UL;
        if (fields.Length > 4)
        {
            idle += fields[4]; // iowait counts as idle for utilisation
        }

        sample = new CpuSample(total, idle);
        return true;
    }

    /// <summary>One <c>/proc/stat</c> cpu line, reduced to the fields load needs.</summary>
    internal readonly record struct CpuSample(ulong Total, ulong Idle);
}
