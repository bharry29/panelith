using System.Runtime.Versioning;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace Panelith.Media;

[SupportedOSPlatform("windows")]
public sealed class VideoBakerTests : IDisposable
{
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid TranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
    private static readonly Guid FragmentedMp4 = new("9BA876F1-419F-4B77-A1E0-35959D9D4004");
    private static readonly SKColor[] Colours = [SKColors.Red, SKColors.Lime, SKColors.Blue];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "panelith-bake-" + Guid.NewGuid().ToString("N"));
    private readonly string _video;

    public VideoBakerTests()
    {
        Directory.CreateDirectory(_folder);
        _video = Path.Combine(_folder, "rgb.mp4");
        if (OperatingSystem.IsWindows() && MediaFoundationDecoder.IsAvailable)
        {
            WriteTestVideo(_video);
        }
    }

    /// <summary>Three seconds at 30 fps, 320 wide: a second each of red, green and blue.</summary>
    private static void WriteTestVideo(string path, bool fragmented = false, int height = 192)
    {
        const int width = 320, fps = 30;
        MediaFactory.MFStartup();
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            if (fragmented)
            {
                attributes.Set(TranscodeContainerType, FragmentedMp4);
            }

            using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, 2_000_000u);
                output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                output.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
                output.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1);
                output.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
                writer.AddStream(output);
            }

            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
                input.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1);
                input.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1);
                writer.SetInputMediaType(0, input, null);
            }

            writer.BeginWriting();
            var bytes = width * height * 4;
            var pixels = new byte[bytes];
            for (var i = 0; i < fps * 3; i++)
            {
                var colour = Colours[i / fps];
                for (var p = 0; p < bytes; p += 4)
                {
                    pixels[p] = colour.Blue;
                    pixels[p + 1] = colour.Green;
                    pixels[p + 2] = colour.Red;
                    pixels[p + 3] = 255;
                }

                using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
                buffer.Lock(out var data, out _, out _);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data, bytes);
                buffer.Unlock();
                buffer.CurrentLength = bytes;

                using var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                sample.SampleTime = TimeSpan.TicksPerSecond * i / fps;
                sample.SampleDuration = TimeSpan.TicksPerSecond / fps;
                writer.WriteSample(0, sample);
            }

            writer.Finalize();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static SKColor Centre(ClipReader reader, int index)
    {
        using var bitmap = SKBitmap.Decode(reader.ReadFrame(index));
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    private static void AssertMostly(SKColor expected, SKColor actual)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) < 48 && Math.Abs(expected.Green - actual.Green) < 48 && Math.Abs(expected.Blue - actual.Blue) < 48,
            $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void AVideoBakesToAPanelSizedClipAtFifteenFps()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var output = Path.Combine(_folder, "out.ptclip");

        var info = VideoBaker.Bake(new BakeRequest(_video, output, 192, 48, LoopCrossfade: false), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(192, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(15, info.Fps);
        Assert.InRange(info.FrameCount, 44, 46);
        using var reader = ClipReader.Open(output);
        AssertMostly(SKColors.Red, Centre(reader, 2));
        AssertMostly(SKColors.Lime, Centre(reader, 22));
        AssertMostly(SKColors.Blue, Centre(reader, info.FrameCount - 3));
        Assert.False(File.Exists(output + ".partial"));
    }

    [Fact]
    public void ABakedClipExportsToAnMp4ThatPlaysBack()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var clip = Path.Combine(_folder, "out.ptclip");
        var mp4 = Path.Combine(_folder, "out.mp4");
        var info = VideoBaker.Bake(new BakeRequest(_video, clip, 192, 48, LoopCrossfade: false), cancellationToken: TestContext.Current.CancellationToken);

        var written = ClipExporter.Export(clip, mp4, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(info.FrameCount, written);
        Assert.False(File.Exists(Path.Combine(_folder, "out.partial.mp4")));
        using var decoder = MediaFoundationDecoder.Open(mp4);
        Assert.Equal(192, decoder.Width);
        Assert.Equal(48, decoder.Height);
        using var bitmap = new SKBitmap(192, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frames = 0;
        var first = SKColors.Empty;
        var last = SKColors.Empty;
        while (decoder.Next() is { } frame)
        {
            using (frame)
            {
                frame.CopyTo(bitmap);
                last = bitmap.GetPixel(96, 24);
                first = frames++ == 0 ? last : first;
            }
        }

        Assert.InRange(frames, info.FrameCount - 2, info.FrameCount);
        AssertMostly(SKColors.Red, first);
        AssertMostly(SKColors.Blue, last);
    }

    [Fact]
    public void OnlyTheVisiblePartOfAPaddedPictureIsDecoded()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var padded = Path.Combine(_folder, "180.mp4");
        WriteTestVideo(padded, height: 180);

        using var decoder = MediaFoundationDecoder.Open(padded);
        Assert.Equal(320, decoder.Width);
        Assert.Equal(180, decoder.Height);
        using var frame = decoder.Next();
        Assert.NotNull(frame);
        using var bitmap = new SKBitmap(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        frame.CopyTo(bitmap);
        AssertMostly(SKColors.Red, bitmap.GetPixel(160, 2));
        AssertMostly(SKColors.Red, bitmap.GetPixel(160, 177));
    }

    [Fact]
    public void AFragmentedMp4IsRewrittenAndBakes()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var fragmented = Path.Combine(_folder, "fragmented.mp4");
        WriteTestVideo(fragmented, fragmented: true);
        Assert.True(Mp4Defragmenter.IsFragmented(fragmented));
        Assert.False(Mp4Defragmenter.IsFragmented(_video));

        var plain = Path.Combine(_folder, "plain.mp4");
        Mp4Defragmenter.Defragment(fragmented, plain, TestContext.Current.CancellationToken);
        Assert.False(Mp4Defragmenter.IsFragmented(plain));

        var output = Path.Combine(_folder, "fragmented.ptclip");
        var info = VideoBaker.Bake(new BakeRequest(fragmented, output, 64, 64, LoopCrossfade: false), cancellationToken: TestContext.Current.CancellationToken);
        Assert.InRange(info.FrameCount, 44, 46);
        using var reader = ClipReader.Open(output);
        AssertMostly(SKColors.Red, Centre(reader, 2));
        AssertMostly(SKColors.Blue, Centre(reader, info.FrameCount - 3));
        PreparedSource.Clear();
    }

    [Fact]
    public void OnlyTheTrimmedPartIsBaked()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var output = Path.Combine(_folder, "green.ptclip");

        var info = VideoBaker.Bake(new BakeRequest(_video, output, 64, 64, Start: TimeSpan.FromSeconds(1.1d), End: TimeSpan.FromSeconds(1.9d), LoopCrossfade: false), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(12, info.FrameCount);
        using var reader = ClipReader.Open(output);
        for (var i = 0; i < info.FrameCount; i++)
        {
            AssertMostly(SKColors.Lime, Centre(reader, i));
        }
    }

    [Fact]
    public void ACancelledBakeLeavesNothingBehind()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var output = Path.Combine(_folder, "cancelled.ptclip");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new SynchronousProgress(p =>
        {
            if (p.Frames > 3)
            {
                cancel.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => VideoBaker.Bake(new BakeRequest(_video, output, 64, 64), progress, cancel.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [Fact]
    public void AFilmstripSpreadsPicturesOverTheWholeVideo()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var folder = Path.Combine(_folder, "strip");
        var frames = new List<ExtractedFrame>();

        var written = VideoFrames.Filmstrip(_video, 3, folder, frames.Add, TestContext.Current.CancellationToken);

        Assert.Equal(3, written);
        Assert.Equal([0, 1, 2], frames.Select(f => f.Index));
        for (var i = 0; i < 3; i++)
        {
            using var bitmap = SKBitmap.Decode(frames[i].Path);
            Assert.Equal(VideoFrames.ThumbnailHeight, bitmap.Height);
            Assert.Equal(150, bitmap.Width);
            AssertMostly(Colours[i], bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
        }
    }

    [Fact]
    public void ThePreviewOfAPartHasOneFramePerStep()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var folder = Path.Combine(_folder, "range");
        var frames = new List<ExtractedFrame>();

        var written = VideoFrames.Range(_video, folder, TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(1.9), 15, frames.Add, TestContext.Current.CancellationToken);

        Assert.InRange(written, 11, 13);
        Assert.Equal(Enumerable.Range(0, written), frames.Select(f => f.Index));
        Assert.Equal(TimeSpan.FromSeconds(1.1), frames[0].Time);
        foreach (var frame in frames)
        {
            using var bitmap = SKBitmap.Decode(frame.Path);
            Assert.Equal(320, bitmap.Width);
            AssertMostly(SKColors.Lime, bitmap.GetPixel(160, bitmap.Height / 2));
        }

        Assert.Empty(Directory.GetFiles(folder, "*.partial"));
    }

    [Fact]
    public void ACancelledPreviewStops()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var folder = Path.Combine(_folder, "cancelled-range");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var seen = 0;

        Assert.Throws<OperationCanceledException>(() => VideoFrames.Range(_video, folder, TimeSpan.Zero, TimeSpan.FromSeconds(3), 15, _ =>
        {
            if (++seen == 4)
            {
                cancel.Cancel();
            }
        }, cancel.Token));
        Assert.Equal(4, Directory.GetFiles(folder, "f*.jpg").Length);
        Assert.Empty(Directory.GetFiles(folder, "*.partial"));
    }

    [Fact]
    public void AFileThatIsNotAVideoIsReportedClearly()
    {
        Assert.SkipUnless(File.Exists(_video), "Media Foundation is not available.");
        var bogus = Path.Combine(_folder, "notes.mp4");
        File.WriteAllText(bogus, "not a video");

        Assert.Throws<InvalidDataException>(() => VideoBaker.Bake(new BakeRequest(bogus, Path.Combine(_folder, "x.ptclip"), 64, 64), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void APictureBakesToAPanelSizedWallpaper()
    {
        var picture = Path.Combine(_folder, "wide.png");
        using (var bitmap = new SKBitmap(400, 100))
        {
            bitmap.Erase(SKColors.Red);
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(200, 0, 200, 100, paint);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(picture, data.ToArray());
        }

        var output = Path.Combine(_folder, "wall.jpg");
        ImageBaker.Bake(picture, output, 100, 100, new CropRect(0.5d, 0d, 0.25d, 1d));

        using var result = SKBitmap.Decode(output);
        Assert.Equal(100, result.Width);
        AssertMostly(SKColors.Blue, result.GetPixel(50, 50));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class SynchronousProgress(Action<BakeProgress> report) : IProgress<BakeProgress>
    {
        public void Report(BakeProgress value) => report(value);
    }
}
