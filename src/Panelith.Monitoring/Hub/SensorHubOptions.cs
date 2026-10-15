namespace Panelith.Monitoring.Hub;

/// <summary>Tunables for <see cref="SensorHub"/>.</summary>
public sealed class SensorHubOptions
{
    /// <summary>How often providers are polled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Per-provider read budget. A source that exceeds this is cancelled for
    /// the tick and, after enough consecutive timeouts, quarantined.
    /// </summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Consecutive failures before a provider is quarantined.</summary>
    public int QuarantineThreshold { get; set; } = 3;

    /// <summary>How long a quarantined provider sits out before one recovery attempt.</summary>
    public TimeSpan QuarantineDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How far back history is retained for graph widgets and avgover/minover/maxover.</summary>
    public TimeSpan HistoryCapacity { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Approximate samples retained per sensor (ring buffer size).</summary>
    public int HistorySamplesPerSensor { get; set; } = 600;
}
