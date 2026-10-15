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

    /// <summary>How long to wait for another helper to finish preparing the same video.</summary>
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The file to decode for <paramref name="source"/>: itself, or a plain copy of a fragmented MP4.</summary>
    public static string Resolve(string source, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(source);
        if (!file.Exists || !Mp4Defragmenter.IsFragmented(file.FullName))
        {
            return source;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var prepared = Path.Combine(CacheFolder, name + ".mp4");
        if (File.Exists(prepared))
        {
            return prepared;
        }

        // The import screen runs several helper processes over the same video
        // at once. They queue here so the copy is made once, and each writes
        // under its own name and puts the result in place in a single step, so
        // nobody can read or decode a half-written copy.
        using var queue = new Mutex(false, @"Local\Panelith.prepared." + name);
        var mine = true;
        try
        {
            mine = queue.WaitOne(PrepareTimeout);
        }
        catch (AbandonedMutexException)
        {
            // Whoever held it stopped part way; the file below says whether it finished.
        }

        try
        {
            if (File.Exists(prepared))
            {
                return prepared;
            }

            Directory.CreateDirectory(CacheFolder);
            var working = Path.Combine(CacheFolder, string.Create(CultureInfo.InvariantCulture, $"{name}.{Environment.ProcessId}.partial"));
            try
            {
                Mp4Defragmenter.Defragment(file.FullName, working, cancellationToken);
                File.Move(working, prepared, overwrite: false);
            }
            catch (IOException) when (File.Exists(prepared))
            {
                // Another helper got there first; its copy is as good as this one.
            }
            finally
            {
                TryDelete(working);
            }

            return prepared;
        }
        finally
        {
            if (mine)
            {
                queue.ReleaseMutex();
            }
        }
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
            TryDelete(file);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
