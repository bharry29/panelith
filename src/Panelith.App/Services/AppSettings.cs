using System.Text.Json;
using Panelith.Engine;
using Panelith.Engine.Configuration;

namespace Panelith.App.Services;

/// <summary>Desktop-only preferences. Panel settings live in panelith.json so the CLI shares them.</summary>
public sealed record AppSettings
{
    /// <summary>Whether the panel cycles through <see cref="RotationScreens"/>.</summary>
    /// <remarks>Rotation now belongs to profiles; these three are read once to seed the Everyday profile.</remarks>
    public bool RotateScreens { get; init; }

    /// <summary>How long each screen stays up while rotating.</summary>
    public int RotationSeconds { get; init; } = 20;

    /// <summary>The screens included in the rotation, in order.</summary>
    public IReadOnlyList<string> RotationScreens { get; init; } = [];

    /// <summary>Start hidden in the notification area.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>Closing the window keeps the panel running from the notification area.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Use simulated sensors, for trying screens on a machine without hardware access.</summary>
    public bool DemoSensors { get; init; }

    /// <summary>Whether global shortcuts are registered.</summary>
    public bool HotkeysEnabled { get; init; } = true;

    /// <summary>Whether values glide, screens crossfade and dimming fades on the panel.</summary>
    public bool SmoothMotion { get; init; } = true;

    /// <summary>The global shortcut for the next screen, or empty for none.</summary>
    public string NextScreenHotkey { get; init; } = "Ctrl+Alt+Right";

    /// <summary>The global shortcut that steps through profiles, or empty for none.</summary>
    public string NextProfileHotkey { get; init; } = "Ctrl+Alt+P";

    /// <summary>Whether the user has read and accepted the note about downloading YouTube videos.</summary>
    public bool YouTubeAccepted { get; init; }
}

/// <summary>Reads and writes <c>app.json</c> next to panelith.json.</summary>
public sealed class AppSettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string File => Path.Combine(paths.Root, "app.json");

    /// <summary>Loads settings, falling back to defaults when the file is missing or damaged.</summary>
    public AppSettings Load()
    {
        var loaded = SettingsFile.Read<AppSettings>(File, Options, out var quarantined);
        LastQuarantined ??= quarantined;
        if (loaded is null)
        {
            return new AppSettings();
        }

        return loaded with
        {
            RotationSeconds = Math.Clamp(loaded.RotationSeconds, 1, 86_400),
            RotationScreens = [.. (loaded.RotationScreens ?? []).Take(256).Select(s => SettingsFile.Clip(s, 260))],
            NextScreenHotkey = SettingsFile.Clip(loaded.NextScreenHotkey, 64),
            NextProfileHotkey = SettingsFile.Clip(loaded.NextProfileHotkey, 64),
        };
    }

    /// <summary>Where a damaged app.json was moved, if that happened this session.</summary>
    public string? LastQuarantined { get; private set; }

    /// <summary>Writes settings.</summary>
    public void Save(AppSettings settings)
    {
        paths.EnsureCreated();
        var temporary = File + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        System.IO.File.Move(temporary, File, overwrite: true);
    }
}
