namespace Panelith.Rendering;

/// <summary>Configuration for a <see cref="ThemeRenderer"/> instance.</summary>
/// <param name="Width">Target panel width in pixels.</param>
/// <param name="Height">Target panel height in pixels.</param>
/// <param name="ShowErrorMarkers">
/// When true, widgets that fail draw a subtle magenta hatch instead of vanishing
/// silently — useful in the theme editor and for <c>panelith theme preview</c>.
/// </param>
/// <param name="ThemeFolder">
/// Override for <c>Theme.BasePath</c> when resolving assets and theme fonts.
/// </param>
/// <param name="MaxCachedImages">Bounded size of the decoded-image cache.</param>
public sealed record ThemeRenderOptions(
    int Width,
    int Height,
    bool ShowErrorMarkers = false,
    string? ThemeFolder = null,
    int MaxCachedImages = 64)
{
    /// <summary>Returns a copy targeting a different panel size.</summary>
    public ThemeRenderOptions WithSize(int width, int height) =>
        this with { Width = width, Height = height };
}
