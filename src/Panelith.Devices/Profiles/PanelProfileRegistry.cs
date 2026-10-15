using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panelith.Devices.Profiles;

/// <summary>
/// The catalogue of panel models Panelith knows how to drive.
/// </summary>
/// <remarks>
/// <para>
/// Built-in profiles are embedded in the assembly. User profiles are JSON files
/// under the configuration folder and take precedence over a built-in profile
/// with the same id, so a user can fix a wrong resolution or add a brand-new
/// panel locally and keep that fix across upgrades.
/// </para>
/// <para>
/// A malformed user profile is reported and skipped rather than being allowed to
/// stop the application: a typo in one file must not cost the user their
/// working panels.
/// </para>
/// </remarks>
public sealed class PanelProfileRegistry
{
    private const string BuiltinResourcePrefix = "Panelith.Devices.Profiles.Builtin.";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly Dictionary<string, PanelProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];
    private readonly ILogger _logger;

    /// <summary>Creates an empty registry.</summary>
    public PanelProfileRegistry(ILogger<PanelProfileRegistry>? logger = null) =>
        _logger = logger as ILogger ?? NullLogger.Instance;

    /// <summary>Every known profile, ordered by vendor then model.</summary>
    public IReadOnlyList<PanelProfile> Profiles =>
        [.. _profiles.Values
            .OrderBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Problems found while loading profiles, for <c>panelith doctor</c>.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Builds a registry containing the built-in profiles and, optionally, user
    /// profiles from disk.
    /// </summary>
    /// <param name="userProfileDirectory">
    /// Folder scanned for <c>*.json</c> profiles, or <see langword="null"/> to
    /// load only the built-ins.
    /// </param>
    /// <param name="logger">Receives load diagnostics.</param>
    public static PanelProfileRegistry Load(
        string? userProfileDirectory = null,
        ILogger<PanelProfileRegistry>? logger = null)
    {
        var registry = new PanelProfileRegistry(logger);
        registry.LoadBuiltins();

        if (userProfileDirectory is not null)
        {
            registry.LoadDirectory(userProfileDirectory);
        }

        return registry;
    }

    /// <summary>Adds the profiles embedded in this assembly.</summary>
    public void LoadBuiltins()
    {
        var assembly = typeof(PanelProfileRegistry).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(BuiltinResourcePrefix, StringComparison.Ordinal)
                || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            TryAddDocument(reader.ReadToEnd(), name, userDefined: false);
        }

        _logger.LogDebug("Loaded {Count} built-in panel profiles.", _profiles.Count);
    }

    /// <summary>Adds every <c>*.json</c> profile in a folder, if it exists.</summary>
    public void LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                TryAddDocument(File.ReadAllText(file), file, userDefined: true);
            }
            catch (IOException ex)
            {
                Report($"Could not read the profile \"{file}\": {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Report($"Could not read the profile \"{file}\": {ex.Message}");
            }
        }
    }

    /// <summary>Adds or replaces one profile.</summary>
    public void Add(PanelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var problems = profile.Validate();
        if (problems.Count > 0)
        {
            throw new PanelProfileException(
                $"The profile \"{profile.Id}\" is not usable:{Environment.NewLine}"
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        _profiles[profile.Id] = profile;
    }

    /// <summary>Looks a profile up by id.</summary>
    public PanelProfile? Find(string id) =>
        !string.IsNullOrWhiteSpace(id) && _profiles.TryGetValue(id, out var profile) ? profile : null;

    /// <summary>
    /// Every profile matching a USB device, best match first.
    /// </summary>
    /// <remarks>
    /// Ordering matters because several models legitimately share one USB id. A
    /// profile that also matches on serial number is more specific and comes
    /// first; a user-defined profile outranks a built-in one; and beyond that,
    /// better-tested profiles come first so that a confident guess is preferred
    /// to a speculative one.
    /// </remarks>
    public IReadOnlyList<PanelProfile> FindByUsb(int vendorId, int productId, string? serialNumber = null) =>
        [.. _profiles.Values
            .Where(p => p.Matches(vendorId, productId, serialNumber))
            .OrderByDescending(p => p.Usb.Any(u => u.SerialNumber is not null))
            .ThenByDescending(p => p.IsUserDefined)
            .ThenByDescending(p => SupportRank(p.Support))
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every USB id any profile matches, for narrowing enumeration.</summary>
    public IReadOnlySet<(int VendorId, int ProductId)> KnownUsbIds
    {
        get
        {
            var ids = new HashSet<(int, int)>();
            foreach (var profile in _profiles.Values)
            {
                foreach (var match in profile.Usb)
                {
                    try
                    {
                        ids.Add((match.VendorId, match.ProductId));
                    }
                    catch (PanelProfileException)
                    {
                        // Already reported at load time; skip the bad entry.
                    }
                }
            }

            return ids;
        }
    }

    /// <summary>
    /// How strongly a support level should win a USB-id tie.
    /// </summary>
    /// <remarks>
    /// <see cref="PanelSupportLevel"/> is ordered for display, not for preference.
    /// A partial profile must not outrank a community-tested one just because
    /// its enum value is larger.
    /// </remarks>
    private static int SupportRank(PanelSupportLevel level) => level switch
    {
        PanelSupportLevel.Verified => 4,
        PanelSupportLevel.Community => 3,
        PanelSupportLevel.Untested => 2,
        PanelSupportLevel.Partial => 1,
        _ => 0,
    };

    /// <summary>Profiles handled by a given driver.</summary>
    public IReadOnlyList<PanelProfile> ForDriver(string driverId) =>
        [.. _profiles.Values.Where(p => string.Equals(p.Driver, driverId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Serialises a profile, for <c>panelith devices export</c>.</summary>
    public static string Serialize(PanelProfile profile) =>
        JsonSerializer.Serialize(profile, SerializerOptions);

    /// <summary>Deserialises one profile document.</summary>
    /// <exception cref="PanelProfileException">The document is not a readable profile.</exception>
    public static IReadOnlyList<PanelProfile> Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            // A document may hold a single profile or an array of them, because
            // vendors' line-ups are naturally written as a list.
            var trimmed = json.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '[')
            {
                return JsonSerializer.Deserialize<List<PanelProfile>>(json, SerializerOptions) ?? [];
            }

            var single = JsonSerializer.Deserialize<PanelProfile>(json, SerializerOptions);
            return single is null ? [] : [single];
        }
        catch (JsonException ex)
        {
            throw new PanelProfileException($"The profile is not valid JSON: {ex.Message}", ex);
        }
    }

    private void TryAddDocument(string json, string origin, bool userDefined)
    {
        IReadOnlyList<PanelProfile> profiles;
        try
        {
            profiles = Deserialize(json);
        }
        catch (PanelProfileException ex)
        {
            Report($"Skipped \"{origin}\": {ex.Message}");
            return;
        }

        foreach (var profile in profiles)
        {
            var candidate = profile with { IsUserDefined = userDefined };
            var problems = candidate.Validate();
            if (problems.Count > 0)
            {
                Report($"Skipped profile \"{candidate.Id}\" from \"{origin}\": {string.Join("; ", problems)}");
                continue;
            }

            if (_profiles.TryGetValue(candidate.Id, out var existing) && existing.IsUserDefined && !userDefined)
            {
                continue;
            }

            if (userDefined && _profiles.ContainsKey(candidate.Id))
            {
                _logger.LogInformation(
                    "User profile \"{Id}\" from {Origin} overrides the built-in profile.",
                    candidate.Id,
                    origin);
            }

            _profiles[candidate.Id] = candidate;
        }
    }

    private void Report(string message)
    {
        _problems.Add(message);
        _logger.LogWarning("{Message}", message);
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
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }
}
