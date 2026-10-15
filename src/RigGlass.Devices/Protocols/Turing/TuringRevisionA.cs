using System.Buffers.Binary;

namespace RigGlass.Devices.Protocols.Turing;

/// <summary>Commands understood by revision A panels.</summary>
public enum TuringRevisionACommand : byte
{
    /// <summary>Asks the panel for its model. Original Turing 3.5" units do not answer.</summary>
    Hello = 69,

    /// <summary>Resets the panel. The serial port disappears and comes back.</summary>
    Reset = 101,

    /// <summary>Clears the screen to white.</summary>
    Clear = 102,

    /// <summary>Blanks the screen to black.</summary>
    ToBlack = 103,

    /// <summary>Turns the backlight off.</summary>
    ScreenOff = 108,

    /// <summary>Turns the backlight on.</summary>
    ScreenOn = 109,

    /// <summary>Sets brightness.</summary>
    SetBrightness = 110,

    /// <summary>Sets orientation and reports the logical screen size.</summary>
    SetOrientation = 121,

    /// <summary>Mirrors the output.</summary>
    SetMirror = 122,

    /// <summary>Writes a list of individually addressed pixels.</summary>
    DisplayPixels = 195,

    /// <summary>Writes a rectangle of pixels.</summary>
    DisplayBitmap = 197,
}

/// <summary>
/// The revision A serial protocol, used by the original Turing Smart Screen
/// 3.5" and by the UsbMonitor panels.
/// </summary>
/// <remarks>
/// <para>
/// Every command is six bytes. The first five pack a target rectangle into 10
/// bits per coordinate, and the sixth is the command code:
/// </para>
/// <code>
/// byte 0: x &gt;&gt; 2
/// byte 1: ((x &amp; 3) &lt;&lt; 6) | (y &gt;&gt; 4)
/// byte 2: ((y &amp; 15) &lt;&lt; 4) | (ex &gt;&gt; 6)
/// byte 3: ((ex &amp; 63) &lt;&lt; 2) | (ey &gt;&gt; 8)
/// byte 4: ey &amp; 255
/// byte 5: command
/// </code>
/// <para>
/// The bit packing is why these panels top out near 320x480: ten bits per
/// coordinate is all the addressing the protocol has.
/// </para>
/// </remarks>
public static class TuringRevisionA
{
    /// <summary>Bytes in a command packet.</summary>
    public const int CommandSize = 6;

    /// <summary>Baud rate for the serial link.</summary>
    public const int BaudRate = 115200;

    /// <summary>
    /// The serial number string the official 3.5" panel reports, used to pick it
    /// out from other CH340-based devices on the same vendor and product id.
    /// </summary>
    public const string IdentifyingSerialNumber = "USB35INCHIPSV2";

    /// <summary>USB ids of the CH340 bridge these panels use.</summary>
    public static (int VendorId, int ProductId) UsbIds => (0x1A86, 0x5722);

    /// <summary>Builds a command packet addressing a rectangle.</summary>
    /// <param name="command">The command code.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="endX">Right edge, inclusive.</param>
    /// <param name="endY">Bottom edge, inclusive.</param>
    public static byte[] BuildCommand(TuringRevisionACommand command, int x = 0, int y = 0, int endX = 0, int endY = 0)
    {
        var packet = new byte[CommandSize];
        WriteCommand(packet, command, x, y, endX, endY);
        return packet;
    }

    /// <summary>Writes a command packet into <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException">The destination is smaller than <see cref="CommandSize"/>.</exception>
    public static void WriteCommand(
        Span<byte> destination,
        TuringRevisionACommand command,
        int x = 0,
        int y = 0,
        int endX = 0,
        int endY = 0)
    {
        if (destination.Length < CommandSize)
        {
            throw new ArgumentException($"A command packet is {CommandSize} bytes.", nameof(destination));
        }

        destination[0] = (byte)(x >> 2);
        destination[1] = (byte)(((x & 3) << 6) + (y >> 4));
        destination[2] = (byte)(((y & 15) << 4) + (endX >> 6));
        destination[3] = (byte)(((endX & 63) << 2) + (endY >> 8));
        destination[4] = (byte)(endY & 255);
        destination[5] = (byte)command;
    }

    /// <summary>The six-byte packet that asks the panel to identify itself.</summary>
    public static byte[] BuildHello() =>
        [(byte)TuringRevisionACommand.Hello, (byte)TuringRevisionACommand.Hello, (byte)TuringRevisionACommand.Hello,
         (byte)TuringRevisionACommand.Hello, (byte)TuringRevisionACommand.Hello, (byte)TuringRevisionACommand.Hello];

    /// <summary>
    /// Builds a brightness command.
    /// </summary>
    /// <param name="percent">Brightness from 0 to 100.</param>
    /// <remarks>
    /// The hardware register runs the other way: 0 is brightest and 255 is
    /// darkest. Sending the percentage straight through makes the panel go dark
    /// as the user drags the slider up.
    /// </remarks>
    public static byte[] BuildSetBrightness(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var level = 255 - (int)Math.Round(clamped / 100d * 255d);
        return BuildCommand(TuringRevisionACommand.SetBrightness, level);
    }

    /// <summary>
    /// Builds the 16-byte orientation command, which also tells the panel the
    /// logical width and height to expect.
    /// </summary>
    /// <param name="orientation">The orientation to apply.</param>
    /// <param name="width">Logical width after rotation.</param>
    /// <param name="height">Logical height after rotation.</param>
    public static byte[] BuildSetOrientation(PanelOrientation orientation, int width, int height)
    {
        var packet = new byte[16];
        WriteCommand(packet, TuringRevisionACommand.SetOrientation);

        // The firmware numbers orientations from 100 rather than 0.
        packet[6] = (byte)(100 + OrientationCode(orientation));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(7, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(9, 2), (ushort)height);
        return packet;
    }

    /// <summary>The firmware's index for an orientation.</summary>
    public static int OrientationCode(PanelOrientation orientation) => orientation switch
    {
        PanelOrientation.Portrait => 0,
        PanelOrientation.Landscape => 1,
        PanelOrientation.ReversePortrait => 2,
        _ => 3,
    };

    /// <summary>Builds the command that precedes a rectangle of RGB565 pixel data.</summary>
    public static byte[] BuildDisplayBitmap(int x, int y, int width, int height) =>
        BuildCommand(TuringRevisionACommand.DisplayBitmap, x, y, x + width - 1, y + height - 1);

    /// <summary>
    /// Identifies a panel from its six-byte reply to <see cref="BuildHello"/>.
    /// </summary>
    /// <remarks>
    /// The reply is the same byte repeated six times. The official Turing 3.5"
    /// panel never answers at all, which is itself the identification: silence
    /// means 320x480.
    /// </remarks>
    public static (int Width, int Height, string Model) IdentifyFromHello(ReadOnlySpan<byte> response)
    {
        if (response.Length < 6)
        {
            return (320, 480, "Turing Smart Screen 3.5\"");
        }

        return response[0] switch
        {
            0x01 => (320, 480, "UsbMonitor 3.5\""),
            0x02 => (480, 800, "UsbMonitor 5\""),
            0x03 => (600, 1024, "UsbMonitor 7\""),
            _ => (320, 480, "Turing Smart Screen 3.5\""),
        };
    }
}
