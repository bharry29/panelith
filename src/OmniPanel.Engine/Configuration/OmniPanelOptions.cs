using System.Text.Json.Serialization;
using OmniPanel.Devices;

namespace OmniPanel.Engine.Configuration;

/// <summary>
/// Everything the runtime needs to know, as it appears in <c>omnipanel.json</c>.
/// </summary>
/// <remarks>
/// Written to be hand-editable. A user who wants to move a panel to a different
/// theme, or to pin a stubborn screen to a specific profile, should be able to
/// do it in a text editor without reverse-engineering the schema, so every
/// section is small, named the way the UI names it, and valid when omitted.
/// </remarks>
public sealed record OmniPanelOptions
{
    /// <summary>Schema version, so later releases can migrate old files.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Per-panel settings, keyed by the panel's stable key.</summary>
    public IReadOnlyList<PanelAssignment> Panels { get; init; } = [];

    /// <summary>Settings applied to panels with no explicit entry.</summary>
    public PanelDefaults Defaults { get; init; } = new();

    /// <summary>Sensor collection settings.</summary>
    public SensorOptions Sensors { get; init; } = new();

    /// <summary>How often the bus is rescanned for panels appearing and disappearing.</summary>
    public double DiscoveryIntervalSeconds { get; init; } = 3d;

    /// <summary>Whether OmniPanel starts driving panels as soon as it launches.</summary>
    public bool StartOnLaunch { get; init; } = true;

    /// <summary>Finds the assignment for a panel, falling back to the defaults.</summary>
    public PanelAssignment AssignmentFor(DetectedPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        foreach (var assignment in Panels)
        {
            if (string.Equals(assignment.Key, panel.StableKey, StringComparison.Ordinal))
            {
                return assignment;
            }
        }

        // A panel nobody has configured still has to do something sensible, or
        // plugging in a second screen would leave it dark with no explanation.
        return new PanelAssignment
        {
            Key = panel.StableKey,
            Theme = Defaults.Theme,
            Enabled = Defaults.Enabled,
            Orientation = Defaults.Orientation,
            Brightness = Defaults.Brightness,
            FramesPerSecond = Defaults.FramesPerSecond,
            JpegQuality = Defaults.JpegQuality,
        };
    }

    /// <summary>Returns a copy with one panel's assignment replaced.</summary>
    public OmniPanelOptions WithAssignment(PanelAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        var panels = Panels.Where(p => !string.Equals(p.Key, assignment.Key, StringComparison.Ordinal)).ToList();
        panels.Add(assignment);
        panels.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return this with { Panels = panels };
    }

    /// <summary>Reports settings that cannot be honoured.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Version <= 0)
        {
            problems.Add($"The configuration version must be positive but is {Version}.");
        }

        if (DiscoveryIntervalSeconds is <= 0 or > 300)
        {
            problems.Add(
                $"The discovery interval must be between 0 and 300 seconds but is {DiscoveryIntervalSeconds}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in Panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Key))
            {
                problems.Add("A panel entry has no key.");
            }
            else if (!seen.Add(panel.Key))
            {
                problems.Add($"Two panel entries share the key \"{panel.Key}\".");
            }

            problems.AddRange(panel.Validate());
        }

        problems.AddRange(Defaults.Validate());
        problems.AddRange(Sensors.Validate());
        return problems;
    }
}

/// <summary>Settings shared by panels that have no entry of their own.</summary>
public record PanelDefaults
{
    /// <summary>Theme id to show.</summary>
    public string Theme { get; init; } = "starter";

    /// <summary>Whether matching panels are driven.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Image rotation.</summary>
    public PanelOrientation Orientation { get; init; } = PanelOrientation.Portrait;

    /// <summary>Backlight brightness, or <see langword="null"/> to leave the panel alone.</summary>
    public int? Brightness { get; init; }

    /// <summary>
    /// Target frame rate, or <see langword="null"/> to use whatever the panel
    /// can sustain.
    /// </summary>
    public double? FramesPerSecond { get; init; }

    /// <summary>JPEG quality for panels whose payload is compressed.</summary>
    public int JpegQuality { get; init; } = 85;

    /// <summary>Whether the image is mirrored, for panels behind a mirror bracket.</summary>
    public bool FlipHorizontally { get; init; }

    /// <summary>Reports settings that cannot be honoured.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Brightness is { } brightness && brightness is < 0 or > 100)
        {
            problems.Add($"Brightness must be between 0 and 100 but is {brightness}.");
        }

        if (FramesPerSecond is { } fps && fps is <= 0 or > 120)
        {
            problems.Add($"The frame rate must be between 0 and 120 but is {fps}.");
        }

        if (JpegQuality is < 1 or > 100)
        {
            problems.Add($"JPEG quality must be between 1 and 100 but is {JpegQuality}.");
        }

        return problems;
    }
}

/// <summary>Settings for one specific panel.</summary>
public sealed record PanelAssignment : PanelDefaults
{
    /// <summary>
    /// The panel's stable key, as reported by <c>omnipanel devices</c>.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>A name the user chose, shown instead of the model name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Forces a specific panel profile rather than letting matching decide.
    /// </summary>
    /// <remarks>
    /// The escape hatch for hardware whose USB ids collide with another model.
    /// Without it, a user with an unlucky panel has no way forward at all.
    /// </remarks>
    public string? ForceProfile { get; init; }

    /// <summary>Theme variables overridden for this panel.</summary>
    /// <remarks>
    /// This is what lets one theme serve two panels that differ only in an
    /// accent colour or a label, rather than forcing a copy of the whole theme.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
}

/// <summary>Sensor collection settings.</summary>
public sealed record SensorOptions
{
    /// <summary>How often sensors are sampled.</summary>
    public double PollIntervalSeconds { get; init; } = 1d;

    /// <summary>How much history is kept for graphs and windowed aggregates.</summary>
    public double HistorySeconds { get; init; } = 300d;

    /// <summary>
    /// Sensor source ids to disable, such as <c>hwinfo</c> on a machine where it
    /// duplicates another backend.
    /// </summary>
    public IReadOnlyList<string> DisabledSources { get; init; } = [];

    /// <summary>
    /// Overrides mapping a well-known alias onto a specific raw sensor id.
    /// </summary>
    /// <remarks>
    /// Automatic mapping is right for most machines and wrong for some. Letting
    /// the user repoint <c>@cpu.temp</c> at the sensor they trust is the
    /// difference between a theme that is useful and one that reads a dead
    /// probe on their motherboard.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? AliasOverrides { get; init; }

    /// <summary>Reports settings that cannot be honoured.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (PollIntervalSeconds is <= 0 or > 60)
        {
            problems.Add(
                $"The sensor poll interval must be between 0 and 60 seconds but is {PollIntervalSeconds}.");
        }

        if (HistorySeconds is < 0 or > 86400)
        {
            problems.Add($"Sensor history must be between 0 and 86400 seconds but is {HistorySeconds}.");
        }

        if (HistorySeconds > 0 && PollIntervalSeconds > HistorySeconds)
        {
            problems.Add(
                $"A {PollIntervalSeconds}s poll interval cannot fill a {HistorySeconds}s history window.");
        }

        return problems;
    }

    /// <summary>Whether a source is enabled.</summary>
    [JsonIgnore]
    public Func<string, bool> IsSourceEnabled => id =>
        !DisabledSources.Any(disabled => string.Equals(disabled, id, StringComparison.OrdinalIgnoreCase));
}
