using System.Buffers;
using Microsoft.Extensions.Logging;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Protocols.Turing;
using RigGlass.Devices.Transports;
using RigGlass.Imaging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// Drives the serial panels: Turing Smart Screen revisions A, B and C, the
/// XuanFang screens, the UsbMonitor range and the Kipye Qiye panels.
/// </summary>
/// <remarks>
/// <para>
/// All of these sit behind a USB-to-serial bridge, and several of them sit
/// behind the <em>same</em> bridge: a stock CH340 at <c>1a86:5722</c> covers
/// both revision A and revision B. The USB ids alone therefore cannot identify
/// the panel, which is why the profiles for those models also match on the
/// serial number string the firmware reports, and why the probe family that
/// selects the command set comes from the profile rather than from a guess.
/// </para>
/// <para>
/// A port with no USB identity is still offered when the user names it
/// explicitly. That matters on macOS and on unusual bridges, where the
/// operating system will not say what is behind a tty.
/// </para>
/// </remarks>
public sealed class TuringSerialDriver : IPanelDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "turing.serial";

    private readonly PanelDriverContext _context;
    private readonly ILogger _logger;

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public TuringSerialDriver(PanelDriverContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = context.LoggerFactory.CreateLogger<TuringSerialDriver>();
    }

    /// <inheritdoc />
    public string Id => DriverId;

    /// <inheritdoc />
    public string DisplayName => "Turing and compatible serial panels";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Profiles.ForDriver(Id).Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<DetectedPanel>>([]);
        }

        var detected = new List<DetectedPanel>();
        foreach (var port in _context.SerialPorts.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!port.HasUsbIdentity)
            {
                continue;
            }

            var profile = SelectProfile(port);
            if (profile is null)
            {
                continue;
            }

            detected.Add(new DetectedPanel(
                profile.ToDescriptor(),
                PanelLocation.Serial(port.PortName, port.VendorId, port.ProductId, port.SerialNumber),
                Id,
                profile.IsUserDefined ? PanelMatchConfidence.UserDefined : PanelMatchConfidence.IdMatch));
        }

        return ValueTask.FromResult<IReadOnlyList<DetectedPanel>>(detected);
    }

    /// <inheritdoc />
    public async ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var profile = _context.Profiles.Find(panel.Descriptor.ProfileId)
            ?? throw new PanelConnectionException(
                $"No profile named \"{panel.Descriptor.ProfileId}\" is loaded any more.",
                PanelFailureReason.Unsupported);

        var portName = panel.Location.PortName
            ?? throw new PanelConnectionException(
                "This panel was discovered without a port name, so it cannot be reopened.",
                PanelFailureReason.Unknown);

        var port = _context.SerialPorts.Open(portName, profile.BaudRate);

        try
        {
            if (profile.StartupDelaySeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(profile.StartupDelaySeconds),
                    _context.TimeProvider,
                    cancellationToken).ConfigureAwait(false);
            }

            var descriptor = Handshake(port, profile, panel.Descriptor, out var variant);
            var connection = new TuringSerialConnection(
                port,
                descriptor,
                panel.Location,
                profile,
                options,
                _context.Codec,
                _logger);

            connection.SetVariant(variant);
            await connection.InitialiseAsync(options, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private PanelProfile? SelectProfile(SerialPortInfo port)
    {
        foreach (var candidate in _context.Profiles.FindByUsb(
            port.VendorId ?? 0,
            port.ProductId ?? 0,
            port.SerialNumber))
        {
            if (string.Equals(candidate.Driver, Id, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private PanelDescriptor Handshake(
        ISerialDevice port,
        PanelProfile profile,
        PanelDescriptor assumed,
        out TuringRevisionBVariant variant)
    {
        variant = TuringRevisionBVariant.Basic;
        port.DiscardInput();

        var response = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            switch (profile.Probe)
            {
                case PanelProbeFamily.TuringRevisionA:
                {
                    port.Write(TuringRevisionA.BuildHello());
                    var read = port.Read(response.AsSpan(0, TuringRevisionA.CommandSize), timeoutMilliseconds: 500);

                    // Silence is the identification for the original 3.5" panel,
                    // which never answers; only a reply can change the geometry.
                    if (read < TuringRevisionA.CommandSize)
                    {
                        return assumed;
                    }

                    var (width, height, model) = TuringRevisionA.IdentifyFromHello(
                        response.AsSpan(0, TuringRevisionA.CommandSize));

                    if (width == assumed.Width && height == assumed.Height)
                    {
                        return assumed;
                    }

                    _logger.LogInformation(
                        "{Port} identified itself as a {Model} ({Width}x{Height}).",
                        port.PortName,
                        model,
                        width,
                        height);

                    return assumed with { Model = model, Width = width, Height = height };
                }

                case PanelProbeFamily.TuringRevisionB:
                {
                    port.Write(TuringRevisionB.BuildHello());
                    var read = port.Read(response.AsSpan(0, TuringRevisionB.CommandSize), timeoutMilliseconds: 500);
                    if (read >= TuringRevisionB.CommandSize)
                    {
                        variant = TuringRevisionB.IdentifyFromHello(response.AsSpan(0, read));
                        _logger.LogInformation("{Port} reports revision B variant {Variant}.", port.PortName, variant);
                    }

                    return assumed;
                }

                case PanelProbeFamily.TuringRevisionC:
                {
                    port.Write(TuringRevisionC.Pad(TuringRevisionC.Hello));
                    var read = port.Read(response.AsSpan(0, 23), timeoutMilliseconds: 1000);
                    if (read > 0 && TuringRevisionC.IsIdentityResponse(response.AsSpan(0, read)))
                    {
                        _logger.LogInformation(
                            "{Port} reports firmware revision {Version}.",
                            port.PortName,
                            TuringRevisionC.ReadRomVersion(response.AsSpan(0, read)));
                    }

                    return assumed;
                }

                case PanelProbeFamily.TuringRevisionD:
                {
                    port.Write(TuringRevisionD.GetInfo);
                    _ = port.Read(response.AsSpan(0, 24), timeoutMilliseconds: 1000);
                    return assumed;
                }

                default:
                    return assumed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(response);
        }
    }
}

/// <summary>An open connection to a serial panel.</summary>
internal sealed class TuringSerialConnection : IPanelConnection
{
    private readonly FramePayloadEncoder _encoder;
    private readonly ILogger _logger;
    private readonly PanelProfile _profile;
    private readonly PanelCapabilities _capabilities;
    private readonly (int Width, int Height) _nativeSize;
    private TuringRevisionBVariant _variant = TuringRevisionBVariant.Basic;
    private ISerialDevice? _port;

    internal TuringSerialConnection(
        ISerialDevice port,
        PanelDescriptor descriptor,
        PanelLocation location,
        PanelProfile profile,
        PanelOpenOptions options,
        IImageCodec codec,
        ILogger logger)
    {
        _port = port;
        _profile = profile;
        _logger = logger;
        _capabilities = descriptor.Capabilities;
        _nativeSize = (descriptor.Width, descriptor.Height);
        Descriptor = descriptor;
        Location = location;
        FrameSize = descriptor.RenderSize(options.Orientation);

        // These panels have no hardware rotation worth trusting across
        // revisions, so the image is always rotated in software and the panel is
        // left in its native orientation.
        _encoder = new FramePayloadEncoder(codec, profile, options, options.Orientation);
    }

    public PanelDescriptor Descriptor { get; }

    public PanelLocation Location { get; }

    public bool IsConnected => _port is { IsOpen: true };

    public (int Width, int Height) FrameSize { get; }

    /// <summary>Puts the panel into the state the frame loop assumes.</summary>
    internal async ValueTask InitialiseAsync(PanelOpenOptions options, CancellationToken cancellationToken)
    {
        var port = _port ?? throw Closed();

        switch (_profile.Probe)
        {
            case PanelProbeFamily.TuringRevisionA:
                port.Write(TuringRevisionA.BuildSetOrientation(
                    PanelOrientation.Portrait,
                    _nativeSize.Width,
                    _nativeSize.Height));
                break;

            case PanelProbeFamily.TuringRevisionB:
                port.Write(TuringRevisionB.BuildSetOrientation(
                    PanelOrientation.Portrait,
                    _nativeSize.Width,
                    _nativeSize.Height));
                break;

            case PanelProbeFamily.TuringRevisionC:
                port.Write(TuringRevisionC.BuildSetOptions(flip180: false));
                port.Write(TuringRevisionC.Pad(TuringRevisionC.StopVideo));
                port.Write(TuringRevisionC.Pad(TuringRevisionC.StopMedia));
                break;

            case PanelProbeFamily.TuringRevisionD:
                port.Write(TuringRevisionD.OrientationCommand(PanelOrientation.Portrait));
                port.Write(TuringRevisionD.EnterPictureMode);
                break;

            default:
                break;
        }

        if (options.Brightness is { } brightness && _capabilities.SupportsBrightness)
        {
            await SetBrightnessAsync(brightness, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask SendFrameAsync(FrameBuffer frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var port = _port ?? throw Closed();
        var payload = _encoder.Encode(frame);

        switch (_profile.Probe)
        {
            case PanelProbeFamily.TuringRevisionB:
                port.Write(TuringRevisionB.BuildDisplayBitmap(0, 0, _nativeSize.Width, _nativeSize.Height));
                break;

            case PanelProbeFamily.TuringRevisionC:
                port.Write(TuringRevisionC.Pad(TuringRevisionC.PreUpdateBitmap));
                port.Write(TuringRevisionC.StartDisplayBitmap.ToArray());
                break;

            case PanelProbeFamily.TuringRevisionD:
                port.Write(TuringRevisionD.BuildBlockWrite(0, _nativeSize.Width - 1, 0, _nativeSize.Height - 1));
                break;

            default:
                port.Write(TuringRevisionA.BuildDisplayBitmap(0, 0, _nativeSize.Width, _nativeSize.Height));
                break;
        }

        port.Write(payload.Span);

        if (_profile.Probe == PanelProbeFamily.TuringRevisionC)
        {
            port.Write(TuringRevisionC.Pad(TuringRevisionC.UpdateBitmap));
        }

        port.Flush();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var port = _port ?? throw Closed();

        if (!_capabilities.SupportsBrightness)
        {
            _logger.LogDebug(
                "Ignoring a brightness request of {Percent}%: {Panel} has no brightness command.",
                percent,
                Descriptor.FullName);
            return ValueTask.CompletedTask;
        }

        var command = _profile.Probe switch
        {
            PanelProbeFamily.TuringRevisionB => TuringRevisionB.BuildSetBrightness(percent, _variant),
            PanelProbeFamily.TuringRevisionC => TuringRevisionC.BuildSetBrightness(percent),
            PanelProbeFamily.TuringRevisionD => TuringRevisionD.BuildSetBrightness(percent),
            _ => TuringRevisionA.BuildSetBrightness(percent),
        };

        port.Write(command);
        port.Flush();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var port = _port ?? throw Closed();

        switch (_profile.Probe)
        {
            case PanelProbeFamily.TuringRevisionC:
                port.Write(TuringRevisionC.Pad(on ? TuringRevisionC.TurnOn : TuringRevisionC.TurnOff));
                break;

            case PanelProbeFamily.TuringRevisionA:
                port.Write(TuringRevisionA.BuildCommand(on
                    ? TuringRevisionACommand.ScreenOn
                    : TuringRevisionACommand.ScreenOff));
                break;

            default:
                // Everything else emulates screen-off by blanking, which is
                // visually equivalent and keeps the link alive.
                return on ? ValueTask.CompletedTask : ClearAsync(cancellationToken);
        }

        port.Flush();
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        using var blank = FrameBuffer.RentCleared(FrameSize.Width, FrameSize.Height);
        return SendFrameAsync(blank, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _encoder.Dispose();
        Interlocked.Exchange(ref _port, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Records the sub-revision a revision B panel reported.</summary>
    internal void SetVariant(TuringRevisionBVariant variant) => _variant = variant;

    private PanelConnectionException Closed() => new(
        "The connection to this panel is closed.",
        PanelFailureReason.Disconnected);
}
