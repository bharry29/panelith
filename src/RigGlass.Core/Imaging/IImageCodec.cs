namespace RigGlass.Imaging;

/// <summary>
/// Encodes and decodes compressed image containers.
/// </summary>
/// <remarks>
/// Kept behind an interface so that the device layer -- which needs JPEG for
/// several panel families -- does not take a dependency on a graphics stack.
/// That keeps protocol tests fast and lets a trimmed headless build swap in a
/// smaller codec.
/// </remarks>
public interface IImageCodec
{
    /// <summary>Encodes a frame as a JPEG file.</summary>
    /// <param name="frame">The pixels to encode.</param>
    /// <param name="quality">Quality from 1 to 100.</param>
    byte[] EncodeJpeg(FrameBuffer frame, int quality = 85);

    /// <summary>Encodes a frame as a PNG file.</summary>
    byte[] EncodePng(FrameBuffer frame);

    /// <summary>
    /// Decodes an image file into a frame buffer, optionally resizing it.
    /// </summary>
    /// <param name="data">The encoded file.</param>
    /// <param name="width">Target width, or <see langword="null"/> to keep the native width.</param>
    /// <param name="height">Target height, or <see langword="null"/> to keep the native height.</param>
    FrameBuffer Decode(ReadOnlySpan<byte> data, int? width = null, int? height = null);

    /// <summary>
    /// Encodes a frame as JPEG, lowering quality until the result fits within
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <remarks>
    /// Several panels cap the payload they will accept per frame, and a busy
    /// theme at high quality can exceed it. Dropping quality is far better than
    /// dropping the frame, which the user sees as a stutter.
    /// </remarks>
    byte[] EncodeJpegWithinBudget(FrameBuffer frame, int maxBytes, int startQuality = 90)
    {
        var quality = Math.Clamp(startQuality, 10, 100);
        while (true)
        {
            var encoded = EncodeJpeg(frame, quality);
            if (encoded.Length <= maxBytes || quality <= 10)
            {
                return encoded;
            }

            quality -= 10;
        }
    }
}
