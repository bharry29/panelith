using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Panelith.App.Controls;
using Panelith.App.Services;
using Panelith.Engine;
using Panelith.Imaging;
using Panelith.Rendering;
using Panelith.Sensors;
using Panelith.Themes;

namespace Panelith.App.ViewModels;

/// <summary>What the panel is showing right now, and how the machine is doing.</summary>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(90);

    private readonly PanelithService _service;
    private readonly ILogger _logger;
    private readonly StatTileViewModel _cpu = new("CPU", Color.Parse("#3ee0c5"), 0, 100);
    private readonly StatTileViewModel _cpuTemp = new("CPU temperature", Color.Parse("#2fd3ff"), 25, 95);
    private readonly StatTileViewModel _gpu = new("GPU", Color.Parse("#7aa2ff"), 0, 100);
    private readonly StatTileViewModel _gpuTemp = new("GPU temperature", Color.Parse("#b48cff"), 25, 95);
    private readonly StatTileViewModel _memory = new("Memory", Color.Parse("#f0c14a"), 0, 100);
    private readonly StatTileViewModel _network = new("Network", Color.Parse("#ff8a5c"), 0);

    private ThemeRenderer? _preview;
    private string? _previewTheme;
    private (int Width, int Height) _previewSize;
    private (int Width, int Height) _previewRendererSize;
    private long _mirrorVersion;
    private bool _rendering;

    public DashboardViewModel(PanelithService service, ScreensViewModel screens)
    {
        _service = service;
        _logger = service.Log.CreateLogger("Panelith.App.Dashboard");
        Screens = screens;
        Tiles = [_cpu, _cpuTemp, _gpu, _gpuTemp, _memory, _network];
    }

    /// <summary>The screen gallery, for the active screen name and the next-screen command.</summary>
    public ScreensViewModel Screens { get; }

    /// <summary>The frame on the panel, or a local preview when no panel is attached.</summary>
    public LiveFrame Mirror { get; } = new();

    /// <summary>Live statistics.</summary>
    public IReadOnlyList<StatTileViewModel> Tiles { get; }

    /// <summary>Attached panels.</summary>
    public ObservableCollection<PanelCardViewModel> Panels { get; } = [];

    [ObservableProperty]
    public partial string MirrorCaption { get; set; } = "Starting…";

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool HasPanels { get; set; }

    [ObservableProperty]
    public partial string MachineName { get; set; } = Environment.MachineName;

    [ObservableProperty]
    public partial string MachineSummary { get; set; } = string.Empty;

    /// <summary>Copies the newest panel frame in. Called often while the dashboard is visible.</summary>
    public void RefreshMirror()
    {
        var mirror = _service.Renderers.Mirror;
        var fresh = mirror.LastPublished is { } last && DateTimeOffset.Now - last < TimeSpan.FromSeconds(1.5);
        IsLive = fresh;

        if (fresh)
        {
            var version = mirror.Version;
            if (version == _mirrorVersion)
            {
                return;
            }

            _mirrorVersion = version;
            var (width, height) = mirror.Size;
            Mirror.Update(width, height, (destination, stride) => mirror.TryCopyTo(destination, width, height, stride));
            return;
        }

        RenderPreview();
    }

    /// <summary>Updates the tiles and the panel list. Called about once a second.</summary>
    public void RefreshStats()
    {
        var hub = _service.Runtime.Sensors;

        var cpuLoad = SensorAccess.Number(hub, WellKnownSensors.CpuLoad);
        _cpu.Value = SensorAccess.Whole(cpuLoad, "%");
        _cpu.Detail = JoinParts(SensorAccess.Format(hub, WellKnownSensors.CpuClock, ""), SensorAccess.Format(hub, WellKnownSensors.CpuPower, ""));
        _cpu.History = hub.ResolveHistory(WellKnownSensors.CpuLoad, HistoryWindow);

        var cpuTemp = SensorAccess.Number(hub, WellKnownSensors.CpuTemperature);
        _cpuTemp.Value = SensorAccess.Whole(cpuTemp, "°C");
        _cpuTemp.Detail = cpuTemp is null
            ? WindowsIntegration.IsElevated ? "Not reported by this CPU" : "Needs administrator rights"
            : "Peak " + SensorAccess.Whole(Max(hub.ResolveHistory(WellKnownSensors.CpuTemperature, HistoryWindow)), "°C") + " in 90 s";
        _cpuTemp.History = hub.ResolveHistory(WellKnownSensors.CpuTemperature, HistoryWindow);

        _gpu.Value = SensorAccess.Whole(SensorAccess.Number(hub, WellKnownSensors.GpuLoad), "%");
        _gpu.Detail = JoinParts(SensorAccess.Format(hub, WellKnownSensors.GpuClock, ""), SensorAccess.Format(hub, WellKnownSensors.GpuPower, ""));
        _gpu.History = hub.ResolveHistory(WellKnownSensors.GpuLoad, HistoryWindow);

        _gpuTemp.Value = SensorAccess.Whole(SensorAccess.Number(hub, WellKnownSensors.GpuTemperature), "°C");
        _gpuTemp.Detail = JoinParts(
            SensorAccess.Format(hub, WellKnownSensors.GpuFan, "") is { Length: > 0 } fan ? "Fan " + fan : "",
            SensorAccess.Format(hub, WellKnownSensors.GpuMemoryUsed, "") is { Length: > 0 } vram ? "VRAM " + vram : "");
        _gpuTemp.History = hub.ResolveHistory(WellKnownSensors.GpuTemperature, HistoryWindow);

        _memory.Value = SensorAccess.Whole(SensorAccess.Number(hub, WellKnownSensors.MemoryLoad), "%");
        _memory.Detail = $"{SensorAccess.Format(hub, WellKnownSensors.MemoryUsed)} of {SensorAccess.Format(hub, WellKnownSensors.MemoryTotal)}";
        _memory.History = hub.ResolveHistory(WellKnownSensors.MemoryLoad, HistoryWindow);

        _network.Value = SensorAccess.Format(hub, WellKnownSensors.NetworkDown);
        _network.Detail = "Up " + SensorAccess.Format(hub, WellKnownSensors.NetworkUp);
        _network.History = hub.ResolveHistory(WellKnownSensors.NetworkDown, HistoryWindow);

        MachineName = SensorAccess.Read(hub, WellKnownSensors.HostName)?.Text is { Length: > 0 } host ? host : Environment.MachineName;
        MachineSummary = JoinParts(
            SensorAccess.Format(hub, WellKnownSensors.CpuName, ""),
            SensorAccess.Format(hub, WellKnownSensors.GpuName, ""),
            SensorAccess.Format(hub, WellKnownSensors.OperatingSystem, ""));

        UpdatePanels(_service.Host.Statuses);
    }

    /// <summary>Forgets the cached preview so an edited screen is reread from disk.</summary>
    public void InvalidatePreview() => _previewTheme = null;

    /// <inheritdoc />
    public void Dispose()
    {
        _preview?.Dispose();
        Mirror.Dispose();
    }

    private void UpdatePanels(IReadOnlyList<PanelRuntimeStatus> statuses)
    {
        for (var i = Panels.Count - 1; i >= 0; i--)
        {
            if (!statuses.Any(s => s.Key == Panels[i].Key))
            {
                Panels.RemoveAt(i);
            }
        }

        foreach (var status in statuses)
        {
            var card = Panels.FirstOrDefault(p => p.Key == status.Key);
            if (card is null)
            {
                card = new PanelCardViewModel(status.Key);
                Panels.Add(card);
            }

            card.Update(status);
        }

        HasPanels = Panels.Count > 0;
        var running = statuses.FirstOrDefault(s => s.State == PanelRuntimeState.Running);
        MirrorCaption = running is not null
            ? $"Live on {running.DisplayName}"
            : statuses.Count > 0
                ? $"{statuses[0].DisplayName} is not showing anything yet. This is a preview."
                : "No panel connected. This is a preview of what it would show.";
        _previewSize = running?.FrameSize ?? statuses.FirstOrDefault()?.FrameSize ?? (1920, 480);
    }

    /// <summary>Draws the active screen locally, for when no panel is publishing frames.</summary>
    private void RenderPreview()
    {
        if (_rendering)
        {
            return;
        }

        var themeId = _service.ActiveScreen;
        var size = _previewSize == default ? (1920, 480) : _previewSize;
        if (_preview is null || _previewTheme != themeId || _previewRendererSize != size)
        {
            _preview?.Dispose();
            _preview = null;
            _previewTheme = themeId;
            _previewRendererSize = size;
            try
            {
                var theme = _service.Library.Load(themeId);
                _preview = new ThemeRenderer(theme, new ThemeRenderOptions(size.Item1, size.Item2, ShowErrorMarkers: true, ThemeFolder: theme.BasePath));
            }
            catch (Exception ex)
            {
                MirrorCaption = $"The screen \"{themeId}\" could not be loaded: {ex.Message}";
                return;
            }
        }

        var renderer = _preview;
        var bindings = _service.Runtime.Bindings;
        _rendering = true;
        Task.Run(() => renderer.Render(bindings)).ContinueWith(
            task =>
            {
                _rendering = false;
                if (task.IsCompletedSuccessfully)
                {
                    using FrameBuffer frame = task.Result;
                    Mirror.Update(frame);
                }
                else
                {
                    _logger.LogWarning("The preview could not be drawn: {Error}", task.Exception?.GetBaseException().ToString());
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static double? Max(IReadOnlyList<double> values) => values.Count == 0 ? null : values.Max();

    private static string JoinParts(params string[] parts) =>
        string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p) && p != "--"));
}
