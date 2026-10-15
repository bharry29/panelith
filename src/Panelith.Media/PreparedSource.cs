using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Panelith.Media;

/// <summary>
/// Turns a video Media Foundation cannot open directly into one it can,
/// once, and remembers the result so the import screen's repeated previews
/// do not redo the work.
/// </summary>
public static class PreparedSource
{
    /// <summary>Where prepared copies are kept until the import screen closes.</summary>
    public static string CacheFolder { get; } = Path.Combine(Path.GetTempPath(), "Panelith", "prepared");

    /// <summary>The file to decode for <paramref name="source"/>: itself, or a plain copy of a fragmented MP4.</summary>
    public static string Resolve(string source, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(source);
        if (!file.Exists || !Mp4Defragmenter.IsFragmented(file.FullName))
        {
            return source;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".mp4";
        var prepared = Path.Combine(CacheFolder, name);
        if (!File.Exists(prepared))
        {
            Directory.CreateDirectory(CacheFolder);
            Mp4Defragmenter.Defragment(file.FullName, prepared, cancellationToken);
        }

        return prepared;
    }

    /// <summary>Deletes every prepared copy that is not in use.</summary>
    public static void Clear()
    {
        if (!Directory.Exists(CacheFolder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(CacheFolder))
        {
            try
            {
                File.Delete(file);
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
