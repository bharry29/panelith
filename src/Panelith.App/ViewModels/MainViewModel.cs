using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Panelith.App.Services;
using Panelith.App.Services.Activity;
using Panelith.Engine;
using Panelith.Hosting.Profiles;

namespace Panelith.App.ViewModels;

/// <summary>A destination in the navigation rail.</summary>
public sealed record NavItem(string Title, Geometry Icon, ObservableObject Page);

/// <summary>The window: navigation, the panel status, and the timers that keep every page live.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string NextScreenAction = "next-screen";
    private const string NextProfileAction = "next-profile";
    private const string ProfileActionPrefix = "profile:";

    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3ee0c5"));
    private static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#f0c14a"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#ff5d73"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#5b6b7f"));

    private readonly PanelithService _service;
    private readonly HotkeyService _hotkeys = new();
    private readonly DispatcherTimer _fast;
    private readonly DispatcherTimer _slow;

    public MainViewModel(PanelithService service, AppSettingsStore store, AppSettings settings)
    {
        _service = service;
        var profileStore = new ProfileStore(service.Runtime.Paths);
        var firstRun = !File.Exists(profileStore.File);
        var set = profileStore.Load(new LegacyRotation(settings.RotationScreens, settings.RotationSeconds, settings.RotateScreens));
        if (profileStore.LastQuarantined is { } bad)
        {
            service.Log.CreateLogger("Panelith.App.Profiles").LogWarning("profiles.json was damaged and was moved to {Path}; the starter profiles are in use.", bad);
            firstRun = true;
        }

        if (firstRun)
        {
            try
            {
                profileStore.Save(set);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                service.Log.CreateLogger("Panelith.App.Profiles").LogWarning("Profiles could not be saved: {Message}", ex.Message);
            }
        }

        ProfileController = new ProfileController(service, profileStore, set);

        Screens = new ScreensViewModel(service, ProfileController);
        Profiles = new ProfilesViewModel(ProfileController, Screens);
        Dashboard = new DashboardViewModel(service, Screens);
        Sensors = new SensorsViewModel(service);
        Designer = new DesignerViewModel(service, Screens);
        Settings = new SettingsViewModel(service, store, settings, _hotkeys);
        Settings.ElevationRequested += (_, _) => ElevationRequested?.Invoke(this, EventArgs.Empty);
        Settings.HotkeysChanged += (_, _) => RegisterHotkeys();
        Screens.ActiveScreenChanged += (_, _) => OnPropertyChanged(nameof(ActiveScreenName));

        NavItems =
        [
            new("Dashboard", Icons.Dashboard, Dashboard),
            new("Screens", Icons.Screens, Screens),
            new("Designer", Icons.Designer, Designer),
            new("Profiles", Icons.Profiles, Profiles),
            new("Sensors", Icons.Sensors, Sensors),
            new("Settings", Icons.Settings, Settings),
        ];
        SelectedNav = NavItems[0];

        service.Log.EntryAdded += entry => Dispatcher.UIThread.Post(() => Settings.Append(entry));
        service.Renderers.ThemeFailed += (_, e) => Dispatcher.UIThread.Post(() =>
            Toast = $"\"{e.ThemeId}\" could not be shown: {e.Exception.Message}");

        ProfileController.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProfileController.Decision) or nameof(ProfileController.IsPinned))
            {
                UpdateProfileStatus();
            }
        };
        ProfileController.SetChanged += (_, _) =>
        {
            UpdateProfileStatus();
            RegisterHotkeys();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        };

        var hotkeyLog = service.Log.CreateLogger("Panelith.App.Hotkeys");
        _hotkeys.Pressed += action => Dispatcher.UIThread.Post(() => OnHotkey(action));
        _hotkeys.Registered += failed => Dispatcher.UIThread.Post(() =>
        {
            Settings.HotkeyProblems = failed.Count == 0 ? null : $"Already used by another program: {string.Join(", ", failed)}";
            if (failed.Count > 0)
            {
                hotkeyLog.LogWarning("These shortcuts are taken by another program: {Shortcuts}", string.Join(", ", failed));
            }
        });
        RegisterHotkeys();

        _fast = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => FastTick());
        _slow = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SlowTick());
        ProfileController.Start();
        _fast.Start();
        _slow.Start();
        SlowTick();
        UpdateProfileStatus();
    }

    /// <summary>Raised when the user asks to restart with administrator rights.</summary>
    public event EventHandler? ElevationRequested;

    /// <summary>Raised when profiles are added, removed or renamed, for the tray menu.</summary>
    public event EventHandler? ProfilesChanged;

    public ProfileController ProfileController { get; }

    public DashboardViewModel Dashboard { get; }

    public ScreensViewModel Screens { get; }

    public ProfilesViewModel Profiles { get; }

    public SensorsViewModel Sensors { get; }

    public DesignerViewModel Designer { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Looking for panels…";

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = Idle;

    [ObservableProperty]
    public partial string? Toast { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush ProfileBrush { get; set; } = Good;

    [ObservableProperty]
    public partial Geometry ProfileIcon { get; set; } = Icons.Screens;

    [ObservableProperty]
    public partial bool IsProfilePinned { get; set; }

    /// <summary>Whether the window is on screen; hidden windows skip all drawing work.</summary>
    public bool IsWindowVisible { get; set; } = true;

    public string ActiveScreenName => Screens.Active?.Name ?? _service.ActiveScreen;

    public bool IsElevated => WindowsIntegration.IsElevated;

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (value.Page == Designer)
        {
            Designer.RefreshScreens();
            Designer.RefreshSensors();
        }

        SlowTick();
    }

    [RelayCommand]
    private void NextScreen()
    {
        Screens.Next();
        OnPropertyChanged(nameof(ActiveScreenName));
    }

    /// <summary>Pins the active profile, or returns to automatic switching when one is pinned.</summary>
    [RelayCommand]
    private void ToggleProfilePin()
    {
        if (ProfileController.IsPinned)
        {
            ProfileController.Pin(null);
        }
        else if (ProfileController.Active is { } active)
        {
            ProfileController.Pin(active.Id);
        }
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    [RelayCommand]
    private void Navigate(string title) =>
        SelectedNav = NavItems.FirstOrDefault(n => n.Title == title) ?? SelectedNav;

    /// <inheritdoc />
    public void Dispose()
    {
        _fast.Stop();
        _slow.Stop();
        _hotkeys.Dispose();
        ProfileController.Dispose();
        Dashboard.Dispose();
        Designer.Dispose();
        Screens.Dispose();
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case NextScreenAction:
                NextScreen();
                break;
            case NextProfileAction:
                ProfileController.PinNext();
                break;
            default:
                if (action.StartsWith(ProfileActionPrefix, StringComparison.Ordinal))
                {
                    ProfileController.TogglePin(action[ProfileActionPrefix.Length..]);
                }

                break;
        }
    }

    private void RegisterHotkeys()
    {
        var settings = Settings.Current;
        if (!settings.HotkeysEnabled)
        {
            _hotkeys.Set([]);
            return;
        }

        var bindings = new List<(string, string?)>
        {
            (NextScreenAction, settings.NextScreenHotkey),
            (NextProfileAction, settings.NextProfileHotkey),
        };
        bindings.AddRange(ProfileController.Set.Profiles.Select(p => (ProfileActionPrefix + p.Id, p.Triggers.Hotkey)));
        _hotkeys.Set(bindings);
    }

    private void UpdateProfileStatus()
    {
        var decision = ProfileController.Decision;
        IsProfilePinned = ProfileController.IsPinned;
        if (decision is null)
        {
            ProfileName = "Starting…";
            ProfileReason = string.Empty;
            return;
        }

        ProfileName = decision.Profile.Name;
        ProfileReason = decision.Reason;
        ProfileBrush = new SolidColorBrush(Color.TryParse(decision.Profile.Accent, out var c) ? c : Color.Parse("#3ee0c5"));
        ProfileIcon = Icons.ForProfile(decision.Profile.Icon);
    }

    private void FastTick()
    {
        if (IsWindowVisible && SelectedNav.Page == Dashboard)
        {
            Dashboard.RefreshMirror();
        }
    }

    private void SlowTick()
    {
        Screens.Tick();
        UpdateStatus();

        if (!IsWindowVisible)
        {
            return;
        }

        if (SelectedNav.Page == Dashboard)
        {
            Dashboard.RefreshStats();
        }
        else if (SelectedNav.Page == Screens || SelectedNav.Page == Profiles)
        {
            Screens.RefreshThumbnails();
        }
        else if (SelectedNav.Page == Sensors)
        {
            Sensors.Refresh();
        }
        else if (SelectedNav.Page == Designer)
        {
            Designer.RefreshRender();
        }
        else if (SelectedNav.Page == Settings)
        {
            Settings.RefreshRtssStatus();
        }

        OnPropertyChanged(nameof(ActiveScreenName));
    }

    private void UpdateStatus()
    {
        var statuses = _service.Host.Statuses;
        var running = statuses.Where(s => s.State == PanelRuntimeState.Running).ToList();
        if (running.Count > 0)
        {
            StatusText = running.Count == 1 ? $"{running[0].DisplayName} · {running[0].FramesPerSecond:0} fps" : $"{running.Count} panels live";
            StatusBrush = Good;
        }
        else if (statuses.FirstOrDefault(s => s.State is PanelRuntimeState.Connecting or PanelRuntimeState.Retrying) is { } connecting)
        {
            StatusText = $"{connecting.DisplayName} · connecting";
            StatusBrush = Busy;
        }
        else if (statuses.FirstOrDefault(s => s.State == PanelRuntimeState.Failed) is { } failed)
        {
            StatusText = $"{failed.DisplayName} · failed";
            StatusBrush = Bad;
        }
        else
        {
            StatusText = "No panel connected";
            StatusBrush = Idle;
        }
    }
}
