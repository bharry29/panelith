using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panelith.Engine.Configuration;

/// <summary>Reads and writes <c>panelith.json</c>.</summary>
/// <remarks>
/// <para>
/// A damaged configuration file must never stop the application: a user whose
/// panel went dark because a half-written JSON file failed to parse has no way
/// to diagnose it and no way back. The file is therefore replaced atomically on
/// write, and a file that cannot be read is moved aside and reported while the
/// defaults take over.
/// </para>
/// </remarks>
public sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly AppPaths _paths;
    private readonly ILogger _logger;

    /// <summary>Creates a store.</summary>
    /// <param name="paths">Where the configuration lives.</param>
    /// <param name="logger">Log sink.</param>
    public ConfigurationStore(AppPaths paths, ILogger<ConfigurationStore>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger as ILogger ?? NullLogger.Instance;
    }

    /// <summary>Problems found the last time configuration was loaded.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>
    /// Loads the configuration, falling back to the defaults when the file is
    /// absent or unreadable.
    /// </summary>
    public PanelithOptions Load()
    {
        var problems = new List<string>();
        var file = _paths.ConfigurationFile;

        if (!File.Exists(file))
        {
            Problems = problems;
            return new PanelithOptions();
        }

        PanelithOptions? loaded = null;
        try
        {
            // Check the size before reading. This file is read at every start-up,
            // and one that is damaged or left behind by something else would
            // otherwise be pulled into memory whole before the parser could object.
            var length = new FileInfo(file).Length;
            if (length > SettingsFile.MaxBytes)
            {
                problems.Add($"{file} is {length / 1024} KB, far larger than a configuration file should be; the defaults are being used instead.");
                QuarantineBrokenFile(file, problems);
            }
            else
            {
                loaded = JsonSerializer.Deserialize<PanelithOptions>(File.ReadAllText(file), SerializerOptions);
            }
        }
        catch (JsonException ex)
        {
            problems.Add($"{file} is not valid JSON ({ex.Message}); the defaults are being used instead.");
            QuarantineBrokenFile(file, problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{file} could not be read ({ex.Message}); the defaults are being used instead.");
        }

        var options = loaded ?? new PanelithOptions();

        foreach (var problem in options.Validate())
        {
            problems.Add($"{file}: {problem}");
        }

        foreach (var problem in problems)
        {
            _logger.LogWarning("{Problem}", problem);
        }

        Problems = problems;
        return options;
    }

    /// <summary>Writes the configuration.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(PanelithOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _paths.EnsureCreated();

        var file = _paths.ConfigurationFile;
        var temporary = file + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(options, SerializerOptions));

        // Replace rather than overwrite: a crash mid-write would otherwise leave
        // a truncated file that fails to parse on the next launch.
        if (File.Exists(file))
        {
            File.Replace(temporary, file, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, file);
        }

        _logger.LogDebug("Wrote configuration to {File}.", file);
    }

    /// <summary>Serialises options to JSON, for diagnostics and tests.</summary>
    public static string Serialize(PanelithOptions options) =>
        JsonSerializer.Serialize(options, SerializerOptions);

    /// <summary>Parses options from JSON.</summary>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static PanelithOptions Deserialize(string json) =>
        JsonSerializer.Deserialize<PanelithOptions>(json, SerializerOptions) ?? new PanelithOptions();

    private void QuarantineBrokenFile(string file, List<string> problems)
    {
        var moved = $"{file}.broken";
        try
        {
            File.Move(file, moved, overwrite: true);
            problems.Add($"The unreadable file was moved to {moved} so it is not lost.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The unreadable file could not be moved aside: {ex.Message}");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,

            // Deeper than any real configuration, and shallow enough that nesting
            // cannot be used to exhaust the stack.
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }
}
