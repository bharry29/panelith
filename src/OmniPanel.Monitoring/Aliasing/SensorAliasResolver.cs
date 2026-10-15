using System.Collections.Frozen;
using OmniPanel.Monitoring.Providers;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Aliasing;

/// <summary>
/// Declarative mapping from raw backend sensors onto
/// <see cref="WellKnownSensors"/> aliases. Priority rules make
/// <c>@cpu.temp</c> prefer package/Tctl over per-core maxima over ACPI, and
/// resolution is deterministic so a theme cannot silently repoint after reboot.
/// </summary>
public sealed class SensorAliasResolver
{
    private readonly Dictionary<string, SensorId> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    private FrozenDictionary<string, SensorId> _lastResolved =
        FrozenDictionary<string, SensorId>.Empty;

    /// <summary>
    /// User-supplied overrides. An override always wins over the built-in
    /// priority rules so an odd motherboard can repoint <c>@cpu.temp</c>.
    /// </summary>
    public IReadOnlyDictionary<string, SensorId> Overrides => _overrides;

    /// <summary>The mapping produced by the most recent <see cref="Resolve"/>.</summary>
    public IReadOnlyDictionary<string, SensorId> LastResolved => _lastResolved;

    /// <summary>Pins <paramref name="alias"/> to a concrete sensor id.</summary>
    public void SetOverride(string alias, SensorId target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (!WellKnownSensors.IsAlias(alias))
        {
            throw new ArgumentException("Overrides must use an @alias name.", nameof(alias));
        }

        if (target.IsEmpty)
        {
            throw new ArgumentException("Override target must be a non-empty sensor id.", nameof(target));
        }

        _overrides[alias] = target;
    }

    /// <summary>Removes a previously set override.</summary>
    public bool ClearOverride(string alias) => _overrides.Remove(alias);

    /// <summary>
    /// Resolves every well-known alias against <paramref name="readings"/>.
    /// Missing aliases are simply omitted from the result.
    /// </summary>
    public IReadOnlyDictionary<string, SensorId> Resolve(IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var list = readings as IList<SensorReading> ?? readings.ToList();
        var map = new Dictionary<string, SensorId>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in WellKnownSensors.All.Keys.OrderBy(static a => a, StringComparer.Ordinal))
        {
            if (_overrides.TryGetValue(alias, out var forced))
            {
                // Overrides win even when the target is currently absent — the
                // binding context then returns Missing, which is what the user
                // asked for by pinning that id.
                map[alias] = forced;
                continue;
            }

            var match = FindBest(alias, list);
            if (match is { } id)
            {
                map[alias] = id;
            }
        }

        _lastResolved = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        return _lastResolved;
    }

    /// <summary>Looks up a single alias against the last resolved map, or resolves fresh.</summary>
    public bool TryResolve(string alias, IEnumerable<SensorReading> readings, out SensorId id)
    {
        if (_overrides.TryGetValue(alias, out id))
        {
            return true;
        }

        if (_lastResolved.TryGetValue(alias, out id))
        {
            return true;
        }

        var resolved = Resolve(readings);
        return resolved.TryGetValue(alias, out id);
    }

    private static SensorId? FindBest(string alias, IList<SensorReading> readings)
    {
        SensorId? best = null;
        var bestScore = int.MinValue;

        foreach (var reading in readings)
        {
            var score = Score(alias, reading);
            if (score < 0)
            {
                continue;
            }

            if (score > bestScore
                || (score == bestScore && best is { } current && reading.Id.CompareTo(current) < 0))
            {
                // Tie-break on ordinal id so the same machine always picks the
                // same sensor across restarts when scores are equal.
                bestScore = score;
                best = reading.Id;
            }
        }

        return best;
    }

    private static int Score(string alias, SensorReading reading)
    {
        var id = reading.Id.Value;
        var name = reading.DisplayName;
        var kind = reading.Kind;

        // Synthetic source: exact path match under synthetic/.
        var syntheticId = SyntheticSensorSource.IdForAlias(alias);
        if (id.Equals(syntheticId.Value, StringComparison.Ordinal))
        {
            return 10_000;
        }

        return alias switch
        {
            WellKnownSensors.CpuTemperature => ScoreCpuTemp(id, name, kind),
            WellKnownSensors.CpuLoad => ScoreCpuLoad(id, name, kind),
            WellKnownSensors.CpuClock => ScoreSimple(id, name, kind, SensorKind.Clock, "cpu", ["clock", "frequency", "mhz"], exclude: ["bus", "uncore"]),
            WellKnownSensors.CpuPower => ScoreSimple(id, name, kind, SensorKind.Power, "cpu", ["package", "ppt", "power"], exclude: []),
            WellKnownSensors.CpuFan => ScoreSimple(id, name, kind, SensorKind.Fan, "cpu", ["cpu", "processor"], exclude: ["gpu"]),
            WellKnownSensors.CpuName => kind == SensorKind.Text && id.StartsWith("cpu/", StringComparison.Ordinal) ? 100 : -1,
            WellKnownSensors.GpuTemperature => ScoreGpu(id, name, kind, SensorKind.Temperature, ["temperature", "temp", "edge", "hotspot", "junction"]),
            WellKnownSensors.GpuLoad => ScoreGpu(id, name, kind, SensorKind.Load, ["core", "gpu", "load", "util", "graphics"]),
            WellKnownSensors.GpuClock => ScoreGpu(id, name, kind, SensorKind.Clock, ["core", "clock", "graphics"]),
            WellKnownSensors.GpuPower => ScoreGpu(id, name, kind, SensorKind.Power, ["power", "board", "package"]),
            WellKnownSensors.GpuFan => ScoreGpu(id, name, kind, SensorKind.Fan, ["fan"]),
            WellKnownSensors.GpuMemoryUsed => ScoreGpu(id, name, kind, SensorKind.Data, ["vram", "memory", "used", "dedicated"]),
            WellKnownSensors.GpuMemoryTotal => ScoreGpu(id, name, kind, SensorKind.Data, ["vram", "memory", "total"]),
            WellKnownSensors.GpuMemoryLoad => ScoreGpu(id, name, kind, SensorKind.Load, ["vram", "memory", "load", "util"]),
            WellKnownSensors.GpuName => kind == SensorKind.Text && id.StartsWith("gpu/", StringComparison.Ordinal) ? ScoreGpuName(id, name) : -1,
            WellKnownSensors.MemoryUsed => ScoreMem(id, name, kind, SensorKind.Data, ["used"], reject: ["swap", "vram"]),
            WellKnownSensors.MemoryTotal => ScoreMem(id, name, kind, SensorKind.Data, ["total"], reject: ["swap", "vram"]),
            WellKnownSensors.MemoryLoad => ScoreMem(id, name, kind, SensorKind.Load, ["load", "used"], reject: ["swap"]),
            WellKnownSensors.NetworkDown => ScoreNet(id, name, kind, down: true),
            WellKnownSensors.NetworkUp => ScoreNet(id, name, kind, down: false),
            WellKnownSensors.DiskLoad => kind == SensorKind.Load && id.Contains("disk", StringComparison.Ordinal)
                ? (id.Contains("/0/", StringComparison.Ordinal) ? 80 : 40)
                : -1,
            WellKnownSensors.DiskTemperature => kind == SensorKind.Temperature && (id.StartsWith("disk/", StringComparison.Ordinal) || ContainsAny(name, ["drive", "nvme", "ssd", "hdd"]))
                ? 50 + (ContainsAny(name, ["composite", "sensor 1"]) ? 20 : 0)
                : -1,
            WellKnownSensors.Uptime => kind == SensorKind.Duration && (id.Contains("uptime", StringComparison.Ordinal) || ContainsAny(name, ["uptime"]))
                ? 100
                : -1,
            WellKnownSensors.HostName => kind == SensorKind.Text && (id.Contains("host", StringComparison.Ordinal) || ContainsAny(name, ["host"]))
                ? 100
                : -1,
            WellKnownSensors.PanelFps => kind == SensorKind.FrameRate && id.StartsWith("panel/", StringComparison.Ordinal)
                ? 100
                : -1,
            _ => -1,
        };
    }

    private static int ScoreCpuTemp(string id, string name, SensorKind kind)
    {
        if (kind != SensorKind.Temperature)
        {
            return -1;
        }

        var inCpu = id.StartsWith("cpu/", StringComparison.Ordinal)
                    || ContainsAny(name, ["cpu", "ryzen", "tctl", "tdie", "package", "coretemp", "k10temp"]);
        var isAcpi = id.StartsWith("acpi/", StringComparison.Ordinal) || ContainsAny(name, ["acpitz", "acpi"]);
        if (!inCpu && !isAcpi)
        {
            return -1;
        }

        if (ContainsAny(id, ["package", "tctl", "tdie"]) || ContainsAny(name, ["package", "tctl", "tdie", "tctl/tdie"]))
        {
            return 300;
        }

        if (ContainsAny(name, ["cpu", "processor"]) && !ContainsAny(name, ["core"]))
        {
            return 250;
        }

        if (inCpu && ContainsAny(name, ["core", "ccd"]))
        {
            return 150;
        }

        if (inCpu)
        {
            return 100;
        }

        // acpitz is a last resort — often ambient or chipset, not the package.
        return isAcpi ? 10 : -1;
    }

    private static int ScoreCpuLoad(string id, string name, SensorKind kind)
    {
        if (kind != SensorKind.Load || !id.StartsWith("cpu/", StringComparison.Ordinal))
        {
            return -1;
        }

        if (id.Contains("/total", StringComparison.Ordinal) || ContainsAny(name, ["total", "cpu load"]))
        {
            return 100;
        }

        if (id.Contains("/core/", StringComparison.Ordinal))
        {
            return 20;
        }

        return 50;
    }

    private static int ScoreGpu(string id, string name, SensorKind kind, SensorKind expected, string[] keywords)
    {
        if (kind != expected)
        {
            return -1;
        }

        var isGpu = id.StartsWith("gpu/", StringComparison.Ordinal)
                    || ContainsAny(name, ["gpu", "geforce", "radeon", "nvidia", "amdgpu", "graphics"]);
        if (!isGpu)
        {
            return -1;
        }

        var score = 50;
        if (ContainsAny(id, keywords) || ContainsAny(name, keywords))
        {
            score += 40;
        }

        // Discrete GPUs outrank integrated ones for the default aliases.
        if (ContainsAny(name, ["nvidia", "geforce", "radeon", "amdgpu", "rtx", "gtx", "rx "])
            || id.Contains("nvidia", StringComparison.Ordinal) || id.Contains("amd", StringComparison.Ordinal))
        {
            score += 100;
        }

        if (ContainsAny(name, ["intel", "uhd", "iris", "arc"]) && !ContainsAny(name, ["nvidia", "radeon"]))
        {
            score -= 50;
        }

        return score;
    }

    private static int ScoreGpuName(string id, string name)
    {
        var score = 50;
        if (ContainsAny(name, ["nvidia", "geforce", "radeon", "rtx", "gtx"]))
        {
            score += 100;
        }

        if (ContainsAny(name, ["intel", "uhd", "iris"]) && !ContainsAny(name, ["nvidia", "radeon"]))
        {
            score -= 40;
        }

        _ = id;
        return score;
    }

    private static int ScoreMem(string id, string name, SensorKind kind, SensorKind expected, string[] keywords, string[] reject)
    {
        if (kind != expected)
        {
            return -1;
        }

        if (!(id.StartsWith("memory/", StringComparison.Ordinal) || ContainsAny(name, ["memory", "ram"])))
        {
            return -1;
        }

        if (ContainsAny(id, reject) || ContainsAny(name, reject))
        {
            return -1;
        }

        return ContainsAny(id, keywords) || ContainsAny(name, keywords) ? 100 : 40;
    }

    private static int ScoreNet(string id, string name, SensorKind kind, bool down)
    {
        if (kind != SensorKind.Throughput)
        {
            return -1;
        }

        if (!id.StartsWith("network/", StringComparison.Ordinal) && !ContainsAny(name, ["network", "download", "upload", "nic"]))
        {
            return -1;
        }

        var direction = down
            ? (id.Contains("down", StringComparison.Ordinal) || ContainsAny(name, ["down", "download", "receive", "rx"]))
            : (id.Contains("up", StringComparison.Ordinal) || ContainsAny(name, ["up", "upload", "transmit", "tx"]));
        if (!direction)
        {
            return -1;
        }

        // Aggregate row wins over a specific interface.
        if (id.StartsWith("network/0/", StringComparison.Ordinal))
        {
            return 200;
        }

        // Virtual interfaces are excluded by the Linux source already, but
        // defend against other backends that do not filter.
        if (IsVirtualInterfaceId(id, name))
        {
            return -1;
        }

        if (ContainsAny(id, ["/eth", "/en", "/wl", "/wlan", "/wifi"]) || ContainsAny(name, ["ethernet", "wi-fi", "wifi", "wlan"]))
        {
            return 120;
        }

        return 60;
    }

    private static int ScoreSimple(
        string id,
        string name,
        SensorKind kind,
        SensorKind expected,
        string devicePrefix,
        string[] keywords,
        string[] exclude)
    {
        if (kind != expected)
        {
            return -1;
        }

        if (!id.StartsWith(devicePrefix + "/", StringComparison.Ordinal)
            && !ContainsAny(name, [devicePrefix]))
        {
            return -1;
        }

        if (ContainsAny(id, exclude) || ContainsAny(name, exclude))
        {
            return -1;
        }

        return ContainsAny(id, keywords) || ContainsAny(name, keywords) ? 100 : 40;
    }

    private static bool IsVirtualInterfaceId(string id, string name)
    {
        foreach (var token in new[] { "/lo", "docker", "veth", "br-", "virbr", "/tun", "/tap", "/wg", "vnet" })
        {
            if (id.Contains(token, StringComparison.OrdinalIgnoreCase)
                || name.Contains(token.Trim('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
