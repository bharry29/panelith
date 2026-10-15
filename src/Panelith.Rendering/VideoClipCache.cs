using Microsoft.Extensions.Logging;
using Panelith.Media;
using SkiaSharp;

namespace Panelith.Rendering;

/// <summary>
/// Plays theme clips: opens each <c>.ptclip</c> once and decodes a frame only
/// when the frame index changes, into one bitmap reused for the clip's lifetime.
/// </summary>
/// <remarks>
/// Frames are picked from wall-clock time rather than counted, so a slow frame
/// skips ahead instead of drifting, and two renderers of the same theme (as
/// during a crossfade) show the same frame.
/// </remarks>
internal sealed class VideoClipCache : IDisposable
{
    private readonly ThemeAssetCache _assets;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, Playback?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public VideoClipCache(ThemeAssetCache assets, ILogger? logger)
    {
        _assets = assets;
        _logger = logger;
    }

    /// <summary>
    /// The frame of <paramref name="relativePath"/> due at <paramref name="now"/>,
    /// or null when the clip cannot be played. The cache owns the bitmap.
    /// </summary>
    public SKBitmap? FrameAt(string relativePath, DateTimeOffset now, double speed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_clips.TryGetValue(relativePath, out var playback))
        {
            playback = Open(relativePath);
            _clips[relativePath] = playback;
        }

        if (playback is null)
        {
            return null;
        }

        var index = playback.Reader.Info.FrameAt(TimeSpan.FromMilliseconds(now.ToUnixTimeMilliseconds()), speed);
        if (index != playback.Index)
        {
            try
            {
                playback.Decode(index);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                _logger?.LogWarning("Clip '{Path}' frame {Index} could not be decoded ({Reason}).", relativePath, index, ex.Message);
                playback.Index = index;
            }
        }

        return playback.HasFrame ? playback.Bitmap : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var playback in _clips.Values)
        {
            playback?.Dispose();
        }

        _clips.Clear();
    }

    private Playback? Open(string relativePath)
    {
        if (!_assets.TryResolvePath(relativePath, out var fullPath))
        {
            _logger?.LogWarning("Clip '{Path}' points outside the theme folder or is absolute.", relativePath);
            return null;
        }

        try
        {
            return new Playback(ClipReader.Open(fullPath));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning("Clip '{Path}' could not be opened ({Reason}).", relativePath, ex.Message);
            return null;
        }
    }

    private sealed class Playback(ClipReader reader) : IDisposable
    {
        private byte[] _buffer = [];

        public ClipReader Reader { get; } = reader;

        public SKBitmap Bitmap { get; } = new(new SKImageInfo(
            reader.Info.Width, reader.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        public int Index { get; set; } = -1;

        public bool HasFrame { get; private set; }

        public void Decode(int index)
        {
            var length = Reader.FrameLength(index);
            if (_buffer.Length < length)
            {
                _buffer = new byte[length];
            }

            Reader.ReadFrame(index, _buffer);
            using var data = SKData.CreateCopy(_buffer.AsSpan(0, length));
            using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("the frame is not an image");
            if (codec.Info.Width != Bitmap.Width || codec.Info.Height != Bitmap.Height)
            {
                throw new InvalidDataException($"the frame is {codec.Info.Width}x{codec.Info.Height}, not the clip's {Bitmap.Width}x{Bitmap.Height}");
            }

            var result = codec.GetPixels(Bitmap.Info, Bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                throw new InvalidDataException($"decoding failed ({result})");
            }

            Bitmap.NotifyPixelsChanged();
            Index = index;
            HasFrame = true;
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            Reader.Dispose();
        }
    }
}
