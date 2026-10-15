using Panelith.Devices;

namespace Panelith.Engine;

/// <summary>What a supervised panel is doing right now.</summary>
public enum PanelRuntimeState
{
    /// <summary>Not being driven, because configuration disabled it.</summary>
    Disabled = 0,

    /// <summary>Opening the panel.</summary>
    Connecting,

    /// <summary>Sending frames.</summary>
    Running,

    /// <summary>Waiting before another attempt after a recoverable failure.</summary>
    Retrying,

    /// <summary>Stopped, because the failure cannot be retried.</summary>
    Failed,

    /// <summary>Shut down deliberately.</summary>
    Stopped,
}

/// <summary>
/// A snapshot of one panel's health, as shown by <c>panelith status</c> and by
/// the desktop UI.
/// </summary>
/// <param name="Key">The panel's stable key.</param>
/// <param name="DisplayName">What to call it.</param>
/// <param name="State">What it is doing.</param>
/// <param name="ThemeId">The theme being drawn.</param>
/// <param name="FrameSize">The size of frame being produced.</param>
/// <param name="FramesSent">Frames accepted by the panel since it was opened.</param>
/// <param name="FramesSkipped">
/// Frames that were not sent because nothing had changed.
/// </param>
/// <param name="FramesPerSecond">Measured rate over the last second.</param>
/// <param name="AverageRenderMilliseconds">Mean time spent drawing a frame.</param>
/// <param name="AverageTransferMilliseconds">Mean time spent pushing a frame to the panel.</param>
/// <param name="Reconnects">How many times the link has been re-established.</param>
/// <param name="LastError">The most recent failure message, if any.</param>
/// <param name="Remedy">What the user can do about it, if anything.</param>
public sealed record PanelRuntimeStatus(
    string Key,
    string DisplayName,
    PanelRuntimeState State,
    string? ThemeId = null,
    (int Width, int Height)? FrameSize = null,
    long FramesSent = 0,
    long FramesSkipped = 0,
    double FramesPerSecond = 0,
    double AverageRenderMilliseconds = 0,
    double AverageTransferMilliseconds = 0,
    int Reconnects = 0,
    string? LastError = null,
    string? Remedy = null)
{
    /// <summary>Whether the panel is currently showing what it should be.</summary>
    public bool IsHealthy => State == PanelRuntimeState.Running;

    /// <summary>A one-line summary.</summary>
    public string Describe()
    {
        var size = FrameSize is { } s ? $"{s.Width}x{s.Height}" : "unknown size";
        var rate = State == PanelRuntimeState.Running
            ? $", {FramesPerSecond:0.0} fps"
            : string.Empty;

        var detail = LastError is { Length: > 0 } error ? $" - {error}" : string.Empty;
        return $"{DisplayName} [{State}] {size}{rate}{detail}";
    }

    /// <summary>Builds the status of a panel that configuration has switched off.</summary>
    public static PanelRuntimeStatus ForDisabled(DetectedPanel panel, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(panel);

        return new PanelRuntimeStatus(
            panel.StableKey,
            displayName ?? panel.Descriptor.FullName,
            PanelRuntimeState.Disabled);
    }
}
