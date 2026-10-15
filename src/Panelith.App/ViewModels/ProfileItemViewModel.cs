using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Panelith.App.Controls;
using Panelith.Hosting.Profiles;

namespace Panelith.App.ViewModels;

/// <summary>A small live preview of one screen.</summary>
public sealed record ScreenThumb(string Id, string Name, LiveFrame Frame, double AspectRatio);

/// <summary>One profile in the list on the Profiles page.</summary>
public sealed partial class ProfileItemViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private bool _loading;

    internal ProfileItemViewModel(ProfilesViewModel owner, Profile profile)
    {
        _owner = owner;
        Profile = profile;
        Load(profile);
    }

    public string Id => Profile.Id;

    internal Profile Profile { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush AccentBrush { get; set; } = Brushes.Teal;

    [ObservableProperty]
    public partial IBrush AccentSoftBrush { get; set; } = Brushes.Transparent;

    [ObservableProperty]
    public partial Geometry Icon { get; set; } = Icons.Screens;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    [ObservableProperty]
    public partial string ActiveReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<ScreenThumb> Thumbs { get; set; } = [];

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading && value != Profile.Enabled)
        {
            _owner.Commit(Profile with { Enabled = value });
        }
    }

    internal void Load(Profile profile)
    {
        _loading = true;
        Profile = profile;
        Name = profile.Name;
        var color = Color.TryParse(profile.Accent, out var parsed) ? parsed : Color.Parse("#3ee0c5");
        AccentBrush = new SolidColorBrush(color);
        AccentSoftBrush = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        Icon = Icons.ForProfile(profile.Icon);
        Enabled = profile.Enabled;
        IsDefault = profile.IsDefault;
        Summary = Describe(profile);
        Thumbs = _owner.ThumbsFor(profile.Screens, 3);
        _loading = false;
    }

    /// <summary>A one-line description of when a profile turns on.</summary>
    public static string Describe(Profile profile)
    {
        var parts = new List<string>();
        var t = profile.Triggers;
        if (t.DetectGames)
        {
            parts.Add("any game");
        }

        if (t.Apps.Count > 0)
        {
            parts.Add(t.Apps.Count <= 2 ? string.Join(", ", t.Apps) : $"{t.Apps[0]}, {t.Apps[1]} +{t.Apps.Count - 2} apps");
        }

        if (t.IdleMinutes is { } idle)
        {
            parts.Add($"idle {idle} min");
        }

        if (t.OnLock)
        {
            parts.Add("PC locked");
        }

        foreach (var s in t.Schedules)
        {
            parts.Add($"{DaysText(s.Days)} {s.Start:HH\\:mm}–{s.End:HH\\:mm}");
        }

        if (parts.Count == 0)
        {
            parts.Add(profile.IsDefault ? "when nothing else applies" : "manual only");
        }
        else if (profile.IsDefault)
        {
            parts.Add("default");
        }

        return string.Join(" · ", parts);
    }

    internal static string DaysText(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 7)
        {
            return "daily";
        }

        if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday))
        {
            return "weekdays";
        }

        if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday))
        {
            return "weekends";
        }

        return string.Join(" ", days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]));
    }
}
