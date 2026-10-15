using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RigGlass.Devices;
using RigGlass.Engine.Configuration;
using RigGlass.Engine.Rendering;
using RigGlass.Expressions;
using RigGlass.Imaging;

namespace RigGlass.Engine;

/// <summary>
/// Keeps one panel showing what it is supposed to show, for as long as the
/// application runs.
/// </summary>
/// <remarks>
/// <para>
/// This is where "works seamlessly" is actually earned. Panels disappear on
/// suspend, reset themselves after a bad frame, and get taken away by the
/// vendor's own software; the user's expectation is that none of that requires
/// their attention. The supervisor therefore owns the whole lifecycle - open,
/// draw, push, detect failure, back off, reopen - and treats every failure as a
/// transition rather than as an exit.
/// </para>
/// <para>
/// The retry policy is driven by <see cref="PanelFailureReason"/> rather than by
/// a blanket timer, because the right response differs sharply. A disconnected
/// panel should be waited for quietly and indefinitely. A busy one should be
/// retried while telling the user to close the other application. A panel that
/// the operating system refuses to open needs a one-time permissions fix and
/// will never succeed on its own, so retrying it forever would only hide the
/// message that explains how to fix it.
/// </para>
/// </remarks>
public sealed class PanelSupervisor : IAsyncDisposable
{
    private const int StatsWindow = 30;

    private readonly DetectedPanel _panel;
    private readonly PanelAssignment _assignment;
    private readonly IPanelConnectionFactory _connections;
    private readonly IPanelRendererFactory _renderers;
    private readonly IBindingContext _bindings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _statusGate = new();

    private readonly double[] _renderTimes = new double[StatsWindow];
    private readonly double[] _transferTimes = new double[StatsWindow];
    private int _statsIndex;
    private int _statsCount;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private PanelRuntimeStatus _status;

    /// <summary>Creates a supervisor for one panel.</summary>
    /// <param name="panel">The panel to drive.</param>
    /// <param name="assignment">How it should be driven.</param>
    /// <param name="connections">Opens the panel.</param>
    /// <param name="renderers">Builds the renderer for its theme.</param>
    /// <param name="bindings">Where theme bindings read their values.</param>
    /// <param name="logger">Log sink.</param>
    /// <param name="timeProvider">Clock, so retry behaviour is testable.</param>
    public PanelSupervisor(
        DetectedPanel panel,
        PanelAssignment assignment,
        IPanelConnectionFactory connections,
        IPanelRendererFactory renderers,
        IBindingContext bindings,
        ILogger<PanelSupervisor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _assignment = assignment ?? throw new ArgumentNullException(nameof(assignment));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger as ILogger ?? NullLogger.Instance;

        _status = new PanelRuntimeStatus(
            panel.StableKey,
            assignment.DisplayName ?? panel.Descriptor.FullName,
            PanelRuntimeState.Stopped,
            assignment.Theme);
    }

    /// <summary>The panel being driven.</summary>
    public DetectedPanel Panel => _panel;

    /// <summary>The panel's current health.</summary>
    public PanelRuntimeStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts driving the panel.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        if (!_assignment.Enabled)
        {
            Update(s => s with { State = PanelRuntimeState.Disabled });
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancellation.Token));
    }

    /// <summary>Stops driving the panel and waits for the loop to finish.</summary>
    public async ValueTask StopAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        var loop = Interlocked.Exchange(ref _loop, null);

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the loop is asked to end.
            }
        }

        cancellation?.Dispose();
        Update(s => s with { State = PanelRuntimeState.Stopped, FramesPerSecond = 0 });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Update(s => s with { State = PanelRuntimeState.Connecting, LastError = null, Remedy = null });

                await DriveAsync(cancellationToken).ConfigureAwait(false);

                // A clean return means cancellation; anything else throws.
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (PanelConnectionException ex)
            {
                if (!ex.IsTransient)
                {
                    _logger.LogError(
                        "{Panel} cannot be driven: {Message}{Remedy}",
                        _status.DisplayName,
                        ex.Message,
                        ex.Remedy is { } remedy ? $" {remedy}" : string.Empty);

                    Update(s => s with
                    {
                        State = PanelRuntimeState.Failed,
                        LastError = ex.Message,
                        Remedy = ex.Remedy,
                        FramesPerSecond = 0,
                    });

                    return;
                }

                attempt++;
                var delay = BackoffFor(ex.Reason, attempt);

                // A panel that simply is not plugged in is the normal state of
                // affairs, not an error worth shouting about on every retry.
                var level = ex.Reason == PanelFailureReason.Disconnected && attempt > 1
                    ? LogLevel.Debug
                    : LogLevel.Warning;

                _logger.Log(
                    level,
                    "{Panel} is unavailable ({Reason}); retrying in {Delay:0.#}s. {Message}",
                    _status.DisplayName,
                    ex.Reason,
                    delay.TotalSeconds,
                    ex.Message);

                Update(s => s with
                {
                    State = PanelRuntimeState.Retrying,
                    LastError = ex.Message,
                    Remedy = ex.Remedy,
                    FramesPerSecond = 0,
                });

                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (PanelRenderException ex)
            {
                // A broken theme is the user's to fix, and retrying it in a
                // tight loop would bury the message that says so.
                _logger.LogError(ex, "The theme for {Panel} could not be prepared.", _status.DisplayName);

                Update(s => s with
                {
                    State = PanelRuntimeState.Failed,
                    LastError = ex.Message,
                    Remedy = "Check the theme with 'rigglass theme validate' and fix the reported problems.",
                    FramesPerSecond = 0,
                });

                return;
            }
        }
    }

    private async Task DriveAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connections
            .OpenAsync(_panel, BuildOpenOptions(), cancellationToken)
            .ConfigureAwait(false);

        var (width, height) = connection.FrameSize;
        using var renderer = _renderers.Create(_assignment.Theme, width, height, _assignment.Variables);
        using var frame = FrameBuffer.RentCleared(width, height);

        var target = _assignment.FramesPerSecond
            ?? Math.Max(0.5d, connection.Descriptor.Capabilities.MaxFramesPerSecond);

        var clock = new FrameClock(target, Math.Min(0.5d, target), _time);

        Update(s => s with
        {
            State = PanelRuntimeState.Running,
            ThemeId = _assignment.Theme,
            FrameSize = (width, height),
            FramesSent = 0,
            FramesSkipped = 0,
            LastError = null,
            Remedy = null,
        });

        _logger.LogInformation(
            "{Panel} is running at {Width}x{Height}, up to {Fps:0.#} fps, showing \"{Theme}\".",
            _status.DisplayName,
            width,
            height,
            target,
            _assignment.Theme);

        var sent = 0L;
        var skipped = 0L;
        var previousHash = 0UL;
        var haveSentOnce = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            await clock.WaitAsync(cancellationToken).ConfigureAwait(false);

            var cycleStart = Stopwatch.GetTimestamp();

            renderer.RenderInto(frame, _bindings);
            var renderMilliseconds = Elapsed(cycleStart);

            // Pushing a frame identical to the one already on screen costs the
            // full transfer and changes nothing the user can see. Skipping it is
            // what keeps a mostly-static theme from holding a core busy.
            var hash = Hash(frame);
            var transferMilliseconds = 0d;

            if (!haveSentOnce || hash != previousHash)
            {
                var transferStart = Stopwatch.GetTimestamp();
                await connection.SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                transferMilliseconds = Elapsed(transferStart);

                previousHash = hash;
                haveSentOnce = true;
                sent++;
            }
            else
            {
                skipped++;
            }

            RecordTiming(renderMilliseconds, transferMilliseconds);
            clock.RecordFrame(renderMilliseconds + transferMilliseconds);

            var currentSent = sent;
            var currentSkipped = skipped;
            var measured = clock.MeasuredFramesPerSecond;
            Update(s => s with
            {
                FramesSent = currentSent,
                FramesSkipped = currentSkipped,
                FramesPerSecond = measured,
                AverageRenderMilliseconds = Average(_renderTimes),
                AverageTransferMilliseconds = Average(_transferTimes),
            });
        }
    }

    private PanelOpenOptions BuildOpenOptions() => new(
        _assignment.Orientation,
        _assignment.Brightness,
        _assignment.JpegQuality,
        _assignment.FlipHorizontally);

    /// <summary>
    /// How long to wait before another attempt.
    /// </summary>
    /// <remarks>
    /// Backoff is capped low for a missing panel, because the user expects it to
    /// light up promptly when they plug it back in, and higher for a busy one,
    /// where the fix is for them to close another program.
    /// </remarks>
    private static TimeSpan BackoffFor(PanelFailureReason reason, int attempt)
    {
        var ceiling = reason switch
        {
            PanelFailureReason.Disconnected => 5d,
            PanelFailureReason.NotReady => 3d,
            PanelFailureReason.Busy => 15d,
            _ => 10d,
        };

        var seconds = Math.Min(ceiling, Math.Pow(2, Math.Min(attempt, 6)) * 0.25d);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// A cheap content hash, used only to answer "did anything change?".
    /// </summary>
    /// <remarks>
    /// Sampling rather than hashing every byte keeps this well under a
    /// millisecond on a 1920x480 frame. A sampled hash can in principle miss a
    /// change, so the stride is deliberately coprime with the row length to
    /// avoid walking the same column of every row.
    /// </remarks>
    private static ulong Hash(FrameBuffer frame)
    {
        var pixels = frame.ReadOnlySpan;
        var hash = 14695981039346656037UL;
        var stride = Math.Max(1, (pixels.Length / 4096) | 1);

        for (var i = 0; i < pixels.Length; i += stride)
        {
            hash = (hash ^ pixels[i]) * 1099511628211UL;
        }

        return hash ^ (ulong)pixels.Length;
    }

    private void RecordTiming(double renderMilliseconds, double transferMilliseconds)
    {
        _renderTimes[_statsIndex] = renderMilliseconds;
        _transferTimes[_statsIndex] = transferMilliseconds;
        _statsIndex = (_statsIndex + 1) % StatsWindow;
        _statsCount = Math.Min(StatsWindow, _statsCount + 1);
    }

    private double Average(double[] samples)
    {
        if (_statsCount == 0)
        {
            return 0;
        }

        var total = 0d;
        for (var i = 0; i < _statsCount; i++)
        {
            total += samples[i];
        }

        return total / _statsCount;
    }

    private static double Elapsed(long since) =>
        (Stopwatch.GetTimestamp() - since) * 1000d / Stopwatch.Frequency;

    private void Update(Func<PanelRuntimeStatus, PanelRuntimeStatus> change)
    {
        lock (_statusGate)
        {
            _status = change(_status);
        }
    }
}

/// <summary>Opens panels on the supervisor's behalf.</summary>
/// <remarks>
/// Narrower than the discovery service, so a supervisor can be tested against a
/// connection that fails in a chosen way on a chosen attempt.
/// </remarks>
public interface IPanelConnectionFactory
{
    /// <summary>Opens a panel.</summary>
    /// <exception cref="PanelConnectionException">The panel could not be opened.</exception>
    ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default);
}
