namespace Panelith.Devices;

/// <summary>Why a panel operation failed, and therefore how to respond to it.</summary>
public enum PanelFailureReason
{
    /// <summary>Cause not classified.</summary>
    Unknown = 0,

    /// <summary>The device is no longer on the bus.</summary>
    Disconnected,

    /// <summary>Another process holds the device. Usually the vendor's own tool.</summary>
    Busy,

    /// <summary>
    /// The operating system refused access. On Linux this means missing udev
    /// rules; on Windows it usually means no WinUSB-class driver is bound.
    /// </summary>
    AccessDenied,

    /// <summary>A transfer did not complete in time.</summary>
    Timeout,

    /// <summary>The panel answered, but not with anything this driver understands.</summary>
    ProtocolError,

    /// <summary>The panel is still booting and should be retried shortly.</summary>
    NotReady,

    /// <summary>The request was valid but the hardware does not support it.</summary>
    Unsupported,
}

/// <summary>
/// A panel operation failed.
/// </summary>
/// <remarks>
/// Carries a <see cref="Reason"/> because the supervisor's response differs
/// sharply by cause: a <see cref="PanelFailureReason.Disconnected"/> panel
/// should be waited for silently, a <see cref="PanelFailureReason.Busy"/> one
/// needs the user to close another application, and an
/// <see cref="PanelFailureReason.AccessDenied"/> one needs a one-time
/// permissions fix that Panelith can print exact instructions for.
/// </remarks>
public sealed class PanelConnectionException : Exception
{
    /// <summary>Creates an exception describing a panel failure.</summary>
    public PanelConnectionException(
        string message,
        PanelFailureReason reason = PanelFailureReason.Unknown,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>What went wrong.</summary>
    public PanelFailureReason Reason { get; }

    /// <summary>Whether retrying the same operation could plausibly succeed.</summary>
    public bool IsTransient => Reason is PanelFailureReason.Disconnected
        or PanelFailureReason.Timeout
        or PanelFailureReason.NotReady
        or PanelFailureReason.Busy;

    /// <summary>
    /// A remedy the user can act on, or <see langword="null"/> when the failure
    /// needs no user action.
    /// </summary>
    public string? Remedy => Reason switch
    {
        PanelFailureReason.Busy =>
            "Another application is using this panel. Close the vendor software "
            + "(for example Thermalright TRCC, Turing's tool, or SignalRGB) and try again.",
        PanelFailureReason.AccessDenied when OperatingSystem.IsLinux() =>
            "Install the udev rules so your user can open the panel: "
            + "sudo panelith doctor --install-udev-rules, then unplug and replug the panel.",
        PanelFailureReason.AccessDenied =>
            "The operating system denied access to the panel. On Windows the panel needs a "
            + "WinUSB-class driver; run 'panelith doctor' for model-specific guidance.",
        PanelFailureReason.NotReady =>
            "The panel is still starting up. This resolves on its own within a few seconds.",
        _ => null,
    };
}
