using CommunityToolkit.Mvvm.ComponentModel;
using Panelith.App.Controls;
using Panelith.Expressions;
using Panelith.Imaging;
using Panelith.Rendering;
using Panelith.Themes;

namespace Panelith.App.ViewModels;

/// <summary>One screen in the gallery, with a live thumbnail.</summary>
public sealed partial class ScreenCardViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailWidth = 960;

    private readonly ScreensViewModel _owner;
    private ThemeRenderer? _renderer;

    internal ScreenCardViewModel(ScreensViewModel owner, string id, Theme? theme, string? problem, bool isUserTheme)
    {
        _owner = owner;
        Id = id;
        Theme = theme;
        Name = theme is { Name.Length: > 0 } ? theme.Name : id;
        Description = problem ?? theme?.Description ?? string.Empty;
        Author = theme?.Author is { Length: > 0 } author ? $"by {author}" : string.Empty;
        IsUserTheme = isUserTheme;
        HasProblem = problem is not null;
        AspectRatio = theme is { Canvas.Height: > 0 } ? theme.Canvas.AspectRatio : 4d;
    }

    /// <summary>The theme id, which is also its folder name.</summary>
    public string Id { get; }

    /// <summary>The loaded theme, or null if it failed to load.</summary>
    internal Theme? Theme { get; }

    public string Name { get; }

    public string Description { get; }

    public string Author { get; }

    public bool IsUserTheme { get; }

    public bool HasProblem { get; }

    public double AspectRatio { get; }

    /// <summary>The live thumbnail.</summary>
    public LiveFrame Thumbnail { get; } = new();

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool InRotation { get; set; }

    partial void OnInRotationChanged(bool value) => _owner.OnRotationMembershipChanged();

    /// <summary>Draws the thumbnail off the UI thread. Returns null when the theme did not load.</summary>
    internal FrameBuffer? RenderThumbnail(IBindingContext bindings)
    {
        if (Theme is null)
        {
            return null;
        }

        _renderer ??= new ThemeRenderer(
            Theme,
            new ThemeRenderOptions(ThumbnailWidth, (int)Math.Round(ThumbnailWidth / AspectRatio), ShowErrorMarkers: true, ThemeFolder: Theme.BasePath));
        return _renderer.Render(bindings);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renderer?.Dispose();
        Thumbnail.Dispose();
    }
}
