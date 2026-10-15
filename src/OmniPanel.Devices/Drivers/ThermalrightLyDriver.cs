using System.Buffers;
using Microsoft.Extensions.Logging;
using OmniPanel.Devices.Profiles;
using OmniPanel.Devices.Protocols.Thermalright;
using OmniPanel.Devices.Transports;
using OmniPanel.Imaging;

namespace OmniPanel.Devices.Drivers;

/// <summary>
/// Drives the Thermalright "LY" bulk panels: the Trofeo Vision family, sold at
/// 6.86", 9.16" and 11.3".
/// </summary>
/// <remarks>
/// One driver covers every size in the family because the panel reports its own
/// geometry during the handshake. That is also why the descriptor produced here
/// is marked <see cref="PanelMatchConfidence.Confirmed"/>: the resolution comes
/// from the hardware rather than from a table that would have to be updated
/// whenever Thermalright adds a size.
/// </remarks>
public sealed class ThermalrightLyDriver : UsbProfileDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "thermalright.ly";

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public ThermalrightLyDriver(PanelDriverContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Id => DriverId;

    /// <inheritdoc />
    public override string DisplayName => "Thermalright Trofeo Vision (LY)";

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

            var descriptor = Handshake(device, profile, panel.Descriptor, device.Info.ProductId);
            var connection = new ThermalrightLyConnection(
                device,
                descriptor,
                panel.Location,
                profile,
                options,
                Context.Codec,
                Logger);

            if (options.Brightness is { } brightness)
            {
                await connection.SetBrightnessAsync(brightness, cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the panel what it is, and corrects the descriptor when it answers
    /// with a size the profile did not predict.
    /// </summary>
    private PanelDescriptor Handshake(
        IUsbBulkDevice device,
        PanelProfile profile,
        PanelDescriptor assumed,
        int productId)
    {
        var response = ArrayPool<byte>.Shared.Rent(LyProtocol.ResponseSize);
        try
        {
            device.Write(LyProtocol.BuildHandshake());

            var read = device.Read(response.AsSpan(0, LyProtocol.ResponseSize));
            if (read <= 0)
            {
                throw new PanelConnectionException(
                    $"{profile.FullName} did not answer the handshake. "
                    + "If the vendor software is running, close it and try again.",
                    PanelFailureReason.NotReady);
            }

            var reply = response.AsSpan(0, read);
            if (!LyProtocol.IsValidHandshakeResponse(reply))
            {
                throw new PanelConnectionException(
                    $"{profile.FullName} answered the handshake with {read} bytes that do not match "
                    + "the LY protocol. The profile may name the wrong driver for this panel.",
                    PanelFailureReason.ProtocolError);
            }

            var (panelModel, subModel) = TrccScreenTable.ReadLyIdentity(reply, productId);
            if (TrccScreenTable.ResolveModel(panelModel, subModel) is not { } screen)
            {
                Logger.LogInformation(
                    "{Panel} reported screen identity {Model}/{Sub}, which is not in the known table; "
                    + "keeping the profile's {Width}x{Height}.",
                    profile.FullName,
                    panelModel,
                    subModel,
                    assumed.Width,
                    assumed.Height);
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

/// <summary>An open connection to an LY panel.</summary>
internal sealed class ThermalrightLyConnection : IPanelConnection
{
    private readonly FramePayloadEncoder _encoder;
    private readonly ILogger _logger;
    private readonly int _productId;
    private byte[]? _transfer;
    private IUsbBulkDevice? _device;

    internal ThermalrightLyConnection(
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
        _productId = device.Info.ProductId;
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

        if ((frame.Width, frame.Height) != FrameSize)
        {
            throw new PanelConnectionException(
                $"This panel expects {FrameSize.Width}x{FrameSize.Height} frames, "
                + $"but a {frame.Width}x{frame.Height} frame was supplied.",
                PanelFailureReason.ProtocolError);
        }

        var payload = _encoder.Encode(frame);
        var transferSize = LyProtocol.TransferSizeFor(payload.Length, _productId);
        var buffer = RentTransfer(transferSize);

        LyProtocol.BuildFrame(payload.Span, _productId, buffer.AsSpan(0, transferSize));

        // The firmware reads a fixed number of bytes per bulk write and stalls
        // if a write straddles the end of the transfer, which is why the chunk
        // count is padded to keep the total a whole multiple of the write size.
        var sent = 0;
        while (sent < transferSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(LyProtocol.BulkWriteSize, transferSize - sent);
            var written = device.Write(buffer.AsSpan(sent, length));
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

    /// <remarks>
    /// The LY firmware has no brightness command: the panel's own buttons
    /// control the backlight. Reporting that honestly is better than silently
    /// accepting a setting that does nothing.
    /// </remarks>
    public ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Ignoring a brightness request of {Percent}%: LY panels have no brightness command.",
            percent);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        on
            ? ValueTask.CompletedTask
            : ClearAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        using var blank = FrameBuffer.RentCleared(FrameSize.Width, FrameSize.Height);
        return SendFrameAsync(blank, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        var transfer = Interlocked.Exchange(ref _transfer, null);
        if (transfer is not null)
        {
            ArrayPool<byte>.Shared.Return(transfer);
        }

        _encoder.Dispose();
        Interlocked.Exchange(ref _device, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private byte[] RentTransfer(int size)
    {
        var buffer = _transfer;
        if (buffer is not null && buffer.Length >= size)
        {
            return buffer;
        }

        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        buffer = ArrayPool<byte>.Shared.Rent(size);
        _transfer = buffer;
        return buffer;
    }

    private PanelConnectionException Closed() => new(
        "The connection to this panel is closed.",
        PanelFailureReason.Disconnected);
}
