using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Panelith.Engine;

namespace Panelith.App.ViewModels;

/// <summary>The health of one attached panel.</summary>
public sealed partial class PanelCardViewModel(string key) : ObservableObject
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3ee0c5"));
    private static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#f0c14a"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#ff5d73"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#5b6b7f"));

    /// <summary>The panel's stable key.</summary>
    public string Key { get; } = key;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string State { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush StateBrush { get; set; } = Idle;

    [ObservableProperty]
    public partial string Resolution { get; set; } = "--";

    [ObservableProperty]
    public partial string Rate { get; set; } = "--";

    [ObservableProperty]
    public partial string Frames { get; set; } = "--";

    [ObservableProperty]
    public partial string Timing { get; set; } = "--";

    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>Copies a status snapshot in.</summary>
    public void Update(PanelRuntimeStatus status)
    {
        Name = status.DisplayName;
        (State, StateBrush) = status.State switch
        {
            PanelRuntimeState.Running => ("Live", Good),
            PanelRuntimeState.Connecting => ("Connecting", Busy),
            PanelRuntimeState.Retrying => ("Reconnecting", Busy),
            PanelRuntimeState.Failed => ("Failed", Bad),
            PanelRuntimeState.Disabled => ("Disabled", Idle),
            _ => ("Stopped", Idle),
        };

        var c = CultureInfo.InvariantCulture;
        Resolution = status.FrameSize is { } size ? $"{size.Width} × {size.Height}" : "--";
        Rate = status.State == PanelRuntimeState.Running ? status.FramesPerSecond.ToString("0.0 fps", c) : "--";
        Frames = status.FramesSent.ToString("N0", c) + (status.FramesSkipped > 0 ? $"  (+{status.FramesSkipped.ToString("N0", c)} unchanged)" : string.Empty);
        Timing = status.State == PanelRuntimeState.Running
            ? $"{status.AverageRenderMilliseconds.ToString("0.0", c)} ms draw · {status.AverageTransferMilliseconds.ToString("0.0", c)} ms USB"
            : "--";
        Problem = status.LastError is { Length: > 0 } error
            ? status.Remedy is { Length: > 0 } remedy ? $"{error} {remedy}" : error
            : null;
    }
}
