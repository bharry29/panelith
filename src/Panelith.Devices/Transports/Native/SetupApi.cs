using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Panelith.Devices.Transports.Native;

/// <summary>
/// The slice of setupapi and advapi32 needed to map a Windows COM port back to
/// the USB device behind it.
/// </summary>
/// <remarks>
/// Windows does not expose the USB ids of a serial port through
/// <c>System.IO.Ports</c>, and the managed alternatives - WMI or the registry
/// facade - each pull in a package that would not otherwise be referenced. The
/// device information set is the same data those APIs read, so going straight to
/// it keeps the dependency footprint at zero.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class SetupApi
{
    internal const int DigcfPresent = 0x02;
    internal const int DicsFlagGlobal = 0x01;
    internal const int DiregDev = 0x01;
    internal const int KeyRead = 0x20019;
    internal const int SpdrpDeviceDesc = 0x00;
    internal const int SpdrpHardwareId = 0x01;
    internal const int SpdrpFriendlyName = 0x0C;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorNoMoreItems = 259;

    /// <summary>GUID_DEVCLASS_PORTS: serial and parallel ports.</summary>
    internal static readonly Guid PortsClass = new("4d36e978-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoData
    {
        internal int Size;
        internal Guid ClassGuid;
        internal int DevInst;
        internal nint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetClassDevs(in Guid classGuid, string? enumerator, nint parent, int flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDeviceInfo(nint deviceInfoSet, int index, ref DeviceInfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetDeviceRegistryProperty(
        nint deviceInfoSet,
        ref DeviceInfoData data,
        int property,
        out int propertyRegDataType,
        byte* buffer,
        int bufferSize,
        out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetDeviceInstanceId(
        nint deviceInfoSet,
        ref DeviceInfoData data,
        char* buffer,
        int bufferSize,
        out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDevRegKey")]
    internal static partial nint OpenDevRegKey(
        nint deviceInfoSet,
        ref DeviceInfoData data,
        int scope,
        int hwProfile,
        int keyType,
        int desiredAccess);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial int RegQueryValueEx(
        nint key,
        string valueName,
        nint reserved,
        out int type,
        byte* data,
        ref int dataSize);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    internal static partial int RegCloseKey(nint key);
}
