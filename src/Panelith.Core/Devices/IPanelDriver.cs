using Panelith.Imaging;

namespace Panelith.Devices;

/// <summary>A panel model found on the bus right now.</summary>
/// <param name="Descriptor">What the panel is and how to talk to it.</param>
/// <param name="Location">Where it is and how to find it again.</param>
/// <param name="DriverId">Which driver reported it.</param>
/// <param name="Confidence">How sure the driver is about <paramref name="Descriptor"/>.</param>
public sealed record DetectedPanel(
    PanelDescriptor Descriptor,
    PanelLocation Location,
    string DriverId,
    PanelMatchConfidence Confidence = PanelMatchConfidence.Confirmed)
{
    /// <summary>Key that survives reconnection, used to pin configuration to this unit.</summary>
    public string StableKey => Location.StableKey;

    /// <summary>A one-line summary for the device list.</summary>
    public string Describe() => $"{Descriptor.FullName} ({Descriptor.Width}x{Descriptor.Height}) at {Location.Describe()}";
}

/// <summary>How a detected panel was identified.</summary>
public enum PanelMatchConfidence
{
    /// <summary>
    /// The USB ids matched a known profile, but the panel was not asked to
    /// confirm its own resolution. Resolution may be wrong for a model that
    /// ships several screen sizes behind one product id.
    /// </summary>
    IdMatch = 0,

    /// <summary>
    /// The panel answered a handshake and reported its own screen geometry, so
    /// the descriptor reflects the hardware rather than a lookup table.
    /// </summary>
    Confirmed,

    /// <summary>
    /// The device was described by hand in a user-supplied profile. Treated as
    /// authoritative, because the user knows better than any heuristic.
    /// </summary>
    UserDefined,
}

/// <summary>
/// Finds panels of one family on the bus and opens connections to them.
/// </summary>
/// <remarks>
/// One driver covers a wire protocol family rather than a single model: the
/// Thermalright <c>ly</c> driver, for instance, handles every Trofeo Vision size
/// because they differ only in the geometry the handshake reports.
/// </remarks>
public interface IPanelDriver
{
    /// <summary>Stable driver id, such as <c>thermalright.ly</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable driver name.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Enumerates panels this driver recognises that are attached right now.
    /// </summary>
    /// <remarks>
    /// Discovery must be non-destructive and must not disturb a panel another
    /// process is driving: a user running the vendor tool alongside Panelith
    /// should see their panel keep working until they deliberately switch over.
    /// </remarks>
    ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a connection to a previously discovered panel.</summary>
    /// <exception cref="PanelConnectionException">The panel could not be opened.</exception>
    ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Options applied when opening a panel.</summary>
/// <param name="Orientation">How the image should be rotated.</param>
/// <param name="Brightness">Initial brightness percentage, or <see langword="null"/> to leave it alone.</param>
/// <param name="JpegQuality">Quality for panels whose payload is JPEG.</param>
/// <param name="FlipHorizontally">Mirror the image, for panels mounted behind glass or in a mirror bracket.</param>
public readonly record struct PanelOpenOptions(
    PanelOrientation Orientation = PanelOrientation.Portrait,
    int? Brightness = null,
    int JpegQuality = 85,
    bool FlipHorizontally = false)
{
    /// <summary>Default options: native orientation, untouched brightness.</summary>
    public static PanelOpenOptions Default { get; } = new();
}

/// <summary>
/// An open connection to one panel.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe. The engine owns one connection per
/// panel and drives it from a single loop.
/// </remarks>
public interface IPanelConnection : IAsyncDisposable
{
    /// <summary>What this connection is talking to.</summary>
    PanelDescriptor Descriptor { get; }

    /// <summary>Where the panel is.</summary>
    PanelLocation Location { get; }

    /// <summary>Whether the link is believed to be usable.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// The size a frame must be to be accepted, accounting for orientation.
    /// </summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>
    /// Sends one full frame.
    /// </summary>
    /// <param name="frame">
    /// Pixels matching <see cref="FrameSize"/>. The connection does not take
    /// ownership; the caller may reuse the buffer once this completes.
    /// </param>
    /// <param name="cancellationToken">Cancels a transfer that is taking too long.</param>
    /// <exception cref="PanelConnectionException">The transfer failed and the link should be considered lost.</exception>
    ValueTask SendFrameAsync(FrameBuffer frame, CancellationToken cancellationToken = default);

    /// <summary>Sets backlight brightness as a percentage, 0 to 100.</summary>
    ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default);

    /// <summary>Turns the backlight on or off without closing the connection.</summary>
    ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default);

    /// <summary>Blanks the panel.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
