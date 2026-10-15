namespace Panelith.Engine;

/// <summary>
/// Where Panelith keeps its configuration, themes and user panel profiles.
/// </summary>
/// <remarks>
/// <para>
/// Every path is derived from one root so that a portable install - or a test -
/// can relocate the whole lot by setting <c>PANELITH_HOME</c>. Users who want
/// their themes on a synced drive, or who run Panelith from a USB stick, should
/// not have to hunt through three platform-specific folders.
/// </para>
/// <para>
/// The defaults follow each platform's convention rather than inventing one:
/// <c>$XDG_CONFIG_HOME/panelith</c> on Linux, <c>%APPDATA%\Panelith</c> on
/// Windows, and <c>~/Library/Application Support/Panelith</c> on macOS.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>The environment variable that relocates the whole configuration tree.</summary>
    public const string HomeVariable = "PANELITH_HOME";

    /// <summary>The name this app used before it became Panelith, still honoured for upgrades.</summary>
    private const string PreviousName = "RigGlass";

    /// <summary>Creates paths rooted at a folder.</summary>
    /// <param name="root">The configuration root.</param>
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The configuration root.</summary>
    public string Root { get; }

    /// <summary>The main configuration file.</summary>
    public string ConfigurationFile => Path.Combine(Root, "panelith.json");

    /// <summary>Folder holding user themes, one subfolder per theme.</summary>
    public string ThemesDirectory => Path.Combine(Root, "themes");

    /// <summary>Folder holding user-supplied panel profiles.</summary>
    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    /// <summary>Folder for log files.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Folder for captured frames and other scratch output.</summary>
    public string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>The paths for the current machine and user.</summary>
    public static AppPaths Default { get; } = new(ResolveDefaultRoot());

    /// <summary>The folder holding one named theme.</summary>
    public string ThemeDirectory(string themeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);

        // Theme ids come from configuration files and from the UI, so they are
        // treated as untrusted input: a traversal here would let a theme read or
        // write anywhere the user can.
        var name = Path.GetFileName(themeId.Trim());
        if (name.Length == 0 || name is "." or "..")
        {
            throw new ArgumentException($"\"{themeId}\" is not a usable theme id.", nameof(themeId));
        }

        return Path.Combine(ThemesDirectory, name);
    }

    /// <summary>Creates every folder that does not exist yet.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ThemesDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    private static string ResolveDefaultRoot()
    {
        // The current variable wins; the previous name still works for existing setups.
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } overridden)
        {
            return overridden;
        }

        if (Environment.GetEnvironmentVariable("RIGGLASS_HOME") is { Length: > 0 } legacy)
        {
            return legacy;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Panelith");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "Panelith");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDirectory = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;

        return Path.Combine(baseDirectory, "panelith");
    }

    /// <summary>
    /// Moves settings kept under the previous app name to <paramref name="root"/>
    /// when that folder does not exist yet, so an upgrade keeps themes and profiles.
    /// Only a root named Panelith is migrated; a custom root is left alone.
    /// Returns the folder moved from, or null.
    /// </summary>
    public static string? MigrateLegacy(string root)
    {
        var full = Path.GetFullPath(root);
        if (Directory.Exists(full) || LegacyRoot(full) is not { } legacy || !Directory.Exists(legacy))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Directory.Move(legacy, full);

        var oldConfiguration = Path.Combine(full, PreviousName.ToLowerInvariant() + ".json");
        var newConfiguration = new AppPaths(full).ConfigurationFile;
        if (File.Exists(oldConfiguration) && !File.Exists(newConfiguration))
        {
            File.Move(oldConfiguration, newConfiguration);
        }

        return legacy;
    }

    private static string? LegacyRoot(string root)
    {
        var parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is null)
        {
            return null;
        }

        return name switch
        {
            "Panelith" => Path.Combine(parent, PreviousName),
            "panelith" => Path.Combine(parent, PreviousName.ToLowerInvariant()),
            _ => null,
        };
    }
}
