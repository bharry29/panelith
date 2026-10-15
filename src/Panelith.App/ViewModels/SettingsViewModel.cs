using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Panelith.App.Services;
using Panelith.App.Services.Activity;
using Panelith.Devices;

namespace Panelith.App.ViewModels;

/// <summary>Panel output, start-up behaviour and diagnostics.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PanelithService _service;
    private readonly AppSettingsStore _store;
    private AppSettings _settings;
    private bool _loading;

    private const string RtssDownload = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";

    public SettingsViewModel(PanelithService service, AppSettingsStore store, AppSettings settings, HotkeyService hotkeys)
    {
        _service = service;
        _store = store;
        _settings = settings;
        _loading = true;
        HotkeysEnabled = settings.HotkeysEnabled;
        NextScreenHotkey = settings.NextScreenHotkey;
        NextProfileHotkey = settings.NextProfileHotkey;
        HotkeyProblems = hotkeys.Failed.Count == 0 ? null : $"Already used by another program: {string.Join(", ", hotkeys.Failed)}";
        RefreshRtssStatus();

        var defaults = service.Options.Defaults;
        FramesPerSecond = (int)Math.Round(defaults.FramesPerSecond ?? 15);
        JpegQuality = defaults.JpegQuality;
        UpsideDown = defaults.Orientation == PanelOrientation.ReversePortrait;
        StartMinimized = settings.StartMinimized;
        CloseToTray = settings.CloseToTray;
        DemoSensors = settings.DemoSensors;
        SmoothMotion = settings.SmoothMotion;
        service.Renderers.Motion = settings.SmoothMotion;
        (StartWithWindows, StartsElevated) = WindowsIntegration.StartupState();
        if (StartsElevated && WindowsIntegration.CheckStartupTask() is { } taskProblem)
        {
            StartupMessage = taskProblem;
            CanRepairStartup = WindowsIntegration.IsElevated;
        }

        foreach (var entry in service.Log.Snapshot().TakeLast(200))
        {
            Log.Add(entry);
        }

        _loading = false;
    }

    /// <summary>Raised when the user asks to restart with administrator rights.</summary>
    public event EventHandler? ElevationRequested;

    /// <summary>Raised when the shortcut settings change.</summary>
    public event EventHandler? HotkeysChanged;

    [ObservableProperty]
    public partial bool HotkeysEnabled { get; set; }

    [ObservableProperty]
    public partial string NextScreenHotkey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NextProfileHotkey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? HotkeyProblems { get; set; }

    [ObservableProperty]
    public partial string RtssStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RtssMissing { get; set; }

    partial void OnHotkeysEnabledChanged(bool value) => SaveHotkeys();

    partial void OnNextScreenHotkeyChanged(string value) => SaveHotkeys();

    partial void OnNextProfileHotkeyChanged(string value) => SaveHotkeys();

    /// <summary>Checks whether RivaTuner is installed, running and measuring a game.</summary>
    public void RefreshRtssStatus()
    {
        var running = Process.GetProcessesByName("RTSS");
        var isRunning = running.Length > 0;
        foreach (var process in running)
        {
            process.Dispose();
        }

        var game = Panelith.Sensors.WellKnownSensors.GameName;
        var hub = _service.Runtime.Sensors;
        var measuring = hub.Aliases.TryGetValue(game, out var id) && hub.Current.TryGet(id, out var reading) && reading.Text is { Length: > 0 }
            ? reading.Text
            : null;

        var installed = isRunning || File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server", "RTSS.exe"));

        RtssMissing = !isRunning;
        RtssStatus = measuring is not null ? $"RivaTuner is running and measuring {measuring}.exe. Game screens show its FPS."
            : isRunning ? "RivaTuner is running. FPS appears on the game screens once a game starts."
            : installed ? "RivaTuner is installed but not running. Start it (or MSI Afterburner) to see in-game FPS."
            : "RivaTuner Statistics Server is not installed. It is free and is what lets the game screens show FPS, frame time and 1% lows.";
    }

    [RelayCommand]
    private void OpenRtssDownload() => Process.Start(new ProcessStartInfo(RtssDownload) { UseShellExecute = true });

    private void SaveHotkeys()
    {
        if (_loading)
        {
            return;
        }

        var nextScreenValid = NextScreenHotkey.Length == 0 || Hotkey.TryParse(NextScreenHotkey) is not null;
        var nextProfileValid = NextProfileHotkey.Length == 0 || Hotkey.TryParse(NextProfileHotkey) is not null;
        if (!nextScreenValid || !nextProfileValid)
        {
            HotkeyProblems = "Use Ctrl, Alt or Win with a key, such as Ctrl+Alt+Right.";
            return;
        }

        HotkeyProblems = null;
        _settings = _store.Load() with
        {
            HotkeysEnabled = HotkeysEnabled,
            NextScreenHotkey = Hotkey.TryParse(NextScreenHotkey)?.Text ?? string.Empty,
            NextProfileHotkey = Hotkey.TryParse(NextProfileHotkey)?.Text ?? string.Empty,
        };

        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HotkeyProblems = $"Settings could not be saved: {ex.Message}";
        }

        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    public ObservableCollection<LogEntry> Log { get; } = [];

    [ObservableProperty]
    public partial int FramesPerSecond { get; set; }

    [ObservableProperty]
    public partial int JpegQuality { get; set; }

    [ObservableProperty]
    public partial bool UpsideDown { get; set; }

    [ObservableProperty]
    public partial bool HasPendingPanelChanges { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool StartsElevated { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool DemoSensors { get; set; }

    [ObservableProperty]
    public partial bool SmoothMotion { get; set; }

    [ObservableProperty]
    public partial string? StartupMessage { get; set; }

    [ObservableProperty]
    public partial bool CanRepairStartup { get; set; }

    public bool IsElevated => WindowsIntegration.IsElevated;

    public bool ShowPawnIO => WindowsIntegration.IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIOText))]
    public partial bool PawnIOMissing { get; set; } = WindowsIntegration.IsSupported && !WindowsIntegration.IsPawnIOInstalled();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIOText))]
    public partial string? PawnIOProblem { get; set; }

    public string PawnIOText => PawnIOProblem
        ?? (PawnIOMissing
            ? "The PawnIO driver is not installed. On most CPUs it is what lets Panelith read CPU temperature, clocks and power. Install PawnIO sets up the signed driver that comes with Panelith; Windows asks for permission. Some anti-cheat software is strict about hardware-access drivers, so check your games' rules first."
            : "The PawnIO driver is installed. CPU temperature, clocks and power are read through it while Panelith runs as administrator.");

    [ObservableProperty]
    public partial bool MediaFeaturePackMissing { get; set; } = WindowsIntegration.IsMediaFoundationMissing;

    [ObservableProperty]
    public partial string MediaFeaturePackText { get; set; } =
        "This edition of Windows has no Media Foundation, which video screens and video wallpapers need. Adding the free Media Feature Pack fixes that; Windows asks for permission.";

    /// <summary>Adds the Windows Media Feature Pack, for Windows N editions.</summary>
    [RelayCommand]
    private async Task AddMediaFeaturePackAsync()
    {
        var problem = await WindowsIntegration.AddMediaFeaturePackAsync();
        MediaFeaturePackText = problem ?? "The Media Feature Pack was added. Restart Windows if videos still do not open.";
    }

    /// <summary>Runs the PawnIO setup that ships with Panelith, or opens its site when this build has none.</summary>
    [RelayCommand]
    private async Task InstallPawnIOAsync()
    {
        PawnIOProblem = null;
        var result = await WindowsIntegration.InstallPawnIOAsync();
        PawnIOMissing = !WindowsIntegration.IsPawnIOInstalled();
        if (PawnIOMissing)
        {
            PawnIOProblem = result ?? "PawnIO was not installed.";
        }
    }

    public bool CanElevate => WindowsIntegration.IsSupported && !WindowsIntegration.IsElevated;

    public string ElevationText => IsElevated
        ? "Running as administrator. Every sensor is available."
        : "Running as a standard user. CPU temperature, clock and power need administrator rights on most CPUs.";

    public string ConfigurationFolder => _service.Runtime.Paths.Root;

    public string ScreensFolder => _service.Runtime.Paths.ThemesDirectory;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    partial void OnFramesPerSecondChanged(int value) => MarkPanelChanged();

    partial void OnJpegQualityChanged(int value) => MarkPanelChanged();

    partial void OnUpsideDownChanged(bool value) => MarkPanelChanged();

    partial void OnStartMinimizedChanged(bool value) => SaveAppSettings();

    partial void OnCloseToTrayChanged(bool value) => SaveAppSettings();

    partial void OnDemoSensorsChanged(bool value) => SaveAppSettings();

    partial void OnSmoothMotionChanged(bool value)
    {
        _service.Renderers.Motion = value;
        SaveAppSettings();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        StartupMessage = WindowsIntegration.SetStartup(value);
        CanRepairStartup = false;
        (var enabled, StartsElevated) = WindowsIntegration.StartupState();
        if (enabled != value)
        {
            StartWithWindows = enabled;
        }
    }

    [RelayCommand]
    private void RepairStartup()
    {
        StartupMessage = WindowsIntegration.RepairStartup();
        CanRepairStartup = false;
        _loading = true;
        (StartWithWindows, StartsElevated) = WindowsIntegration.StartupState();
        _loading = false;
    }

    /// <summary>Reopens the panel with the new output settings.</summary>
    [RelayCommand]
    private async Task ApplyPanelSettingsAsync()
    {
        HasPendingPanelChanges = false;
        await _service.UpdateDefaultsAsync(d => d with
        {
            FramesPerSecond = FramesPerSecond,
            JpegQuality = JpegQuality,
            Orientation = UpsideDown ? PanelOrientation.ReversePortrait : PanelOrientation.Portrait,
        });
    }

    [RelayCommand]
    private void RestartAsAdministrator() => ElevationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenConfigurationFolder() => OpenFolder(ConfigurationFolder);

    [RelayCommand]
    private void OpenScreensFolder() => OpenFolder(ScreensFolder);

    /// <summary>Adds a log line. Called on the UI thread.</summary>
    public void Append(LogEntry entry)
    {
        Log.Add(entry);
        while (Log.Count > 200)
        {
            Log.RemoveAt(0);
        }
    }

    internal static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener, $"\"{folder}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing opened; there is nowhere in these menus to say so.
        }
    }

    private void MarkPanelChanged()
    {
        if (!_loading)
        {
            HasPendingPanelChanges = true;
        }
    }

    private void SaveAppSettings()
    {
        if (_loading)
        {
            return;
        }

        _settings = _store.Load() with
        {
            StartMinimized = StartMinimized,
            CloseToTray = CloseToTray,
            DemoSensors = DemoSensors,
            SmoothMotion = SmoothMotion,
        };

        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StartupMessage = $"Settings could not be saved: {ex.Message}";
        }
    }

    /// <summary>The current desktop settings, including ones changed here.</summary>
    public AppSettings Current => _settings;
}
