using Panelith.Devices.Transports;

namespace Panelith.Devices.Drivers;

/// <summary>
/// Reports panels that Panelith can identify but cannot yet drive.
/// </summary>
/// <remarks>
/// <para>
/// Some panels are recognisable from their USB descriptors long before their
/// protocol is understood - the Turing 8.8" line obfuscates its frame packets,
/// and the BeadaPanel and WeAct families are only partly documented. The
/// tempting option is to leave them out of the catalogue entirely, but then a
/// user with one of those panels sees an empty device list and no explanation,
/// which reads as "this software is broken".
/// </para>
/// <para>
/// Listing them with an honest explanation is better. The panel appears in
/// <c>panelith devices</c> with its support status, opening it fails with a
/// message naming the reason, and the user knows to look for progress rather
/// than to debug their cable.
/// </para>
/// </remarks>
public sealed class RecognizedOnlyDriver : UsbProfileDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "recognized-only";

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    public RecognizedOnlyDriver(PanelDriverContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public override string Id => DriverId;

    /// <inheritdoc />
    public override string DisplayName => "Recognised but unsupported panels";

    /// <inheritdoc />
    protected override IUsbDeviceAccess Access => Context.BulkAccess;

    /// <inheritdoc />
    public override ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var profile = ProfileFor(panel);
        var reason = profile.Notes is { Length: > 0 } notes
            ? $" {notes}"
            : string.Empty;

        throw new PanelConnectionException(
            $"Panelith recognises {profile.FullName} but cannot drive it yet.{reason} "
            + "Protocol notes and captures are very welcome: see docs/contributing.md.",
            PanelFailureReason.Unsupported);
    }
}
