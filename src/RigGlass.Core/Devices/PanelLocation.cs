using System.Globalization;

namespace RigGlass.Devices;

/// <summary>
/// Where a physically-present panel can be found, and -- just as importantly --
/// how to find the same panel again after it disappears and comes back.
/// </summary>
/// <remarks>
/// <para>
/// Panels drop off the bus constantly: on suspend, on resume, when the firmware
/// resets itself after a bad frame, and on some Turing revisions simply after
/// enough idle time. Reconnecting correctly means knowing whether the device
/// that just appeared is "the same one" as the one that vanished, which is why
/// this type keeps both a durable key and a volatile address.
/// </para>
/// <para>
/// <see cref="StableKey"/> prefers a serial number when the device reports a
/// unique one, and falls back to the physical port path. A port path is stable
/// as long as the user does not move the cable, which is the best that can be
/// done for the many panels that ship with an identical hard-coded serial.
/// </para>
/// </remarks>
public sealed record PanelLocation
{
    private PanelLocation(PanelTransportKind transport)
    {
        Transport = transport;
    }

    /// <summary>Which transport this address belongs to.</summary>
    public PanelTransportKind Transport { get; private init; }

    /// <summary>USB vendor id, for USB transports.</summary>
    public int VendorId { get; private init; }

    /// <summary>USB product id, for USB transports.</summary>
    public int ProductId { get; private init; }

    /// <summary>USB bus number, when the platform reports one.</summary>
    public int? BusNumber { get; private init; }

    /// <summary>USB device address on the bus, when the platform reports one.</summary>
    public int? DeviceAddress { get; private init; }

    /// <summary>
    /// Platform-specific path, such as a Linux sysfs path, a Windows device
    /// instance path, or a serial port name.
    /// </summary>
    public string? DevicePath { get; private init; }

    /// <summary>Serial number string, when the device reports one.</summary>
    public string? SerialNumber { get; private init; }

    /// <summary>Serial port name, such as <c>COM5</c> or <c>/dev/ttyACM0</c>.</summary>
    public string? PortName { get; private init; }

    /// <summary>Creates a USB address.</summary>
    public static PanelLocation Usb(
        PanelTransportKind transport,
        int vendorId,
        int productId,
        int? busNumber = null,
        int? deviceAddress = null,
        string? devicePath = null,
        string? serialNumber = null) =>
        new(transport)
        {
            VendorId = vendorId,
            ProductId = productId,
            BusNumber = busNumber,
            DeviceAddress = deviceAddress,
            DevicePath = devicePath,
            SerialNumber = NormalizeSerial(serialNumber),
        };

    /// <summary>Creates a serial-port address.</summary>
    public static PanelLocation Serial(string portName, int? vendorId = null, int? productId = null, string? serialNumber = null) =>
        new(PanelTransportKind.Serial)
        {
            PortName = portName,
            VendorId = vendorId ?? 0,
            ProductId = productId ?? 0,
            SerialNumber = NormalizeSerial(serialNumber),
        };

    /// <summary>Creates the address of a virtual panel.</summary>
    public static PanelLocation Virtual(string name) =>
        new(PanelTransportKind.Virtual) { DevicePath = name };

    /// <summary>
    /// A key that identifies this panel across disconnect and reconnect cycles.
    /// </summary>
    public string StableKey
    {
        get
        {
            if (Transport == PanelTransportKind.Virtual)
            {
                return $"virtual:{DevicePath}";
            }

            var vidPid = string.Create(CultureInfo.InvariantCulture, $"{VendorId:x4}:{ProductId:x4}");

            if (!string.IsNullOrEmpty(SerialNumber))
            {
                return $"usb:{vidPid}:sn={SerialNumber}";
            }

            if (Transport == PanelTransportKind.Serial && !string.IsNullOrEmpty(PortName))
            {
                return $"serial:{PortName}";
            }

            if (BusNumber is { } bus && DeviceAddress is { } address)
            {
                return string.Create(CultureInfo.InvariantCulture, $"usb:{vidPid}:port={bus}.{address}");
            }

            return !string.IsNullOrEmpty(DevicePath)
                ? $"usb:{vidPid}:path={DevicePath}"
                : $"usb:{vidPid}";
        }
    }

    /// <summary>
    /// Whether this location carries enough information to distinguish two
    /// otherwise identical panels plugged in at the same time.
    /// </summary>
    /// <remarks>
    /// When this is <see langword="false"/> and more than one unit of the same
    /// model is present, RigGlass pins each panel to a bus address and warns
    /// that moving the cable will shuffle the assignment.
    /// </remarks>
    public bool IsUnambiguous =>
        !string.IsNullOrEmpty(SerialNumber)
        || (Transport == PanelTransportKind.Serial && !string.IsNullOrEmpty(PortName))
        || (BusNumber is not null && DeviceAddress is not null);

    /// <summary>A short description for logs and the device list.</summary>
    public string Describe() => Transport switch
    {
        PanelTransportKind.Virtual => $"virtual panel \"{DevicePath}\"",
        PanelTransportKind.Serial => $"{PortName} ({VendorId:x4}:{ProductId:x4})",
        _ when BusNumber is { } bus && DeviceAddress is { } address =>
            string.Create(CultureInfo.InvariantCulture, $"USB {VendorId:x4}:{ProductId:x4} at bus {bus} device {address}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"USB {VendorId:x4}:{ProductId:x4}"),
    };

    /// <summary>
    /// Drops serial numbers that are shared across every unit of a model and so
    /// cannot identify one.
    /// </summary>
    /// <remarks>
    /// Several panel families hard-code a placeholder here -- a run of zeros, or
    /// a literal marketing string such as <c>USB35INCHIPSV2</c>. Treating those
    /// as unique would make two identical panels collide on one
    /// <see cref="StableKey"/> and have them fight over a single connection.
    /// </remarks>
    private static string? NormalizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        var trimmed = serial.Trim();
        if (trimmed.Length < 4)
        {
            return null;
        }

        var allSame = trimmed.All(c => c == trimmed[0]);
        return allSame ? null : trimmed;
    }
}
