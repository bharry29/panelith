using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace Panelith.Media;

/// <summary>
/// Decodes the first video stream of a file to BGRA pictures with Windows
/// Media Foundation, using whatever codecs Windows has (H.264, HEVC, VP9 and
/// AV1 with the store extensions).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationDecoder : IDisposable
{
    private static readonly Lock StartupGate = new();
    private static int s_users;

    private readonly IMFSourceReader _reader;
    private Format _format;
    private IMFSample? _pending;
    private bool _ended;
    private bool _disposed;

    private MediaFoundationDecoder(IMFSourceReader reader, Format format, TimeSpan? duration)
    {
        _reader = reader;
        _format = format;
        Duration = duration;
    }

    /// <summary>Picture width.</summary>
    public int Width => _format.Width;

    /// <summary>Picture height.</summary>
    public int Height => _format.Height;

    /// <summary>Length of the video, when the file says.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Whether Media Foundation is present. Windows N editions lack it until the Media Feature Pack is added.</summary>
    public static bool IsAvailable =>
        NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "mfplat.dll"), out var handle) && Free(handle);

    /// <summary>Opens <paramref name="path"/> for decoding.</summary>
    public static MediaFoundationDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Windows Media Foundation is not installed. On Windows N editions add the Media Feature Pack.");
        }

        Startup();
        IMFSourceReader? reader = null;
        try
        {
            using (var attributes = MediaFactory.MFCreateAttributes(1))
            {
                attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
                reader = MediaFactory.MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes);
            }

            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (var wanted = MediaFactory.MFCreateMediaType())
            {
                wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                wanted.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, wanted);
            }

            var decoder = new MediaFoundationDecoder(reader, ReadFormat(reader), ReadDuration(reader));
            reader = null;
            try
            {
                decoder.Prime();
            }
            catch
            {
                decoder.Dispose();
                throw;
            }

            return decoder;
        }
        catch (SharpGenException ex)
        {
            throw new InvalidDataException($"Windows cannot decode this video ({ex.Message.Trim()}). It may need a codec from the Microsoft Store.", ex);
        }
        finally
        {
            if (reader is not null)
            {
                reader.Dispose();
                Shutdown();
            }
        }
    }

    /// <summary>Moves to <paramref name="position"/>; decoding resumes at the key frame before it.</summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position > TimeSpan.Zero)
        {
            _pending?.Dispose();
            _pending = null;
            _ended = false;
            _reader.SetCurrentPosition(position.Ticks);
        }
    }

    /// <summary>The next picture, or null at the end of the video.</summary>
    public IDecodedFrame? Next()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is { } pending)
        {
            _pending = null;
            return new Frame(pending, _format);
        }

        var sample = _ended ? null : Read(allowResize: false);
        return sample is null ? null : new Frame(sample, _format);
    }

    /// <summary>
    /// Decodes up to the first picture. The container's size is often only a
    /// guess (H.264 rounds 1080 rows up to 1088); the decoder's first output
    /// says the real one, so the size is settled before anyone reads it.
    /// </summary>
    private void Prime() => _pending = Read(allowResize: true);

    private IMFSample? Read(bool allowResize)
    {
        while (true)
        {
            IMFSample? sample;
            SourceReaderFlag flags;
            try
            {
                sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out flags, out _);
            }
            catch (SharpGenException ex)
            {
                throw new InvalidDataException($"Decoding the video failed ({ex.Message.Trim()}).", ex);
            }

            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new InvalidDataException("Decoding the video failed.");
            }

            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                var format = ReadFormat(_reader);
                if (!allowResize && (format.Width != Width || format.Height != Height))
                {
                    sample?.Dispose();
                    throw new InvalidDataException($"The video changes size part way through, from {Width}x{Height} to {format.Width}x{format.Height}, which Panelith cannot bake.");
                }

                _format = format;
            }

            if (sample is not null)
            {
                return sample;
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                _ended = true;
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending?.Dispose();
        _pending = null;
        _reader.Dispose();
        Shutdown();
    }

    private static bool Free(IntPtr handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }

    private static void Startup()
    {
        lock (StartupGate)
        {
            if (s_users++ == 0)
            {
                MediaFactory.MFStartup();
            }
        }
    }

    private static void Shutdown()
    {
        lock (StartupGate)
        {
            if (--s_users == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    private static Format ReadFormat(IMFSourceReader reader)
    {
        using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        var width = (int)(size >> 32);
        var height = (int)(size & 0xFFFFFFFF);
        if (width is < 1 or > ClipLimits.MaxDimension * 2 || height is < 1 or > ClipLimits.MaxDimension * 2)
        {
            throw new InvalidDataException($"The video is {width}x{height}, outside the supported size.");
        }

        // Uncompressed RGB may be stored bottom-up, which Media Foundation marks with a negative stride.
        var stride = width * 4;
        if (type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stored).Success)
        {
            stride = unchecked((int)stored);
        }

        // H.264 codes whole 16-pixel blocks, so a 1920x804 video decodes as 1920x816
        // with junk below the picture. The display aperture says which part is real.
        var (x, y, w, h) = (0, 0, width, height);
        if (type.GetBlobSize(MinimumDisplayAperture, out var blobSize).Success && blobSize >= 16)
        {
            var area = type.GetBlob(MinimumDisplayAperture);
            var ax = BitConverter.ToInt16(area, 2);
            var ay = BitConverter.ToInt16(area, 6);
            var aw = BitConverter.ToInt32(area, 8);
            var ah = BitConverter.ToInt32(area, 12);
            if (ax >= 0 && ay >= 0 && aw > 0 && ah > 0 && ax + aw <= width && ay + ah <= height)
            {
                (x, y, w, h) = (ax, ay, aw, ah);
            }
        }

        return new Format(w, h, stride, x, y, height);
    }

    /// <summary>MF_MT_MINIMUM_DISPLAY_APERTURE.</summary>
    private static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");

    private readonly record struct Format(int Width, int Height, int Stride, int Left, int Top, int CodedHeight);

    private static TimeSpan? ReadDuration(IMFSourceReader reader)
    {
        try
        {
            var value = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            return value.Value is ulong ticks and > 0 ? TimeSpan.FromTicks((long)ticks) : null;
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    private sealed class Frame(IMFSample sample, Format format) : IDecodedFrame
    {
        public TimeSpan Timestamp { get; } = TimeSpan.FromTicks(sample.SampleTime);

        public TimeSpan Duration { get; } = TimeSpan.FromTicks(Math.Max(0, sample.SampleDuration));

        public void CopyTo(SKBitmap destination)
        {
            var (width, height) = (format.Width, format.Height);
            if (destination.Width != width || destination.Height != height || destination.ColorType != SKColorType.Bgra8888)
            {
                throw new ArgumentException("The bitmap does not match the video.", nameof(destination));
            }

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var data, out _, out var length);
            try
            {
                var rowBytes = width * 4;
                var left = format.Left * 4L;
                var pitch = Math.Abs(format.Stride);
                if (pitch < left + rowBytes || (long)pitch * (format.CodedHeight - 1) + left + rowBytes > length)
                {
                    throw new InvalidDataException("A decoded picture is smaller than its format says.");
                }

                var target = destination.GetPixels();
                for (var y = 0; y < height; y++)
                {
                    var codedRow = format.Top + y;
                    var sourceRow = format.Stride < 0 ? format.CodedHeight - 1 - codedRow : codedRow;
                    unsafe
                    {
                        var from = new ReadOnlySpan<byte>((byte*)data + ((long)sourceRow * pitch) + left, rowBytes);
                        var to = new Span<byte>((byte*)target + ((long)y * destination.RowBytes), rowBytes);
                        from.CopyTo(to);
                        MakeOpaque(MemoryMarshal.Cast<byte, uint>(to));
                    }
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }

        public void Dispose() => sample.Dispose();

        // RGB32 leaves the alpha byte undefined; JPEG ignores it but Skia's blending does not.
        private static void MakeOpaque(Span<uint> pixels)
        {
            var alpha = new Vector<uint>(0xFF000000);
            var i = 0;
            for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                (new Vector<uint>(pixels[i..]) | alpha).CopyTo(pixels[i..]);
            }

            for (; i < pixels.Length; i++)
            {
                pixels[i] |= 0xFF000000;
            }
        }
    }
}
