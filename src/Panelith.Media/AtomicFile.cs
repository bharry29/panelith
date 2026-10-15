namespace Panelith.Media;

/// <summary>
/// Writes a file in one step. The bytes go under a working name and are put
/// in place once they are all there, so nothing ever reads half a picture,
/// and a write that fails leaves nothing behind.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, replacing what is there.</summary>
    public static void Write(string path, byte[] bytes)
    {
        var working = path + ".partial";
        try
        {
            File.WriteAllBytes(working, bytes);
            File.Move(working, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(working);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
