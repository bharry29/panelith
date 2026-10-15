using System.Text.Json;
using System.Text.Json.Serialization;

namespace Panelith.Themes;

/// <summary>Reads and writes theme documents.</summary>
public static class ThemeSerializer
{
    /// <summary>
    /// The options every theme document is read and written with.
    /// </summary>
    /// <remarks>
    /// Enums serialise as camel-case names rather than numbers, because theme
    /// files are hand-edited and <c>"scaling": "fit"</c> is self-explanatory
    /// where <c>"scaling": 0</c> is not. Trailing commas and comments are
    /// tolerated on read for the same reason.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The largest theme document read, in bytes.</summary>
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    /// <summary>The most widgets a theme may contain, counting those inside groups.</summary>
    public const int MaxWidgets = 2000;

    /// <summary>How deeply groups may nest.</summary>
    public const int MaxGroupDepth = 16;

    /// <summary>Reads a theme document's text, refusing files too large to be a real theme.</summary>
    /// <exception cref="ThemeFormatException">The file is larger than <see cref="MaxDocumentBytes"/>.</exception>
    public static string ReadDocument(string file)
    {
        var length = new FileInfo(file).Length;
        if (length > MaxDocumentBytes)
        {
            throw new ThemeFormatException(
                $"The theme file is {length / (1024 * 1024)} MB; themes may be at most {MaxDocumentBytes / (1024 * 1024)} MB.");
        }

        return File.ReadAllText(file);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,

            // Room for MaxGroupDepth nested groups; anything deeper is refused.
            MaxDepth = 96,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }

    /// <summary>Deserialises a theme from JSON text.</summary>
    /// <param name="json">The document.</param>
    /// <param name="basePath">Folder used to resolve asset paths.</param>
    /// <exception cref="ThemeFormatException">The document is not a readable theme.</exception>
    public static Theme Deserialize(string json, string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxDocumentBytes)
        {
            throw new ThemeFormatException($"The theme document is larger than {MaxDocumentBytes / (1024 * 1024)} MB.");
        }

        Theme? theme;
        try
        {
            theme = JsonSerializer.Deserialize<Theme>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ThemeFormatException($"The theme is not valid JSON: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            // Thrown when the polymorphic "type" discriminator names a widget
            // kind this build does not have, which is the common failure when a
            // theme is written for a newer version.
            throw new ThemeFormatException(
                $"The theme uses a widget type this version of Panelith does not understand: {ex.Message}",
                ex);
        }

        if (theme is null)
        {
            throw new ThemeFormatException("The theme document is empty.");
        }

        if (theme.Schema > ThemeSchema.CurrentVersion)
        {
            throw new ThemeFormatException(
                $"The theme targets schema version {theme.Schema} but this build supports up to "
                + $"{ThemeSchema.CurrentVersion}. Update Panelith to use it.");
        }

        if (theme.Schema < ThemeSchema.MinimumSupportedVersion)
        {
            throw new ThemeFormatException(
                $"The theme targets schema version {theme.Schema}, which is no longer supported.");
        }

        var count = 0;
        CheckSize(theme.Widgets, depth: 0, ref count);

        return basePath is null ? theme : theme with { BasePath = basePath };
    }

    private static void CheckSize(IReadOnlyList<ThemeWidget>? widgets, int depth, ref int count)
    {
        if (widgets is null || widgets.Count == 0)
        {
            return;
        }

        if (depth > MaxGroupDepth)
        {
            throw new ThemeFormatException($"The theme nests groups more than {MaxGroupDepth} deep.");
        }

        foreach (var widget in widgets)
        {
            if (widget is null)
            {
                continue;
            }

            if (++count > MaxWidgets)
            {
                throw new ThemeFormatException($"The theme has more than {MaxWidgets} widgets.");
            }

            CheckSize(widget.Children, depth + 1, ref count);
        }
    }

    /// <summary>Serialises a theme to JSON text.</summary>
    public static string Serialize(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return JsonSerializer.Serialize(theme, Options);
    }

    /// <summary>Reads a theme from a file, or from <c>theme.json</c> inside a folder.</summary>
    /// <exception cref="FileNotFoundException">No theme document was found at that path.</exception>
    /// <exception cref="ThemeFormatException">The document is not a readable theme.</exception>
    public static async Task<Theme> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = Directory.Exists(path) ? Path.Combine(path, ThemeSchema.FileName) : path;
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"No theme document at \"{file}\".", file);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var json = await Task.Run(() => ReadDocument(file), cancellationToken).ConfigureAwait(false);
        var theme = Deserialize(json, Path.GetDirectoryName(Path.GetFullPath(file)));

        // A theme with no id is addressed by its folder name, which is how the
        // bundled themes and most hand-written ones are organised.
        return string.IsNullOrWhiteSpace(theme.Id)
            ? theme with { Id = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file))) ?? "theme" }
            : theme;
    }

    /// <summary>Writes a theme to a file, creating the folder if needed.</summary>
    public static async Task SaveAsync(Theme theme, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = Directory.Exists(path) ? Path.Combine(path, ThemeSchema.FileName) : path;
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        await File.WriteAllTextAsync(file, Serialize(theme), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A theme document could not be read.</summary>
public sealed class ThemeFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ThemeFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
