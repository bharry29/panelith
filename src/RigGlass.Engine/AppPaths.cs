namespace RigGlass.Engine;

/// <summary>
/// Where RigGlass keeps its configuration, themes and user panel profiles.
/// </summary>
/// <remarks>
/// <para>
/// Every path is derived from one root so that a portable install - or a test -
/// can relocate the whole lot by setting <c>RIGGLASS_HOME</c>. Users who want
/// their themes on a synced drive, or who run RigGlass from a USB stick, should
/// not have to hunt through three platform-specific folders.
/// </para>
/// <para>
/// The defaults follow each platform's convention rather than inventing one:
/// <c>$XDG_CONFIG_HOME/rigglass</c> on Linux, <c>%APPDATA%\RigGlass</c> on
/// Windows, and <c>~/Library/Application Support/RigGlass</c> on macOS.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>The environment variable that relocates the whole configuration tree.</summary>
    public const string HomeVariable = "RIGGLASS_HOME";

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
    public string ConfigurationFile => Path.Combine(Root, "rigglass.json");

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
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } overridden)
        {
            return overridden;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RigGlass");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "RigGlass");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDirectory = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;

        return Path.Combine(baseDirectory, "rigglass");
    }
}
