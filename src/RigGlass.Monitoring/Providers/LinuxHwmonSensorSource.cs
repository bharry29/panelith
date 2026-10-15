using System.Globalization;
using RigGlass.Monitoring.IO;
using RigGlass.Sensors;

namespace RigGlass.Monitoring.Providers;

/// <summary>
/// Walks <c>/sys/class/hwmon</c> and projects each channel onto a stable
/// <see cref="SensorId"/>. All I/O goes through <see cref="IFileSystem"/> so
/// tests can feed a fabricated tree.
/// </summary>
public sealed class LinuxHwmonSensorSource : ISensorSource
{
    private static readonly FrozenDriverMap Drivers = FrozenDriverMap.Create();

    private readonly IFileSystem _fs;
    private readonly string _hwmonRoot;
    private readonly Dictionary<string, int> _instanceCounters = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a source rooted at the standard hwmon path.
    /// </summary>
    public LinuxHwmonSensorSource(IFileSystem? fileSystem = null)
        : this(fileSystem ?? RealFileSystem.Instance, "/sys/class/hwmon")
    {
    }

    /// <summary>
    /// Creates a source rooted at an arbitrary hwmon directory — used by tests
    /// that build a miniature <c>/sys</c> tree.
    /// </summary>
    public LinuxHwmonSensorSource(IFileSystem fileSystem, string hwmonRoot)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _hwmonRoot = hwmonRoot ?? throw new ArgumentNullException(nameof(hwmonRoot));
    }

    /// <inheritdoc />
    public string Id => "linux-hwmon";

    /// <inheritdoc />
    public string DisplayName => "Linux hwmon";

    /// <inheritdoc />
    /// <remarks>
    /// Availability is the presence of the hwmon root, not the host OS: production
    /// code only registers this source on Linux, while tests feed a fabricated
    /// tree on any platform.
    /// </remarks>
    public bool IsAvailable => _fs.DirectoryExists(_hwmonRoot);

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

        _instanceCounters.Clear();
        var deduplicator = new SensorIdDeduplicator();
        var readings = new List<SensorReading>();

        foreach (var deviceDir in _fs.EnumerateDirectories(_hwmonRoot).OrderBy(static p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ReadTrimmed(Path.Combine(deviceDir, "name")) ?? Path.GetFileName(deviceDir);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var driver = Drivers.Classify(name);
            var instance = NextInstance(driver.DeviceClass, name);
            ReadDevice(deviceDir, driver, instance, name, deduplicator, readings);
        }

        return ValueTask.FromResult<IReadOnlyList<SensorReading>>(readings);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ReadDevice(
        string deviceDir,
        DriverIdentity driver,
        int instance,
        string chipName,
        SensorIdDeduplicator deduplicator,
        List<SensorReading> readings)
    {
        foreach (var file in _fs.EnumerateFiles(deviceDir))
        {
            var fileName = Path.GetFileName(file);
            if (!TryParseChannel(fileName, out var channelKind, out var index, out var suffix))
            {
                continue;
            }

            // Only *_input files are the live values; labels and limits ride along.
            if (!suffix.Equals("input", StringComparison.Ordinal))
            {
                continue;
            }

            var raw = ReadDouble(file);
            if (raw is null)
            {
                continue;
            }

            var (kind, value, scaleOk) = ScaleInput(channelKind, raw.Value);
            if (!scaleOk)
            {
                continue;
            }

            var label = ReadTrimmed(Path.Combine(deviceDir, $"{channelKind}{index}_label"))
                        ?? $"{channelKind}{index}";
            var min = ReadScaledLimit(deviceDir, channelKind, index, "min", channelKind);
            var max = ReadScaledLimit(deviceDir, channelKind, index, "max", channelKind)
                      ?? ReadScaledLimit(deviceDir, channelKind, index, "crit", channelKind);

            var id = deduplicator.Unique(SensorId.From(
                driver.DeviceClass,
                instance.ToString(CultureInfo.InvariantCulture),
                KindSegment(kind),
                label));

            readings.Add(SensorReading.Numeric(
                id,
                $"{chipName} {label}",
                kind,
                value,
                min,
                max,
                Id));
        }
    }

    private double? ReadScaledLimit(string deviceDir, string channelKind, int index, string suffix, string forKind)
    {
        var raw = ReadDouble(Path.Combine(deviceDir, $"{channelKind}{index}_{suffix}"));
        if (raw is null)
        {
            return null;
        }

        var (kind, value, ok) = ScaleInput(forKind, raw.Value);
        _ = kind;
        return ok ? value : null;
    }

    private int NextInstance(string deviceClass, string chipName)
    {
        // Instance indexes are per device class so cpu/0 stays the first CPU
        // sensor chip even when an NVMe device sits at hwmon3.
        var key = deviceClass + "\0" + chipName;
        if (_instanceCounters.TryGetValue(deviceClass, out var next))
        {
            // Prefer a stable index per chip name within the class: first time
            // we see "coretemp" it is cpu/0, second distinct chip of class cpu
            // becomes cpu/1.
            _ = key;
            _instanceCounters[deviceClass] = next + 1;
            return next;
        }

        _instanceCounters[deviceClass] = 1;
        return 0;
    }

    private string? ReadTrimmed(string path)
    {
        var text = _fs.ReadAllText(path);
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private double? ReadDouble(string path)
    {
        var text = ReadTrimmed(path);
        if (text is null)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // Kernel drivers use -1 as "not present" for some optional channels.
        if (value < 0d && value > -2d)
        {
            return null;
        }

        return value;
    }

    private static (SensorKind Kind, double Value, bool Ok) ScaleInput(string channelKind, double raw) =>
        channelKind switch
        {
            "temp" => (SensorKind.Temperature, raw / 1000d, true),
            "fan" => (SensorKind.Fan, raw, true),
            "in" => (SensorKind.Voltage, raw / 1000d, true),
            "power" => (SensorKind.Power, raw / 1_000_000d, true),
            "curr" => (SensorKind.Current, raw / 1000d, true),
            "energy" => (SensorKind.Data, raw, true),
            "pwm" => (SensorKind.Control, raw / 255d * 100d, true),
            "freq" => (SensorKind.Clock, raw / 1_000_000d, true),
            _ => (SensorKind.Unknown, raw, false),
        };

    private static string KindSegment(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "temperature",
        SensorKind.Fan => "fan",
        SensorKind.Voltage => "voltage",
        SensorKind.Power => "power",
        SensorKind.Current => "current",
        SensorKind.Control => "control",
        SensorKind.Clock => "clock",
        SensorKind.Data => "data",
        _ => "unknown",
    };

    private static bool TryParseChannel(string fileName, out string channelKind, out int index, out string suffix)
    {
        channelKind = string.Empty;
        index = 0;
        suffix = string.Empty;

        // Forms: temp1_input, fan2_label, in0_max, power1_crit, curr1_input.
        var underscore = fileName.IndexOf('_');
        if (underscore <= 0 || underscore == fileName.Length - 1)
        {
            return false;
        }

        suffix = fileName[(underscore + 1)..];
        var head = fileName[..underscore];
        var digitStart = head.Length;
        while (digitStart > 0 && char.IsAsciiDigit(head[digitStart - 1]))
        {
            digitStart--;
        }

        if (digitStart == 0 || digitStart == head.Length)
        {
            return false;
        }

        channelKind = head[..digitStart];
        if (!int.TryParse(head.AsSpan(digitStart), NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        return channelKind is "temp" or "fan" or "in" or "power" or "curr" or "energy" or "pwm" or "freq";
    }

    private readonly struct DriverIdentity(string deviceClass, string displayFamily)
    {
        public string DeviceClass { get; } = deviceClass;
        public string DisplayFamily { get; } = displayFamily;
    }

    private sealed class FrozenDriverMap
    {
        private readonly Dictionary<string, DriverIdentity> _exact;

        private FrozenDriverMap(Dictionary<string, DriverIdentity> exact) => _exact = exact;

        public static FrozenDriverMap Create()
        {
            var map = new Dictionary<string, DriverIdentity>(StringComparer.OrdinalIgnoreCase)
            {
                ["coretemp"] = new("cpu", "Intel CPU"),
                ["k10temp"] = new("cpu", "AMD CPU"),
                ["zenpower"] = new("cpu", "AMD CPU"),
                ["amdgpu"] = new("gpu", "AMD GPU"),
                ["nouveau"] = new("gpu", "NVIDIA GPU"),
                ["nvidia"] = new("gpu", "NVIDIA GPU"),
                ["nvme"] = new("disk", "NVMe"),
                ["drivetemp"] = new("disk", "Drive"),
                ["acpitz"] = new("acpi", "ACPI thermal"),
                ["asus_wmi_sensors"] = new("motherboard", "ASUS WMI"),
                ["asus_wmi"] = new("motherboard", "ASUS WMI"),
                ["nct6775"] = new("motherboard", "Nuvoton"),
                ["nct6776"] = new("motherboard", "Nuvoton"),
                ["nct6779"] = new("motherboard", "Nuvoton"),
                ["nct6791"] = new("motherboard", "Nuvoton"),
                ["nct6792"] = new("motherboard", "Nuvoton"),
                ["nct6793"] = new("motherboard", "Nuvoton"),
                ["nct6795"] = new("motherboard", "Nuvoton"),
                ["nct6796"] = new("motherboard", "Nuvoton"),
                ["nct6797"] = new("motherboard", "Nuvoton"),
                ["nct6798"] = new("motherboard", "Nuvoton"),
                ["it87"] = new("motherboard", "ITE"),
                ["it8728"] = new("motherboard", "ITE"),
                ["it8686"] = new("motherboard", "ITE"),
                ["it8688"] = new("motherboard", "ITE"),
                ["it8689"] = new("motherboard", "ITE"),
                ["it8792"] = new("motherboard", "ITE"),
                ["gigabyte_wmi"] = new("motherboard", "Gigabyte WMI"),
                ["thinkpad"] = new("laptop", "ThinkPad"),
                ["BAT0"] = new("battery", "Battery"),
                ["BAT1"] = new("battery", "Battery"),
            };
            return new FrozenDriverMap(map);
        }

        public DriverIdentity Classify(string name) =>
            _exact.TryGetValue(name, out var identity)
                ? identity
                : name.StartsWith("nct", StringComparison.OrdinalIgnoreCase)
                    ? new DriverIdentity("motherboard", "Nuvoton")
                    : name.StartsWith("it", StringComparison.OrdinalIgnoreCase)
                        ? new DriverIdentity("motherboard", "ITE")
                        : new DriverIdentity("hwmon", name);
    }
}
