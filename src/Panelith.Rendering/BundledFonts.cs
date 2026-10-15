using SkiaSharp;

namespace Panelith.Rendering;

/// <summary>
/// Inter, embedded so text still draws when the process cannot see system fonts.
/// </summary>
/// <remarks>
/// The Linux Skia build Panelith ships does not link fontconfig, so
/// family-name lookup cannot see fonts installed on the
/// machine. Without a face of our own, every label renders as an empty run and
/// a panel looks blank apart from shapes. Inter is included under the SIL Open
/// Font License; see Fonts/OFL.txt.
/// </remarks>
internal static class BundledFonts
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKData> Cache = new(StringComparer.Ordinal);

    /// <summary>A new typeface for the requested weight and slant, or null if the files are missing.</summary>
    public static SKTypeface? Create(int weight, bool italic)
    {
        var fileName = (weight >= 600, italic) switch
        {
            (true, true) => "Inter-BoldItalic.ttf",
            (true, false) => "Inter-Bold.ttf",
            (false, true) => "Inter-Italic.ttf",
            _ => "Inter-Regular.ttf",
        };

        var data = Load(fileName) ?? Load("Inter-Regular.ttf");
        return data is null ? null : SKTypeface.FromData(data);
    }

    private static SKData? Load(string fileName)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            var assembly = typeof(BundledFonts).Assembly;
            string? resource = null;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(fileName, StringComparison.Ordinal))
                {
                    resource = name;
                    break;
                }
            }

            if (resource is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return null;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var data = SKData.CreateCopy(copy.ToArray());
            Cache[fileName] = data;
            return data;
        }
    }
}
