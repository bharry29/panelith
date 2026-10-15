using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace RigGlass.Rendering;

/// <summary>
/// Resolves theme font family names to <see cref="SKTypeface"/> instances.
/// </summary>
/// <remarks>
/// Themes are authored on Windows against Inter / Segoe UI and opened on Linux
/// panel hosts. A missing family must never abort a frame — we walk theme-local
/// font files, then the system catalogue, then a bundled fallback, and finally
/// Skia's default, logging each unresolved name once.
/// </remarks>
public sealed class FontResolver : IDisposable
{
    private readonly string? _themeFolder;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<FontKey, SKTypeface> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _themeFontFiles;
    private SKTypeface? _fallback;
    private bool _disposed;

    /// <summary>Creates a resolver scoped to an optional theme folder.</summary>
    public FontResolver(string? themeFolder = null, ILogger? logger = null)
    {
        _themeFolder = themeFolder;
        _logger = logger;
        _themeFontFiles = DiscoverThemeFonts(themeFolder);
    }

    /// <summary>
    /// Resolves a typeface. Never throws; returns Skia's default when nothing
    /// matches.
    /// </summary>
    public SKTypeface Resolve(string? family, int weight = 400, bool italic = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new FontKey(family ?? string.Empty, NormalizeWeight(weight), italic);
        return _cache.GetOrAdd(key, Create);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var face in _cache.Values)
        {
            face.Dispose();
        }

        _cache.Clear();
        _fallback?.Dispose();
        _fallback = null;
    }

    private SKTypeface Create(FontKey key)
    {
        var style = key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
        var fontStyle = new SKFontStyle(key.Weight, 5, style);

        // (a) Font files shipped inside the theme folder.
        if (_themeFontFiles.Count > 0)
        {
            var fromTheme = MatchThemeFile(key.Family, fontStyle);
            if (fromTheme is not null)
            {
                return fromTheme;
            }
        }

        // (b) Installed system fonts by family name.
        if (!string.IsNullOrWhiteSpace(key.Family))
        {
            var matched = SKTypeface.FromFamilyName(key.Family, fontStyle);
            if (matched is not null && !IsFallbackAlias(matched, key.Family))
            {
                return matched;
            }

            matched?.Dispose();
            WarnOnce(key.Family);
        }

        // (c) Bundled / common Linux fallbacks, then Skia default.
        return GetFallback(fontStyle);
    }

    private SKTypeface? MatchThemeFile(string family, SKFontStyle style)
    {
        foreach (var path in _themeFontFiles)
        {
            if (!string.IsNullOrEmpty(family))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.Contains(family, StringComparison.OrdinalIgnoreCase)
                    || family.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var face = SKTypeface.FromFile(path);
                    if (face is not null)
                    {
                        return face;
                    }
                }
            }
        }

        // No family match — if the theme shipped exactly one font, use it.
        if (_themeFontFiles.Count == 1 && string.IsNullOrEmpty(family))
        {
            return SKTypeface.FromFile(_themeFontFiles[0]);
        }

        // Try every file and pick one whose family matches.
        foreach (var path in _themeFontFiles)
        {
            var face = SKTypeface.FromFile(path);
            if (face is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(family)
                || face.FamilyName.Contains(family, StringComparison.OrdinalIgnoreCase)
                || family.Contains(face.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }

            face.Dispose();
        }

        _ = style; // weight matching across files is best-effort via FromFamilyName
        return null;
    }

    private SKTypeface GetFallback(SKFontStyle style)
    {
        foreach (var candidate in FallbackFamilies)
        {
            var face = SKTypeface.FromFamilyName(candidate, style);
            if (face is not null && string.Equals(face.FamilyName, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return face;
            }

            face?.Dispose();
        }

        _fallback ??= SKTypeface.CreateDefault() ?? SKTypeface.Default;
        // Return a distinct instance when possible so disposal of cached entries
        // does not dispose the shared fallback.
        return SKTypeface.FromFamilyName(_fallback.FamilyName, style) ?? SKTypeface.Default;
    }

    private void WarnOnce(string family)
    {
        if (!_warned.TryAdd(family, 0))
        {
            return;
        }

        _logger?.LogWarning(
            "Font family '{Family}' was not found; falling back to a system default. Themes authored on Windows often need their fonts shipped in the theme folder.",
            family);
    }

    /// <summary>
    /// Skia's FromFamilyName silently substitutes a default when the family is
    /// missing; detect that so we can keep searching.
    /// </summary>
    private static bool IsFallbackAlias(SKTypeface matched, string requested)
    {
        if (string.Equals(matched.FamilyName, requested, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Some platforms return "sans-serif" / "Arial" as the substitute.
        return !matched.FamilyName.Contains(requested, StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizeWeight(int weight) => Math.Clamp(weight, 100, 900);

    private static List<string> DiscoverThemeFonts(string? themeFolder)
    {
        if (string.IsNullOrWhiteSpace(themeFolder) || !Directory.Exists(themeFolder))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var ext in new[] { "*.ttf", "*.otf", "*.ttc" })
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(themeFolder, ext, SearchOption.AllDirectories));
            }
            catch (IOException)
            {
                // Theme folder vanished between validation and render — ignore.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    private static readonly string[] FallbackFamilies =
    [
        "Inter",
        "DejaVu Sans",
        "Liberation Sans",
        "Noto Sans",
        "FreeSans",
        "Arial",
        "Helvetica",
    ];

    private readonly record struct FontKey(string Family, int Weight, bool Italic);
}
