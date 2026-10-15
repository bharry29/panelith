namespace RigGlass.Devices;

/// <summary>How a panel's image is rotated relative to its native scan order.</summary>
public enum PanelOrientation
{
    /// <summary>No rotation.</summary>
    Portrait = 0,

    /// <summary>Rotated 90 degrees clockwise.</summary>
    Landscape = 90,

    /// <summary>Rotated 180 degrees.</summary>
    ReversePortrait = 180,

    /// <summary>Rotated 270 degrees clockwise.</summary>
    ReverseLandscape = 270,
}

/// <summary>Which transport a panel speaks.</summary>
public enum PanelTransportKind
{
    /// <summary>USB HID interrupt or feature reports.</summary>
    Hid = 0,

    /// <summary>Raw USB bulk endpoints on a vendor-specific interface.</summary>
    UsbBulk,

    /// <summary>USB mass-storage Bulk-Only Transport with vendor SCSI opcodes.</summary>
    UsbMassStorage,

    /// <summary>A USB-to-serial bridge presenting a COM port or tty.</summary>
    Serial,

    /// <summary>No hardware: renders to disk or memory for previews and tests.</summary>
    Virtual,
}

/// <summary>
/// What a panel can do beyond accepting frames. Drives which controls the UI
/// offers and which optimisations the engine may use.
/// </summary>
/// <param name="SupportsBrightness">Brightness can be set from software.</param>
/// <param name="SupportsScreenPower">The backlight can be switched off without disconnecting.</param>
/// <param name="SupportsPartialUpdate">A sub-rectangle can be updated without redrawing the whole frame.</param>
/// <param name="SupportsHardwareOrientation">The panel itself can rotate, so software need not.</param>
/// <param name="MaxFramesPerSecond">
/// The highest full-frame rate the link can sustain. Measured rather than
/// claimed: see docs/device-support.md.
/// </param>
/// <param name="RequiresExclusiveAccess">
/// The device cannot be shared, so the vendor tool must be closed first.
/// </param>
public readonly record struct PanelCapabilities(
    bool SupportsBrightness = false,
    bool SupportsScreenPower = false,
    bool SupportsPartialUpdate = false,
    bool SupportsHardwareOrientation = false,
    double MaxFramesPerSecond = 10d,
    bool RequiresExclusiveAccess = true)
{
    /// <summary>Capabilities of a panel that can only accept whole frames.</summary>
    public static PanelCapabilities Minimal { get; } = new();
}

/// <summary>
/// Identifies a panel model and the wire format it speaks: everything needed to
/// drive it, independent of whether a unit is currently plugged in.
/// </summary>
/// <param name="ProfileId">
/// Stable id of the profile this descriptor came from, such as
/// <c>thermalright.trofeo-vision-6.86</c>.
/// </param>
/// <param name="Vendor">Vendor name as users know it, such as <c>Thermalright</c>.</param>
/// <param name="Model">Model name as users know it, such as <c>Trofeo Vision 6.86"</c>.</param>
/// <param name="Width">Native panel width in pixels, before orientation is applied.</param>
/// <param name="Height">Native panel height in pixels, before orientation is applied.</param>
/// <param name="Transport">Which transport the panel speaks.</param>
/// <param name="PixelFormat">The payload layout the panel expects.</param>
/// <param name="Capabilities">What the panel can do beyond accepting frames.</param>
public sealed record PanelDescriptor(
    string ProfileId,
    string Vendor,
    string Model,
    int Width,
    int Height,
    PanelTransportKind Transport,
    PanelPixelFormat PixelFormat,
    PanelCapabilities Capabilities)
{
    /// <summary>Vendor and model, for display.</summary>
    public string FullName => $"{Vendor} {Model}";

    /// <summary>Native pixel count.</summary>
    public int PixelCount => Width * Height;

    /// <summary>
    /// The dimensions a theme should be rendered at for a given orientation,
    /// with width and height swapped for the quarter-turn orientations.
    /// </summary>
    public (int Width, int Height) RenderSize(PanelOrientation orientation) =>
        orientation is PanelOrientation.Landscape or PanelOrientation.ReverseLandscape
            ? (Height, Width)
            : (Width, Height);
}
