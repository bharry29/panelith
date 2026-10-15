namespace OmniPanel.Monitoring.IO;

/// <summary>
/// Narrow filesystem surface used by Linux sensor sources so tests can supply a
/// fabricated <c>/sys</c> or <c>/proc</c> tree without touching the live host.
/// </summary>
public interface IFileSystem
{
    /// <summary>Whether <paramref name="path"/> exists as a directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>Whether <paramref name="path"/> exists as a file.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Enumerates immediate child directory paths of <paramref name="path"/>,
    /// or an empty sequence when the directory is missing.
    /// </summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>
    /// Enumerates immediate child file paths of <paramref name="path"/>,
    /// or an empty sequence when the directory is missing.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string path);

    /// <summary>
    /// Reads the entire file as UTF-8 text. Returns <see langword="null"/> when
    /// the file is missing or unreadable — sources treat that as "sensor absent"
    /// rather than throwing.
    /// </summary>
    string? ReadAllText(string path);
}
