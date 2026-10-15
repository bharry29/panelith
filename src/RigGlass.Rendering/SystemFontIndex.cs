using SkiaSharp;

namespace RigGlass.Rendering;

/// <summary>
/// Finds a font file on disk when the graphics stack cannot ask the operating
/// system by family name.
/// </summary>
internal static class SystemFontIndex
{
    private static readonly string[] Extensions = [".ttf", ".otf", ".ttc"];

    private static readonly Lazy<string[]> Files = new(Discover);

    /// <summary>A typeface whose file name matches <paramref name="family"/>, or null.</summary>
    public static SKTypeface? Match(string? family, int weight, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        var needle = Compact(family);
        if (needle.Length < 3)
        {
            return null;
        }

        string? regular = null;
        var wantsBold = weight >= 600;
        foreach (var path in Files.Value)
        {
            var name = Compact(Path.GetFileNameWithoutExtension(path));
            if (!name.Contains(needle, StringComparison.Ordinal))
            {
                continue;
            }

            var isBold = name.Contains("bold", StringComparison.Ordinal);
            var isItalic = name.Contains("italic", StringComparison.Ordinal)
                || name.Contains("oblique", StringComparison.Ordinal);

            if (isBold == wantsBold && isItalic == italic)
            {
                return SKTypeface.FromFile(path);
            }

            if (!isBold && !isItalic)
            {
                regular ??= path;
            }
        }

        return regular is null ? null : SKTypeface.FromFile(regular);
    }

    private static string[] Discover()
    {
        var roots = new List<string>
        {
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts"),
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        };

        var found = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(path);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [.. found];
    }

    private static string Compact(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
