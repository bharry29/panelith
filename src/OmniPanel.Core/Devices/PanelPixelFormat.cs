namespace OmniPanel.Devices;

/// <summary>
/// The byte layout a panel expects for frame payloads.
/// </summary>
/// <remarks>
/// The variety here is not gratuitous: every one of these is required by at
/// least one shipping panel family, and getting a single one wrong produces a
/// recognisable artefact (swapped red and blue, a 90-degree rotation, or a
/// diagonal tear) rather than a clean failure. See docs/protocols.md.
/// </remarks>
public enum PanelPixelFormat
{
    /// <summary>16-bit 5-6-5, high byte first, row-major. Used by Thermalright SCSI and LY panels.</summary>
    Rgb565BigEndian = 0,

    /// <summary>16-bit 5-6-5, low byte first, row-major. Used by the Turing serial revisions.</summary>
    Rgb565LittleEndian,

    /// <summary>
    /// 16-bit 5-6-5, low byte first, written column-major after a vertical flip,
    /// with the last pixel of each column forced to zero.
    /// </summary>
    /// <remarks>
    /// This is the layout the Winbond-based Thermalright HID panels want. The
    /// zeroed final pixel is not a quirk of this implementation: the stock
    /// firmware treats that word as a row terminator and leaves a bright line
    /// across the panel if real pixel data is written there.
    /// </remarks>
    Rgb565LittleEndianColumns,

    /// <summary>24-bit red, green, blue.</summary>
    Rgb888,

    /// <summary>24-bit blue, green, red. Used by Turing revision C.</summary>
    Bgr888,

    /// <summary>32-bit blue, green, red, alpha. Used by Turing revision C for partial updates.</summary>
    Bgra8888,

    /// <summary>32-bit red, green, blue, alpha, matching <see cref="Imaging.FrameBuffer"/> exactly.</summary>
    Rgba8888,

    /// <summary>A complete JPEG file per frame. Used by the newer Thermalright and Turing USB panels.</summary>
    Jpeg,

    /// <summary>A complete PNG file per frame.</summary>
    Png,
}

/// <summary>Helpers for reasoning about <see cref="PanelPixelFormat"/> values.</summary>
public static class PanelPixelFormatExtensions
{
    /// <summary>
    /// Whether the format is a compressed container whose payload size depends
    /// on image content rather than on the pixel count.
    /// </summary>
    public static bool IsCompressed(this PanelPixelFormat format) =>
        format is PanelPixelFormat.Jpeg or PanelPixelFormat.Png;

    /// <summary>
    /// Bytes per pixel for uncompressed formats, or <c>0</c> for compressed ones.
    /// </summary>
    public static int BytesPerPixel(this PanelPixelFormat format) => format switch
    {
        PanelPixelFormat.Rgb565BigEndian
            or PanelPixelFormat.Rgb565LittleEndian
            or PanelPixelFormat.Rgb565LittleEndianColumns => 2,
        PanelPixelFormat.Rgb888 or PanelPixelFormat.Bgr888 => 3,
        PanelPixelFormat.Bgra8888 or PanelPixelFormat.Rgba8888 => 4,
        _ => 0,
    };

    /// <summary>
    /// The exact payload size for a full frame, or <see langword="null"/> when
    /// the format is compressed and the size is only known after encoding.
    /// </summary>
    public static int? PayloadSize(this PanelPixelFormat format, int width, int height)
    {
        var bpp = format.BytesPerPixel();
        return bpp == 0 ? null : width * height * bpp;
    }
}
