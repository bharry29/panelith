using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Providers.Hwinfo;

/// <summary>
/// Pure parser for an HWiNFO shared-memory snapshot. Kept free of Win32 so the
/// layout is unit-tested on every OS against synthetic byte buffers.
/// </summary>
public static class HwinfoSharedMemoryParser
{
    /// <summary>
    /// Parses <paramref name="buffer"/> into sensor readings. Returns an empty
    /// list when the signature is missing, dead, or the offsets do not fit.
    /// </summary>
    public static IReadOnlyList<SensorReading> Parse(ReadOnlySpan<byte> buffer, string sourceId = "hwinfo")
    {
        if (buffer.Length < HwinfoSharedMemoryFormat.HeaderSize)
        {
            return [];
        }

        var header = MemoryMarshal.Read<HwinfoSharedMemoryFormat.Header>(buffer);
        if (header.Signature != HwinfoSharedMemoryFormat.LiveSignature)
        {
            return [];
        }

        if (header.SizeOfSensorElement < 8 || header.SizeOfReadingElement < 0x124)
        {
            return [];
        }

        if (!Fits(buffer.Length, header.OffsetOfSensorSection, header.SizeOfSensorElement, header.NumSensorElements)
            || !Fits(buffer.Length, header.OffsetOfReadingSection, header.SizeOfReadingElement, header.NumReadingElements))
        {
            return [];
        }

        var sensors = new (uint Id, uint Instance, string Name)[header.NumSensorElements];
        for (var i = 0; i < header.NumSensorElements; i++)
        {
            var offset = (int)(header.OffsetOfSensorSection + (i * header.SizeOfSensorElement));
            var slice = buffer.Slice(offset, (int)header.SizeOfSensorElement);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(slice);
            var instance = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..]);
            var name = ReadFixedString(slice[8..], 128);
            if (string.IsNullOrWhiteSpace(name) && slice.Length >= 136 + 128)
            {
                name = ReadFixedString(slice[136..], 128);
            }

            sensors[i] = (id, instance, string.IsNullOrWhiteSpace(name) ? $"sensor{i}" : name);
        }

        var deduplicator = new SensorIdDeduplicator();
        var readings = new List<SensorReading>((int)header.NumReadingElements);

        for (var i = 0; i < header.NumReadingElements; i++)
        {
            var offset = (int)(header.OffsetOfReadingSection + (i * header.SizeOfReadingElement));
            var slice = buffer.Slice(offset, (int)header.SizeOfReadingElement);
            var type = (HwinfoSharedMemoryFormat.ReadingType)BinaryPrimitives.ReadUInt32LittleEndian(slice);
            var sensorIndex = BinaryPrimitives.ReadUInt32LittleEndian(slice[4..]);
            _ = BinaryPrimitives.ReadUInt32LittleEndian(slice[8..]); // reading id

            var label = ReadFixedString(slice[12..], 128);
            if (string.IsNullOrWhiteSpace(label) && slice.Length >= 140 + 128)
            {
                label = ReadFixedString(slice[140..], 128);
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                label = $"reading{i}";
            }

            if (sensorIndex >= sensors.Length)
            {
                continue;
            }

            var kind = MapKind(type);
            if (kind == SensorKind.Unknown)
            {
                continue;
            }

            if (slice.Length < 0x11C + 8)
            {
                continue;
            }

            var value = BinaryPrimitives.ReadDoubleLittleEndian(slice[0x11C..]);
            double? min = null;
            double? max = null;
            if (slice.Length >= 0x124 + 8)
            {
                min = BinaryPrimitives.ReadDoubleLittleEndian(slice[0x124..]);
            }

            if (slice.Length >= 0x12C + 8)
            {
                max = BinaryPrimitives.ReadDoubleLittleEndian(slice[0x12C..]);
            }

            var parent = sensors[sensorIndex];
            var deviceClass = ClassifyDevice(parent.Name);
            var id = deduplicator.Unique(SensorId.From(
                deviceClass,
                parent.Instance.ToString(CultureInfo.InvariantCulture),
                KindSegment(kind),
                label));

            readings.Add(SensorReading.Numeric(
                id,
                $"{parent.Name} {label}",
                kind,
                double.IsNaN(value) ? null : value,
                min is { } mn && !double.IsNaN(mn) ? mn : null,
                max is { } mx && !double.IsNaN(mx) ? mx : null,
                sourceId));
        }

        return readings;
    }

    /// <summary>
    /// Explains why a mapping buffer is unusable, for the actionable
    /// "enable shared memory in HWiNFO settings" hint.
    /// </summary>
    public static string DescribeUnavailability(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return "HWiNFO shared memory is not available. Start HWiNFO and enable "
                   + "\"Shared Memory Support\" under Settings → User Interface.";
        }

        if (buffer.Length < HwinfoSharedMemoryFormat.HeaderSize)
        {
            return "HWiNFO shared memory mapping is truncated.";
        }

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (signature == HwinfoSharedMemoryFormat.DeadSignature)
        {
            return "HWiNFO shared memory is shut down (signature DEAD).";
        }

        if (signature != HwinfoSharedMemoryFormat.LiveSignature)
        {
            return "HWiNFO shared memory signature mismatch; is Shared Memory Support enabled?";
        }

        return "HWiNFO shared memory header is invalid.";
    }

    private static bool Fits(int length, uint offset, uint elementSize, uint count)
    {
        if (count == 0)
        {
            return true;
        }

        if (elementSize == 0 || count > 100_000)
        {
            return false;
        }

        try
        {
            var end = checked((long)offset + ((long)elementSize * count));
            return end <= length;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string ReadFixedString(ReadOnlySpan<byte> data, int maxChars)
    {
        var length = Math.Min(maxChars, data.Length);
        var span = data[..length];
        var nul = span.IndexOf((byte)0);
        if (nul >= 0)
        {
            span = span[..nul];
        }

        return Encoding.UTF8.GetString(span).Trim();
    }

    private static SensorKind MapKind(HwinfoSharedMemoryFormat.ReadingType type) => type switch
    {
        HwinfoSharedMemoryFormat.ReadingType.Temperature => SensorKind.Temperature,
        HwinfoSharedMemoryFormat.ReadingType.Voltage => SensorKind.Voltage,
        HwinfoSharedMemoryFormat.ReadingType.Fan => SensorKind.Fan,
        HwinfoSharedMemoryFormat.ReadingType.Current => SensorKind.Current,
        HwinfoSharedMemoryFormat.ReadingType.Power => SensorKind.Power,
        HwinfoSharedMemoryFormat.ReadingType.Clock => SensorKind.Clock,
        HwinfoSharedMemoryFormat.ReadingType.Usage => SensorKind.Load,
        _ => SensorKind.Unknown,
    };

    private static string KindSegment(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "temperature",
        SensorKind.Voltage => "voltage",
        SensorKind.Fan => "fan",
        SensorKind.Current => "current",
        SensorKind.Power => "power",
        SensorKind.Clock => "clock",
        SensorKind.Load => "load",
        _ => "other",
    };

    private static string ClassifyDevice(string sensorName)
    {
        var name = sensorName.ToLowerInvariant();
        if (name.Contains("cpu", StringComparison.Ordinal) || name.Contains("ryzen", StringComparison.Ordinal)
            || name.Contains("core", StringComparison.Ordinal) && name.Contains("intel", StringComparison.Ordinal))
        {
            return "cpu";
        }

        if (name.Contains("gpu", StringComparison.Ordinal) || name.Contains("geforce", StringComparison.Ordinal)
            || name.Contains("radeon", StringComparison.Ordinal) || name.Contains("nvidia", StringComparison.Ordinal))
        {
            return "gpu";
        }

        if (name.Contains("memory", StringComparison.Ordinal) || name.Contains("dimm", StringComparison.Ordinal))
        {
            return "memory";
        }

        if (name.Contains("drive", StringComparison.Ordinal) || name.Contains("nvme", StringComparison.Ordinal)
            || name.Contains("ssd", StringComparison.Ordinal) || name.Contains("hdd", StringComparison.Ordinal))
        {
            return "disk";
        }

        if (name.Contains("network", StringComparison.Ordinal) || name.Contains("nic", StringComparison.Ordinal))
        {
            return "network";
        }

        return "hwinfo";
    }
}
