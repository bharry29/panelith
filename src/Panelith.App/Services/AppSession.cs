using System.Text.Json;
using Panelith.Engine;
using Panelith.Engine.Configuration;

namespace Panelith.App.Services;

/// <summary>Where the window was and how big, in its restored state.</summary>
/// <param name="X">The left edge, in screen pixels.</param>
/// <param name="Y">The top edge, in screen pixels.</param>
/// <param name="Width">The width, in window units.</param>
/// <param name="Height">The height, in window units.</param>
/// <param name="Maximized">Whether the window filled the screen.</param>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>How the window was left, so the next launch opens the same way.</summary>
public sealed record AppSession
{
    public WindowPlacement? Window { get; init; }

    /// <summary>The title of the page that was open.</summary>
    public string? Page { get; init; }

    /// <summary>The search typed on the Sensors page.</summary>
    public string? SensorFilter { get; init; }

    /// <summary>The screen open in the designer.</summary>
    public string? DesignerScreen { get; init; }

    /// <summary>The profile selected on the Profiles page.</summary>
    public string? SelectedProfile { get; init; }
}

/// <summary>Reads and writes <c>session.json</c> next to app.json.</summary>
public sealed class AppSessionStore(AppPaths paths)
{
    private const double MaxWindowSize = 32_768;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string File => Path.Combine(paths.Root, "session.json");

    /// <summary>Where a damaged session.json was moved, if that happened this session.</summary>
    public string? LastQuarantined { get; private set; }

    /// <summary>Loads the last session, or an empty one when the file is missing or damaged.</summary>
    public AppSession Load()
    {
        var loaded = SettingsFile.Read<AppSession>(File, Options, out var quarantined);
        LastQuarantined ??= quarantined;
        if (loaded is null)
        {
            return new AppSession();
        }

        return loaded with
        {
            Window = loaded.Window is { } w && IsSize(w.Width) && IsSize(w.Height) ? w : null,
            Page = Optional(loaded.Page, 64),
            SensorFilter = Optional(loaded.SensorFilter, 256),
            DesignerScreen = Optional(loaded.DesignerScreen, 260),
            SelectedProfile = Optional(loaded.SelectedProfile, 260),
        };
    }

    /// <summary>Writes the session.</summary>
    public void Save(AppSession session)
    {
        paths.EnsureCreated();
        var temporary = File + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(session, Options));
        System.IO.File.Move(temporary, File, overwrite: true);
    }

    private static bool IsSize(double value) => double.IsFinite(value) && value is >= 1 and <= MaxWindowSize;

    private static string? Optional(string? text, int maximum) =>
        string.IsNullOrEmpty(text) ? null : SettingsFile.Clip(text, maximum);
}
