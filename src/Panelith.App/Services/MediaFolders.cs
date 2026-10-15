namespace Panelith.App.Services;

/// <summary>
/// Where Panelith keeps videos people may want to see again: the full YouTube
/// downloads, and an MP4 of every loop it made into a screen.
/// </summary>
public static class MediaFolders
{
    private const string InvalidCharacters = "\"<>|:*?\\/";

    /// <summary><c>Videos\Panelith</c>, or a folder in local app data when the user has no Videos folder.</summary>
    public static string Root
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return videos.Length > 0
                ? Path.Combine(videos, "Panelith")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Panelith", "Videos");
        }
    }

    /// <summary>Full videos downloaded from YouTube.</summary>
    public static string Downloads => Path.Combine(Root, "Downloads");

    /// <summary>The loops made into screens, as MP4s any player can open.</summary>
    public static string Screens => Path.Combine(Root, "Screens");

    /// <summary>Turns <paramref name="name"/> into something Windows accepts as a file name, at most 100 characters.</summary>
    public static string SafeName(string? name)
    {
        var cleaned = new string([.. (name ?? string.Empty).Select(c => InvalidCharacters.Contains(c) || char.IsControl(c) ? ' ' : c)]);
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[..100].TrimEnd();
        }

        return cleaned.Length > 0 ? cleaned : "video";
    }

    /// <summary>A path in <paramref name="folder"/> for <paramref name="name"/> that no file uses yet, adding " (2)" and so on.</summary>
    public static string FreshFile(string folder, string name, string extension)
    {
        Directory.CreateDirectory(folder);
        var stem = SafeName(name);
        var path = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        }

        return path;
    }
}
