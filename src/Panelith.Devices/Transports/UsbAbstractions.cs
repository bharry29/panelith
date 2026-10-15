namespace Panelith.Devices.Transports;

/// <summary>A USB device as seen by enumeration, before it is opened.</summary>
/// <param name="VendorId">USB vendor id.</param>
/// <param name="ProductId">USB product id.</param>
/// <param name="BusNumber">Bus number, when the platform reports one.</param>
/// <param name="DeviceAddress">Address on the bus, when the platform reports one.</param>
/// <param name="SerialNumber">Serial number string, when readable without opening the device.</param>
/// <param name="Manufacturer">Manufacturer string, when readable.</param>
/// <param name="Product">Product string, when readable.</param>
/// <param name="DevicePath">Platform-specific path, used as a fallback identity.</param>
public sealed record UsbDeviceInfo(
    int VendorId,
    int ProductId,
    int? BusNumber = null,
    int? DeviceAddress = null,
    string? SerialNumber = null,
    string? Manufacturer = null,
    string? Product = null,
    string? DevicePath = null)
{
    /// <summary>A short description for logs.</summary>
    public string Describe() =>
        $"{VendorId:x4}:{ProductId:x4}"
        + (Product is not null ? $" \"{Product}\"" : string.Empty)
        + (BusNumber is { } bus && DeviceAddress is { } address ? $" (bus {bus} device {address})" : string.Empty);
}

/// <summary>The bulk endpoints found on an interface.</summary>
/// <param name="InterfaceNumber">The interface these endpoints belong to.</param>
/// <param name="EndpointOut">Address of the bulk OUT endpoint.</param>
/// <param name="EndpointIn">Address of the bulk IN endpoint, when the interface has one.</param>
/// <param name="MaxPacketSize">The OUT endpoint's maximum packet size.</param>
/// <param name="InterfaceClass">
/// The claimed interface's USB class code. Drivers check this to tell a
/// vendor-specific bulk panel from one that speaks the mass-storage transport,
/// because several Thermalright models ship both behind one product id.
/// </param>
public readonly record struct UsbBulkEndpoints(
    byte InterfaceNumber,
    byte EndpointOut,
    byte? EndpointIn,
    int MaxPacketSize,
    byte InterfaceClass = 0);

/// <summary>
/// An open USB device with claimed bulk endpoints.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe: one connection is driven by one loop.
/// </remarks>
public interface IUsbBulkDevice : IDisposable
{
    /// <summary>What was opened.</summary>
    UsbDeviceInfo Info { get; }

    /// <summary>The claimed endpoints.</summary>
    UsbBulkEndpoints Endpoints { get; }

    /// <summary>Whether the handle is still believed valid.</summary>
    bool IsOpen { get; }

    /// <summary>Writes to the bulk OUT endpoint.</summary>
    /// <returns>How many bytes the device accepted.</returns>
    /// <exception cref="PanelConnectionException">The transfer failed.</exception>
    int Write(ReadOnlySpan<byte> data, int timeoutMilliseconds = 5000);

    /// <summary>Reads from the bulk IN endpoint.</summary>
    /// <returns>How many bytes were read.</returns>
    /// <exception cref="PanelConnectionException">The transfer failed, or the device has no IN endpoint.</exception>
    int Read(Span<byte> buffer, int timeoutMilliseconds = 5000);

    /// <summary>Clears a stall condition on an endpoint.</summary>
    void ClearHalt(byte endpoint);

    /// <summary>
    /// Resets the device's USB port. The handle is unusable afterwards and the
    /// device must be opened again.
    /// </summary>
    void Reset()
    {
    }
}

/// <summary>An open USB HID device.</summary>
public interface IUsbHidDevice : IDisposable
{
    /// <summary>What was opened.</summary>
    UsbDeviceInfo Info { get; }

    /// <summary>Whether the handle is still believed valid.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// The largest output report this device accepts, including the report id
    /// byte.
    /// </summary>
    int OutputReportLength { get; }

    /// <summary>The largest input report this device sends, including the report id byte.</summary>
    int InputReportLength { get; }

    /// <summary>Writes one output report. The first byte is the report id.</summary>
    /// <exception cref="PanelConnectionException">The write failed.</exception>
    void Write(ReadOnlySpan<byte> report);

    /// <summary>Reads one input report.</summary>
    /// <returns>How many bytes were read, or <c>0</c> on timeout.</returns>
    int Read(Span<byte> buffer, int timeoutMilliseconds = 2000);
}

/// <summary>An open serial port.</summary>
public interface ISerialDevice : IDisposable
{
    /// <summary>The port name, such as <c>COM5</c> or <c>/dev/ttyACM0</c>.</summary>
    string PortName { get; }

    /// <summary>Whether the port is open.</summary>
    bool IsOpen { get; }

    /// <summary>Writes bytes to the port.</summary>
    /// <exception cref="PanelConnectionException">The write failed.</exception>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes, or as many as arrive before the timeout.</summary>
    /// <returns>How many bytes were read.</returns>
    int Read(Span<byte> buffer, int timeoutMilliseconds = 2000);

    /// <summary>Discards anything buffered on the receive side.</summary>
    void DiscardInput();

    /// <summary>Blocks until everything written has left the port.</summary>
    void Flush();
}

/// <summary>Finds USB devices and opens them.</summary>
/// <remarks>
/// Abstracted so that discovery can be exercised against a scripted device list
/// in tests, and so that the Windows WinUSB backend and the libusb backend can
/// be selected per platform without the drivers noticing.
/// </remarks>
public interface IUsbDeviceAccess
{
    /// <summary>Whether this backend can run on the current machine.</summary>
    bool IsAvailable { get; }

    /// <summary>A short description of the backend, for diagnostics.</summary>
    string BackendName { get; }

    /// <summary>Lists attached USB devices, optionally filtered by vendor and product id.</summary>
    IReadOnlyList<UsbDeviceInfo> Enumerate(IReadOnlySet<(int VendorId, int ProductId)>? filter = null);

    /// <summary>
    /// Opens a device for bulk transfers, claiming the first interface that has
    /// the endpoints a panel needs.
    /// </summary>
    /// <exception cref="PanelConnectionException">The device could not be opened or claimed.</exception>
    IUsbBulkDevice OpenBulk(UsbDeviceInfo device, bool requireInputEndpoint = true);

    /// <summary>Opens a device as HID.</summary>
    /// <exception cref="PanelConnectionException">The device could not be opened.</exception>
    IUsbHidDevice OpenHid(UsbDeviceInfo device);
}
