using System.Buffers;
using Microsoft.Extensions.Logging;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Protocols.Thermalright;
using RigGlass.Devices.Transports;
using RigGlass.Imaging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// Drives the Thermalright panels that present themselves as USB mass storage
/// and carry frames inside vendor SCSI commands.
/// </summary>
/// <remarks>
/// <para>
/// These panels enumerate as a disk, so the operating system binds its storage
/// driver to them before RigGlass sees them. On Linux that driver is detached
/// automatically when permissions allow, which is the single most common reason
/// this family needs the udev rules installed. On Windows the device must be
/// rebound to a WinUSB-class driver first.
/// </para>
/// <para>
/// Frames are written as a sequence of Bulk-Only Transport commands. Each
/// command is a wrapper, then the data, then a status wrapper that must be read
/// back before the next command: skipping the status read leaves the endpoint
/// out of step and every later frame fails.
/// </para>
/// </remarks>
public sealed class ThermalrightScsiDriver : UsbProfileDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "thermalright.scsi";

    private const int BootRetryLimit = 20;

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public ThermalrightScsiDriver(PanelDriverContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Id => DriverId;

    /// <inheritdoc />
    public override string DisplayName => "Thermalright mass-storage panels";

    /// <inheritdoc />
    protected override IUsbDeviceAccess Access => Context.BulkAccess;

    /// <inheritdoc />
    public override async ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var profile = ProfileFor(panel);
        var device = Context.BulkAccess.OpenBulk(ToDeviceInfo(panel), requireInputEndpoint: true);

        try
        {
            await WaitForStartupAsync(profile, cancellationToken).ConfigureAwait(false);

            var descriptor = await IdentifyAsync(device, profile, panel.Descriptor, cancellationToken)
                .ConfigureAwait(false);

            return new ThermalrightScsiConnection(
                device,
                descriptor,
                panel.Location,
                profile,
                options,
                Context.Codec,
                Logger);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private async ValueTask<PanelDescriptor> IdentifyAsync(
        IUsbBulkDevice device,
        PanelProfile profile,
        PanelDescriptor assumed,
        CancellationToken cancellationToken)
    {
        var response = ArrayPool<byte>.Shared.Rent(TrccProtocol.ScsiPollSize);
        try
        {
            for (var attempt = 0; attempt < BootRetryLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = ScsiTransport.Poll(device, response.AsSpan(0, TrccProtocol.ScsiPollSize));
                if (read <= 0)
                {
                    return assumed;
                }

                var reply = response.AsSpan(0, read);
                if (TrccProtocol.IsScsiStillBooting(reply))
                {
                    Logger.LogDebug(
                        "{Panel} is still booting (attempt {Attempt} of {Limit}).",
                        profile.FullName,
                        attempt + 1,
                        BootRetryLimit);

                    await Task.Delay(TimeSpan.FromMilliseconds(500), Context.TimeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var (panelModel, subModel) = TrccScreenTable.ReadBulkIdentity(reply);
                var code = TrccScreenTable.BulkScreenCodeFor(panelModel, subModel);
                if (TrccScreenTable.Resolve(code, panelModel, subModel) is not { } screen
                    || (screen.Width == assumed.Width && screen.Height == assumed.Height))
                {
                    return assumed;
                }

                Logger.LogInformation(
                    "{Panel} reports {Width}x{Height}, overriding the profile's {ProfileWidth}x{ProfileHeight}.",
                    profile.FullName,
                    screen.Width,
                    screen.Height,
                    assumed.Width,
                    assumed.Height);

                return assumed with { Width = screen.Width, Height = screen.Height };
            }

            throw new PanelConnectionException(
                $"{profile.FullName} was still reporting that it is starting up after "
                + $"{BootRetryLimit} attempts.",
                PanelFailureReason.NotReady);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(response);
        }
    }
}

/// <summary>The Bulk-Only Transport exchange, shared by identification and frame writes.</summary>
internal static class ScsiTransport
{
    private static int _tag;

    /// <summary>Runs a data-in command and returns how many bytes arrived.</summary>
    internal static int Poll(IUsbBulkDevice device, Span<byte> destination)
    {
        var tag = NextTag();
        device.Write(TrccProtocol.BuildCommandBlockWrapper(
            tag,
            destination.Length,
            TrccProtocol.BuildScsiPollCommand(destination.Length),
            dataIn: true));

        var read = device.Read(destination);
        ReadStatus(device, tag);
        return read;
    }

    /// <summary>Runs a data-out command carrying one chunk of frame data.</summary>
    internal static void WriteChunk(IUsbBulkDevice device, int chunkIndex, ReadOnlySpan<byte> chunk)
    {
        var tag = NextTag();
        device.Write(TrccProtocol.BuildCommandBlockWrapper(
            tag,
            chunk.Length,
            TrccProtocol.BuildScsiWriteCommand(chunkIndex, chunk.Length)));

        var sent = 0;
        while (sent < chunk.Length)
        {
            var written = device.Write(chunk[sent..]);
            if (written <= 0)
            {
                throw new PanelConnectionException(
                    "The panel stopped accepting frame data part-way through a chunk.",
                    PanelFailureReason.Timeout);
            }

            sent += written;
        }

        ReadStatus(device, tag);
    }

    private static void ReadStatus(IUsbBulkDevice device, uint expectedTag)
    {
        Span<byte> status = stackalloc byte[TrccProtocol.CommandStatusWrapperSize];
        var read = device.Read(status);

        if (read < TrccProtocol.CommandStatusWrapperSize)
        {
            throw new PanelConnectionException(
                $"The panel returned a {read}-byte command status instead of "
                + $"{TrccProtocol.CommandStatusWrapperSize} bytes.",
                PanelFailureReason.ProtocolError);
        }

        if (!TrccProtocol.TryReadCommandStatus(status, out var tag, out var code))
        {
            throw new PanelConnectionException(
                "The panel returned a malformed command status wrapper.",
                PanelFailureReason.ProtocolError);
        }

        // A mismatched tag means the endpoint is one reply out of step, which
        // never recovers on its own: every subsequent command would read the
        // previous command's status.
        if (tag != expectedTag)
        {
            throw new PanelConnectionException(
                $"The panel answered command {tag} while command {expectedTag} was outstanding.",
                PanelFailureReason.ProtocolError);
        }

        if (code != 0)
        {
            throw new PanelConnectionException(
                $"The panel rejected a command with status {code}.",
                PanelFailureReason.ProtocolError);
        }
    }

    private static uint NextTag() => unchecked((uint)Interlocked.Increment(ref _tag));
}

/// <summary>An open connection to a mass-storage Thermalright panel.</summary>
internal sealed class ThermalrightScsiConnection : IPanelConnection
{
    private readonly FramePayloadEncoder _encoder;
    private readonly ILogger _logger;
    private readonly int _chunkSize;
    private IUsbBulkDevice? _device;

    internal ThermalrightScsiConnection(
        IUsbBulkDevice device,
        PanelDescriptor descriptor,
        PanelLocation location,
        PanelProfile profile,
        PanelOpenOptions options,
        IImageCodec codec,
        ILogger logger)
    {
        _device = device;
        _logger = logger;
        _chunkSize = Math.Max(512, profile.ChunkSize);
        Descriptor = descriptor;
        Location = location;
        FrameSize = descriptor.RenderSize(options.Orientation);
        _encoder = new FramePayloadEncoder(codec, profile, options, options.Orientation);
    }

    public PanelDescriptor Descriptor { get; }

    public PanelLocation Location { get; }

    public bool IsConnected => _device is { IsOpen: true };

    public (int Width, int Height) FrameSize { get; }

    public ValueTask SendFrameAsync(FrameBuffer frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var device = _device ?? throw Closed();
        var payload = _encoder.Encode(frame);

        var chunkIndex = 0;
        for (var offset = 0; offset < payload.Length; offset += _chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(_chunkSize, payload.Length - offset);
            ScsiTransport.WriteChunk(device, chunkIndex++, payload.Span.Slice(offset, length));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Ignoring a brightness request of {Percent}%: these panels have no brightness command.",
            percent);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        on ? ValueTask.CompletedTask : ClearAsync(cancellationToken);

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        using var blank = FrameBuffer.RentCleared(FrameSize.Width, FrameSize.Height);
        return SendFrameAsync(blank, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _encoder.Dispose();
        Interlocked.Exchange(ref _device, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private PanelConnectionException Closed() => new(
        "The connection to this panel is closed.",
        PanelFailureReason.Disconnected);
}
