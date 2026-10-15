using System.Text.Json;

namespace Panelith.Engine.Configuration;

/// <summary>
/// Reads small JSON settings files defensively: oversized or unreadable files
/// are moved aside as <c>.bad</c> instead of being silently overwritten, so a
/// damaged file can still be recovered by hand.
/// </summary>
public static class SettingsFile
{
    /// <summary>The largest settings file read, in bytes.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Reads <paramref name="file"/>. Returns null when it does not exist,
    /// cannot be opened right now, or was damaged and has been quarantined.
    /// </summary>
    /// <param name="file">The settings file.</param>
    /// <param name="options">Serializer options.</param>
    /// <param name="quarantined">Set to the <c>.bad</c> path when the file was moved aside.</param>
    public static T? Read<T>(string file, JsonSerializerOptions options, out string? quarantined)
        where T : class
    {
        quarantined = null;
        string text;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > MaxBytes)
            {
                quarantined = Quarantine(file);
                return null;
            }

            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable for now: leave it alone and use defaults this time.
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            quarantined = Quarantine(file);
            return null;
        }
    }

    /// <summary>Moves a damaged file to <c>name.bad</c>, replacing an older one. Returns the new path, or null.</summary>
    public static string? Quarantine(string file)
    {
        var bad = file + ".bad";
        try
        {
            File.Move(file, bad, overwrite: true);
            return bad;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Shortens <paramref name="text"/> to at most <paramref name="maximum"/> characters.</summary>
    public static string Clip(string? text, int maximum) =>
        text is null ? string.Empty : text.Length <= maximum ? text : text[..maximum];
}
