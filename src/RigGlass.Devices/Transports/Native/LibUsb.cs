using System.Runtime.InteropServices;

namespace RigGlass.Devices.Transports.Native;

/// <summary>libusb-1.0 result codes.</summary>
internal enum LibUsbError
{
    Success = 0,
    Io = -1,
    InvalidParam = -2,
    Access = -3,
    NoDevice = -4,
    NotFound = -5,
    Busy = -6,
    Timeout = -7,
    Overflow = -8,
    Pipe = -9,
    Interrupted = -10,
    NoMemory = -11,
    NotSupported = -12,
    Other = -99,
}

/// <summary>USB interface class codes this project cares about.</summary>
internal static class UsbInterfaceClass
{
    internal const byte MassStorage = 0x08;
    internal const byte Hid = 0x03;
    internal const byte VendorSpecific = 0xFF;
    internal const byte CdcData = 0x0A;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbDeviceDescriptor
{
    internal byte Length;
    internal byte DescriptorType;
    internal ushort BcdUsb;
    internal byte DeviceClass;
    internal byte DeviceSubClass;
    internal byte DeviceProtocol;
    internal byte MaxPacketSize0;
    internal ushort IdVendor;
    internal ushort IdProduct;
    internal ushort BcdDevice;
    internal byte IManufacturer;
    internal byte IProduct;
    internal byte ISerialNumber;
    internal byte NumConfigurations;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbConfigDescriptor
{
    internal byte Length;
    internal byte DescriptorType;
    internal ushort TotalLength;
    internal byte NumInterfaces;
    internal byte ConfigurationValue;
    internal byte IConfiguration;
    internal byte Attributes;
    internal byte MaxPower;
    internal nint Interfaces;
    internal nint Extra;
    internal int ExtraLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbInterface
{
    internal nint AltSetting;
    internal int NumAltSetting;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbInterfaceDescriptor
{
    internal byte Length;
    internal byte DescriptorType;
    internal byte InterfaceNumber;
    internal byte AlternateSetting;
    internal byte NumEndpoints;
    internal byte InterfaceClass;
    internal byte InterfaceSubClass;
    internal byte InterfaceProtocol;
    internal byte IInterface;
    internal nint Endpoints;
    internal nint Extra;
    internal int ExtraLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LibUsbEndpointDescriptor
{
    internal byte Length;
    internal byte DescriptorType;
    internal byte EndpointAddress;
    internal byte Attributes;
    internal ushort MaxPacketSize;
    internal byte Interval;
    internal byte Refresh;
    internal byte SynchAddress;
    internal nint Extra;
    internal int ExtraLength;

    internal bool IsBulk => (Attributes & 0x03) == 0x02;

    internal bool IsIn => (EndpointAddress & 0x80) != 0;
}

/// <summary>
/// Raw bindings to libusb-1.0.
/// </summary>
/// <remarks>
/// <para>
/// libusb is loaded by soname rather than by package, because every platform
/// ships it under a different file name and, on Windows, it is normally
/// side-loaded next to the application. <see cref="Resolver"/> tries the
/// plausible names in turn so a user who installed libusb through their package
/// manager, through Homebrew, or by dropping a DLL beside the executable all
/// end up working without configuration.
/// </para>
/// <para>
/// Nothing here throws. Callers translate result codes into
/// <see cref="PanelConnectionException"/> so that the cause survives as a
/// <see cref="PanelFailureReason"/> rather than as prose.
/// </para>
/// </remarks>
internal static partial class LibUsb
{
    internal const string LibraryName = "libusb-1.0";

    private static readonly string[] CandidateNames =
    [
        "libusb-1.0.so.0",
        "libusb-1.0.so",
        "libusb-1.0.dylib",
        "libusb-1.0.0.dylib",
        "libusb-1.0.dll",
        "libusb.dll",
    ];

    private static int _resolverInstalled;

    /// <summary>
    /// Installs the import resolver. Safe to call repeatedly and from several
    /// threads; only the first call registers.
    /// </summary>
    internal static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(LibUsb).Assembly, Resolver);
    }

    private static nint Resolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return nint.Zero;
        }

        foreach (var candidate in CandidateNames)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    /// <summary>Whether libusb can be loaded on this machine.</summary>
    internal static bool TryProbe(out string? failure)
    {
        EnsureResolver();

        try
        {
            var result = (LibUsbError)Init(out var context);
            if (result != LibUsbError.Success)
            {
                failure = $"libusb_init failed: {Describe(result)}";
                return false;
            }

            Exit(context);
            failure = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            failure = "libusb-1.0 is not installed.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            failure = $"The installed libusb-1.0 is too old: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException)
        {
            failure = "The installed libusb-1.0 has the wrong architecture for this process.";
            return false;
        }
    }

    /// <summary>Turns a result code into text, preferring libusb's own wording.</summary>
    internal static string Describe(LibUsbError error)
    {
        try
        {
            var text = Marshal.PtrToStringAnsi(StrError((int)error));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return $"{text} ({(int)error})";
            }
        }
        catch (DllNotFoundException)
        {
            // Fall through to the managed description.
        }
        catch (EntryPointNotFoundException)
        {
            // Fall through to the managed description.
        }

        return $"{error} ({(int)error})";
    }

    /// <summary>Maps a libusb result code onto the cause taxonomy the supervisor acts on.</summary>
    internal static PanelFailureReason ToFailureReason(LibUsbError error) => error switch
    {
        LibUsbError.NoDevice or LibUsbError.NotFound => PanelFailureReason.Disconnected,
        LibUsbError.Access => PanelFailureReason.AccessDenied,
        LibUsbError.Busy => PanelFailureReason.Busy,
        LibUsbError.Timeout => PanelFailureReason.Timeout,
        LibUsbError.NotSupported => PanelFailureReason.Unsupported,
        LibUsbError.Pipe or LibUsbError.Overflow => PanelFailureReason.ProtocolError,
        _ => PanelFailureReason.Unknown,
    };

    [LibraryImport(LibraryName, EntryPoint = "libusb_init")]
    internal static partial int Init(out nint context);

    [LibraryImport(LibraryName, EntryPoint = "libusb_exit")]
    internal static partial void Exit(nint context);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_device_list")]
    internal static partial nint GetDeviceList(nint context, out nint list);

    [LibraryImport(LibraryName, EntryPoint = "libusb_free_device_list")]
    internal static partial void FreeDeviceList(nint list, int unrefDevices);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_device_descriptor")]
    internal static partial int GetDeviceDescriptor(nint device, out LibUsbDeviceDescriptor descriptor);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_bus_number")]
    internal static partial byte GetBusNumber(nint device);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_device_address")]
    internal static partial byte GetDeviceAddress(nint device);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_port_number")]
    internal static partial byte GetPortNumber(nint device);

    [LibraryImport(LibraryName, EntryPoint = "libusb_ref_device")]
    internal static partial nint RefDevice(nint device);

    [LibraryImport(LibraryName, EntryPoint = "libusb_unref_device")]
    internal static partial void UnrefDevice(nint device);

    [LibraryImport(LibraryName, EntryPoint = "libusb_open")]
    internal static partial int Open(nint device, out nint handle);

    [LibraryImport(LibraryName, EntryPoint = "libusb_close")]
    internal static partial void Close(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_string_descriptor_ascii")]
    internal static unsafe partial int GetStringDescriptorAscii(nint handle, byte index, byte* data, int length);

    [LibraryImport(LibraryName, EntryPoint = "libusb_get_active_config_descriptor")]
    internal static partial int GetActiveConfigDescriptor(nint device, out nint config);

    [LibraryImport(LibraryName, EntryPoint = "libusb_free_config_descriptor")]
    internal static partial void FreeConfigDescriptor(nint config);

    [LibraryImport(LibraryName, EntryPoint = "libusb_kernel_driver_active")]
    internal static partial int KernelDriverActive(nint handle, int interfaceNumber);

    [LibraryImport(LibraryName, EntryPoint = "libusb_detach_kernel_driver")]
    internal static partial int DetachKernelDriver(nint handle, int interfaceNumber);

    [LibraryImport(LibraryName, EntryPoint = "libusb_attach_kernel_driver")]
    internal static partial int AttachKernelDriver(nint handle, int interfaceNumber);

    [LibraryImport(LibraryName, EntryPoint = "libusb_set_auto_detach_kernel_driver")]
    internal static partial int SetAutoDetachKernelDriver(nint handle, int enable);

    [LibraryImport(LibraryName, EntryPoint = "libusb_claim_interface")]
    internal static partial int ClaimInterface(nint handle, int interfaceNumber);

    [LibraryImport(LibraryName, EntryPoint = "libusb_release_interface")]
    internal static partial int ReleaseInterface(nint handle, int interfaceNumber);

    [LibraryImport(LibraryName, EntryPoint = "libusb_bulk_transfer")]
    internal static unsafe partial int BulkTransfer(
        nint handle,
        byte endpoint,
        byte* data,
        int length,
        out int transferred,
        uint timeoutMilliseconds);

    [LibraryImport(LibraryName, EntryPoint = "libusb_clear_halt")]
    internal static partial int ClearHalt(nint handle, byte endpoint);

    [LibraryImport(LibraryName, EntryPoint = "libusb_reset_device")]
    internal static partial int ResetDevice(nint handle);

    [LibraryImport(LibraryName, EntryPoint = "libusb_strerror")]
    internal static partial nint StrError(int errorCode);
}
