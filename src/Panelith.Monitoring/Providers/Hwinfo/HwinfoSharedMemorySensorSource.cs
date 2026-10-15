using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using Panelith.Sensors;

namespace Panelith.Monitoring.Providers.Hwinfo;

/// <summary>
/// Reads HWiNFO's <c>Global\HWiNFO_SENS_SM2</c> shared-memory feed. Many
/// enthusiasts already run HWiNFO and it exposes more sensors than LHM, so this
/// is a high-value Windows backend. Reports <see cref="ISensorSource.IsAvailable"/>
/// as false — with an actionable message — when the mapping is missing or shared
/// memory support is unchecked in HWiNFO's settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HwinfoSharedMemorySensorSource : ISensorSource
{
    private string? _unavailableReason;
    private bool _probed;

    /// <summary>
    /// Why the source is unavailable, once probed. Null while still unknown or
    /// when the mapping is healthy.
    /// </summary>
    public string? UnavailableReason => _unavailableReason;

    /// <inheritdoc />
    public string Id => "hwinfo";

    /// <inheritdoc />
    public string DisplayName => "HWiNFO Shared Memory";

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                _unavailableReason = "HWiNFO shared memory is only available on Windows.";
                return false;
            }

            if (!_probed)
            {
                _ = TryOpenMapping(out _);
            }

            return _unavailableReason is null && _probed;
        }
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _unavailableReason = "HWiNFO shared memory is only available on Windows.";
            _probed = true;
            return ValueTask.CompletedTask;
        }

        _ = TryOpenMapping(out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        if (!TryOpenMapping(out var buffer) || buffer is null)
        {
            return ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        }

        var readings = HwinfoSharedMemoryParser.Parse(buffer, Id);
        return ValueTask.FromResult(readings);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The largest shared-memory block copied.</summary>
    internal const long MaxMappingBytes = 64L * 1024 * 1024;

    private bool TryOpenMapping(out byte[]? buffer)
    {
        buffer = null;
        _probed = true;

        if (!OperatingSystem.IsWindows())
        {
            _unavailableReason = "HWiNFO shared memory is only available on Windows.";
            return false;
        }

        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(
                HwinfoSharedMemoryFormat.MappingName,
                MemoryMappedFileRights.Read);
            using var accessor = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (accessor.Capacity > MaxMappingBytes)
            {
                // Real HWiNFO mappings are a few megabytes; refuse to copy anything
                // that only another program squatting on the name could produce.
                _unavailableReason = $"The HWiNFO shared memory is larger than {MaxMappingBytes / (1024 * 1024)} MB, so it was not read.";
                return false;
            }

            var length = (int)accessor.Capacity;
            if (length < HwinfoSharedMemoryFormat.HeaderSize)
            {
                _unavailableReason = HwinfoSharedMemoryParser.DescribeUnavailability([]);
                return false;
            }

            buffer = new byte[length];
            accessor.ReadArray(0, buffer, 0, length);

            if (buffer.Length >= 4)
            {
                var signature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                if (signature != HwinfoSharedMemoryFormat.LiveSignature)
                {
                    _unavailableReason = HwinfoSharedMemoryParser.DescribeUnavailability(buffer);
                    buffer = null;
                    return false;
                }
            }

            _unavailableReason = null;
            return true;
        }
        catch (FileNotFoundException)
        {
            _unavailableReason =
                "HWiNFO shared memory mapping was not found. Start HWiNFO and enable "
                + "\"Shared Memory Support\" under Settings → User Interface "
                + "(the checkbox is off by default).";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _unavailableReason =
                "HWiNFO shared memory exists but could not be opened. Try running "
                + "Panelith with the same elevation as HWiNFO.";
            return false;
        }
        catch (IOException ex)
        {
            _unavailableReason = "HWiNFO shared memory could not be read: " + ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Cross-platform façade that constructs <see cref="HwinfoSharedMemorySensorSource"/>
/// only on Windows, and otherwise reports unavailable without tripping CA1416.
/// </summary>
public sealed class HwinfoSensorSource : ISensorSource
{
    private readonly ISensorSource _inner;

    /// <summary>Creates the Windows implementation when running on Windows.</summary>
    public HwinfoSensorSource()
    {
        if (OperatingSystem.IsWindows())
        {
            _inner = new HwinfoSharedMemorySensorSource();
        }
        else
        {
            _inner = new UnavailableSource(
                "hwinfo",
                "HWiNFO Shared Memory",
                "HWiNFO shared memory is only available on Windows.");
        }
    }

    /// <summary>
    /// Why the source is unavailable, when known. Surfaced so the UI can show
    /// the "enable Shared Memory Support" hint.
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            if (_inner is UnavailableSource stub)
            {
                return stub.Reason;
            }

            if (OperatingSystem.IsWindows() && _inner is HwinfoSharedMemorySensorSource win)
            {
                return win.UnavailableReason;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public string Id => _inner.Id;

    /// <inheritdoc />
    public string DisplayName => _inner.DisplayName;

    /// <inheritdoc />
    public bool IsAvailable => _inner.IsAvailable;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class UnavailableSource(string id, string displayName, string reason) : ISensorSource
    {
        public string Reason { get; } = reason;
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public bool IsAvailable => false;
        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SensorReading>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
