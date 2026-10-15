using Panelith.Expressions;
using Panelith.Sensors;

namespace Panelith.Rendering.Motion;

/// <summary>
/// Wraps a binding context so numeric sensor values glide between readings
/// instead of jumping once a second.
/// </summary>
/// <remarks>
/// <para>
/// Each new reading first goes through a light exponential filter whose
/// strength depends on the kind of sensor, so a CPU load bouncing between 21%
/// and 27% reads as a calm number. The filtered value becomes a target, and
/// every frame returns a value eased toward it, starting from whatever is on
/// screen so a reading that arrives mid-move does not jerk.
/// </para>
/// <para>
/// Because the smoothing happens here, every widget that reads a sensor (text,
/// bars, gauges, colour stops) moves smoothly without knowing about it. Text
/// values and history pass through unchanged. One instance belongs to one
/// render loop and is not thread-safe.
/// </para>
/// </remarks>
public sealed class MotionBindingContext : IBindingContext, ISensorKindProvider, ISmoothedBindingContext
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PruneAfter = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly List<string> _stale = [];
    private IBindingContext _inner = EmptyBindingContext.Instance;
    private DateTimeOffset _sampleTime;
    private long _sampleVersion;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private long _now;
    private long _lastPrune;

    /// <summary>Creates the context.</summary>
    /// <param name="time">The clock animations run on; the system clock when null.</param>
    public MotionBindingContext(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _now = _time.GetTimestamp();
        _lastPrune = _now;
    }

    /// <summary>Whether values are smoothed. When off, every value passes straight through.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a move toward a new reading takes, as a fraction of the time between readings.</summary>
    public double EaseFraction { get; set; } = 0.85;

    /// <summary>The time between readings, as measured.</summary>
    public TimeSpan SampleInterval => _interval;

    /// <inheritdoc />
    public IBindingContext Raw => _inner;

    /// <inheritdoc />
    public DateTimeOffset Now => _inner.Now;

    /// <summary>Points the context at this frame's readings. Call once per frame before rendering.</summary>
    public void Reset(IBindingContext inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _now = _time.GetTimestamp();

        if (inner is ISampledBindingContext sampled)
        {
            var sampleTime = sampled.SampleTime;
            if (sampleTime != _sampleTime)
            {
                if (_sampleTime != default && sampleTime > _sampleTime)
                {
                    var measured = sampleTime - _sampleTime;
                    measured = measured < MinimumInterval ? MinimumInterval : measured > MaximumInterval ? MaximumInterval : measured;
                    _interval = TimeSpan.FromTicks((long)((_interval.Ticks * 0.7) + (measured.Ticks * 0.3)));
                }

                _sampleTime = sampleTime;
                _sampleVersion++;
            }
        }

        if (_time.GetElapsedTime(_lastPrune, _now) > PruneAfter)
        {
            Prune();
        }
    }

    /// <inheritdoc />
    public BindingValue ResolveSensor(string reference)
    {
        var raw = _inner.ResolveSensor(reference);
        if (!Enabled || raw.Kind != BindingValueKind.Number || double.IsInfinity(raw.Number))
        {
            _tracks.Remove(reference);
            return raw;
        }

        var value = raw.Number;
        if (!_tracks.TryGetValue(reference, out var track))
        {
            track = new Track(value, _now, _sampleVersion, FilterStrength(KindOf(reference)));
            _tracks[reference] = track;
            return raw;
        }

        track.LastUsed = _now;
        var newReading = _inner is ISampledBindingContext
            ? track.Version != _sampleVersion
            : value != track.LastRaw;
        if (newReading)
        {
            track.Version = _sampleVersion;
            track.LastRaw = value;
            track.Filtered += track.Alpha * (value - track.Filtered);
            track.From = Current(track);
            track.To = track.Filtered;
            track.Start = _now;
        }

        return BindingValue.FromNumber(Current(track));
    }

    /// <inheritdoc />
    public BindingValue ResolveVariable(string name) => _inner.ResolveVariable(name);

    /// <inheritdoc />
    public IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) => _inner.ResolveHistory(reference, window);

    /// <inheritdoc />
    public IReadOnlyList<HistorySample> ResolveHistorySamples(string reference, TimeSpan window) =>
        _inner.ResolveHistorySamples(reference, window);

    /// <inheritdoc />
    public SensorKind KindOf(string reference) =>
        _inner is ISensorKindProvider provider
            ? provider.KindOf(reference)
            : WellKnownSensors.All.TryGetValue(reference, out var kind) ? kind : SensorKind.Unknown;

    /// <summary>Ease-out cubic: fast at first, settling gently.</summary>
    public static double EaseOut(double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        var inverse = 1d - t;
        return 1d - (inverse * inverse * inverse);
    }

    /// <summary>
    /// How much of each new reading the filter takes, from 0 (ignore it) to 1
    /// (take it as is). Noisy, fast-changing kinds are filtered harder.
    /// </summary>
    public static double FilterStrength(SensorKind kind) => kind switch
    {
        SensorKind.Load => 0.45,
        SensorKind.Power => 0.5,
        SensorKind.FrameRate => 0.5,
        SensorKind.Throughput => 0.6,
        SensorKind.Clock => 0.6,
        SensorKind.Fan => 0.6,
        SensorKind.Temperature => 0.75,
        _ => 1d,
    };

    private double Current(Track track)
    {
        var duration = _interval.TotalSeconds * EaseFraction;
        var elapsed = _time.GetElapsedTime(track.Start, _now).TotalSeconds;
        var progress = duration <= 0 ? 1d : EaseOut(elapsed / duration);
        return track.From + ((track.To - track.From) * progress);
    }

    private void Prune()
    {
        _lastPrune = _now;
        foreach (var (reference, track) in _tracks)
        {
            if (_time.GetElapsedTime(track.LastUsed, _now) > PruneAfter)
            {
                _stale.Add(reference);
            }
        }

        foreach (var reference in _stale)
        {
            _tracks.Remove(reference);
        }

        _stale.Clear();
    }

    private sealed class Track(double value, long now, long version, double alpha)
    {
        public double Alpha { get; } = alpha;

        public double LastRaw { get; set; } = value;

        public double Filtered { get; set; } = value;

        public double From { get; set; } = value;

        public double To { get; set; } = value;

        public long Start { get; set; } = now;

        public long LastUsed { get; set; } = now;

        public long Version { get; set; } = version;
    }
}
