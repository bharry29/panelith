using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Panelith.Sensors;

namespace Panelith.Monitoring.Providers;

/// <summary>
/// Disk read and write rates and activity for all physical disks, from the
/// Windows performance counters.
/// </summary>
/// <remarks>
/// LibreHardwareMonitor only reports drive throughput with administrator
/// rights; the performance counters work for every user.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDiskCounters : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint StatusValid = 0;
    private const uint StatusNewData = 1;

    private readonly nint _query;
    private readonly nint _read;
    private readonly nint _write;
    private readonly nint _idle;
    private bool _disposed;

    private WindowsDiskCounters(nint query, nint read, nint write, nint idle)
    {
        _query = query;
        _read = read;
        _write = write;
        _idle = idle;
    }

    /// <summary>Opens the counters, or returns null when they are unavailable.</summary>
    public static WindowsDiskCounters? TryOpen()
    {
        try
        {
            if (PdhOpenQuery(null, 0, out var query) != 0)
            {
                return null;
            }

            if (PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", 0, out var read) != 0
                || PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", 0, out var write) != 0
                || PdhAddEnglishCounter(query, @"\PhysicalDisk(_Total)\% Idle Time", 0, out var idle) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }

            PdhCollectQueryData(query);
            return new WindowsDiskCounters(query, read, write, idle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Samples the counters and appends readings.</summary>
    public void AddReadings(List<SensorReading> readings, string sourceId)
    {
        if (_disposed || PdhCollectQueryData(_query) != 0)
        {
            return;
        }

        var read = Read(_read);
        var write = Read(_write);
        var idle = Read(_idle);
        readings.Add(SensorReading.Numeric(SensorId.From("disk", "all", "throughput", "read"), "Disk Read", SensorKind.Throughput, read, 0d, null, sourceId));
        readings.Add(SensorReading.Numeric(SensorId.From("disk", "all", "throughput", "write"), "Disk Write", SensorKind.Throughput, write, 0d, null, sourceId));
        double? activity = idle is { } i ? Math.Clamp(100d - i, 0d, 100d) : null;
        readings.Add(SensorReading.Numeric(SensorId.From("disk", "all", "load", "activity"), "Disk Activity", SensorKind.Load, activity, 0d, 100d, sourceId));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            PdhCloseQuery(_query);
        }
    }

    private static double? Read(nint counter) =>
        PdhGetFormattedCounterValue(counter, FormatDouble, out _, out var value) == 0
        && value.Status is StatusValid or StatusNewData
            ? Math.Max(0d, value.Value)
            : null;

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)]
        public uint Status;

        [FieldOffset(8)]
        public double Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PdhCloseQuery(nint query);
}
