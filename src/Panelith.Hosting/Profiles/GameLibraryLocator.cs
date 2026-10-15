using System.Text.Json;
using System.Text.RegularExpressions;

namespace Panelith.Hosting.Profiles;

/// <summary>Finds the folders game launchers install games into.</summary>
public static partial class GameLibraryLocator
{
    private static readonly string[] PerDriveFolders =
    [
        @"SteamLibrary\steamapps\common",
        @"Steam\steamapps\common",
        @"Program Files (x86)\Steam\steamapps\common",
        @"Program Files\Steam\steamapps\common",
        @"Program Files\Epic Games",
        @"Epic Games",
        @"XboxGames",
        @"Program Files\EA Games",
        @"EA Games",
        @"Program Files (x86)\Origin Games",
        @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
        @"Program Files (x86)\GOG Galaxy\Games",
        @"GOG Games",
        @"Riot Games",
        @"Program Files\Riot Games",
    ];

    /// <summary>Every library folder that exists on this PC.</summary>
    public static IReadOnlyList<string> Locate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var roots = new List<string>();
        foreach (var drive in SafeDrives())
        {
            roots.AddRange(PerDriveFolders.Select(folder => Path.Combine(drive, folder)));
        }

        var steam = SteamInstallPath();
        if (steam is not null)
        {
            roots.Add(Path.Combine(steam, "steamapps", "common"));
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (TryRead(vdf) is { } text)
            {
                roots.AddRange(ParseSteamLibraries(text).Select(library => Path.Combine(library, "steamapps", "common")));
            }
        }

        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests))
        {
            foreach (var file in SafeFiles(manifests, "*.item"))
            {
                if (TryRead(file) is { } json && ParseEpicManifest(json) is { } location)
                {
                    roots.Add(location);
                }
            }
        }

        return [.. roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)];
    }

    /// <summary>Reads the library paths out of Steam's <c>libraryfolders.vdf</c>.</summary>
    public static IReadOnlyList<string> ParseSteamLibraries(string vdf) =>
        [.. SteamPathPattern().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal))];

    /// <summary>Reads the install folder out of an Epic Games launcher manifest.</summary>
    public static string? ParseEpicManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("InstallLocation", out var location) && location.ValueKind == JsonValueKind.String
                ? location.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a file lives inside one of the folders.</summary>
    public static bool IsUnder(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var root in roots)
        {
            var prefix = Normalize(root);
            if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path) =>
        path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    private static string? SteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && path.Length > 0
                ? path.Replace('/', '\\')
                : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (ready)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try
        {
            return Directory.GetFiles(folder, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathPattern();
}
