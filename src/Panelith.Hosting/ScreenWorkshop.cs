using System.Globalization;
using System.Text;
using Panelith.Engine;
using Panelith.Themes;

namespace Panelith.Hosting;

/// <summary>What a new video screen shows on top of the video.</summary>
public enum VideoOverlay
{
    /// <summary>Just the video.</summary>
    None,

    /// <summary>A line of CPU, GPU and memory readings along the bottom.</summary>
    Readings,

    /// <summary>The time and date.</summary>
    Clock,
}

/// <summary>
/// Creates and changes screens in the user's screens folder: new video
/// screens, editable copies of bundled screens, and wallpapers.
/// </summary>
public sealed class ScreenWorkshop
{
    /// <summary>The scrim used under readings when the caller does not choose one.</summary>
    public const string DefaultScrim = "#00000059";

    private readonly ThemeLibrary _library;
    private readonly string _userRoot;

    /// <summary>Creates a workshop writing into <paramref name="paths"/>' screens folder.</summary>
    public ScreenWorkshop(ThemeLibrary library, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _library = library;
        _userRoot = Path.GetFullPath(paths.ThemesDirectory);
    }

    /// <summary>
    /// Reserves a new, empty folder for a screen called <paramref name="name"/>.
    /// The id is the name made filename-safe, with a number added if it is taken.
    /// </summary>
    public (string Id, string Folder) NewScreenFolder(string name)
    {
        var stem = Slug(name);
        for (var n = 1; n < 1000; n++)
        {
            var id = n == 1 ? stem : $"{stem}-{n}";
            if (_library.Resolve(id) is not null)
            {
                continue;
            }

            var folder = Path.Combine(_userRoot, id);
            if (Directory.Exists(folder))
            {
                continue;
            }

            Directory.CreateDirectory(folder);
            return (id, folder);
        }

        throw new IOException($"Could not find a free folder name for \"{name}\".");
    }

    /// <summary>A screen that loops <paramref name="clip"/> full-bleed, with an optional overlay.</summary>
    public static Theme VideoScreen(string id, string name, int width, int height, string clip, double speed, VideoOverlay overlay)
    {
        var widgets = new List<ThemeWidget>();
        string? scrim = null;
        var unit = height / 480d;
        switch (overlay)
        {
            case VideoOverlay.Readings:
                scrim = "#00000033";
                widgets.Add(new TextWidget
                {
                    Id = "readings",
                    X = width * 0.03d,
                    Y = height - (64d * unit),
                    Width = width * 0.94d,
                    Height = 48d * unit,
                    Text = "CPU {@cpu.temp:0}\u00B0  {@cpu.load:0}%   \u00B7   GPU {@gpu.temp:0}\u00B0  {@gpu.load:0}%   \u00B7   RAM {@mem.load:0}%",
                    FontSize = 34d * unit,
                    FontWeight = 600,
                    Align = HorizontalAlignment.Right,
                    VerticalAlign = VerticalAlignment.Middle,
                    StrokeColor = "#000000aa",
                    StrokeWidth = 3d * unit,
                });
                break;
            case VideoOverlay.Clock:
                scrim = "#00000033";
                widgets.Add(new TextWidget
                {
                    Id = "time",
                    X = width * 0.55d,
                    Y = 40d * unit,
                    Width = width * 0.42d,
                    Height = 300d * unit,
                    Text = "{@sys.time}",
                    FontSize = 240d * unit,
                    FontWeight = 300,
                    Align = HorizontalAlignment.Right,
                    VerticalAlign = VerticalAlignment.Middle,
                    StrokeColor = "#00000088",
                    StrokeWidth = 4d * unit,
                });
                widgets.Add(new TextWidget
                {
                    Id = "date",
                    X = width * 0.55d,
                    Y = 340d * unit,
                    Width = width * 0.42d,
                    Height = 60d * unit,
                    Text = "{@sys.date}",
                    FontSize = 44d * unit,
                    Align = HorizontalAlignment.Right,
                    StrokeColor = "#00000088",
                    StrokeWidth = 3d * unit,
                });
                break;
        }

        return new Theme
        {
            Id = id,
            Name = name,
            Author = Environment.UserName,
            Description = "A video loop made with Panelith.",
            Canvas = new ThemeCanvas(width, height, ThemeScaleMode.Fill, "#000000"),
            Background = new ThemeBackground("#000000", Video: clip, Scrim: scrim, VideoSpeed: speed),
            Defaults = ThemeDefaults.Default with { FontFamily = "Segoe UI Variable Display", Color = "#f2f5fa" },
            Widgets = widgets,
        };
    }

    /// <summary>
    /// The user-folder copy of a screen, making one first when it ships with
    /// Panelith so the original stays untouched. Returns the folder to edit.
    /// </summary>
    public (string Id, string Folder, Theme Theme) Editable(string id)
    {
        var file = _library.Resolve(id) ?? throw new FileNotFoundException($"There is no screen called \"{id}\".", id);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var theme = _library.Load(file);
        if (IsInside(folder, _userRoot))
        {
            return (id, folder, theme);
        }

        var (copyId, copyFolder) = NewScreenFolder(id + "-custom");
        CopyFolder(folder, copyFolder);
        var copy = theme with { Id = copyId, Name = theme.Name + " (custom)", BasePath = copyFolder };
        Save(copy, copyFolder);
        return (copyId, copyFolder, copy);
    }

    /// <summary>
    /// <paramref name="theme"/> with a new wallpaper. Exactly one of
    /// <paramref name="image"/> and <paramref name="video"/> is set; the other is cleared.
    /// </summary>
    public static Theme WithWallpaper(Theme theme, string? image, string? video, double speed, string? scrim)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if ((image is null) == (video is null))
        {
            throw new ArgumentException("A wallpaper is either a picture or a video.");
        }

        return theme with
        {
            Background = theme.Background with
            {
                Image = image,
                Fit = ImageFit.Cover,
                Video = video,
                VideoSpeed = speed,
                Scrim = scrim,
            },
        };
    }

    /// <summary><paramref name="theme"/> with its wallpaper picture or video removed.</summary>
    public static Theme WithoutWallpaper(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return theme with { Background = theme.Background with { Image = null, Video = null, Scrim = null, VideoSpeed = 1d } };
    }

    /// <summary>
    /// A file name in <paramref name="folder"/> that does not exist yet, so a
    /// new wallpaper never replaces one a running renderer still has open.
    /// </summary>
    public static string FreshAssetName(string folder, string stem, string extension)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var name = n == 0 ? $"{stem}-{stamp}{extension}" : $"{stem}-{stamp}-{n}{extension}";
            if (!File.Exists(Path.Combine(folder, name)))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// Deletes wallpaper files in <paramref name="folder"/> the theme no longer
    /// uses. Files still open elsewhere are left for next time.
    /// </summary>
    public static void RemoveUnusedWallpapers(string folder, Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { theme.Background.Image, theme.Background.Video })
        {
            if (path is not null)
            {
                keep.Add(Path.GetFileName(path));
            }
        }

        foreach (var file in Directory.EnumerateFiles(folder, "wallpaper-*"))
        {
            if (keep.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Writes <paramref name="theme"/> to <c>theme.json</c> in <paramref name="folder"/>, atomically.</summary>
    public void Save(Theme theme, string folder)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var full = Path.GetFullPath(folder);
        if (!IsInside(full, _userRoot))
        {
            throw new UnauthorizedAccessException("Only screens in your own screens folder can be changed.");
        }

        var target = Path.Combine(full, ThemeSchema.FileName);
        var partial = target + ".partial";
        File.WriteAllText(partial, ThemeSerializer.Serialize(theme), new UTF8Encoding(false));
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>Deletes a screen from the user's folder.</summary>
    public void Delete(string id)
    {
        var file = _library.Resolve(id);
        if (file is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (!IsInside(folder, _userRoot))
        {
            throw new UnauthorizedAccessException("Screens that ship with Panelith cannot be deleted.");
        }

        DeleteFolder(folder);
    }

    /// <summary>
    /// Deletes a screen folder with its <c>theme.json</c> last, so a picture or
    /// clip that is still open stops the delete with the screen still whole,
    /// instead of leaving files behind that no screen lists.
    /// </summary>
    public static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var theme = Path.Combine(folder, ThemeSchema.FileName);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(path, theme, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }

        File.Delete(theme);
        Directory.Delete(folder, recursive: true);
    }

    /// <summary>A folder-safe id: lower-case letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "screen" : slug;
    }

    private static bool IsInside(string folder, string root)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            if (new DirectoryInfo(directory).LinkTarget is null)
            {
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
            }
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null)
            {
                continue;
            }

            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            info.CopyTo(target, overwrite: false);
        }
    }
}
