using System.Buffers.Binary;

namespace Panelith.Devices.Protocols.Thermalright;

/// <summary>
/// The wire families spoken by Thermalright's TRCC panels.
/// </summary>
/// <remarks>
/// Thermalright has shipped at least five different link layers across its
/// cooler range. They share the PM/SUB screen identification scheme (see
/// <see cref="TrccScreenTable"/>) but agree on nothing else, so each gets its
/// own handshake packet and frame header.
/// </remarks>
public enum TrccWireFamily
{
    /// <summary>Raw USB bulk with a 64-byte frame header. Several AIO variants.</summary>
    Bulk = 0,

    /// <summary>HID with a <c>DA DB DC DD</c>-prefixed header. Winbond USBDISPLAY panels.</summary>
    HidType2,

    /// <summary>HID with a short <c>0x69 0x88</c> header. ALi Corp panels.</summary>
    HidType3,

    /// <summary>USB mass storage Bulk-Only Transport with vendor opcode <c>0xF5</c>.</summary>
    Scsi,

    /// <summary>Chunked bulk JPEG. The Trofeo Vision family.</summary>
    Ly,
}

/// <summary>
/// Handshake packets and frame headers for the Thermalright wire families.
/// </summary>
/// <remarks>
/// Pure byte construction only; the I/O lives in the driver. See
/// docs/protocols.md for where these layouts come from.
/// </remarks>
public static class TrccProtocol
{
    /// <summary>Size of the raw-bulk frame header, prefix included.</summary>
    public const int BulkHeaderSize = 64;

    /// <summary>Size of the HID type 2 frame header, prefix included.</summary>
    public const int HidType2HeaderSize = 22;

    /// <summary>Size of the HID type 3 frame header.</summary>
    public const int HidType3HeaderSize = 8;

    /// <summary>Vendor SCSI opcode used for both polling and frame writes.</summary>
    public const byte ScsiOpcode = 0xF5;

    /// <summary>Bytes in a Bulk-Only Transport command block wrapper.</summary>
    public const int CommandBlockWrapperSize = 31;

    /// <summary>Bytes in a Bulk-Only Transport command status wrapper.</summary>
    public const int CommandStatusWrapperSize = 13;

    /// <summary>Magic prefix on raw-bulk headers and handshakes.</summary>
    private static ReadOnlySpan<byte> BulkMagic => [0x12, 0x34, 0x56, 0x78];

    /// <summary>Magic prefix on HID type 2 headers and handshakes.</summary>
    private static ReadOnlySpan<byte> HidType2Magic => [0xDA, 0xDB, 0xDC, 0xDD];

    /// <summary>
    /// The raw-bulk init packet: 64 bytes, the magic prefix, and a single
    /// <c>0x01</c> at offset 56.
    /// </summary>
    public static byte[] BuildBulkHandshake()
    {
        var packet = new byte[64];
        BulkMagic.CopyTo(packet);
        packet[56] = 0x01;
        return packet;
    }

    /// <summary>The HID type 2 init packet: the magic prefix, then <c>0x01</c> at offset 12, padded to 512 bytes.</summary>
    public static byte[] BuildHidType2Handshake()
    {
        var packet = new byte[512];
        HidType2Magic.CopyTo(packet);
        packet[12] = 0x01;
        return packet;
    }

    /// <summary>The HID type 3 init packet: a fixed 16-byte preamble padded to 1040 bytes.</summary>
    public static byte[] BuildHidType3Handshake()
    {
        var packet = new byte[1040];
        ReadOnlySpan<byte> preamble =
        [
            0xF5, 0x00, 0x01, 0x00, 0xBC, 0xFF, 0xB6, 0xC8,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00,
        ];
        preamble.CopyTo(packet);
        return packet;
    }

    /// <summary>
    /// The raw-bulk frame header.
    /// </summary>
    /// <param name="width">Panel width.</param>
    /// <param name="height">Panel height.</param>
    /// <param name="payloadSize">Size of the pixel or JPEG payload that follows.</param>
    /// <param name="usesJpeg">
    /// Whether the payload is JPEG. Selects the command byte: 2 for JPEG, 3 for
    /// raw RGB565.
    /// </param>
    public static byte[] BuildBulkHeader(int width, int height, int payloadSize, bool usesJpeg)
    {
        var header = new byte[BulkHeaderSize];
        var span = header.AsSpan();
        BulkMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], usesJpeg ? 2u : 3u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], (uint)height);

        // Offsets 16 to 55 stay zero; the firmware ignores them.
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..60], 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..64], (uint)payloadSize);
        return header;
    }

    /// <summary>The HID type 2 frame header.</summary>
    public static byte[] BuildHidType2Header(int width, int height, int payloadSize)
    {
        var header = new byte[HidType2HeaderSize];
        var span = header.AsSpan();
        HidType2Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..14], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], (uint)payloadSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 0);
        return header;
    }

    /// <summary>The HID type 3 frame header.</summary>
    public static byte[] BuildHidType3Header(int width, int height)
    {
        var header = new byte[HidType3HeaderSize];
        var span = header.AsSpan();
        span[0] = 0x69;
        span[1] = 0x88;
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..4], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], 0);
        return header;
    }

    /// <summary>
    /// Builds a Bulk-Only Transport command block wrapper.
    /// </summary>
    /// <param name="tag">
    /// Caller-chosen tag echoed back in the status wrapper, used to match
    /// replies to requests.
    /// </param>
    /// <param name="transferLength">Bytes of data that will follow.</param>
    /// <param name="commandBlock">The SCSI command descriptor block.</param>
    /// <param name="dataIn">Whether data flows from the device to the host.</param>
    public static byte[] BuildCommandBlockWrapper(
        uint tag,
        int transferLength,
        ReadOnlySpan<byte> commandBlock,
        bool dataIn = false)
    {
        if (commandBlock.Length is 0 or > 16)
        {
            throw new ArgumentException(
                $"A command descriptor block is 1 to 16 bytes; got {commandBlock.Length}.",
                nameof(commandBlock));
        }

        var wrapper = new byte[CommandBlockWrapperSize];
        var span = wrapper.AsSpan();
        "USBC"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], tag);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], (uint)transferLength);
        span[12] = dataIn ? (byte)0x80 : (byte)0x00;
        span[13] = 0;
        span[14] = (byte)commandBlock.Length;
        commandBlock.CopyTo(span[15..]);
        return wrapper;
    }

    /// <summary>
    /// The command descriptor block that writes one frame chunk.
    /// </summary>
    /// <param name="chunkIndex">Zero-based index of the chunk.</param>
    /// <param name="chunkLength">Bytes in the chunk.</param>
    public static byte[] BuildScsiWriteCommand(int chunkIndex, int chunkLength)
    {
        var cdb = new byte[16];
        cdb[0] = ScsiOpcode;
        cdb[1] = 0x01;
        cdb[2] = 0x01;
        cdb[3] = (byte)chunkIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(cdb.AsSpan(12, 4), (uint)chunkLength);
        return cdb;
    }

    /// <summary>The command descriptor block that asks a SCSI panel to identify itself.</summary>
    public static byte[] BuildScsiPollCommand(int transferLength)
    {
        var cdb = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(cdb.AsSpan(0, 4), ScsiOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(cdb.AsSpan(12, 4), (uint)transferLength);
        return cdb;
    }

    /// <summary>How many bytes a SCSI identification poll asks for.</summary>
    public const int ScsiPollSize = 0xE100;

    /// <summary>
    /// The marker a SCSI panel returns while it is still booting, in which case
    /// the poll should be retried after a short wait.
    /// </summary>
    private static ReadOnlySpan<byte> ScsiBootSignature => [0xA1, 0xA2, 0xA3, 0xA4];

    /// <summary>Whether a poll reply means "still booting, ask again".</summary>
    public static bool IsScsiStillBooting(ReadOnlySpan<byte> response) =>
        response.Length >= 8 && response[4..8].SequenceEqual(ScsiBootSignature);

    /// <summary>
    /// Reads the status wrapper a device returns after a Bulk-Only Transport
    /// command.
    /// </summary>
    /// <param name="response">The 13 bytes read from the bulk IN endpoint.</param>
    /// <param name="tag">Receives the echoed tag.</param>
    /// <param name="status">Receives the status byte, <c>0</c> meaning success.</param>
    /// <returns>Whether the reply was a well-formed status wrapper.</returns>
    public static bool TryReadCommandStatus(ReadOnlySpan<byte> response, out uint tag, out byte status)
    {
        tag = 0;
        status = 0xFF;

        if (response.Length < CommandStatusWrapperSize || !response[..4].SequenceEqual("USBS"u8))
        {
            return false;
        }

        tag = BinaryPrimitives.ReadUInt32LittleEndian(response[4..8]);
        status = response[12];
        return true;
    }

    /// <summary>Whether a HID type 2 reply looks like one of these panels.</summary>
    public static bool IsValidHidType2Response(ReadOnlySpan<byte> response) =>
        response.Length >= 20 && response[..4].SequenceEqual(HidType2Magic) && response[12] == 0x01;

    /// <summary>Whether a HID type 3 reply looks like one of these panels.</summary>
    public static bool IsValidHidType3Response(ReadOnlySpan<byte> response) =>
        response.Length >= 14 && response[0] is 0x65 or 0x66;

    /// <summary>
    /// The screen code a HID type 3 reply names. The firmware reports it one
    /// higher than the table index.
    /// </summary>
    public static int HidType3ScreenCode(ReadOnlySpan<byte> response) => response[0] - 1;

    /// <summary>Whether a raw-bulk reply looks like one of these panels.</summary>
    public static bool IsValidBulkResponse(ReadOnlySpan<byte> response) =>
        response.Length >= 41 && response[24] != 0;
}
