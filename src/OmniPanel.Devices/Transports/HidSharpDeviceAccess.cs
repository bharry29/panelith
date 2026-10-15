using HidSharp;
using Microsoft.Extensions.Logging;

namespace OmniPanel.Devices.Transports;

/// <summary>
/// HID access through HidSharp.
/// </summary>
/// <remarks>
/// HID panels are opened through the operating system's HID stack rather than
/// through libusb. That matters for two reasons: the system stack needs no
/// driver replacement on Windows and no udev rule beyond read-write access to
/// the <c>hidraw</c> node on Linux, and it leaves any other interface the device
/// exposes - several cooler panels also present a fan controller - working for
/// whatever else is talking to it.
/// </remarks>
public sealed class HidSharpDeviceAccess : IUsbDeviceAccess
{
    private readonly ILogger? _logger;

    /// <summary>Creates the backend.</summary>
    /// <param name="logger">Optional log sink.</param>
    public HidSharpDeviceAccess(ILogger<HidSharpDeviceAccess>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            try
            {
                _ = DeviceList.Local;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string BackendName => "hidsharp";

    /// <inheritdoc />
    public IReadOnlyList<UsbDeviceInfo> Enumerate(IReadOnlySet<(int VendorId, int ProductId)>? filter = null)
    {
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PlatformNotSupportedException)
        {
            _logger?.LogDebug(ex, "The HID backend is unavailable on this machine.");
            return [];
        }

        var found = new List<UsbDeviceInfo>();
        foreach (var device in devices)
        {
            if (filter is not null && !filter.Contains((device.VendorID, device.ProductID)))
            {
                continue;
            }

            found.Add(Describe(device));
        }

        return found;
    }

    /// <inheritdoc />
    /// <remarks>
    /// HID devices cannot be driven as raw bulk endpoints without displacing the
    /// system HID stack, so this always fails. The driver stack picks the
    /// backend from the profile's transport rather than probing.
    /// </remarks>
    public IUsbBulkDevice OpenBulk(UsbDeviceInfo device, bool requireInputEndpoint = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        throw new PanelConnectionException(
            "The HID backend does not expose bulk endpoints; use the libusb backend instead.",
            PanelFailureReason.Unsupported);
    }

    /// <inheritdoc />
    public IUsbHidDevice OpenHid(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var match = FindDevice(device)
            ?? throw new PanelConnectionException(
                $"{device.Describe()} is no longer attached.",
                PanelFailureReason.Disconnected);

        // Exclusive access is deliberately not requested. The vendor tool holding
        // the panel is a situation to report, not to fight over, and a shared
        // open lets discovery confirm a panel that something else is driving.
        var configuration = new OpenConfiguration();
        configuration.SetOption(OpenOption.Interruptible, true);

        if (!match.TryOpen(configuration, out var stream))
        {
            throw new PanelConnectionException(
                $"Could not open {device.Describe()} as a HID device. "
                + "Another application may be holding it.",
                PanelFailureReason.Busy);
        }

        return new HidSharpDevice(match, stream, Describe(match));
    }

    private HidDevice? FindDevice(UsbDeviceInfo wanted)
    {
        HidDevice? firstModelMatch = null;

        foreach (var device in DeviceList.Local.GetHidDevices(wanted.VendorId, wanted.ProductId))
        {
            if (wanted.DevicePath is { Length: > 0 } path
                && string.Equals(device.DevicePath, path, StringComparison.Ordinal))
            {
                return device;
            }

            if (wanted.SerialNumber is { Length: > 0 } serial
                && string.Equals(TryReadSerial(device), serial, StringComparison.Ordinal))
            {
                return device;
            }

            firstModelMatch ??= device;
        }

        // Falling back to the first device of the right model is correct for the
        // common single-panel case and is why a panel with a blank serial still
        // works; two identical blank-serial panels are disambiguated by path.
        return firstModelMatch;
    }

    private static UsbDeviceInfo Describe(HidDevice device) => new(
        device.VendorID,
        device.ProductID,
        BusNumber: null,
        DeviceAddress: null,
        TryReadSerial(device),
        TryRead(device.GetManufacturer),
        TryRead(device.GetProductName),
        device.DevicePath);

    private static string? TryReadSerial(HidDevice device) => TryRead(device.GetSerialNumber);

    private static string? TryRead(Func<string> read)
    {
        // HidSharp surfaces an unreadable descriptor as an exception rather than
        // as a null, and an unreadable descriptor is normal: plenty of panels
        // report no serial at all.
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>An open HID stream.</summary>
internal sealed class HidSharpDevice : IUsbHidDevice
{
    private readonly HidDevice _device;
    private HidStream? _stream;

    internal HidSharpDevice(HidDevice device, HidStream stream, UsbDeviceInfo info)
    {
        _device = device;
        _stream = stream;
        Info = info;
        OutputReportLength = SafeLength(device.GetMaxOutputReportLength);
        InputReportLength = SafeLength(device.GetMaxInputReportLength);
    }

    public UsbDeviceInfo Info { get; }

    public bool IsOpen => _stream is not null;

    public int OutputReportLength { get; }

    public int InputReportLength { get; }

    public void Write(ReadOnlySpan<byte> report)
    {
        var stream = _stream ?? throw Closed();

        try
        {
            stream.Write(report);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Dispose();
            throw new PanelConnectionException(
                $"Failed to write a HID report to {Info.Describe()}: {ex.Message}",
                PanelFailureReason.Disconnected,
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new PanelConnectionException(
                $"Writing a HID report to {Info.Describe()} timed out.",
                PanelFailureReason.Timeout,
                ex);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMilliseconds = 2000)
    {
        var stream = _stream ?? throw Closed();

        try
        {
            stream.ReadTimeout = timeoutMilliseconds;
            return stream.Read(buffer);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Dispose();
            throw new PanelConnectionException(
                $"Failed to read a HID report from {Info.Describe()}: {ex.Message}",
                PanelFailureReason.Disconnected,
                ex);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        GC.KeepAlive(_device);
    }

    private PanelConnectionException Closed() => new(
        $"The HID connection to {Info.Describe()} is closed.",
        PanelFailureReason.Disconnected);

    private static int SafeLength(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return 0;
        }
    }
}
