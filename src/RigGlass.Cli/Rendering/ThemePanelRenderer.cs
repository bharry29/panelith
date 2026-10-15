using RigGlass.Cli.Themes;
using RigGlass.Engine.Rendering;
using RigGlass.Expressions;
using RigGlass.Imaging;
using RigGlass.Rendering;
using RigGlass.Themes;

namespace RigGlass.Cli.Rendering;

/// <summary>Builds a <see cref="ThemeRenderer"/> for each panel the engine opens.</summary>
public sealed class ThemePanelRendererFactory : IPanelRendererFactory
{
    private readonly ThemeLibrary _library;

    /// <summary>Creates the factory.</summary>
    public ThemePanelRendererFactory(ThemeLibrary library) => _library = library;

    /// <inheritdoc />
    public IPanelRenderer Create(
        string themeId,
        int width,
        int height,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        Theme theme;
        try
        {
            theme = _library.Load(themeId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ThemeFormatException)
        {
            throw new PanelRenderException($"The theme \"{themeId}\" could not be loaded: {ex.Message}", ex);
        }

        if (variables is { Count: > 0 })
        {
            var merged = theme.Variables is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(theme.Variables, StringComparer.OrdinalIgnoreCase);

            foreach (var (key, value) in variables)
            {
                merged[key] = value;
            }

            theme = theme with { Variables = merged };
        }

        var diagnostics = ThemeValidator.Validate(theme, checkAssets: true);
        if (ThemeValidator.HasErrors(diagnostics))
        {
            throw new PanelRenderException(
                $"The theme \"{theme.Name}\" has errors:{Environment.NewLine}"
                + string.Join(Environment.NewLine, diagnostics.Where(d => d.Severity == ThemeDiagnosticSeverity.Error)));
        }

        var renderer = new ThemeRenderer(
            theme,
            new ThemeRenderOptions(width, height, ShowErrorMarkers: true, ThemeFolder: theme.BasePath));

        return new SkiaPanelRenderer(renderer, width, height);
    }
}

/// <summary>Adapts <see cref="ThemeRenderer"/> to the engine's renderer contract.</summary>
internal sealed class SkiaPanelRenderer : IPanelRenderer
{
    private readonly ThemeRenderer _renderer;

    internal SkiaPanelRenderer(ThemeRenderer renderer, int width, int height)
    {
        _renderer = renderer;
        Size = (width, height);
    }

    public (int Width, int Height) Size { get; }

    public void RenderInto(FrameBuffer target, IBindingContext context) =>
        _renderer.RenderInto(target, context);

    public void Dispose() => _renderer.Dispose();
}
