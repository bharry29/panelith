namespace RigGlass.Devices.Protocols.Turing;

/// <summary>Commands understood by revision B panels.</summary>
public enum TuringRevisionBCommand : byte
{
    /// <summary>Opens the link and reports the sub-revision.</summary>
    Hello = 0xCA,

    /// <summary>Sets orientation.</summary>
    SetOrientation = 0xCB,

    /// <summary>Writes a rectangle of pixels.</summary>
    DisplayBitmap = 0xCC,

    /// <summary>Sets the colour of the LEDs on the back of the panel.</summary>
    SetLighting = 0xCD,

    /// <summary>Sets brightness.</summary>
    SetBrightness = 0xCE,
}

/// <summary>
/// Which revision B variant a panel is, which decides what its brightness
/// control can do.
/// </summary>
public enum TuringRevisionBVariant
{
    /// <summary>Standard panel, backlight only on or off.</summary>
    Basic = 0xA01,

    /// <summary>"Flagship" panel with rear LEDs, backlight only on or off.</summary>
    Flagship = 0xA02,

    /// <summary>Standard panel with a 0-255 brightness range.</summary>
    BasicDimmable = 0xA11,

    /// <summary>"Flagship" panel with rear LEDs and a 0-255 brightness range.</summary>
    FlagshipDimmable = 0xA12,
}

/// <summary>
/// The revision B serial protocol, used by the XuanFang 3.5" panels and their
/// "flagship" variant.
/// </summary>
/// <remarks>
/// Commands are ten bytes: the command code, eight payload bytes, then the
/// command code again as a frame terminator. The repeated code is what lets the
/// firmware resynchronise after a partial write, which matters because these
/// panels share a USB-to-serial bridge with revision A and can be left
/// mid-command by another application.
/// </remarks>
public static class TuringRevisionB
{
    /// <summary>Bytes in a command packet.</summary>
    public const int CommandSize = 10;

    /// <summary>Baud rate for the serial link.</summary>
    public const int BaudRate = 115200;

    /// <summary>
    /// The serial number string these panels report, used to tell them apart
    /// from revision A panels behind the same CH340 bridge.
    /// </summary>
    public const string IdentifyingSerialNumber = "2017-2-25";

    /// <summary>USB ids of the CH340 bridge these panels use.</summary>
    public static (int VendorId, int ProductId) UsbIds => (0x1A86, 0x5722);

    /// <summary>Builds a command packet.</summary>
    /// <param name="command">The command code.</param>
    /// <param name="payload">Up to eight payload bytes; shorter payloads are zero-padded.</param>
    /// <exception cref="ArgumentException">The payload is longer than eight bytes.</exception>
    public static byte[] BuildCommand(TuringRevisionBCommand command, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > 8)
        {
            throw new ArgumentException($"A revision B payload is at most 8 bytes; got {payload.Length}.", nameof(payload));
        }

        var packet = new byte[CommandSize];
        packet[0] = (byte)command;
        payload.CopyTo(packet.AsSpan(1));
        packet[9] = (byte)command;
        return packet;
    }

    /// <summary>The packet that opens the link.</summary>
    public static byte[] BuildHello() => BuildCommand(TuringRevisionBCommand.Hello, "HELLO"u8);

    /// <summary>
    /// Reads the sub-revision out of a hello reply.
    /// </summary>
    /// <remarks>
    /// The reply carries the revision as three ASCII hex digits at offsets 6 to
    /// 8, so <c>"A11"</c> becomes <see cref="TuringRevisionBVariant.BasicDimmable"/>.
    /// An unrecognised reply falls back to the least capable variant, which is
    /// safe: a panel that can dim will merely be driven on or off.
    /// </remarks>
    public static TuringRevisionBVariant IdentifyFromHello(ReadOnlySpan<byte> response)
    {
        if (response.Length < 9)
        {
            return TuringRevisionBVariant.Basic;
        }

        Span<char> digits = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            digits[i] = (char)response[6 + i];
        }

        return int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value)
            && Enum.IsDefined(typeof(TuringRevisionBVariant), value)
                ? (TuringRevisionBVariant)value
                : TuringRevisionBVariant.Basic;
    }

    /// <summary>Whether a variant supports a graduated brightness range.</summary>
    public static bool SupportsBrightnessRange(TuringRevisionBVariant variant) =>
        variant is TuringRevisionBVariant.BasicDimmable or TuringRevisionBVariant.FlagshipDimmable;

    /// <summary>Whether a variant has the rear LED array.</summary>
    public static bool HasRearLighting(TuringRevisionBVariant variant) =>
        variant is TuringRevisionBVariant.Flagship or TuringRevisionBVariant.FlagshipDimmable;

    /// <summary>
    /// Builds a brightness command appropriate to the panel variant.
    /// </summary>
    /// <param name="percent">Brightness from 0 to 100.</param>
    /// <param name="variant">Which variant is being driven.</param>
    public static byte[] BuildSetBrightness(int percent, TuringRevisionBVariant variant)
    {
        var clamped = Math.Clamp(percent, 0, 100);

        if (!SupportsBrightnessRange(variant))
        {
            // Basic panels take 0 for on and 1 for off, so anything above zero
            // percent means "lit".
            return BuildCommand(TuringRevisionBCommand.SetBrightness, [(byte)(clamped > 0 ? 0 : 1)]);
        }

        // Dimmable panels also run inverted: 0 is brightest.
        var level = 255 - (int)Math.Round(clamped / 100d * 255d);
        return BuildCommand(TuringRevisionBCommand.SetBrightness, [(byte)level]);
    }

    /// <summary>
    /// Builds an orientation command.
    /// </summary>
    /// <remarks>
    /// The firmware only rotates by quarter turns in one direction, so the
    /// reversed orientations are produced by rotating the image in software and
    /// sending the matching base orientation.
    /// </remarks>
    public static byte[] BuildSetOrientation(PanelOrientation orientation, int width, int height)
    {
        var landscape = orientation is PanelOrientation.Landscape or PanelOrientation.ReverseLandscape;
        Span<byte> payload = stackalloc byte[8];
        payload[0] = landscape ? (byte)1 : (byte)0;
        payload[1] = (byte)(width >> 8);
        payload[2] = (byte)(width & 0xFF);
        payload[3] = (byte)(height >> 8);
        payload[4] = (byte)(height & 0xFF);
        return BuildCommand(TuringRevisionBCommand.SetOrientation, payload);
    }

    /// <summary>Builds the command that precedes a rectangle of RGB565 pixel data.</summary>
    public static byte[] BuildDisplayBitmap(int x, int y, int width, int height)
    {
        var endX = x + width - 1;
        var endY = y + height - 1;
        Span<byte> payload = stackalloc byte[8];
        payload[0] = (byte)(x >> 8);
        payload[1] = (byte)(x & 0xFF);
        payload[2] = (byte)(y >> 8);
        payload[3] = (byte)(y & 0xFF);
        payload[4] = (byte)(endX >> 8);
        payload[5] = (byte)(endX & 0xFF);
        payload[6] = (byte)(endY >> 8);
        payload[7] = (byte)(endY & 0xFF);
        return BuildCommand(TuringRevisionBCommand.DisplayBitmap, payload);
    }

    /// <summary>Builds a command setting the rear LED colour, on variants that have one.</summary>
    public static byte[] BuildSetLighting(byte red, byte green, byte blue) =>
        BuildCommand(TuringRevisionBCommand.SetLighting, [red, green, blue]);
}
