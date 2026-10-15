using System.Globalization;
using System.Text.Json.Serialization;

namespace Panelith.Devices.Profiles;

/// <summary>A USB vendor and product id a profile matches.</summary>
/// <param name="Vendor">Vendor id, decimal or <c>0x</c>-prefixed hex.</param>
/// <param name="Product">Product id, decimal or <c>0x</c>-prefixed hex.</param>
/// <param name="SerialNumber">
/// When set, the profile only matches a device reporting this serial number.
/// Needed where one vendor and product id covers several unrelated panels.
/// </param>
public readonly record struct UsbMatch(string Vendor, string Product, string? SerialNumber = null)
{
    /// <summary>The vendor id as a number.</summary>
    public int VendorId => ParseId(Vendor, nameof(Vendor));

    /// <summary>The product id as a number.</summary>
    public int ProductId => ParseId(Product, nameof(Product));

    private static int ParseId(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PanelProfileException($"A USB match needs a {field.ToLowerInvariant()} id.");
        }

        var text = value.Trim();
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : -1
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
                ? dec
                : -1;

        if (parsed is < 0 or > 0xFFFF)
        {
            throw new PanelProfileException($"\"{value}\" is not a 16-bit USB {field.ToLowerInvariant()} id.");
        }

        return parsed;
    }
}

/// <summary>How a panel is identified once it has been found.</summary>
public enum PanelProbeFamily
{
    /// <summary>
    /// Do not ask the panel anything; trust the resolution in the profile.
    /// </summary>
    None = 0,

    /// <summary>Thermalright chunked bulk JPEG handshake.</summary>
    ThermalrightLy,

    /// <summary>Thermalright raw bulk handshake.</summary>
    ThermalrightBulk,

    /// <summary>Thermalright HID type 2 handshake.</summary>
    ThermalrightHidType2,

    /// <summary>Thermalright HID type 3 handshake.</summary>
    ThermalrightHidType3,

    /// <summary>Thermalright SCSI identification poll.</summary>
    ThermalrightScsi,

    /// <summary>Turing revision A hello.</summary>
    TuringRevisionA,

    /// <summary>Turing revision B hello.</summary>
    TuringRevisionB,

    /// <summary>Turing revision C hello.</summary>
    TuringRevisionC,

    /// <summary>Kipye Qiye "revision D" info request.</summary>
    TuringRevisionD,
}

/// <summary>
/// Everything Panelith needs to drive one panel model, expressed as data.
/// </summary>
/// <remarks>
/// Built-in profiles ship as embedded resources; user profiles are JSON files
/// in the configuration folder and override built-ins with the same id, so a
/// user can correct a wrong resolution without waiting for a release.
/// </remarks>
public sealed record PanelProfile
{
    /// <summary>Stable id, conventionally <c>vendor.model</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Vendor name as users know it.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Model name as users know it.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Other names this model is sold under, for searching.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>Which driver speaks this panel's protocol.</summary>
    public string Driver { get; init; } = "generic";

    /// <summary>USB ids that identify this panel.</summary>
    public IReadOnlyList<UsbMatch> Usb { get; init; } = [];

    /// <summary>Native width in pixels, before orientation.</summary>
    public int Width { get; init; }

    /// <summary>Native height in pixels, before orientation.</summary>
    public int Height { get; init; }

    /// <summary>The payload layout the panel expects.</summary>
    public PanelPixelFormat PixelFormat { get; init; } = PanelPixelFormat.Rgb565BigEndian;

    /// <summary>Which transport the panel speaks.</summary>
    public PanelTransportKind Transport { get; init; } = PanelTransportKind.UsbBulk;

    /// <summary>How the panel is asked to confirm its own geometry.</summary>
    public PanelProbeFamily Probe { get; init; } = PanelProbeFamily.None;

    /// <summary>The frame header, if any.</summary>
    public HeaderTemplate Header { get; init; } = HeaderTemplate.None;

    /// <summary>
    /// A per-chunk header, for transports that repeat a header on every chunk
    /// rather than sending one header per frame.
    /// </summary>
    public HeaderTemplate? ChunkHeader { get; init; }

    /// <summary>Bytes per transfer, including any chunk header.</summary>
    public int ChunkSize { get; init; } = 16384;

    /// <summary>The command byte the header's <c>command</c> placeholder resolves to.</summary>
    public int Command { get; init; }

    /// <summary>Quality used when <see cref="PixelFormat"/> is JPEG.</summary>
    public int JpegQuality { get; init; } = 85;

    /// <summary>Largest frame payload the panel accepts, or <see langword="null"/> for no limit.</summary>
    public int? MaxPayloadBytes { get; init; }

    /// <summary>HID report id prefixed to each write, for HID transports.</summary>
    public byte ReportId { get; init; }

    /// <summary>Baud rate, for serial transports.</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>Address of the bulk IN endpoint, for transports that read replies.</summary>
    public byte EndpointIn { get; init; } = 0x81;

    /// <summary>
    /// How long to wait after opening before sending the first frame. Some
    /// firmwares drop anything that arrives while they are still initialising.
    /// </summary>
    public double StartupDelaySeconds { get; init; }

    /// <summary>What the panel can do beyond accepting frames.</summary>
    public PanelCapabilities Capabilities { get; init; } = PanelCapabilities.Minimal;

    /// <summary>Orientation applied when the user has not chosen one.</summary>
    public PanelOrientation DefaultOrientation { get; init; } = PanelOrientation.Portrait;

    /// <summary>How thoroughly this model has been tested.</summary>
    public PanelSupportLevel Support { get; init; } = PanelSupportLevel.Untested;

    /// <summary>Notes shown in the device list and in the support table.</summary>
    public string? Notes { get; init; }

    /// <summary>Whether this profile came from a user file rather than the built-in set.</summary>
    [JsonIgnore]
    public bool IsUserDefined { get; init; }

    /// <summary>Vendor and model, for display.</summary>
    [JsonIgnore]
    public string FullName => $"{Vendor} {Model}".Trim();

    /// <summary>Builds the descriptor this profile describes.</summary>
    public PanelDescriptor ToDescriptor() => new(
        Id,
        Vendor,
        Model,
        Width,
        Height,
        Transport,
        PixelFormat,
        Capabilities);

    /// <summary>Whether this profile claims a device with these USB ids.</summary>
    public bool Matches(int vendorId, int productId, string? serialNumber = null)
    {
        foreach (var match in Usb)
        {
            if (match.VendorId != vendorId || match.ProductId != productId)
            {
                continue;
            }

            if (match.SerialNumber is { } required
                && !string.Equals(required, serialNumber, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks the profile for internal contradictions.
    /// </summary>
    /// <returns>Problems found, phrased for whoever wrote the profile.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            problems.Add("The profile needs an id.");
        }

        if (Width <= 0 || Height <= 0)
        {
            problems.Add($"The screen size must be positive but is {Width}x{Height}.");
        }

        if (ChunkSize <= 0)
        {
            problems.Add($"The chunk size must be positive but is {ChunkSize}.");
        }

        if (JpegQuality is < 1 or > 100)
        {
            problems.Add($"JPEG quality must be between 1 and 100 but is {JpegQuality}.");
        }

        if (Transport != PanelTransportKind.Virtual && Usb.Count == 0)
        {
            problems.Add("The profile has no USB ids, so no device can ever match it.");
        }

        foreach (var match in Usb)
        {
            try
            {
                _ = match.VendorId;
                _ = match.ProductId;
            }
            catch (PanelProfileException ex)
            {
                problems.Add(ex.Message);
            }
        }

        // A chunk header that is as large as the chunk leaves no room for data,
        // which would loop forever sending empty chunks.
        if (ChunkHeader is { } chunkHeader && chunkHeader.Size >= ChunkSize)
        {
            problems.Add(
                $"The per-chunk header is {chunkHeader.Size} bytes, which leaves no payload room "
                + $"in a {ChunkSize}-byte chunk.");
        }

        if (MaxPayloadBytes is { } max && max > 0 && !PixelFormat.IsCompressed())
        {
            var required = PixelFormat.PayloadSize(Width, Height) ?? 0;
            if (required > max)
            {
                problems.Add(
                    $"A full {Width}x{Height} {PixelFormat} frame is {required} bytes, which exceeds the "
                    + $"declared {max}-byte payload limit.");
            }
        }

        try
        {
            _ = Header.Size;
            _ = ChunkHeader?.Size;
        }
        catch (PanelProfileException ex)
        {
            problems.Add(ex.Message);
        }

        return problems;
    }
}

/// <summary>How well a panel model is known to work.</summary>
public enum PanelSupportLevel
{
    /// <summary>
    /// The protocol is implemented but no one has reported driving this exact
    /// model. Expected to work; please report back either way.
    /// </summary>
    Untested = 0,

    /// <summary>Reported working by at least one user.</summary>
    Community,

    /// <summary>Verified against the hardware by a maintainer.</summary>
    Verified,

    /// <summary>Known to have problems; see the profile notes.</summary>
    Partial,
}
