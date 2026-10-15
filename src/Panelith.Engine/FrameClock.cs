namespace Panelith.Engine;

/// <summary>
/// Paces a render loop and measures what it actually achieved.
/// </summary>
/// <remarks>
/// <para>
/// Panels differ by more than an order of magnitude in how fast they can accept
/// frames: a serial Turing panel manages one or two full frames a second, while
/// a bulk JPEG panel will take thirty. Asking for more than the link can carry
/// does not produce a faster display - the writes simply queue - but it does
/// produce a pegged core, which is the complaint users most often level at
/// panel software.
/// </para>
/// <para>
/// So the clock treats the configured rate as a ceiling and lowers it towards
/// what the panel is measurably sustaining. When a cycle overruns its budget the
/// next deadline is pushed out rather than being chased, which keeps the loop
/// from spinning without sleeping.
/// </para>
/// </remarks>
public sealed class FrameClock
{
    private readonly TimeProvider _time;
    private readonly double _minimumInterval;
    private readonly double _maximumInterval;
    private double _interval;
    private long _nextDeadline;
    private long _windowStart;
    private int _framesInWindow;

    /// <summary>Creates a clock.</summary>
    /// <param name="targetFramesPerSecond">The ceiling to aim for.</param>
    /// <param name="minimumFramesPerSecond">
    /// The floor the clock will not back off below, so a panel never appears to
    /// have frozen.
    /// </param>
    /// <param name="timeProvider">Clock, so pacing is testable.</param>
    public FrameClock(
        double targetFramesPerSecond,
        double minimumFramesPerSecond = 0.5,
        TimeProvider? timeProvider = null)
    {
        if (targetFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetFramesPerSecond),
                targetFramesPerSecond,
                "The target frame rate must be positive.");
        }

        if (minimumFramesPerSecond <= 0 || minimumFramesPerSecond > targetFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumFramesPerSecond),
                minimumFramesPerSecond,
                "The minimum frame rate must be positive and no greater than the target.");
        }

        _time = timeProvider ?? TimeProvider.System;
        _minimumInterval = 1000d / targetFramesPerSecond;
        _maximumInterval = 1000d / minimumFramesPerSecond;
        _interval = _minimumInterval;
        TargetFramesPerSecond = targetFramesPerSecond;

        _nextDeadline = _time.GetTimestamp();
        _windowStart = _nextDeadline;
    }

    /// <summary>The configured ceiling.</summary>
    public double TargetFramesPerSecond { get; }

    /// <summary>The rate the clock is currently pacing at.</summary>
    public double CurrentFramesPerSecond => 1000d / _interval;

    /// <summary>The rate measured over the last completed second.</summary>
    public double MeasuredFramesPerSecond { get; private set; }

    /// <summary>How long to wait before the next cycle should begin.</summary>
    public TimeSpan TimeUntilNextFrame
    {
        get
        {
            var remaining = Milliseconds(_nextDeadline - _time.GetTimestamp());
            return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
        }
    }

    /// <summary>
    /// Records that a cycle finished and schedules the next one.
    /// </summary>
    /// <param name="cycleMilliseconds">
    /// How long the render and transfer took. A cycle that took longer than the
    /// interval means the panel cannot keep up, so the interval grows.
    /// </param>
    public void RecordFrame(double cycleMilliseconds)
    {
        var now = _time.GetTimestamp();

        // Grow towards the measured cost, shrink back gently once it recovers.
        // Reacting instantly in both directions makes the rate oscillate
        // visibly, which looks worse than a slightly stale target.
        if (cycleMilliseconds > _interval)
        {
            _interval = Math.Min(_maximumInterval, (_interval + cycleMilliseconds) / 2d);
        }
        else
        {
            _interval = Math.Max(_minimumInterval, (_interval * 0.9d) + (_minimumInterval * 0.1d));
        }

        _nextDeadline = Advance(now);

        _framesInWindow++;
        var windowMilliseconds = Milliseconds(now - _windowStart);
        if (windowMilliseconds >= 1000d)
        {
            MeasuredFramesPerSecond = _framesInWindow * 1000d / windowMilliseconds;
            _framesInWindow = 0;
            _windowStart = now;
        }
    }

    /// <summary>Waits until the next cycle is due.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        var wait = TimeUntilNextFrame;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drops any accumulated lateness, after a reconnect or a pause.</summary>
    public void Reset()
    {
        _interval = _minimumInterval;
        _nextDeadline = _time.GetTimestamp();
        _windowStart = _nextDeadline;
        _framesInWindow = 0;
        MeasuredFramesPerSecond = 0;
    }

    private long Advance(long now)
    {
        var candidate = _nextDeadline + (long)(_interval * _time.TimestampFrequency / 1000d);

        // If the loop has fallen far behind - a suspended machine, a blocked
        // write - catching up by firing a burst of frames would only make the
        // backlog worse, so the schedule restarts from now.
        return candidate < now ? now + (long)(_interval * _time.TimestampFrequency / 1000d) : candidate;
    }

    private double Milliseconds(long ticks) => ticks * 1000d / _time.TimestampFrequency;
}
