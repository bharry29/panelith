using System.Collections.Concurrent;

namespace RigGlass.Monitoring.IO;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for fixture-driven tests. Paths are
/// compared with ordinal rules after normalising directory separators.
/// </summary>
public sealed class MemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, string> _files =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _directories =
        new(StringComparer.Ordinal);

    /// <summary>Registers a directory so enumeration finds it even when empty.</summary>
    public MemoryFileSystem AddDirectory(string path)
    {
        var normalized = Normalize(path);
        EnsureAncestors(normalized);
        _directories[normalized] = 0;
        return this;
    }

    /// <summary>Writes a file, creating parent directories as needed.</summary>
    public MemoryFileSystem AddFile(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var normalized = Normalize(path);
        EnsureAncestors(normalized);
        _files[normalized] = contents;
        return this;
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) =>
        _directories.ContainsKey(Normalize(path));

    /// <inheritdoc />
    public bool FileExists(string path) =>
        _files.ContainsKey(Normalize(path));

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string path)
    {
        var prefix = Normalize(path).TrimEnd('/');
        if (prefix.Length == 0)
        {
            prefix = "/";
        }

        var needle = prefix == "/" ? "/" : prefix + "/";
        foreach (var directory in _directories.Keys)
        {
            if (!directory.StartsWith(needle, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = directory.AsSpan(needle.Length);
            if (remainder.Length > 0 && !remainder.Contains('/'))
            {
                yield return directory;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string path)
    {
        var prefix = Normalize(path).TrimEnd('/');
        if (prefix.Length == 0)
        {
            prefix = "/";
        }

        var needle = prefix == "/" ? "/" : prefix + "/";
        foreach (var file in _files.Keys)
        {
            if (!file.StartsWith(needle, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = file.AsSpan(needle.Length);
            if (remainder.Length > 0 && !remainder.Contains('/'))
            {
                yield return file;
            }
        }
    }

    /// <inheritdoc />
    public string? ReadAllText(string path) =>
        _files.TryGetValue(Normalize(path), out var contents) ? contents : null;

    private void EnsureAncestors(string path)
    {
        var current = path;
        while (true)
        {
            var slash = current.LastIndexOf('/');
            if (slash <= 0)
            {
                _directories["/"] = 0;
                return;
            }

            current = current[..slash];
            if (current.Length == 0)
            {
                _directories["/"] = 0;
                return;
            }

            _directories[current] = 0;
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized.Length == 0 ? "/" : normalized;
    }
}
