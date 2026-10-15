using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Panelith.App.Services.Activity;
using Panelith.Hosting.Profiles;

namespace Panelith.App.ViewModels;

/// <summary>The Profiles page: every profile, which one is active and why, and an editor.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly ProfileController _controller;
    private readonly ScreensViewModel _screens;
    private bool _syncing;

    public ProfilesViewModel(ProfileController controller, ScreensViewModel screens)
    {
        _controller = controller;
        _screens = screens;
        Sync();
        Selected = Items.FirstOrDefault(i => i.Id == controller.Active?.Id) ?? Items.FirstOrDefault();
        controller.SetChanged += (_, _) => Sync();
        screens.CardsReloaded += (_, _) =>
        {
            foreach (var item in Items)
            {
                item.Load(item.Profile);
            }

            if (Selected is { } selected)
            {
                Editor = new ProfileEditorViewModel(this, selected.Profile);
                Editor.SetPinned(string.Equals(_controller.Set.Pinned, selected.Id, StringComparison.OrdinalIgnoreCase));
            }
        };
        controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileController.Decision))
            {
                UpdateActive();
            }
        };
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial ProfileItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial ProfileEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    public partial bool Automatic { get; set; }

    [ObservableProperty]
    public partial string ActiveText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    internal int Count => Items.Count;

    partial void OnSelectedChanged(ProfileItemViewModel? value)
    {
        if (value is null)
        {
            Editor = null;
            return;
        }

        if (Editor?.Id != value.Id)
        {
            Editor = new ProfileEditorViewModel(this, value.Profile);
            Editor.SetPinned(string.Equals(_controller.Set.Pinned, value.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    partial void OnAutomaticChanged(bool value)
    {
        if (!_syncing && value != _controller.Automatic)
        {
            _controller.SetAutomatic(value);
        }
    }

    /// <summary>Saves an edited profile.</summary>
    internal void Commit(Profile profile)
    {
        var set = _controller.Set;
        var profiles = set.Profiles.Select(p =>
            string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase) ? profile
            : profile.IsDefault && p.IsDefault ? p with { IsDefault = false }
            : p).ToList();
        _controller.Update(set with { Profiles = profiles });
    }

    /// <summary>Live previews for screen ids, skipping unknown ones.</summary>
    internal IReadOnlyList<ScreenThumb> ThumbsFor(IEnumerable<string> ids, int max) =>
        [.. ids.Select(_screens.Find).OfType<ScreenCardViewModel>().Take(max).Select(ToThumb)];

    /// <summary>Live previews for every screen.</summary>
    internal IEnumerable<ScreenThumb> AllThumbs() => _screens.Cards.Where(c => !c.HasProblem).Select(ToThumb);

    [RelayCommand]
    private void NewProfile()
    {
        var id = UniqueId("profile");
        var profile = new Profile
        {
            Id = id,
            Name = "New profile",
            Icon = "screen",
            Accent = "#5bd1ff",
            Screens = [.. _screens.Cards.Where(c => !c.HasProblem).Take(2).Select(c => c.Id)],
        };
        var set = _controller.Set;
        var profiles = set.Profiles.ToList();
        profiles.Insert(Math.Max(0, profiles.FindIndex(p => p.IsDefault)), profile);
        _controller.Update(set with { Profiles = profiles });
        Selected = Items.FirstOrDefault(i => i.Id == id);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected?.Profile is not { } source)
        {
            return;
        }

        var copy = source with
        {
            Id = UniqueId(source.Id),
            Name = source.Name + " copy",
            IsDefault = false,
            Triggers = source.Triggers with { Hotkey = null },
        };
        var set = _controller.Set;
        var profiles = set.Profiles.ToList();
        profiles.Insert(profiles.FindIndex(p => p.Id == source.Id) + 1, copy);
        _controller.Update(set with { Profiles = profiles });
        Selected = Items.FirstOrDefault(i => i.Id == copy.Id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } item || Items.Count <= 1)
        {
            return;
        }

        var set = _controller.Set;
        var index = Items.IndexOf(item);
        _controller.Update(set with
        {
            Profiles = [.. set.Profiles.Where(p => p.Id != item.Id)],
            Pinned = set.Pinned == item.Id ? null : set.Pinned,
        });
        Selected = Items.Count > 0 ? Items[Math.Clamp(index, 0, Items.Count - 1)] : null;
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    /// <summary>Pins the selected profile, or unpins it.</summary>
    [RelayCommand]
    private void TogglePinSelected()
    {
        if (Selected is { } item)
        {
            _controller.TogglePin(item.Id);
        }
    }

    [RelayCommand]
    private void Unpin() => _controller.Pin(null);

    private void Move(int delta)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var set = _controller.Set;
        var profiles = set.Profiles.ToList();
        var index = profiles.FindIndex(p => p.Id == item.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= profiles.Count)
        {
            return;
        }

        (profiles[index], profiles[target]) = (profiles[target], profiles[index]);
        _controller.Update(set with { Profiles = profiles });
        Selected = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    private string UniqueId(string stem)
    {
        var baseId = new string([.. stem.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        if (baseId.Length == 0)
        {
            baseId = "profile";
        }

        var id = baseId;
        for (var n = 2; _controller.Set.Find(id) is not null; n++)
        {
            id = $"{baseId}-{n}";
        }

        return id;
    }

    /// <summary>Brings the list in line with the controller's profiles, keeping the selection and editor.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var profiles = _controller.Set.Profiles;
            var sameShape = profiles.Count == Items.Count && profiles.Select(p => p.Id).SequenceEqual(Items.Select(i => i.Id));
            if (sameShape)
            {
                for (var i = 0; i < profiles.Count; i++)
                {
                    Items[i].Load(profiles[i]);
                }
            }
            else
            {
                var selectedId = Selected?.Id;
                Items.Clear();
                foreach (var profile in profiles)
                {
                    Items.Add(new ProfileItemViewModel(this, profile));
                }

                Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Items.FirstOrDefault();
            }

            if (Editor is { } editor && _controller.Set.Find(editor.Id) is { } edited)
            {
                editor.Reload(edited);
            }

            Automatic = _controller.Automatic;
            UpdateActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateActive()
    {
        var decision = _controller.Decision;
        var pinned = _controller.Set.Pinned;
        foreach (var item in Items)
        {
            item.IsActive = decision is not null && item.Id == decision.Profile.Id;
            item.ActiveReason = item.IsActive ? decision!.Reason : string.Empty;
            item.IsPinned = string.Equals(item.Id, pinned, StringComparison.OrdinalIgnoreCase);
        }

        IsPinned = pinned is not null;
        Editor?.SetPinned(Editor.Id == pinned);
        ActiveText = decision is null
            ? "Working out which profile fits…"
            : $"{decision.Profile.Name} is on the panel · {decision.Reason}";
    }

    private static ScreenThumb ToThumb(ScreenCardViewModel card) => new(card.Id, card.Name, card.Thumbnail, card.AspectRatio);
}
