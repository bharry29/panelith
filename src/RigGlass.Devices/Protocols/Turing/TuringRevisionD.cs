using System.Buffers.Binary;

namespace RigGlass.Devices.Protocols.Turing;

/// <summary>
/// The revision D serial protocol, used by the Kipye Qiye Smart Display 3.5".
/// </summary>
/// <remarks>
/// <para>
/// Commands are short ASCII-ish opcodes: most begin with <c>'C'</c> (0x43)
/// followed by a letter selecting the operation. A frame is sent by declaring
/// the target rectangle, entering picture mode, streaming RGB565 big-endian
/// pixels in 63-byte lines each prefixed with <c>0x50</c>, and leaving picture
/// mode.
/// </para>
/// <para>
/// This panel has no reset and no clear: blanking is done by filling the screen
/// with a colour, and the backlight is switched by setting brightness to zero.
/// </para>
/// </remarks>
public static class TuringRevisionD
{
    /// <summary>Baud rate for the serial link.</summary>
    public const int BaudRate = 115200;

    /// <summary>Bytes of pixel data per streamed line.</summary>
    public const int PixelLineSize = 63;

    /// <summary>Prefix byte on each streamed pixel line.</summary>
    public const byte PixelLinePrefix = 0x50;

    /// <summary>USB ids this panel enumerates with.</summary>
    public static (int VendorId, int ProductId) UsbIds => (0x454D, 0x4E41);

    /// <summary>Asks the panel for its identity.</summary>
    public static ReadOnlySpan<byte> GetInfo => [71, 0, 0, 0];

    /// <summary>Sets the native portrait orientation.</summary>
    public static ReadOnlySpan<byte> SetOrientationNative => [67, 72, 0, 0];

    /// <summary>Sets the 180-degree orientation.</summary>
    public static ReadOnlySpan<byte> SetOrientationReversed => [67, 71, 0, 0];

    /// <summary>Sets portrait with horizontal mirroring.</summary>
    public static ReadOnlySpan<byte> SetOrientationMirrored => [67, 68, 0, 0];

    /// <summary>Sets reverse portrait with horizontal mirroring.</summary>
    public static ReadOnlySpan<byte> SetOrientationReversedMirrored => [67, 70, 0, 0];

    /// <summary>Begins a pixel transmission.</summary>
    public static ReadOnlySpan<byte> EnterPictureMode => [68, 0, 0, 0];

    /// <summary>Ends a pixel transmission.</summary>
    public static ReadOnlySpan<byte> ExitPictureMode => [65, 0, 0, 0];

    /// <summary>Opcode prefix for the brightness command.</summary>
    private static ReadOnlySpan<byte> SetBacklight => [67, 67];

    /// <summary>Opcode prefix for the fill-with-colour command.</summary>
    private static ReadOnlySpan<byte> DisplayColor => [67, 66];

    /// <summary>Opcode prefix for the declare-rectangle command.</summary>
    private static ReadOnlySpan<byte> BlockWrite => [67, 65];

    /// <summary>
    /// Builds a brightness command.
    /// </summary>
    /// <param name="percent">Brightness from 0 to 100.</param>
    /// <remarks>
    /// The register runs 0 to 500 and, unusually for these panels, is not
    /// inverted: 500 really is brightest.
    /// </remarks>
    public static byte[] BuildSetBrightness(int percent)
    {
        var level = Math.Clamp(percent, 0, 100) * 5;
        var packet = new byte[SetBacklight.Length + 2];
        SetBacklight.CopyTo(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(SetBacklight.Length, 2), (ushort)level);
        return packet;
    }

    /// <summary>Builds a command filling the screen with one RGB565 colour.</summary>
    public static byte[] BuildFill(ushort rgb565)
    {
        var packet = new byte[DisplayColor.Length + 2];
        DisplayColor.CopyTo(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(DisplayColor.Length, 2), rgb565);
        return packet;
    }

    /// <summary>
    /// Builds the command declaring the rectangle the following pixels fill.
    /// </summary>
    /// <remarks>
    /// The coordinate order is both X bounds and then both Y bounds, rather than
    /// the two corners, which is easy to get backwards.
    /// </remarks>
    public static byte[] BuildBlockWrite(int x0, int x1, int y0, int y1)
    {
        var packet = new byte[BlockWrite.Length + 8];
        BlockWrite.CopyTo(packet);
        var span = packet.AsSpan(BlockWrite.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[..2], (ushort)x0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], (ushort)x1);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)y0);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], (ushort)y1);
        return packet;
    }

    /// <summary>The orientation command matching a requested orientation.</summary>
    /// <remarks>
    /// Only the portrait pair is implemented in firmware, so the landscape
    /// orientations are produced by rotating the image in software and keeping
    /// the panel in portrait.
    /// </remarks>
    public static ReadOnlySpan<byte> OrientationCommand(PanelOrientation orientation, bool mirror = false) =>
        orientation switch
        {
            PanelOrientation.ReversePortrait or PanelOrientation.ReverseLandscape =>
                mirror ? SetOrientationReversedMirrored : SetOrientationReversed,
            _ => mirror ? SetOrientationMirrored : SetOrientationNative,
        };
}
