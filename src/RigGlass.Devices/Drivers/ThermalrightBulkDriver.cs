using System.Buffers;
using Microsoft.Extensions.Logging;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Protocols.Thermalright;
using RigGlass.Devices.Transports;
using RigGlass.Imaging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// Drives the Thermalright panels that speak raw bulk USB with a 64-byte frame
/// header, which covers several of the Grand Vision and AIO display variants.
/// </summary>
public sealed class ThermalrightBulkDriver : UsbProfileDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "thermalright.bulk";

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public ThermalrightBulkDriver(PanelDriverContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Id => DriverId;

    /// <inheritdoc />
    public override string DisplayName => "Thermalright raw-bulk panels";

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

            var descriptor = Handshake(device, profile, panel.Descriptor);
            return new ThermalrightBulkConnection(
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

    private PanelDescriptor Handshake(IUsbBulkDevice device, PanelProfile profile, PanelDescriptor assumed)
    {
        var response = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            device.Write(TrccProtocol.BuildBulkHandshake());

            var read = device.Read(response.AsSpan(0, 64));
            if (read <= 0)
            {
                // These firmwares answer the init packet inconsistently - some
                // variants stay silent until the first frame arrives. Treating
                // silence as fatal would refuse hardware that works, so the
                // profile's geometry is used and the panel is driven anyway.
                Logger.LogDebug("{Panel} did not answer the bulk init packet; using the profile geometry.", profile.FullName);
                return assumed;
            }

            var reply = response.AsSpan(0, read);
            if (!TrccProtocol.IsValidBulkResponse(reply))
            {
                return assumed;
            }

            var (panelModel, subModel) = TrccScreenTable.ReadBulkIdentity(reply);
            var code = TrccScreenTable.BulkScreenCodeFor(panelModel, subModel);
            if (TrccScreenTable.Resolve(code, panelModel, subModel) is not { } screen)
            {
                return assumed;
            }

            if (screen.Width == assumed.Width && screen.Height == assumed.Height)
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
        finally
        {
            ArrayPool<byte>.Shared.Return(response);
        }
    }
}

/// <summary>An open connection to a raw-bulk Thermalright panel.</summary>
internal sealed class ThermalrightBulkConnection : IPanelConnection
{
    private readonly FramePayloadEncoder _encoder;
    private readonly ILogger _logger;
    private readonly bool _usesJpeg;
    private readonly int _chunkSize;
    private readonly (int Width, int Height) _nativeSize;
    private IUsbBulkDevice? _device;

    internal ThermalrightBulkConnection(
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
        _usesJpeg = profile.PixelFormat == PanelPixelFormat.Jpeg;
        _chunkSize = Math.Max(512, profile.ChunkSize);
        _nativeSize = (descriptor.Width, descriptor.Height);
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

        // The header describes the panel's own scan dimensions, not the
        // orientation the theme was rendered at.
        device.Write(TrccProtocol.BuildBulkHeader(_nativeSize.Width, _nativeSize.Height, payload.Length, _usesJpeg));

        var sent = 0;
        while (sent < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(_chunkSize, payload.Length - sent);
            var written = device.Write(payload.Span.Slice(sent, length));
            if (written <= 0)
            {
                throw new PanelConnectionException(
                    "The panel stopped accepting frame data part-way through a transfer.",
                    PanelFailureReason.Timeout);
            }

            sent += written;
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
