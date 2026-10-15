using Panelith.Engine;
using Panelith.Themes;

namespace Panelith.Hosting;

/// <summary>Finds theme documents shipped with Panelith and ones the user added.</summary>
public sealed class ThemeLibrary
{
    private readonly AppPaths _paths;

    /// <summary>Creates a library for the given configuration root.</summary>
    public ThemeLibrary(AppPaths paths) => _paths = paths;

    /// <summary>
    /// Loads a theme by id or by a path to its <c>theme.json</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">No theme with that name exists.</exception>
    public Theme Load(string themeIdOrPath)
    {
        var path = Resolve(themeIdOrPath)
            ?? throw new FileNotFoundException(
                $"No theme named \"{themeIdOrPath}\" was found. Looked in {_paths.ThemesDirectory} and next to the panelith executable.",
                themeIdOrPath);

        var json = ThemeSerializer.ReadDocument(path);
        var theme = ThemeSerializer.Deserialize(json, Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(theme.Id)
            ? theme with { Id = Path.GetFileName(Path.GetDirectoryName(path)) ?? "theme" }
            : theme;
    }

    /// <summary>
    /// Loads a theme by id or by a path to its <c>theme.json</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">No theme with that name exists.</exception>
    public Task<Theme> LoadAsync(string themeIdOrPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(themeIdOrPath));
    }

    /// <summary>Every theme.json that can be loaded, user themes first.</summary>
    public IReadOnlyList<string> List()
    {
        var found = new List<string>();
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            found.AddRange(Directory.EnumerateFiles(root, "theme.json", SearchOption.AllDirectories));
        }

        return found;
    }

    /// <summary>
    /// One entry per theme id, user themes shadowing bundled ones of the same
    /// name, so a copied-and-edited theme replaces the original instead of
    /// appearing twice.
    /// </summary>
    public IReadOnlyList<ThemeEntry> Catalog()
    {
        var entries = new Dictionary<string, ThemeEntry>(StringComparer.OrdinalIgnoreCase);
        var userRoot = Path.GetFullPath(_paths.ThemesDirectory);

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(folder, "theme.json");
                var id = Path.GetFileName(folder);
                if (File.Exists(file) && !entries.ContainsKey(id))
                {
                    var isUser = Path.GetFullPath(root).Equals(userRoot, StringComparison.OrdinalIgnoreCase);
                    entries[id] = new ThemeEntry(id, file, isUser);
                }
            }
        }

        return [.. entries.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The theme.json path for an id or an explicit file, or null.</summary>
    public string? Resolve(string themeIdOrPath)
    {
        if (string.IsNullOrWhiteSpace(themeIdOrPath))
        {
            return null;
        }

        if (File.Exists(themeIdOrPath))
        {
            return Path.GetFullPath(themeIdOrPath);
        }

        var asFolder = Path.Combine(themeIdOrPath, "theme.json");
        if (File.Exists(asFolder))
        {
            return Path.GetFullPath(asFolder);
        }

        foreach (var root in Roots())
        {
            var candidate = Path.Combine(root, themeIdOrPath, "theme.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> Roots()
    {
        yield return _paths.ThemesDirectory;

        var bundled = Path.Combine(AppContext.BaseDirectory, "themes");
        yield return bundled;

        // Running from the source tree, the themes folder sits next to src/.
        var walk = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && walk is not null; depth++)
        {
            var candidate = Path.Combine(walk.FullName, "themes");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }

            walk = walk.Parent;
        }
    }
}

/// <summary>A theme the library can load.</summary>
/// <param name="Id">The folder name, which is how configuration refers to it.</param>
/// <param name="File">The theme.json path.</param>
/// <param name="IsUserTheme">Whether it lives in the user's theme folder rather than shipping with Panelith.</param>
public sealed record ThemeEntry(string Id, string File, bool IsUserTheme);
