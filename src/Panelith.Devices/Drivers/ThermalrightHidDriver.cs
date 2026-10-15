using System.Buffers;
using Microsoft.Extensions.Logging;
using Panelith.Devices.Profiles;
using Panelith.Devices.Protocols.Thermalright;
using Panelith.Devices.Transports;
using Panelith.Imaging;

namespace Panelith.Devices.Drivers;

/// <summary>
/// Drives the Thermalright panels that present as HID devices: the Frozen Magic
/// and Frozen Warframe screens, among others.
/// </summary>
/// <remarks>
/// Two incompatible header layouts ship behind this transport. The profile's
/// probe family selects between them rather than the driver guessing, because
/// the two are indistinguishable from the USB descriptors alone and sending the
/// wrong one leaves the panel showing a frozen half-frame.
/// </remarks>
public sealed class ThermalrightHidDriver : UsbProfileDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "thermalright.hid";

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public ThermalrightHidDriver(PanelDriverContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Id => DriverId;

    /// <inheritdoc />
    public override string DisplayName => "Thermalright HID panels";

    /// <inheritdoc />
    protected override IUsbDeviceAccess Access => Context.HidAccess;

    /// <inheritdoc />
    public override async ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var profile = ProfileFor(panel);
        var family = profile.Probe switch
        {
            PanelProbeFamily.ThermalrightHidType3 => TrccWireFamily.HidType3,
            _ => TrccWireFamily.HidType2,
        };

        var device = Context.HidAccess.OpenHid(ToDeviceInfo(panel));

        try
        {
            await WaitForStartupAsync(profile, cancellationToken).ConfigureAwait(false);

            var descriptor = Handshake(device, profile, panel.Descriptor, family);
            return new ThermalrightHidConnection(
                device,
                descriptor,
                panel.Location,
                profile,
                options,
                family,
                Context.Codec,
                Logger);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private PanelDescriptor Handshake(
        IUsbHidDevice device,
        PanelProfile profile,
        PanelDescriptor assumed,
        TrccWireFamily family)
    {
        var handshake = family == TrccWireFamily.HidType3
            ? TrccProtocol.BuildHidType3Handshake()
            : TrccProtocol.BuildHidType2Handshake();

        HidReportWriter.Write(device, profile.ReportId, handshake);

        var size = Math.Max(64, device.InputReportLength);
        var response = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var read = device.Read(response.AsSpan(0, size));
            if (read <= 0)
            {
                Logger.LogDebug(
                    "{Panel} did not answer the HID init report; using the profile geometry.",
                    profile.FullName);
                return assumed;
            }

            var reply = response.AsSpan(0, read);

            // HidSharp hands back the report id in byte zero on some platforms
            // and strips it on others, so the payload is matched at both offsets
            // rather than assuming either convention.
            var payload = Matches(reply, family) ? reply
                : read > 1 && Matches(reply[1..], family) ? reply[1..]
                : default;

            if (payload.IsEmpty)
            {
                return assumed;
            }

            if (family != TrccWireFamily.HidType3)
            {
                return assumed;
            }

            var code = TrccProtocol.HidType3ScreenCode(payload);
            if (TrccScreenTable.Resolve(code) is not { } screen
                || (screen.Width == assumed.Width && screen.Height == assumed.Height))
            {
                return assumed;
            }

            Logger.LogInformation(
                "{Panel} reports screen code {Code} ({Width}x{Height}), overriding the profile's "
                + "{ProfileWidth}x{ProfileHeight}.",
                profile.FullName,
                code,
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

    private static bool Matches(ReadOnlySpan<byte> reply, TrccWireFamily family) => family == TrccWireFamily.HidType3
        ? TrccProtocol.IsValidHidType3Response(reply)
        : TrccProtocol.IsValidHidType2Response(reply);
}

/// <summary>Splits a payload into HID output reports of the size the device accepts.</summary>
internal static class HidReportWriter
{
    internal static void Write(IUsbHidDevice device, byte reportId, ReadOnlySpan<byte> payload)
    {
        // A HID output report is a fixed size: the device rejects anything
        // shorter, so the final partial chunk has to be zero-padded rather than
        // truncated.
        var reportLength = device.OutputReportLength > 1 ? device.OutputReportLength : 65;
        var dataPerReport = reportLength - 1;
        var report = ArrayPool<byte>.Shared.Rent(reportLength);

        try
        {
            var span = report.AsSpan(0, reportLength);
            for (var offset = 0; offset < payload.Length; offset += dataPerReport)
            {
                span.Clear();
                span[0] = reportId;

                var length = Math.Min(dataPerReport, payload.Length - offset);
                payload.Slice(offset, length).CopyTo(span[1..]);
                device.Write(span);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(report);
        }
    }
}

/// <summary>An open connection to a HID Thermalright panel.</summary>
internal sealed class ThermalrightHidConnection : IPanelConnection
{
    private readonly FramePayloadEncoder _encoder;
    private readonly ILogger _logger;
    private readonly TrccWireFamily _family;
    private readonly byte _reportId;
    private readonly (int Width, int Height) _nativeSize;
    private IUsbHidDevice? _device;

    internal ThermalrightHidConnection(
        IUsbHidDevice device,
        PanelDescriptor descriptor,
        PanelLocation location,
        PanelProfile profile,
        PanelOpenOptions options,
        TrccWireFamily family,
        IImageCodec codec,
        ILogger logger)
    {
        _device = device;
        _logger = logger;
        _family = family;
        _reportId = profile.ReportId;
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

        var header = _family == TrccWireFamily.HidType3
            ? TrccProtocol.BuildHidType3Header(_nativeSize.Width, _nativeSize.Height)
            : TrccProtocol.BuildHidType2Header(_nativeSize.Width, _nativeSize.Height, payload.Length);

        // Header and payload go out as one logical stream, because the firmware
        // counts bytes from the start of the header rather than resynchronising
        // on report boundaries.
        var combined = ArrayPool<byte>.Shared.Rent(header.Length + payload.Length);
        try
        {
            header.CopyTo(combined.AsSpan());
            payload.Span.CopyTo(combined.AsSpan(header.Length));
            HidReportWriter.Write(device, _reportId, combined.AsSpan(0, header.Length + payload.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(combined);
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
