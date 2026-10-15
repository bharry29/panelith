using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OmniPanel.Devices.Transports.Native;

namespace OmniPanel.Devices.Transports;

/// <summary>
/// Bulk USB access through libusb-1.0.
/// </summary>
/// <remarks>
/// <para>
/// This is the portable backend: the same code drives a panel on Linux, macOS
/// and Windows, the only difference being which kernel driver has to be moved
/// out of the way first. On Linux libusb detaches <c>usb-storage</c> or
/// <c>usbhid</c> automatically when the process has permission; on Windows the
/// device must already be bound to a WinUSB-class driver, which is what the
/// vendor installers do and what <c>omnipanel doctor</c> explains how to arrange
/// for devices they do not cover.
/// </para>
/// <para>
/// Each operation uses its own libusb context rather than a shared one. A
/// context is cheap next to discovery's natural cadence of seconds, and keeping
/// them separate removes a whole class of shutdown-ordering bugs where a
/// context is torn down while a panel still holds a handle inside it.
/// </para>
/// </remarks>
public sealed class LibUsbDeviceAccess : IUsbDeviceAccess
{
    private readonly ILogger? _logger;
    private readonly Lazy<string?> _probeFailure;

    /// <summary>Creates the backend.</summary>
    /// <param name="logger">Optional log sink.</param>
    public LibUsbDeviceAccess(ILogger<LibUsbDeviceAccess>? logger = null)
    {
        _logger = logger;
        _probeFailure = new Lazy<string?>(
            () => LibUsb.TryProbe(out var failure) ? null : failure,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool IsAvailable => _probeFailure.Value is null;

    /// <inheritdoc />
    public string BackendName => "libusb-1.0";

    /// <summary>
    /// Why the backend is unavailable, or <see langword="null"/> when it works.
    /// Shown by <c>omnipanel doctor</c>.
    /// </summary>
    public string? UnavailableReason => _probeFailure.Value;

    /// <inheritdoc />
    public IReadOnlyList<UsbDeviceInfo> Enumerate(IReadOnlySet<(int VendorId, int ProductId)>? filter = null)
    {
        if (!IsAvailable)
        {
            return [];
        }

        var context = CreateContext();
        try
        {
            var count = (long)LibUsb.GetDeviceList(context, out var list);
            if (count < 0)
            {
                _logger?.LogDebug("libusb_get_device_list failed: {Error}", LibUsb.Describe((LibUsbError)count));
                return [];
            }

            try
            {
                var found = new List<UsbDeviceInfo>();
                for (long i = 0; i < count; i++)
                {
                    var device = Marshal.ReadIntPtr(list, (int)(i * nint.Size));
                    if (device == nint.Zero)
                    {
                        continue;
                    }

                    if (LibUsb.GetDeviceDescriptor(device, out var descriptor) != 0)
                    {
                        continue;
                    }

                    if (filter is not null && !filter.Contains((descriptor.IdVendor, descriptor.IdProduct)))
                    {
                        continue;
                    }

                    found.Add(ReadDeviceInfo(device, descriptor));
                }

                return found;
            }
            finally
            {
                LibUsb.FreeDeviceList(list, unrefDevices: 1);
            }
        }
        finally
        {
            LibUsb.Exit(context);
        }
    }

    /// <inheritdoc />
    public IUsbBulkDevice OpenBulk(UsbDeviceInfo device, bool requireInputEndpoint = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!IsAvailable)
        {
            throw new PanelConnectionException(
                $"libusb is not usable on this machine: {UnavailableReason}",
                PanelFailureReason.Unsupported);
        }

        var context = CreateContext();
        var handleOwned = false;
        var handle = nint.Zero;
        var claimedInterface = -1;

        try
        {
            handle = OpenHandle(context, device, out var nativeDevice);
            handleOwned = true;

            // Unsupported on Windows, which is not an error: there is no kernel
            // driver to detach there, only a bound WinUSB stack.
            _ = LibUsb.SetAutoDetachKernelDriver(handle, 1);

            UsbBulkEndpoints? discovered;
            try
            {
                discovered = FindBulkEndpoints(nativeDevice, requireInputEndpoint);
            }
            finally
            {
                // The open handle holds its own reference, so the one taken
                // during the search is released here.
                LibUsb.UnrefDevice(nativeDevice);
            }

            var endpoints = discovered
                ?? throw new PanelConnectionException(
                    $"{device.Describe()} has no bulk endpoints to drive a panel with.",
                    PanelFailureReason.Unsupported);

            Claim(handle, endpoints.InterfaceNumber, device);
            claimedInterface = endpoints.InterfaceNumber;

            var opened = new LibUsbBulkDevice(context, handle, endpoints, device, _logger);
            handleOwned = false;
            claimedInterface = -1;
            return opened;
        }
        catch
        {
            if (claimedInterface >= 0)
            {
                _ = LibUsb.ReleaseInterface(handle, claimedInterface);
            }

            if (handleOwned)
            {
                LibUsb.Close(handle);
            }

            LibUsb.Exit(context);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// libusb can drive HID devices, but only by displacing the system HID
    /// stack, which breaks any panel that also presents a keyboard or a
    /// multimedia interface. HID panels are therefore opened through
    /// <see cref="HidSharpDeviceAccess"/> instead.
    /// </remarks>
    public IUsbHidDevice OpenHid(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        throw new PanelConnectionException(
            "The libusb backend does not open HID devices; use the HID backend instead.",
            PanelFailureReason.Unsupported);
    }

    /// <summary>
    /// Releases the kernel driver claim on a device so another application can
    /// take it back, used when switching a panel away from OmniPanel.
    /// </summary>
    public void ReattachKernelDriver(UsbDeviceInfo device, byte interfaceNumber)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!IsAvailable || !OperatingSystem.IsLinux())
        {
            return;
        }

        var context = CreateContext();
        try
        {
            var handle = OpenHandle(context, device, out var nativeDevice);
            try
            {
                _ = LibUsb.AttachKernelDriver(handle, interfaceNumber);
            }
            finally
            {
                LibUsb.UnrefDevice(nativeDevice);
                LibUsb.Close(handle);
            }
        }
        catch (PanelConnectionException)
        {
            // Best effort: the panel is already gone or was never detached.
        }
        finally
        {
            LibUsb.Exit(context);
        }
    }

    private static nint CreateContext()
    {
        LibUsb.EnsureResolver();

        var result = (LibUsbError)LibUsb.Init(out var context);
        return result == LibUsbError.Success
            ? context
            : throw new PanelConnectionException(
                $"Could not initialise libusb: {LibUsb.Describe(result)}",
                LibUsb.ToFailureReason(result));
    }

    private static UsbDeviceInfo ReadDeviceInfo(nint device, LibUsbDeviceDescriptor descriptor)
    {
        string? manufacturer = null;
        string? product = null;
        string? serial = null;

        // Reading string descriptors needs a handle. Opening without claiming an
        // interface does not disturb whichever process is driving the panel, so
        // discovery stays non-destructive, but it can still fail on a device the
        // current user has no permission for - in which case the identity falls
        // back to the bus address.
        if (LibUsb.Open(device, out var handle) == 0)
        {
            try
            {
                manufacturer = ReadStringDescriptor(handle, descriptor.IManufacturer);
                product = ReadStringDescriptor(handle, descriptor.IProduct);
                serial = ReadStringDescriptor(handle, descriptor.ISerialNumber);
            }
            finally
            {
                LibUsb.Close(handle);
            }
        }

        return new UsbDeviceInfo(
            descriptor.IdVendor,
            descriptor.IdProduct,
            LibUsb.GetBusNumber(device),
            LibUsb.GetDeviceAddress(device),
            serial,
            manufacturer,
            product,
            DevicePath: null);
    }

    private static unsafe string? ReadStringDescriptor(nint handle, byte index)
    {
        if (index == 0)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[256];
        int length;
        fixed (byte* pointer = buffer)
        {
            length = LibUsb.GetStringDescriptorAscii(handle, index, pointer, buffer.Length);
        }

        if (length <= 0)
        {
            return null;
        }

        var text = System.Text.Encoding.ASCII.GetString(buffer[..length]).Trim();
        return text.Length == 0 ? null : text;
    }

    private static nint OpenHandle(nint context, UsbDeviceInfo wanted, out nint nativeDevice)
    {
        var count = (long)LibUsb.GetDeviceList(context, out var list);
        if (count < 0)
        {
            throw new PanelConnectionException(
                $"Could not list USB devices: {LibUsb.Describe((LibUsbError)count)}",
                PanelFailureReason.Unknown);
        }

        var matched = nint.Zero;
        try
        {
            for (long i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(list, (int)(i * nint.Size));
                if (device == nint.Zero || LibUsb.GetDeviceDescriptor(device, out var descriptor) != 0)
                {
                    continue;
                }

                if (descriptor.IdVendor != wanted.VendorId || descriptor.IdProduct != wanted.ProductId)
                {
                    continue;
                }

                // Bus and address pin the exact unit when two identical panels
                // are attached; without them any match of the right model will do.
                if (wanted.BusNumber is { } bus && LibUsb.GetBusNumber(device) != bus)
                {
                    continue;
                }

                if (wanted.DeviceAddress is { } address && LibUsb.GetDeviceAddress(device) != address)
                {
                    continue;
                }

                matched = LibUsb.RefDevice(device);
                break;
            }
        }
        finally
        {
            LibUsb.FreeDeviceList(list, unrefDevices: 1);
        }

        if (matched == nint.Zero)
        {
            throw new PanelConnectionException(
                $"{wanted.Describe()} is no longer attached.",
                PanelFailureReason.Disconnected);
        }

        var result = (LibUsbError)LibUsb.Open(matched, out var handle);
        if (result != LibUsbError.Success)
        {
            LibUsb.UnrefDevice(matched);
            throw new PanelConnectionException(
                $"Could not open {wanted.Describe()}: {LibUsb.Describe(result)}",
                LibUsb.ToFailureReason(result));
        }

        nativeDevice = matched;
        return handle;
    }

    private static UsbBulkEndpoints? FindBulkEndpoints(nint device, bool requireInputEndpoint)
    {
        if (LibUsb.GetActiveConfigDescriptor(device, out var configPointer) != 0)
        {
            return null;
        }

        try
        {
            var config = Marshal.PtrToStructure<LibUsbConfigDescriptor>(configPointer);
            UsbBulkEndpoints? fallback = null;

            for (var i = 0; i < config.NumInterfaces; i++)
            {
                var interfacePointer = config.Interfaces + (i * Marshal.SizeOf<LibUsbInterface>());
                var usbInterface = Marshal.PtrToStructure<LibUsbInterface>(interfacePointer);

                for (var alt = 0; alt < usbInterface.NumAltSetting; alt++)
                {
                    var altPointer = usbInterface.AltSetting + (alt * Marshal.SizeOf<LibUsbInterfaceDescriptor>());
                    var descriptor = Marshal.PtrToStructure<LibUsbInterfaceDescriptor>(altPointer);

                    var candidate = FindEndpointsOn(descriptor);
                    if (candidate is not { } endpoints)
                    {
                        continue;
                    }

                    if (requireInputEndpoint && endpoints.EndpointIn is null)
                    {
                        fallback ??= endpoints;
                        continue;
                    }

                    return endpoints;
                }
            }

            return requireInputEndpoint ? null : fallback;
        }
        finally
        {
            LibUsb.FreeConfigDescriptor(configPointer);
        }
    }

    private static UsbBulkEndpoints? FindEndpointsOn(LibUsbInterfaceDescriptor descriptor)
    {
        byte? endpointOut = null;
        byte? endpointIn = null;
        var maxPacketSize = 512;

        for (var i = 0; i < descriptor.NumEndpoints; i++)
        {
            var pointer = descriptor.Endpoints + (i * Marshal.SizeOf<LibUsbEndpointDescriptor>());
            var endpoint = Marshal.PtrToStructure<LibUsbEndpointDescriptor>(pointer);

            if (!endpoint.IsBulk)
            {
                continue;
            }

            if (endpoint.IsIn)
            {
                endpointIn ??= endpoint.EndpointAddress;
            }
            else if (endpointOut is null)
            {
                endpointOut = endpoint.EndpointAddress;
                maxPacketSize = endpoint.MaxPacketSize == 0 ? 512 : endpoint.MaxPacketSize;
            }
        }

        return endpointOut is { } outAddress
            ? new UsbBulkEndpoints(
                descriptor.InterfaceNumber,
                outAddress,
                endpointIn,
                maxPacketSize,
                descriptor.InterfaceClass)
            : null;
    }

    private static void Claim(nint handle, byte interfaceNumber, UsbDeviceInfo device)
    {
        var result = (LibUsbError)LibUsb.ClaimInterface(handle, interfaceNumber);
        if (result == LibUsbError.Success)
        {
            return;
        }

        // Auto-detach is unavailable on some kernels and on every Windows
        // backend; retry once with an explicit detach before giving up, since
        // that is the difference between "works" and "works only as root".
        if (result == LibUsbError.Busy && LibUsb.KernelDriverActive(handle, interfaceNumber) == 1)
        {
            if (LibUsb.DetachKernelDriver(handle, interfaceNumber) == 0)
            {
                result = (LibUsbError)LibUsb.ClaimInterface(handle, interfaceNumber);
                if (result == LibUsbError.Success)
                {
                    return;
                }
            }
        }

        throw new PanelConnectionException(
            $"Could not claim interface {interfaceNumber} on {device.Describe()}: {LibUsb.Describe(result)}",
            LibUsb.ToFailureReason(result));
    }
}
