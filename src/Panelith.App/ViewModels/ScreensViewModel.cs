using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Panelith.App.Services;
using Panelith.App.Services.Activity;
using Panelith.Hosting;
using Panelith.Hosting.Profiles;
using Panelith.Imaging;
using Panelith.Themes;

namespace Panelith.App.ViewModels;

/// <summary>Every screen that can be put on the panel, plus the active profile's rotation.</summary>
public sealed partial class ScreensViewModel : ObservableObject, IDisposable
{
    private readonly PanelithService _service;
    private readonly ProfileController _profiles;
    private readonly ILogger _logger;
    private readonly List<ScreenCardViewModel> _retired = [];
    private DateTime _lastRotation = DateTime.UtcNow;
    private bool _renderingThumbnails;
    private bool _loading;

    public ScreensViewModel(PanelithService service, ProfileController profiles)
    {
        _service = service;
        _profiles = profiles;
        _logger = service.Log.CreateLogger("Panelith.App.Screens");
        _loading = true;
        RotationSeconds = 20;
        Reload();
        profiles.ActiveProfileChanged += (_, e) => ApplyProfile(e.Profile, e.Switched);
    }

    /// <summary>Raised after the screen on the panel changes.</summary>
    public event EventHandler? ActiveScreenChanged;

    public ObservableCollection<ScreenCardViewModel> Cards { get; } = [];

    /// <summary>The user's own screens, newest first.</summary>
    public ObservableCollection<ScreenCardViewModel> UserCards { get; } = [];

    /// <summary>The screens that come with Panelith.</summary>
    public ObservableCollection<ScreenCardViewModel> IncludedCards { get; } = [];

    public bool HasUserCards => UserCards.Count > 0;

    /// <summary>The panel's shape, which every thumbnail box takes so the cards line up.</summary>
    public double ThumbnailAspect => _service.PanelSize is var (width, height) && height > 0 ? width / (double)height : 4d;

    [ObservableProperty]
    public partial ScreenCardViewModel? Active { get; set; }

    [ObservableProperty]
    public partial bool RotateScreens { get; set; }

    [ObservableProperty]
    public partial int RotationSeconds { get; set; }

    [ObservableProperty]
    public partial string RotationSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    /// <summary>Where user screens live, for the "open folder" button.</summary>
    public string UserScreensFolder => _service.Runtime.Paths.ThemesDirectory;

    /// <summary>The engine, for the import window.</summary>
    internal PanelithService Service => _service;

    /// <summary>Rereads the gallery and puts a newly made or changed screen on the panel.</summary>
    public void ShowNew(string id)
    {
        Reload();
        if (Find(id) is { } card)
        {
            Show(card);
        }
    }

    /// <summary>Takes the wallpaper off one of the user's own screens.</summary>
    [RelayCommand]
    private void RemoveWallpaper(ScreenCardViewModel? card)
    {
        if (card is not { HasWallpaper: true, Theme: { } theme })
        {
            return;
        }

        try
        {
            var workshop = new ScreenWorkshop(_service.Library, _service.Runtime.Paths);
            var (_, folder, _) = workshop.Editable(card.Id);
            var plain = ScreenWorkshop.WithoutWallpaper(theme);
            workshop.Save(plain, folder);
            Reload();
            ScreenWorkshop.RemoveUnusedWallpapers(folder, plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the wallpaper from {Screen}: {Error}", card.Id, ex.Message);
        }
    }

    /// <summary>
    /// Deletes one of the user's own screens. The panel lets go of a screen's
    /// clips a moment after switching away, so a locked file is retried for a
    /// few seconds. Returns false when the screen could not be deleted.
    /// </summary>
    public async Task<bool> DeleteAsync(ScreenCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!card.IsUserTheme)
        {
            return false;
        }

        if (card.IsActive && Cards.FirstOrDefault(c => c != card && !c.HasProblem) is { } next)
        {
            Show(next);
        }

        Cards.Remove(card);
        UserCards.Remove(card);
        OnPropertyChanged(nameof(HasUserCards));
        _retired.Add(card);
        DisposeRetiredCards();
        _service.Renderers.Reload();
        var workshop = new ScreenWorkshop(_service.Library, _service.Runtime.Paths);
        var deleted = false;
        for (var attempt = 0; !deleted; attempt++)
        {
            try
            {
                workshop.Delete(card.Id);
                deleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 20)
                {
                    _logger.LogWarning("Could not delete {Screen}: {Error}", card.Id, ex.Message);
                    break;
                }

                await Task.Delay(250);
            }
        }

        Reload();
        return deleted;
    }

    partial void OnRotateScreensChanged(bool value)
    {
        _lastRotation = DateTime.UtcNow;
        SaveToProfile();
    }

    partial void OnRotationSecondsChanged(int value) => SaveToProfile();

    /// <summary>Finds a card by screen id.</summary>
    public ScreenCardViewModel? Find(string id) =>
        Cards.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a screen on the panel.</summary>
    [RelayCommand]
    public void Show(ScreenCardViewModel? card)
    {
        if (card is null || card.HasProblem)
        {
            return;
        }

        _service.Show(card.Id);
        _lastRotation = DateTime.UtcNow;
        MarkActive();
        ActiveScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves to the next screen: the next one in the profile, or simply the next one in the gallery.</summary>
    [RelayCommand]
    public void Next()
    {
        var pool = RotationPool();
        if (pool.Count < 2)
        {
            pool = [.. Cards.Where(c => !c.HasProblem)];
        }

        if (pool.Count == 0)
        {
            return;
        }

        var index = Active is null ? -1 : pool.IndexOf(Active);
        Show(pool[(index + 1) % pool.Count]);
    }

    /// <summary>Rereads every screen from disk.</summary>
    [RelayCommand]
    public void Reload()
    {
        _loading = true;
        _retired.AddRange(Cards);
        Cards.Clear();
        DisposeRetiredCards();
        var rotation = new HashSet<string>(_profiles.Active?.Screens ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _service.Library.Catalog())
        {
            Theme? theme = null;
            string? problem = null;
            try
            {
                theme = _service.Library.Load(entry.File);
                var errors = ThemeValidator.Validate(theme).Where(d => d.Severity == ThemeDiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    problem = "This screen has errors: " + errors[0];
                    theme = null;
                }
            }
            catch (Exception ex) when (ex is ThemeFormatException or IOException)
            {
                problem = ex.Message;
            }

            Cards.Add(new ScreenCardViewModel(this, entry.Id, theme, problem, entry.IsUserTheme)
            {
                InRotation = rotation.Contains(entry.Id),
                Modified = LastWrite(entry.File),
            });
        }

        UserCards.Clear();
        IncludedCards.Clear();
        foreach (var card in Cards.Where(c => c.IsUserTheme).OrderByDescending(c => c.Modified))
        {
            UserCards.Add(card);
        }

        foreach (var card in Cards.Where(c => !c.IsUserTheme))
        {
            IncludedCards.Add(card);
        }

        OnPropertyChanged(nameof(HasUserCards));
        _loading = false;
        _service.Renderers.Reload();
        MarkActive();
        UpdateRotationSummary();
        CardsReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after <see cref="Reload"/> replaced every card and its thumbnail.</summary>
    public event EventHandler? CardsReloaded;

    /// <summary>
    /// Takes on a profile's screens and rotation. When the profile just became
    /// active, its first screen goes on the panel unless the current screen is
    /// part of it already.
    /// </summary>
    public void ApplyProfile(Profile profile, bool switched)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _loading = true;
        try
        {
            ProfileName = profile.Name;
            RotateScreens = profile.Rotate;
            RotationSeconds = Math.Clamp(profile.RotationSeconds, 5, 600);
            var screens = new HashSet<string>(profile.Screens, StringComparer.OrdinalIgnoreCase);
            foreach (var card in Cards)
            {
                card.InRotation = screens.Contains(card.Id);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateRotationSummary();
        if (!switched)
        {
            return;
        }

        _lastRotation = DateTime.UtcNow;
        if (profile.Screens.Select(Find).FirstOrDefault(c => c is { HasProblem: false }) is { } first
            && !string.Equals(first.Id, _service.ActiveScreen, StringComparison.OrdinalIgnoreCase))
        {
            Show(first);
        }
    }

    /// <summary>Redraws every thumbnail with current sensor values.</summary>
    public void RefreshThumbnails()
    {
        if (_renderingThumbnails)
        {
            return;
        }

        _renderingThumbnails = true;
        var cards = Cards.ToList();
        var bindings = _service.Runtime.Bindings;
        Task.Run(() =>
        {
            var frames = new List<(ScreenCardViewModel Card, FrameBuffer Frame)>();
            foreach (var card in cards)
            {
                try
                {
                    if (card.RenderThumbnail(bindings) is { } frame)
                    {
                        frames.Add((card, frame));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The gallery was reloaded mid-render.
                }
            }

            return frames;
        }).ContinueWith(
            task =>
            {
                _renderingThumbnails = false;
                DisposeRetiredCards();
                if (!task.IsCompletedSuccessfully)
                {
                    _logger.LogWarning("Screen thumbnails could not be drawn: {Error}", task.Exception?.GetBaseException().ToString());
                    return;
                }

                foreach (var (card, frame) in task.Result)
                {
                    using (frame)
                    {
                        if (Cards.Contains(card))
                        {
                            card.Thumbnail.Update(frame);
                        }
                    }
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Advances the rotation when its time is up. Called about once a second.</summary>
    public void Tick()
    {
        if (RotateScreens && DateTime.UtcNow - _lastRotation >= TimeSpan.FromSeconds(RotationSeconds))
        {
            Next();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var card in Cards)
        {
            card.Dispose();
        }
    }

    internal void OnRotationMembershipChanged()
    {
        if (_loading)
        {
            return;
        }

        SaveToProfile();
        UpdateRotationSummary();
    }

    /// <summary>The screens to cycle, in the profile's order.</summary>
    private List<ScreenCardViewModel> RotationPool()
    {
        var order = _profiles.Active?.Screens ?? [];
        var ticked = Cards.Where(c => c.InRotation && !c.HasProblem).ToList();
        return [.. ticked.OrderBy(c => IndexOrEnd(order, c.Id))];
    }

    private static DateTime LastWrite(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    private static int IndexOrEnd(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Disposes cards removed by a reload, once no thumbnail render can still be using them.</summary>
    private void DisposeRetiredCards()
    {
        if (_renderingThumbnails)
        {
            return;
        }

        foreach (var card in _retired)
        {
            card.Dispose();
        }

        _retired.Clear();
    }

    private void MarkActive()
    {
        var active = _service.ActiveScreen;
        foreach (var card in Cards)
        {
            card.IsActive = string.Equals(card.Id, active, StringComparison.OrdinalIgnoreCase);
        }

        Active = Cards.FirstOrDefault(c => c.IsActive);
    }

    private void UpdateRotationSummary()
    {
        var count = Cards.Count(c => c.InRotation);
        var name = ProfileName.Length > 0 ? ProfileName : "This profile";
        RotationSummary = count switch
        {
            0 => $"{name} has no screens ticked. Tick \"In rotation\" on the screens it should show.",
            1 => $"{name} shows 1 screen. Tick at least two to cycle between them.",
            _ => $"{name} cycles {count} screens.",
        };
    }

    private void SaveToProfile()
    {
        if (_loading || _profiles.Active is not { } profile)
        {
            return;
        }

        var ticked = Cards.Where(c => c.InRotation).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var screens = profile.Screens.Where(ticked.Contains).ToList();
        screens.AddRange(Cards.Where(c => c.InRotation && !screens.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).Select(c => c.Id));

        _profiles.UpdateProfile(profile with
        {
            Rotate = RotateScreens,
            RotationSeconds = RotationSeconds,
            Screens = screens,
        });
    }
}
