namespace Panelith.Themes;

/// <summary>
/// The one rule for keeping a theme inside its own folder.
/// </summary>
/// <remarks>
/// Themes are shared between people, so every path that comes out of a
/// <c>theme.json</c> is somebody else's input. Checking the rule in one place
/// keeps the validator, the asset cache and the theme library from drifting
/// apart: a near-miss such as comparing with a plain <c>StartsWith</c> lets
/// "../rig-other/secret.png" pass for a theme in "rig", because the sibling
/// folder's path really does start with the theme's path.
/// </remarks>
public static class ThemePath
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="folder"/>,
    /// returning false unless the result stays inside that folder.
    /// </summary>
    /// <param name="folder">The folder the result must stay inside.</param>
    /// <param name="relativePath">A path from a theme, relative to <paramref name="folder"/>.</param>
    /// <param name="fullPath">The resolved absolute path, or an empty string.</param>
    public static bool TryResolveInside(string folder, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // An absolute path, a drive letter or a UNC share is not "inside" anything
        // the theme brought with it, whatever it normalises to.
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var resolved = Path.GetFullPath(Path.Combine(folder, relativePath));
        if (!IsInside(folder, resolved))
        {
            return false;
        }

        fullPath = resolved;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="folder"/> itself or
    /// something within it. Both are resolved first, so "..", short names and
    /// trailing separators cannot hide a way out.
    /// </summary>
    /// <param name="folder">The containing folder.</param>
    /// <param name="candidate">The path to test.</param>
    public static bool IsInside(string folder, string candidate)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(candidate);

        // The separator is what stops "rig" from matching "rig-other".
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }
}
