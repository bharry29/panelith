using System.Runtime.Versioning;
using SharpGen.Runtime;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace Panelith.Media;

/// <summary>
/// Writes a baked <c>.ptclip</c> out as an ordinary H.264 MP4, so the loop a
/// screen plays can be watched in any video player.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipExporter
{
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// Encodes <paramref name="clip"/> into <paramref name="output"/> at
    /// <paramref name="speed"/> times its normal rate. Returns the number of frames.
    /// The file only appears once it is complete.
    /// </summary>
    public static int Export(string clip, string output, double speed = 1d, IProgress<BakeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clip);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (!double.IsFinite(speed) || speed is < 0.1d or > 10d)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "The speed must be between 0.1 and 10.");
        }

        using var reader = ClipReader.Open(clip);
        var (width, height, fps, count) = (reader.Info.Width & ~1, reader.Info.Height & ~1, reader.Info.Fps, reader.Info.FrameCount);
        if (width < 2 || height < 2 || count == 0)
        {
            throw new InvalidDataException("The clip has no pictures to export.");
        }

        if (!MediaFoundationDecoder.IsAvailable)
        {
            throw new InvalidOperationException("Windows Media Foundation is not installed. On Windows N editions add the Media Feature Pack.");
        }

        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + ".partial.mp4");
        File.Delete(partial);
        MediaFactory.MFStartup();
        try
        {
            var rate = ((ulong)Math.Round(fps * speed * 1000d) << 32) | 1000u;
            var frameTicks = (long)Math.Round(TimeSpan.TicksPerSecond / (fps * speed));
            using (var writer = MediaFactory.MFCreateSinkWriterFromURL(partial, null, null))
            {
                using (var type = MediaFactory.MFCreateMediaType())
                {
                    type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    type.Set(MediaTypeAttributeKeys.Subtype, H264);
                    type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(width * height * fps * 0.2d, 1_000_000d, 40_000_000d));
                    type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                    type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
                    type.Set(MediaTypeAttributeKeys.FrameRate, rate);
                    type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
                    writer.AddStream(type);
                }

                using (var type = MediaFactory.MFCreateMediaType())
                {
                    type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                    type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                    type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
                    type.Set(MediaTypeAttributeKeys.FrameRate, rate);
                    type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
                    type.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
                    writer.SetInputMediaType(0, type, null);
                }

                writer.BeginWriting();
                var bytes = width * height * 4;
                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var picture = SKImage.FromEncodedData(reader.ReadFrame(i)) ?? throw new InvalidDataException($"Frame {i} of the clip could not be read."))
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.DrawImage(picture, 0, 0, SKSamplingOptions.Default);
                    }

                    using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
                    buffer.Lock(out var data, out _, out _);
                    try
                    {
                        for (var y = 0; y < height; y++)
                        {
                            unsafe
                            {
                                new ReadOnlySpan<byte>((byte*)bitmap.GetPixels() + ((long)y * bitmap.RowBytes), width * 4)
                                    .CopyTo(new Span<byte>((byte*)data + ((long)y * width * 4), width * 4));
                            }
                        }
                    }
                    finally
                    {
                        buffer.Unlock();
                    }

                    buffer.CurrentLength = bytes;
                    using var sample = MediaFactory.MFCreateSample();
                    sample.AddBuffer(buffer);
                    sample.SampleTime = i * frameTicks;
                    sample.SampleDuration = frameTicks;
                    writer.WriteSample(0, sample);
                    progress?.Report(new BakeProgress((i + 1) / (double)count, i + 1));
                }

                writer.Finalize();
            }

            File.Move(partial, output, overwrite: true);
            return count;
        }
        catch (SharpGenException ex)
        {
            throw new InvalidDataException($"Windows could not write the MP4 ({ex.Message.Trim()}).", ex);
        }
        finally
        {
            MediaFactory.MFShutdown();
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
