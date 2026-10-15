using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Transports;
using RigGlass.Imaging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// The services every driver needs, gathered in one place.
/// </summary>
/// <remarks>
/// Passed as a single object rather than as six constructor parameters because
/// drivers are also constructed in tests, where every one of these is replaced
/// with a fake, and because adding a service later should not change every
/// driver's signature.
/// </remarks>
public sealed class PanelDriverContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="profiles">The panel catalogue.</param>
    /// <param name="codec">JPEG and PNG codec.</param>
    /// <param name="bulkAccess">Backend for raw bulk USB, normally libusb.</param>
    /// <param name="hidAccess">Backend for HID, normally HidSharp.</param>
    /// <param name="serialPorts">Backend for serial ports.</param>
    /// <param name="loggerFactory">Log sink.</param>
    /// <param name="timeProvider">Clock, so that retry and delay behaviour is testable.</param>
    public PanelDriverContext(
        PanelProfileRegistry profiles,
        IImageCodec codec,
        IUsbDeviceAccess bulkAccess,
        IUsbDeviceAccess hidAccess,
        ISerialPortEnumerator serialPorts,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        BulkAccess = bulkAccess ?? throw new ArgumentNullException(nameof(bulkAccess));
        HidAccess = hidAccess ?? throw new ArgumentNullException(nameof(hidAccess));
        SerialPorts = serialPorts ?? throw new ArgumentNullException(nameof(serialPorts));
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The panel catalogue.</summary>
    public PanelProfileRegistry Profiles { get; }

    /// <summary>JPEG and PNG codec.</summary>
    public IImageCodec Codec { get; }

    /// <summary>Backend for raw bulk USB.</summary>
    public IUsbDeviceAccess BulkAccess { get; }

    /// <summary>Backend for HID.</summary>
    public IUsbDeviceAccess HidAccess { get; }

    /// <summary>Backend for serial ports.</summary>
    public ISerialPortEnumerator SerialPorts { get; }

    /// <summary>Log sink.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Clock used for startup delays and retry backoff.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Creates a logger for a driver.</summary>
    public ILogger LoggerFor<T>() => LoggerFactory.CreateLogger<T>();
}
