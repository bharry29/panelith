using System.Buffers.Binary;

namespace RigGlass.Devices.Protocols.Turing;

/// <summary>Which revision C panel is attached. Decides the frame opcode.</summary>
public enum TuringRevisionCModel
{
    /// <summary>2.1" round, 480x480.</summary>
    Round2Inch,

    /// <summary>5", 480x800.</summary>
    Display5Inch,

    /// <summary>8.8" ultrawide, 480x1920.</summary>
    Display8Inch,
}

/// <summary>
/// The revision C serial protocol, used by the 2.1", 5" and 8.8" Turing panels.
/// </summary>
/// <remarks>
/// <para>
/// Commands begin with an opcode byte followed by the signature
/// <c>EF 69 00</c>, and every message is zero-padded up to a multiple of 250
/// bytes. Sending an unpadded message leaves the firmware waiting for the
/// remainder and the panel stops responding until it is power-cycled, which is
/// the usual cause of a revision C panel appearing "stuck".
/// </para>
/// <para>
/// A full-frame update is a fixed sequence: prepare, start, declare size, send
/// the pixels, then poll status. Pixels are BGRA for partial updates and BGR
/// for full frames.
/// </para>
/// </remarks>
public static class TuringRevisionC
{
    /// <summary>Baud rate for the serial link.</summary>
    public const int BaudRate = 115200;

    /// <summary>Every message is padded to a multiple of this many bytes.</summary>
    public const int MessageAlignment = 250;

    /// <summary>Bytes the panel returns in reply to a hello.</summary>
    public const int HelloResponseSize = 23;

    /// <summary>Bytes the panel returns in reply to a status query.</summary>
    public const int StatusResponseSize = 1024;

    /// <summary>The prefix an identification reply starts with.</summary>
    public const string IdentityPrefix = "chs_";

    /// <summary>ROM version assumed when the panel reports an implausible one.</summary>
    public const int DefaultRomVersion = 87;

    /// <summary>USB ids these panels enumerate with.</summary>
    public static (int VendorId, int ProductId) UsbIds => (0x1D6B, 0x0106);

    /// <summary>Asks the panel to identify itself.</summary>
    public static ReadOnlySpan<byte> Hello =>
        [0x01, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xC5, 0xD3];

    /// <summary>Sets start-up mode, flip and sleep behaviour.</summary>
    public static ReadOnlySpan<byte> Options => [0x7D, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x2D];

    /// <summary>Restarts the panel.</summary>
    public static ReadOnlySpan<byte> Restart => [0x84, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Turns the backlight off.</summary>
    public static ReadOnlySpan<byte> TurnOff => [0x83, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Turns the backlight on.</summary>
    public static ReadOnlySpan<byte> TurnOn => [0x83, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Stops any video playback the panel started on its own.</summary>
    public static ReadOnlySpan<byte> StopVideo => [0x79, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Stops any slideshow the panel started on its own.</summary>
    public static ReadOnlySpan<byte> StopMedia => [0x96, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Asks whether the panel is ready for the next frame.</summary>
    public static ReadOnlySpan<byte> QueryStatus => [0xCF, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Precedes a full-frame update.</summary>
    public static ReadOnlySpan<byte> PreUpdateBitmap => [0x86, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01];

    /// <summary>Opens a full-frame update. Padded with <c>0x2C</c> rather than zero.</summary>
    public static ReadOnlySpan<byte> StartDisplayBitmap => [0x2C];

    /// <summary>Precedes a partial update.</summary>
    public static ReadOnlySpan<byte> UpdateBitmap => [0xCC, 0xEF, 0x69, 0x00];

    /// <summary>Sets brightness. The level byte follows.</summary>
    public static ReadOnlySpan<byte> SetBrightness => [0x7B, 0xEF, 0x69, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

    /// <summary>The byte <see cref="StartDisplayBitmap"/> is padded with.</summary>
    public const byte StartDisplayBitmapPadding = 0x2C;

    /// <summary>The frame opcode for a model.</summary>
    public static ReadOnlySpan<byte> FrameOpcodeFor(TuringRevisionCModel model) => model switch
    {
        TuringRevisionCModel.Round2Inch => [0xC8, 0xEF, 0x69, 0x00, 0x0E, 0x10],
        TuringRevisionCModel.Display5Inch => [0xC8, 0xEF, 0x69, 0x00, 0x17, 0x70],
        _ => [0xC8, 0xEF, 0x69, 0x00, 0x38, 0x40],
    };

    /// <summary>The model matching a resolution, or <see langword="null"/> when unrecognised.</summary>
    public static TuringRevisionCModel? ModelFor(int width, int height) => (width, height) switch
    {
        (480, 480) => TuringRevisionCModel.Round2Inch,
        (480, 800) => TuringRevisionCModel.Display5Inch,
        (480, 1920) => TuringRevisionCModel.Display8Inch,
        _ => null,
    };

    /// <summary>
    /// Pads a message up to a multiple of <see cref="MessageAlignment"/>.
    /// </summary>
    /// <param name="message">The unpadded message.</param>
    /// <param name="padding">The byte to pad with.</param>
    public static byte[] Pad(ReadOnlySpan<byte> message, byte padding = 0x00)
    {
        var remainder = message.Length % MessageAlignment;
        if (remainder == 0)
        {
            return message.ToArray();
        }

        var padded = new byte[message.Length + (MessageAlignment - remainder)];
        message.CopyTo(padded);
        if (padding != 0)
        {
            padded.AsSpan(message.Length).Fill(padding);
        }

        return padded;
    }

    /// <summary>Builds a padded command with an optional payload appended to the opcode.</summary>
    public static byte[] BuildCommand(ReadOnlySpan<byte> opcode, ReadOnlySpan<byte> payload = default, byte padding = 0x00)
    {
        Span<byte> message = opcode.Length + payload.Length <= 64
            ? stackalloc byte[opcode.Length + payload.Length]
            : new byte[opcode.Length + payload.Length];
        opcode.CopyTo(message);
        payload.CopyTo(message[opcode.Length..]);
        return Pad(message, padding);
    }

    /// <summary>
    /// Builds a brightness command.
    /// </summary>
    /// <remarks>
    /// Unlike revisions A and B, this register is not inverted: 255 is brightest.
    /// </remarks>
    public static byte[] BuildSetBrightness(int percent)
    {
        var level = (byte)(Math.Clamp(percent, 0, 100) / 100d * 255d);
        return BuildCommand(SetBrightness, [level]);
    }

    /// <summary>Builds the options command that sets start-up mode and flip.</summary>
    public static byte[] BuildSetOptions(bool flip180) =>
        BuildCommand(Options, [0x00, 0x00, flip180 ? (byte)0x01 : (byte)0x00, 0x00]);

    /// <summary>
    /// Builds the frame-size declaration that precedes a full-frame payload.
    /// </summary>
    /// <remarks>
    /// The value is derived from the panel width alone rather than from the
    /// frame's real byte count. It reproduces what the vendor tool sends, and
    /// the firmware rejects anything else.
    /// </remarks>
    public static byte[] BuildFrameSize(TuringRevisionCModel model, int width)
    {
        Span<byte> payload = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)(width * width / 64));
        return BuildCommand(FrameOpcodeFor(model), payload);
    }

    /// <summary>
    /// Reads the ROM version out of an identification reply such as
    /// <c>chs_5inch.v1.87</c>.
    /// </summary>
    /// <remarks>
    /// The model name in the reply is unreliable -- 2.1" panels identify
    /// themselves as 5" -- so only the version is taken from it, and the model
    /// is determined from the resolution instead.
    /// </remarks>
    public static int ReadRomVersion(ReadOnlySpan<byte> response)
    {
        Span<char> printable = stackalloc char[response.Length];
        var length = 0;
        foreach (var b in response)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                printable[length++] = (char)b;
            }
        }

        var text = new string(printable[..length]);
        var parts = text.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[2], out var version))
        {
            return DefaultRomVersion;
        }

        return version is < 80 or > 100 ? DefaultRomVersion : version;
    }

    /// <summary>Whether an identification reply came from one of these panels.</summary>
    public static bool IsIdentityResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < IdentityPrefix.Length)
        {
            return false;
        }

        for (var start = 0; start + IdentityPrefix.Length <= response.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < IdentityPrefix.Length; i++)
            {
                if (response[start + i] != IdentityPrefix[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
