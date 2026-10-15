using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RigGlass.Devices.Transports.Native;

namespace RigGlass.Devices.Transports;

/// <summary>The default serial backend, built on <see cref="SerialPort"/>.</summary>
public sealed class SerialPortEnumerator : ISerialPortEnumerator
{
    private readonly ILogger? _logger;

    /// <summary>Creates the backend.</summary>
    /// <param name="logger">Optional log sink.</param>
    public SerialPortEnumerator(ILogger<SerialPortEnumerator>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> Enumerate()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return EnumerateWindows(_logger);
            }

            if (OperatingSystem.IsLinux())
            {
                return EnumerateLinux();
            }

            return EnumerateByNameOnly();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger?.LogDebug(ex, "Serial port enumeration failed.");
            return [];
        }
    }

    /// <inheritdoc />
    public ISerialDevice Open(string portName, int baudRate, int readTimeoutMilliseconds = 2000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = readTimeoutMilliseconds,
            WriteTimeout = 5000,

            // Several bridge chips hold the panel in reset until DTR is asserted,
            // and hardware flow control on a two-wire panel link deadlocks the
            // first write, so both are set explicitly rather than left to the
            // platform default.
            DtrEnable = true,
            RtsEnable = true,
            Handshake = Handshake.None,
        };

        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new PanelConnectionException(
                $"{portName} is in use by another application.",
                PanelFailureReason.Busy,
                ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw new PanelConnectionException(
                $"Could not open {portName}: {ex.Message}",
                PanelFailureReason.Disconnected,
                ex);
        }

        return new SystemSerialDevice(port);
    }

    private static IReadOnlyList<SerialPortInfo> EnumerateByNameOnly() =>
        [.. SerialPort.GetPortNames().Order(StringComparer.Ordinal).Select(name => new SerialPortInfo(name))];

    private static IReadOnlyList<SerialPortInfo> EnumerateLinux()
    {
        const string TtyRoot = "/sys/class/tty";
        if (!Directory.Exists(TtyRoot))
        {
            return EnumerateByNameOnly();
        }

        var ports = new List<SerialPortInfo>();
        foreach (var entry in Directory.EnumerateDirectories(TtyRoot).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);

            // Only tty nodes with a backing device are real ports; the rest are
            // virtual consoles, of which a Linux box has dozens.
            var devicePath = Path.Combine(entry, "device");
            if (!Directory.Exists(devicePath) && !File.Exists(devicePath))
            {
                continue;
            }

            var portName = $"/dev/{name}";
            var (vendorId, productId, serial, product) = ReadLinuxUsbIdentity(devicePath);
            ports.Add(new SerialPortInfo(portName, vendorId, productId, serial, product));
        }

        return ports.Count == 0 ? EnumerateByNameOnly() : ports;
    }

    private static (int? VendorId, int? ProductId, string? Serial, string? Product) ReadLinuxUsbIdentity(string devicePath)
    {
        // The USB device node sits a few levels above the tty: tty -> interface
        // -> device for ttyUSB, one level further for ttyACM. Walking up until
        // idVendor appears covers both without special-casing the driver.
        var current = devicePath;
        for (var depth = 0; depth < 6; depth++)
        {
            var resolved = ResolveLink(current);
            if (resolved is null)
            {
                return (null, null, null, null);
            }

            var vendorFile = Path.Combine(resolved, "idVendor");
            if (File.Exists(vendorFile))
            {
                return (
                    ReadHexFile(vendorFile),
                    ReadHexFile(Path.Combine(resolved, "idProduct")),
                    ReadTextFile(Path.Combine(resolved, "serial")),
                    ReadTextFile(Path.Combine(resolved, "product")));
            }

            current = Path.Combine(resolved, "..");
        }

        return (null, null, null, null);
    }

    private static string? ResolveLink(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new DirectoryInfo(full);
            return info.LinkTarget is { } target
                ? Path.GetFullPath(Path.Combine(full, "..", target))
                : (info.Exists ? full : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static int? ReadHexFile(string path)
    {
        var text = ReadTextFile(path);
        return text is not null && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ReadTextFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe IReadOnlyList<SerialPortInfo> EnumerateWindows(ILogger? logger)
    {
        var known = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var set = SetupApi.GetClassDevs(SetupApi.PortsClass, null, nint.Zero, SetupApi.DigcfPresent);
        if (set == nint.Zero || set == -1)
        {
            logger?.LogDebug("SetupDiGetClassDevs returned no device information set.");
            return EnumerateByNameOnly();
        }

        var ports = new List<SerialPortInfo>();
        try
        {
            var data = new SetupApi.DeviceInfoData { Size = Marshal.SizeOf<SetupApi.DeviceInfoData>() };
            for (var index = 0; SetupApi.EnumDeviceInfo(set, index, ref data); index++)
            {
                var portName = ReadPortName(set, ref data);
                if (portName is null || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                known.Remove(portName);

                var hardwareIds = ReadMultiStringProperty(set, ref data, SetupApi.SpdrpHardwareId);
                var (vendorId, productId) = ParseUsbIds(hardwareIds);
                var description = ReadStringProperty(set, ref data, SetupApi.SpdrpFriendlyName)
                    ?? ReadStringProperty(set, ref data, SetupApi.SpdrpDeviceDesc);

                ports.Add(new SerialPortInfo(
                    portName,
                    vendorId,
                    productId,
                    ReadInstanceSerial(set, ref data),
                    description));
            }
        }
        finally
        {
            _ = SetupApi.DestroyDeviceInfoList(set);
        }

        // Anything System.IO.Ports can see but the device information set could
        // not describe is still a usable port; losing it would hide a panel.
        ports.AddRange(known.Order(StringComparer.Ordinal).Select(name => new SerialPortInfo(name)));
        return ports;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? ReadPortName(nint set, ref SetupApi.DeviceInfoData data)
    {
        var key = SetupApi.OpenDevRegKey(
            set,
            ref data,
            SetupApi.DicsFlagGlobal,
            0,
            SetupApi.DiregDev,
            SetupApi.KeyRead);

        if (key == nint.Zero || key == -1)
        {
            return null;
        }

        try
        {
            Span<byte> buffer = stackalloc byte[256];
            var size = buffer.Length;
            fixed (byte* pointer = buffer)
            {
                if (SetupApi.RegQueryValueEx(key, "PortName", nint.Zero, out _, pointer, ref size) != 0)
                {
                    return null;
                }
            }

            return DecodeUtf16(buffer[..Math.Min(size, buffer.Length)]);
        }
        finally
        {
            _ = SetupApi.RegCloseKey(key);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? ReadStringProperty(nint set, ref SetupApi.DeviceInfoData data, int property)
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* pointer = buffer)
        {
            if (!SetupApi.GetDeviceRegistryProperty(set, ref data, property, out _, pointer, buffer.Length, out var required))
            {
                return null;
            }

            return DecodeUtf16(buffer[..Math.Min(required, buffer.Length)]);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string[] ReadMultiStringProperty(nint set, ref SetupApi.DeviceInfoData data, int property)
    {
        Span<byte> buffer = stackalloc byte[1024];
        fixed (byte* pointer = buffer)
        {
            if (!SetupApi.GetDeviceRegistryProperty(set, ref data, property, out _, pointer, buffer.Length, out var required))
            {
                return [];
            }

            var text = MemoryMarshal.Cast<byte, char>(buffer[..(Math.Min(required, buffer.Length) & ~1)]);
            return [.. text.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? ReadInstanceSerial(nint set, ref SetupApi.DeviceInfoData data)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* pointer = buffer)
        {
            if (!SetupApi.GetDeviceInstanceId(set, ref data, pointer, buffer.Length, out var required))
            {
                return null;
            }

            var id = new string(pointer, 0, Math.Max(0, Math.Min(required, buffer.Length) - 1));

            // The instance id ends in either the device's own serial number or a
            // bus-position token containing '&'. Only the former identifies a
            // unit across reconnection, so the latter is deliberately dropped.
            var lastSeparator = id.LastIndexOf('\\');
            if (lastSeparator < 0 || lastSeparator == id.Length - 1)
            {
                return null;
            }

            var tail = id[(lastSeparator + 1)..];
            return tail.Contains('&', StringComparison.Ordinal) ? null : tail;
        }
    }

    private static string? DecodeUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            return null;
        }

        var text = MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]).ToString();
        var terminator = text.IndexOf('\0', StringComparison.Ordinal);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return text.Length == 0 ? null : text;
    }

    internal static (int? VendorId, int? ProductId) ParseUsbIds(IReadOnlyList<string> hardwareIds)
    {
        foreach (var id in hardwareIds)
        {
            var vendor = ExtractHexAfter(id, "VID_");
            var product = ExtractHexAfter(id, "PID_");
            if (vendor is not null && product is not null)
            {
                return (vendor, product);
            }
        }

        return (null, null);
    }

    private static int? ExtractHexAfter(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        if (start + 4 > text.Length)
        {
            return null;
        }

        return int.TryParse(
            text.AsSpan(start, 4),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}
