using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OmniPanel.Imaging;
using SkiaSharp;

namespace OmniPanel.Rendering;

/// <summary>
/// Resolves and caches theme-relative images.
/// </summary>
/// <remarks>
/// <see cref="OmniPanel.Themes.ThemeValidator"/> already rejects absolute and
/// escaping paths, but a theme can be edited between validation and render, so
/// every lookup re-checks containment. Missing assets return a visible
/// placeholder bitmap rather than throwing into the render loop.
/// </remarks>
public sealed class ThemeAssetCache : IDisposable
{
    private readonly string? _themeFolder;
    private readonly IImageCodec _codec;
    private readonly ILogger? _logger;
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.OrdinalIgnoreCase);
    private SKBitmap? _placeholder;
    private bool _disposed;

    /// <summary>Creates a cache rooted at <paramref name="themeFolder"/>.</summary>
    public ThemeAssetCache(
        string? themeFolder,
        IImageCodec? codec = null,
        int capacity = 64,
        ILogger? logger = null)
    {
        _themeFolder = string.IsNullOrWhiteSpace(themeFolder) ? null : Path.GetFullPath(themeFolder);
        _codec = codec ?? new SkiaImageCodec();
        _capacity = Math.Max(4, capacity);
        _logger = logger;
    }

    /// <summary>
    /// Loads <paramref name="relativePath"/> as an <see cref="SKBitmap"/>,
    /// optionally resized. The caller must not dispose the returned bitmap —
    /// the cache owns it.
    /// </summary>
    public SKBitmap GetBitmap(string? relativePath, int? width = null, int? height = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(relativePath) || _themeFolder is null)
        {
            return GetPlaceholder(width ?? 32, height ?? 32);
        }

        if (!TryResolvePath(relativePath, out var fullPath))
        {
            WarnOnce(relativePath, "points outside the theme folder or is absolute");
            return GetPlaceholder(width ?? 32, height ?? 32);
        }

        var key = $"{fullPath}|{width}|{height}";
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastAccess = Environment.TickCount64;
            return existing.Bitmap;
        }

        if (!File.Exists(fullPath))
        {
            WarnOnce(relativePath, "does not exist");
            return GetPlaceholder(width ?? 32, height ?? 32);
        }

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            using var frame = _codec.Decode(bytes, width, height);
            var bitmap = FrameToBitmap(frame);
            EvictIfNeeded();
            _entries[key] = new CacheEntry(bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            WarnOnce(relativePath, ex.Message);
            return GetPlaceholder(width ?? 32, height ?? 32);
        }
    }

    /// <summary>
    /// Returns the raw file bytes for animated-frame decoding, or
    /// <see langword="null"/> when the asset cannot be read.
    /// </summary>
    public byte[]? TryReadBytes(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _themeFolder is null)
        {
            return null;
        }

        if (!TryResolvePath(relativePath, out var fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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
        foreach (var entry in _entries.Values)
        {
            entry.Bitmap.Dispose();
        }

        _entries.Clear();
        _placeholder?.Dispose();
        _placeholder = null;
    }

    private bool TryResolvePath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (_themeFolder is null)
        {
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        // Block path traversal even when the OS would resolve it.
        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            var probe = Path.GetFullPath(Path.Combine(_themeFolder, relativePath));
            if (!probe.StartsWith(_themeFolder, StringComparison.OrdinalIgnoreCase)
                && !probe.StartsWith(_themeFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Still require containment after normalisation.
            if (!probe.StartsWith(_themeFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = probe;
            return true;
        }

        fullPath = Path.GetFullPath(Path.Combine(_themeFolder, relativePath));
        var root = _themeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count < _capacity)
        {
            return;
        }

        // Drop the least-recently-used half to avoid thrashing one-by-one.
        var victims = _entries
            .OrderBy(e => e.Value.LastAccess)
            .Take(Math.Max(1, _capacity / 4))
            .Select(e => e.Key)
            .ToArray();

        foreach (var key in victims)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                entry.Bitmap.Dispose();
            }
        }
    }

    private SKBitmap GetPlaceholder(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // One shared placeholder is fine; callers draw it scaled into their box.
        if (_placeholder is not null)
        {
            return _placeholder;
        }

        var info = new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(40, 40, 48, 255));
            using var paint = new SKPaint { Color = new SKColor(220, 64, 160, 255), IsStroke = true, StrokeWidth = 2 };
            canvas.DrawRect(1, 1, 30, 30, paint);
            canvas.DrawLine(2, 2, 30, 30, paint);
            canvas.DrawLine(30, 2, 2, 30, paint);
        }

        _placeholder = bitmap;
        _ = (width, height);
        return bitmap;
    }

    private void WarnOnce(string path, string reason)
    {
        if (!_warned.TryAdd(path, 0))
        {
            return;
        }

        _logger?.LogWarning("Theme asset '{Path}' could not be loaded ({Reason}).", path, reason);
    }

    private static SKBitmap FrameToBitmap(FrameBuffer frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var span = frame.ReadOnlySpan;
        var dest = bitmap.GetPixelSpan();
        span.CopyTo(dest);
        return bitmap;
    }

    private sealed class CacheEntry(SKBitmap bitmap)
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public long LastAccess { get; set; } = Environment.TickCount64;
    }
}
