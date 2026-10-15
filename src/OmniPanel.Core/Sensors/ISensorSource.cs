namespace OmniPanel.Sensors;

/// <summary>
/// Supplies sensor readings from one backend, such as the Linux hwmon tree,
/// LibreHardwareMonitor, or a plugin.
/// </summary>
/// <remarks>
/// <para>
/// A source is polled from a single background thread. Implementations do not
/// need to be thread-safe for concurrent <see cref="ReadAsync"/> calls, but they
/// must not block for longer than the poll interval; a source that needs to do
/// slow work should cache internally and return the cached value.
/// </para>
/// <para>
/// Sources are expected to degrade rather than fail. A backend that is
/// unavailable on the current machine should report
/// <see cref="IsAvailable"/> as <see langword="false"/> and return no readings,
/// so that the rest of the pipeline keeps running.
/// </para>
/// </remarks>
public interface ISensorSource : IAsyncDisposable
{
    /// <summary>
    /// Stable identifier for this source, used to namespace sensor ids and to
    /// enable or disable the source in configuration. Lowercase, no spaces.
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable name, shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this backend can run on the current machine. Checked once during
    /// startup; a source reporting <see langword="false"/> is never polled.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether this source needs elevated privileges to produce complete data.
    /// Used to show an actionable hint rather than silently missing sensors.
    /// </summary>
    bool RequiresElevation => false;

    /// <summary>
    /// Prepares the backend. Called once before the first <see cref="ReadAsync"/>.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples every sensor this source exposes.
    /// </summary>
    /// <remarks>
    /// Returning an empty sequence is valid and means "nothing to report right
    /// now"; it does not mark the source as broken.
    /// </remarks>
    ValueTask<IReadOnlyList<SensorReading>> ReadAsync(CancellationToken cancellationToken = default);
}
