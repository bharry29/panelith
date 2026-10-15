using System.Text.Json;
using System.Text.Json.Serialization;
using Panelith.Engine;
using Panelith.Engine.Configuration;

namespace Panelith.Hosting.Profiles;

/// <summary>The rotation the desktop app kept before profiles existed.</summary>
/// <param name="Screens">The screens that were ticked for rotation.</param>
/// <param name="Seconds">How long each one stayed up.</param>
/// <param name="Rotate">Whether rotation was switched on.</param>
public sealed record LegacyRotation(IReadOnlyList<string> Screens, int Seconds, bool Rotate);

/// <summary>Reads and writes <c>profiles.json</c>, creating the starter profiles on first use.</summary>
public sealed class ProfileStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The file profiles are kept in.</summary>
    public string File => Path.Combine(paths.Root, "profiles.json");

    /// <summary>
    /// Loads the profiles. When there is no file yet, or it cannot be read, the
    /// starter profiles are returned, carrying over an older rotation if given.
    /// </summary>
    public ProfileSet Load(LegacyRotation? legacy = null)
    {
        // A damaged file is moved aside as profiles.json.bad, and the starter set
        // is used rather than leaving the panel without profiles.
        var loaded = SettingsFile.Read<ProfileSet>(File, Options, out var quarantined);
        LastQuarantined = quarantined;
        return loaded is { Profiles.Count: > 0 } ? Sanitize(loaded) : DefaultProfiles.Create(legacy);
    }

    /// <summary>Where the last <see cref="Load"/> moved a damaged file, or null.</summary>
    public string? LastQuarantined { get; private set; }

    /// <summary>The most profiles kept.</summary>
    public const int MaxProfiles = 64;

    /// <summary>The most screens or programs listed in one profile.</summary>
    public const int MaxListLength = 256;

    /// <summary>The longest name, id or program name kept.</summary>
    public const int MaxTextLength = 260;

    /// <summary>Trims a hand-edited set to sizes the app is built for.</summary>
    internal static ProfileSet Sanitize(ProfileSet set) => set with
    {
        Pinned = set.Pinned is null ? null : SettingsFile.Clip(set.Pinned, MaxTextLength),
        Profiles = [.. set.Profiles.Where(p => p is not null).Take(MaxProfiles).Select(p => p with
        {
            Id = SettingsFile.Clip(p.Id, MaxTextLength),
            Name = SettingsFile.Clip(p.Name, MaxTextLength),
            Icon = SettingsFile.Clip(p.Icon, 64),
            Accent = SettingsFile.Clip(p.Accent, 32),
            Screens = [.. (p.Screens ?? []).Take(MaxListLength).Select(s => SettingsFile.Clip(s, MaxTextLength))],
            RotationSeconds = Math.Clamp(p.RotationSeconds, 1, 86_400),
            Dim = Math.Clamp(p.Dim, 0d, 1d),
            Triggers = (p.Triggers ?? new ProfileTriggers()) with
            {
                Apps = [.. (p.Triggers?.Apps ?? []).Take(MaxListLength).Select(a => SettingsFile.Clip(a, MaxTextLength))],
                Schedules = [.. (p.Triggers?.Schedules ?? []).Where(s => s is not null).Take(64)],
                Hotkey = p.Triggers?.Hotkey is { } hotkey ? SettingsFile.Clip(hotkey, 64) : null,
            },
        })],
    };

    /// <summary>Writes the profiles.</summary>
    public void Save(ProfileSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        paths.EnsureCreated();
        var temporary = File + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(set, Options));
        System.IO.File.Move(temporary, File, overwrite: true);
    }
}

/// <summary>The profiles a new install starts with.</summary>
public static class DefaultProfiles
{
    /// <summary>Programs that mean video, photo or 3D work.</summary>
    public static readonly IReadOnlyList<string> CreativeApps =
    [
        "Adobe Premiere Pro", "AfterFX", "Adobe Media Encoder", "Resolve", "Photoshop", "Lightroom", "lightroomclassic",
        "Illustrator", "blender", "vegas*", "CapCut", "HandBrake", "Fusion360", "Cinema 4D", "Houdini*", "UnrealEditor",
        "Unity", "kdenlive", "shotcut", "gimp*", "krita",
    ];

    /// <summary>Programs that mean streaming or recording.</summary>
    public static readonly IReadOnlyList<string> StreamingApps =
    [
        "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "vMix64",
    ];

    /// <summary>Builds the starter profiles.</summary>
    public static ProfileSet Create(LegacyRotation? legacy = null)
    {
        var everyday = new Profile
        {
            Id = "everyday",
            Name = "Everyday",
            Icon = "home",
            Accent = "#3ee0c5",
            IsDefault = true,
            Screens = ["aurora", "overview", "pulse"],
            Rotate = true,
            RotationSeconds = 20,
            Triggers = new ProfileTriggers { Hotkey = "Ctrl+Alt+1" },
        };

        if (legacy is { Screens.Count: > 0 })
        {
            everyday = everyday with
            {
                Screens = legacy.Screens,
                Rotate = legacy.Rotate,
                RotationSeconds = Math.Clamp(legacy.Seconds, 5, 600),
            };
        }

        return new ProfileSet
        {
            Profiles =
            [
                new Profile
                {
                    Id = "gaming",
                    Name = "Gaming",
                    Icon = "gaming",
                    Accent = "#76b900",
                    Screens = ["game", "esports", "gpu-focus"],
                    Rotate = false,
                    Triggers = new ProfileTriggers { DetectGames = true, Hotkey = "Ctrl+Alt+2" },
                },
                new Profile
                {
                    Id = "streaming",
                    Name = "Streaming",
                    Icon = "streaming",
                    Accent = "#ff5d73",
                    Screens = ["studio", "gpu-focus"],
                    RotationSeconds = 20,
                    Triggers = new ProfileTriggers { Apps = StreamingApps, Hotkey = "Ctrl+Alt+3" },
                },
                new Profile
                {
                    Id = "creating",
                    Name = "Creating",
                    Icon = "creating",
                    Accent = "#d29cff",
                    Screens = ["studio", "pulse"],
                    RotationSeconds = 20,
                    Triggers = new ProfileTriggers { Apps = CreativeApps, Hotkey = "Ctrl+Alt+4" },
                },
                new Profile
                {
                    Id = "away",
                    Name = "Away",
                    Icon = "away",
                    Accent = "#7aa2ff",
                    Screens = ["clock"],
                    Rotate = false,
                    Triggers = new ProfileTriggers { IdleMinutes = 5, OnLock = true, Hotkey = "Ctrl+Alt+5" },
                },
                new Profile
                {
                    Id = "night",
                    Name = "Night",
                    Icon = "night",
                    Accent = "#f0a35c",
                    Screens = ["night"],
                    Rotate = false,
                    Dim = 0.4,
                    Triggers = new ProfileTriggers
                    {
                        Schedules = [new ScheduleWindow { Start = new TimeOnly(23, 0), End = new TimeOnly(7, 0) }],
                        Hotkey = "Ctrl+Alt+6",
                    },
                },
                new Profile
                {
                    Id = "work",
                    Name = "Work",
                    Icon = "work",
                    Accent = "#f0c14a",
                    Enabled = false,
                    Screens = ["desk", "minimal"],
                    RotationSeconds = 30,
                    Triggers = new ProfileTriggers
                    {
                        Schedules = [new ScheduleWindow { Days = ScheduleWindow.Weekdays, Start = new TimeOnly(9, 0), End = new TimeOnly(17, 0) }],
                        Hotkey = "Ctrl+Alt+7",
                    },
                },
                everyday,
            ],
        };
    }
}
