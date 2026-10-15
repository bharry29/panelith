using System.Buffers.Binary;
using System.Text;
using RigGlass.Monitoring.Providers.Hwinfo;
using RigGlass.Sensors;
using Xunit;

namespace RigGlass.Monitoring;

public sealed class HwinfoSharedMemoryParserTests
{
    [Fact]
    public void ParsesSyntheticBuffer()
    {
        const int sensorSize = 264;
        const int readingSize = 316;
        const int sensorCount = 1;
        const int readingCount = 2;
        const int sensorOffset = 48;
        const int readingOffset = sensorOffset + (sensorSize * sensorCount);
        var buffer = new byte[readingOffset + (readingSize * readingCount)];

        WriteHeader(buffer, sensorOffset, sensorSize, sensorCount, readingOffset, readingSize, readingCount);
        WriteSensor(buffer.AsSpan(sensorOffset, sensorSize), id: 1, instance: 0, "CPU [#0]: AMD Ryzen");
        WriteReading(buffer.AsSpan(readingOffset, readingSize), type: 1, sensorIndex: 0, "CPU Package", 62.5);
        WriteReading(buffer.AsSpan(readingOffset + readingSize, readingSize), type: 7, sensorIndex: 0, "CPU Total Load", 33.0);

        var readings = HwinfoSharedMemoryParser.Parse(buffer);
        Assert.Equal(2, readings.Count);
        Assert.Contains(readings, r => r.Kind == SensorKind.Temperature && r.Value == 62.5);
        Assert.Contains(readings, r => r.Kind == SensorKind.Load && r.Value == 33.0);
        Assert.All(readings, r => Assert.StartsWith("cpu/", r.Id.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyOrDeadSignatureYieldsNoReadings()
    {
        Assert.Empty(HwinfoSharedMemoryParser.Parse([]));

        var dead = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(dead, HwinfoSharedMemoryFormat.DeadSignature);
        Assert.Empty(HwinfoSharedMemoryParser.Parse(dead));
        Assert.Contains("Shared Memory", HwinfoSharedMemoryParser.DescribeUnavailability([]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FacadeIsUnavailableOnNonWindowsWithoutThrowing()
    {
        var source = new HwinfoSensorSource();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(source.IsAvailable);
            Assert.NotNull(source.UnavailableReason);
        }
    }

    private static void WriteHeader(
        byte[] buffer,
        int sensorOffset,
        int sensorSize,
        int sensorCount,
        int readingOffset,
        int readingSize,
        int readingCount)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), HwinfoSharedMemoryFormat.LiveSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(12), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x14), (uint)sensorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x18), (uint)sensorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x1C), (uint)sensorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x20), (uint)readingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x24), (uint)readingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x28), (uint)readingCount);
    }

    private static void WriteSensor(Span<byte> slice, uint id, uint instance, string name)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(slice, id);
        BinaryPrimitives.WriteUInt32LittleEndian(slice[4..], instance);
        Encoding.UTF8.GetBytes(name, slice[8..]);
    }

    private static void WriteReading(Span<byte> slice, uint type, uint sensorIndex, string label, double value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(slice, type);
        BinaryPrimitives.WriteUInt32LittleEndian(slice[4..], sensorIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(slice[8..], 1);
        Encoding.UTF8.GetBytes(label, slice[12..]);
        BinaryPrimitives.WriteDoubleLittleEndian(slice[0x11C..], value);
        BinaryPrimitives.WriteDoubleLittleEndian(slice[0x124..], value - 1);
        BinaryPrimitives.WriteDoubleLittleEndian(slice[0x12C..], value + 1);
        BinaryPrimitives.WriteDoubleLittleEndian(slice[0x134..], value);
    }
}
