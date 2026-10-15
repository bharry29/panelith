using System.Buffers.Binary;

namespace RigGlass.Devices.Protocols.Thermalright;

/// <summary>
/// Frame construction for the Thermalright "LY" bulk panels, which cover the
/// Trofeo Vision family (USB <c>0416:5408</c> and <c>0416:5409</c>).
/// </summary>
/// <remarks>
/// <para>
/// A frame is a complete JPEG file, split into 512-byte chunks. Each chunk
/// carries a 16-byte header followed by up to 496 bytes of JPEG data, and the
/// whole run is pushed to the bulk OUT endpoint in 4 KiB writes.
/// </para>
/// <para>
/// The 16-byte chunk header is laid out as:
/// </para>
/// <list type="table">
///   <item><term>0</term><description>Always <c>0x01</c>.</description></item>
///   <item><term>1</term><description>Always <c>0xFF</c>.</description></item>
///   <item><term>2-5</term><description>Total payload size, little-endian 32-bit.</description></item>
///   <item><term>6-7</term><description>Bytes of data in this chunk, little-endian 16-bit.</description></item>
///   <item><term>8</term><description>Command: 1 for <c>0x5408</c>, 2 for <c>0x5409</c>.</description></item>
///   <item><term>9-10</term><description>Total chunk count, little-endian 16-bit.</description></item>
///   <item><term>11-12</term><description>This chunk's index, little-endian 16-bit.</description></item>
///   <item><term>13-15</term><description>Zero.</description></item>
/// </list>
/// <para>
/// Everything in this type is pure, so the exact bytes can be asserted in tests
/// without hardware present. See docs/protocols.md for provenance.
/// </para>
/// </remarks>
public static class LyProtocol
{
    /// <summary>Product id of the original LY firmware.</summary>
    public const int ProductIdLy = 0x5408;

    /// <summary>Product id of the revised "LY1" firmware.</summary>
    public const int ProductIdLy1 = 0x5409;

    /// <summary>Thermalright's USB vendor id for these panels.</summary>
    public const int VendorId = 0x0416;

    /// <summary>Bytes per chunk, header included.</summary>
    public const int ChunkSize = 512;

    /// <summary>Bytes of header at the start of each chunk.</summary>
    public const int ChunkHeaderSize = 16;

    /// <summary>Bytes of payload each chunk can carry.</summary>
    public const int ChunkDataSize = ChunkSize - ChunkHeaderSize;

    /// <summary>Bytes per bulk write.</summary>
    public const int BulkWriteSize = 4096;

    /// <summary>Size of the handshake and acknowledgement reads.</summary>
    public const int ResponseSize = 512;

    /// <summary>Size of the handshake packet.</summary>
    public const int HandshakeSize = 2048;

    /// <summary>
    /// The packet that wakes the panel and asks it to describe itself. The
    /// panel replies with <see cref="ResponseSize"/> bytes.
    /// </summary>
    public static ReadOnlySpan<byte> Handshake =>
    [
        0x02, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // The remaining 2032 bytes are zero; BuildHandshake pads them.
    ];

    /// <summary>Builds the full 2048-byte handshake packet.</summary>
    public static byte[] BuildHandshake()
    {
        var packet = new byte[HandshakeSize];
        Handshake.CopyTo(packet);
        return packet;
    }

    /// <summary>The command byte for a product id.</summary>
    public static byte CommandFor(int productId) => productId == ProductIdLy1 ? (byte)2 : (byte)1;

    /// <summary>
    /// How many chunks the chunk count is rounded up to a multiple of.
    /// </summary>
    /// <remarks>
    /// The original firmware expects whole 2 KiB groups, so its chunk count is
    /// padded to a multiple of four; the revised firmware accepts any count.
    /// Getting this wrong makes the panel wait forever for data that never
    /// arrives and then drop the connection.
    /// </remarks>
    public static int PaddingMultipleFor(int productId) => productId == ProductIdLy1 ? 1 : 4;

    /// <summary>The number of chunks a payload of this size needs, before padding.</summary>
    /// <remarks>
    /// The count is deliberately <c>size / 496 + 1</c> rather than a ceiling
    /// division: a payload that divides exactly still gets a final chunk, which
    /// carries a data length of zero and signals end-of-frame to the firmware.
    /// </remarks>
    public static int ChunkCountFor(int payloadSize) => (payloadSize / ChunkDataSize) + 1;

    /// <summary>The total transfer size for a payload, padding included.</summary>
    public static int TransferSizeFor(int payloadSize, int productId)
    {
        var chunks = ChunkCountFor(payloadSize);
        var multiple = PaddingMultipleFor(productId);
        var remainder = chunks % multiple;
        if (remainder != 0)
        {
            chunks += multiple - remainder;
        }

        return chunks * ChunkSize;
    }

    /// <summary>
    /// Packs a payload into the chunked wire format.
    /// </summary>
    /// <param name="payload">The JPEG file to send.</param>
    /// <param name="productId">Which firmware variant is being driven.</param>
    /// <param name="destination">
    /// Receives the chunks. Must be at least <see cref="TransferSizeFor"/>
    /// bytes and is fully overwritten, padding included.
    /// </param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static int BuildFrame(ReadOnlySpan<byte> payload, int productId, Span<byte> destination)
    {
        var total = TransferSizeFor(payload.Length, productId);
        if (destination.Length < total)
        {
            throw new ArgumentException(
                $"A {payload.Length}-byte payload needs {total} bytes of chunk buffer, "
                + $"but only {destination.Length} were supplied.",
                nameof(destination));
        }

        var window = destination[..total];
        window.Clear();

        var chunkCount = ChunkCountFor(payload.Length);
        var command = CommandFor(productId);
        var lastChunkLength = payload.Length % ChunkDataSize;

        for (var index = 0; index < chunkCount; index++)
        {
            var chunk = window.Slice(index * ChunkSize, ChunkSize);
            var isLast = index == chunkCount - 1;
            var dataLength = isLast ? lastChunkLength : ChunkDataSize;

            chunk[0] = 0x01;
            chunk[1] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[2..6], (uint)payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk[6..8], (ushort)dataLength);
            chunk[8] = command;
            BinaryPrimitives.WriteUInt16LittleEndian(chunk[9..11], (ushort)chunkCount);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk[11..13], (ushort)index);

            if (dataLength > 0)
            {
                payload.Slice(index * ChunkDataSize, dataLength).CopyTo(chunk[ChunkHeaderSize..]);
            }
        }

        return total;
    }

    /// <summary>Packs a payload into a newly allocated buffer.</summary>
    public static byte[] BuildFrame(ReadOnlySpan<byte> payload, int productId)
    {
        var buffer = new byte[TransferSizeFor(payload.Length, productId)];
        BuildFrame(payload, productId, buffer);
        return buffer;
    }

    /// <summary>
    /// Whether a handshake reply looks like one of these panels.
    /// </summary>
    /// <remarks>
    /// Byte 8 is documented as <c>1</c>, but shipping Trofeo Vision 9.16
    /// firmware answers <c>2</c>. Both are accepted: rejecting the second would
    /// make RigGlass refuse to drive hardware that works fine.
    /// </remarks>
    public static bool IsValidHandshakeResponse(ReadOnlySpan<byte> response) =>
        response.Length >= 37 && response[0] == 3 && response[1] == 0xFF && response[8] is 1 or 2;
}
