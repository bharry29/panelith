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
    private readonly int _stride;
    private bool _disposed;

    private MediaFoundationDecoder(IMFSourceReader reader, int width, int height, int stride, TimeSpan? duration)
    {
        _reader = reader;
        Width = width;
        Height = height;
        _stride = stride;
        Duration = duration;
    }

    /// <summary>Picture width.</summary>
    public int Width { get; }

    /// <summary>Picture height.</summary>
    public int Height { get; }

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

            var (width, height, stride) = ReadFormat(reader);
            var decoder = new MediaFoundationDecoder(reader, width, height, stride, ReadDuration(reader));
            reader = null;
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
            _reader.SetCurrentPosition(position.Ticks);
        }
    }

    /// <summary>The next picture, or null at the end of the video.</summary>
    public IDecodedFrame? Next()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                var (width, height, _) = ReadFormat(_reader);
                if (width != Width || height != Height)
                {
                    sample?.Dispose();
                    throw new InvalidDataException("The video changes size part way through, which Panelith cannot bake.");
                }
            }

            if (sample is not null)
            {
                return new Frame(sample, Width, Height, _stride);
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
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

    private static (int Width, int Height, int Stride) ReadFormat(IMFSourceReader reader)
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

        return (width, height, stride);
    }

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

    private sealed class Frame(IMFSample sample, int width, int height, int stride) : IDecodedFrame
    {
        public TimeSpan Timestamp { get; } = TimeSpan.FromTicks(sample.SampleTime);

        public TimeSpan Duration { get; } = TimeSpan.FromTicks(Math.Max(0, sample.SampleDuration));

        public void CopyTo(SKBitmap destination)
        {
            if (destination.Width != width || destination.Height != height || destination.ColorType != SKColorType.Bgra8888)
            {
                throw new ArgumentException("The bitmap does not match the video.", nameof(destination));
            }

            using var buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out var data, out _, out var length);
            try
            {
                var rowBytes = width * 4;
                var pitch = Math.Abs(stride);
                if (pitch < rowBytes || (long)pitch * (height - 1) + rowBytes > length)
                {
                    throw new InvalidDataException("A decoded picture is smaller than its format says.");
                }

                var target = destination.GetPixels();
                for (var y = 0; y < height; y++)
                {
                    var sourceRow = stride < 0 ? height - 1 - y : y;
                    unsafe
                    {
                        var from = new ReadOnlySpan<byte>((byte*)data + ((long)sourceRow * pitch), rowBytes);
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
