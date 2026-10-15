using System.Buffers.Binary;

namespace RigGlass.Devices.Protocols.Thermalright;

/// <summary>
/// Visible resolution of a Trofeo Vision that speaks the LY bulk protocol.
/// </summary>
/// <remarks>
/// <para>
/// One product id covers the 9.16" and the 11.3". The cooler-oriented PM/SUB
/// screen table mis-sizes these: it was built for the Frozen* line, and feeding
/// a Trofeo handshake through it reports heights the panel does not show. The
/// sizes here come from the init reply itself.
/// </para>
/// <para>
/// The reply carries a width and height as little-endian 32-bit values, but the
/// height is sometimes an internal buffer (599 has been observed) rather than
/// the visible area. A size is only trusted when it is one the family actually
/// ships. Otherwise the model byte is used, which distinguishes the 9.16" from
/// the 11.3" directly.
/// </para>
/// </remarks>
public static class TrofeoLyGeometry
{
    /// <summary>
    /// Reads the visible size out of an LY handshake reply.
    /// </summary>
    /// <returns>
    /// The size, or <see langword="null"/> when the reply does not identify a
    /// known Trofeo Vision.
    /// </returns>
    public static (int Width, int Height)? FromHandshake(ReadOnlySpan<byte> response)
    {
        if (response.Length >= 32)
        {
            var width = BinaryPrimitives.ReadInt32LittleEndian(response.Slice(24, 4));
            var height = BinaryPrimitives.ReadInt32LittleEndian(response.Slice(28, 4));
            if (IsShippedSize(width, height))
            {
                return (width, height);
            }
        }

        if (response.Length < 21)
        {
            return null;
        }

        return response[20] switch
        {
            0x01 or 0x02 or 0x03 => (1920, 480),
            0x05 => (1920, 400),
            _ => null,
        };
    }

    /// <summary>Whether a size is one Thermalright actually sells in this family.</summary>
    private static bool IsShippedSize(int width, int height) => (width, height) is
        (1280, 480) or (1920, 480) or (1920, 462) or (1920, 440) or (1920, 400);
}
