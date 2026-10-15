using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniPanel.Themes;

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
                $"The theme uses a widget type this version of OmniPanel does not understand: {ex.Message}",
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
                + $"{ThemeSchema.CurrentVersion}. Update OmniPanel to use it.");
        }

        if (theme.Schema < ThemeSchema.MinimumSupportedVersion)
        {
            throw new ThemeFormatException(
                $"The theme targets schema version {theme.Schema}, which is no longer supported.");
        }

        return basePath is null ? theme : theme with { BasePath = basePath };
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

        var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
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
