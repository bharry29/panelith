using System.Text.Json.Serialization;

namespace Panelith.Themes;

/// <summary>
/// A complete panel layout: a design canvas plus the widgets drawn on it.
/// </summary>
public sealed record Theme
{
    /// <summary>The schema version this document was written against.</summary>
    public int Schema { get; init; } = ThemeSchema.CurrentVersion;

    /// <summary>Stable identifier, unique within a theme library.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Author credit.</summary>
    public string? Author { get; init; }

    /// <summary>Free-form description shown in the theme picker.</summary>
    public string? Description { get; init; }

    /// <summary>Theme version string, for the author's own use.</summary>
    public string? Version { get; init; }

    /// <summary>Licence of the theme and its assets.</summary>
    public string? License { get; init; }

    /// <summary>The canvas the layout was authored against.</summary>
    public ThemeCanvas Canvas { get; init; } = ThemeCanvas.Default;

    /// <summary>What is drawn behind every widget.</summary>
    public ThemeBackground Background { get; init; } = ThemeBackground.Default;

    /// <summary>Values inherited by widgets that do not set them.</summary>
    public ThemeDefaults Defaults { get; init; } = ThemeDefaults.Default;

    /// <summary>
    /// Named expressions usable from any binding, so a value used in several
    /// widgets is written once.
    /// </summary>
    /// <remarks>
    /// Referenced as <c>$name</c>. For example a theme can define
    /// <c>"vramPercent": "@gpu.vram.used / @gpu.vram.total * 100"</c> and then
    /// bind three widgets to <c>$vramPercent</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }

    /// <summary>The widgets, drawn in order so later entries sit on top.</summary>
    public IReadOnlyList<ThemeWidget> Widgets { get; init; } = [];

    /// <summary>
    /// Folder the theme was loaded from, used to resolve asset paths. Not part
    /// of the serialised document.
    /// </summary>
    [JsonIgnore]
    public string? BasePath { get; init; }

    /// <summary>Every widget in the tree, parents before children.</summary>
    public IEnumerable<ThemeWidget> EnumerateWidgets()
    {
        static IEnumerable<ThemeWidget> Walk(IReadOnlyList<ThemeWidget> widgets)
        {
            foreach (var widget in widgets)
            {
                yield return widget;
                foreach (var child in Walk(widget.Children))
                {
                    yield return child;
                }
            }
        }

        return Walk(Widgets);
    }
}

/// <summary>The canvas a theme was authored against.</summary>
/// <param name="Width">Design width in theme units.</param>
/// <param name="Height">Design height in theme units.</param>
/// <param name="Scaling">How the canvas is mapped onto a differently-sized panel.</param>
/// <param name="Background">Colour shown in the letterbox bars under <see cref="ThemeScaleMode.Fit"/>.</param>
public readonly record struct ThemeCanvas(
    int Width,
    int Height,
    ThemeScaleMode Scaling = ThemeScaleMode.Fit,
    string? Background = null)
{
    /// <summary>A 480x320 canvas, the most common small-panel size.</summary>
    public static ThemeCanvas Default { get; } = new(480, 320);

    /// <summary>Aspect ratio, width over height.</summary>
    public double AspectRatio => Height == 0 ? 1d : (double)Width / Height;
}

/// <summary>What a theme draws behind its widgets.</summary>
/// <param name="Color">Flat fill colour.</param>
/// <param name="Image">Path to a background image, relative to the theme folder.</param>
/// <param name="Fit">How that image fills the canvas.</param>
/// <param name="GradientTo">When set, the background is a gradient from <paramref name="Color"/> to this.</param>
/// <param name="GradientAngle">Gradient direction in degrees, clockwise from left-to-right.</param>
/// <param name="Video">Path to a looping <c>.ptclip</c> clip drawn over the colour and image.</param>
/// <param name="Scrim">Colour, usually translucent black, laid over the image or video so widgets stay readable.</param>
/// <param name="VideoSpeed">Playback rate multiplier for <paramref name="Video"/>.</param>
public readonly record struct ThemeBackground(
    string? Color = null,
    string? Image = null,
    ImageFit Fit = ImageFit.Cover,
    string? GradientTo = null,
    double GradientAngle = 0d,
    string? Video = null,
    string? Scrim = null,
    double VideoSpeed = 1d)
{
    /// <summary>An opaque near-black background.</summary>
    public static ThemeBackground Default { get; } = new("#000000");
}

/// <summary>Values widgets inherit when they do not specify their own.</summary>
/// <param name="FontFamily">Default font family.</param>
/// <param name="Color">Default foreground colour.</param>
/// <param name="AccentColor">Colour used by bars and gauges that set no colour of their own.</param>
/// <param name="TrackColor">Colour used for the unfilled part of bars and gauges.</param>
/// <param name="Motion">Whether values glide between readings. On unless set to false.</param>
public readonly record struct ThemeDefaults(
    string? FontFamily = null,
    string? Color = null,
    string? AccentColor = null,
    string? TrackColor = null,
    bool? Motion = null)
{
    /// <summary>Light text on a dark panel, with a cyan accent.</summary>
    public static ThemeDefaults Default { get; } = new("Inter", "#e6edf3", "#36c5f0", "#1f2733");
}

/// <summary>Schema version constants for the theme document format.</summary>
public static class ThemeSchema
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The oldest schema version this build can read.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The conventional file name of a theme document inside a theme folder.</summary>
    public const string FileName = "theme.json";
}
