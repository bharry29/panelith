namespace Panelith.Devices.Transports;

/// <summary>A serial port, with the USB identity behind it when one is known.</summary>
/// <param name="PortName">The port name, such as <c>COM5</c> or <c>/dev/ttyUSB0</c>.</param>
/// <param name="VendorId">USB vendor id of the bridge chip, when resolvable.</param>
/// <param name="ProductId">USB product id of the bridge chip, when resolvable.</param>
/// <param name="SerialNumber">USB serial number, when the bridge reports one.</param>
/// <param name="Description">Friendly name from the operating system.</param>
public readonly record struct SerialPortInfo(
    string PortName,
    int? VendorId = null,
    int? ProductId = null,
    string? SerialNumber = null,
    string? Description = null)
{
    /// <summary>Whether a USB identity was resolved for this port.</summary>
    public bool HasUsbIdentity => VendorId is not null && ProductId is not null;

    /// <summary>Converts to the shared USB identity shape used by profile matching.</summary>
    public UsbDeviceInfo ToDeviceInfo() => new(
        VendorId ?? 0,
        ProductId ?? 0,
        BusNumber: null,
        DeviceAddress: null,
        SerialNumber,
        Manufacturer: null,
        Description,
        PortName);

    /// <summary>A short description for logs and the device list.</summary>
    public string Describe() => HasUsbIdentity
        ? $"{PortName} ({VendorId:x4}:{ProductId:x4}{(Description is not null ? $" {Description}" : string.Empty)})"
        : PortName;
}

/// <summary>Finds serial ports and the USB devices behind them.</summary>
/// <remarks>
/// Panels that speak a serial protocol sit behind a generic bridge chip - a
/// CH340 or a CDC-ACM interface - so the port name alone says nothing about
/// what is attached. Recovering the bridge's USB ids is what lets a profile
/// distinguish a Turing panel from an Arduino on the next port, which is why
/// this is worth a platform-specific lookup rather than probing every port with
/// a handshake.
/// </remarks>
public interface ISerialPortEnumerator
{
    /// <summary>Lists the serial ports present right now.</summary>
    IReadOnlyList<SerialPortInfo> Enumerate();

    /// <summary>Opens a port.</summary>
    /// <param name="portName">The port name.</param>
    /// <param name="baudRate">Line speed.</param>
    /// <param name="readTimeoutMilliseconds">Default read timeout.</param>
    /// <exception cref="PanelConnectionException">The port could not be opened.</exception>
    ISerialDevice Open(string portName, int baudRate, int readTimeoutMilliseconds = 2000);
}
