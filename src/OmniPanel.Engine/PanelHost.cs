using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniPanel.Devices;
using OmniPanel.Devices.Drivers;
using OmniPanel.Engine.Configuration;
using OmniPanel.Engine.Rendering;
using OmniPanel.Expressions;

namespace OmniPanel.Engine;

/// <summary>
/// Watches the bus and keeps a supervisor running for every configured panel.
/// </summary>
/// <remarks>
/// <para>
/// Hotplug is the whole point of this type. A panel that is plugged in while
/// OmniPanel is running should start showing its theme without the user doing
/// anything, and one that is unplugged should stop cleanly rather than leaving
/// a loop retrying a handle that no longer exists.
/// </para>
/// <para>
/// Supervisors are keyed on <see cref="PanelLocation.StableKey"/>, so a panel
/// that drops off during suspend and comes back on resume is recognised as the
/// same screen and keeps its theme, its orientation and its statistics.
/// </para>
/// </remarks>
public sealed class PanelHost : IAsyncDisposable
{
    private readonly PanelDiscoveryService _discovery;
    private readonly IPanelRendererFactory _renderers;
    private readonly IBindingContext _bindings;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, PanelSupervisor> _supervisors = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private OmniPanelOptions _options;
    private CancellationTokenSource? _cancellation;
    private Task? _watcher;

    /// <summary>Creates the host.</summary>
    /// <param name="discovery">Finds and opens panels.</param>
    /// <param name="renderers">Builds renderers for themes.</param>
    /// <param name="bindings">Where theme bindings read their values.</param>
    /// <param name="options">Current configuration.</param>
    /// <param name="loggerFactory">Log sink.</param>
    /// <param name="timeProvider">Clock, so the discovery cadence is testable.</param>
    public PanelHost(
        PanelDiscoveryService discovery,
        IPanelRendererFactory renderers,
        IBindingContext bindings,
        OmniPanelOptions options,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<PanelHost>();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The health of every panel the host knows about.</summary>
    public IReadOnlyList<PanelRuntimeStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return [.. _supervisors.Values
                    .Select(s => s.Status)
                    .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>Panels currently being supervised.</summary>
    public IReadOnlyList<DetectedPanel> Panels
    {
        get
        {
            lock (_gate)
            {
                return [.. _supervisors.Values.Select(s => s.Panel)];
            }
        }
    }

    /// <summary>Starts discovery and begins driving panels.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_watcher is not null)
        {
            return;
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;

        // One synchronous pass first, so that a caller who starts the host and
        // immediately reads Statuses sees the panels that are already attached
        // rather than an empty list.
        await ReconcileAsync(token).ConfigureAwait(false);

        _watcher = Task.Run(() => WatchAsync(token), CancellationToken.None);
    }

    /// <summary>Stops every panel and waits for the loops to finish.</summary>
    public async ValueTask StopAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        var watcher = Interlocked.Exchange(ref _watcher, null);

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (watcher is not null)
        {
            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the watcher is asked to end.
            }
        }

        cancellation?.Dispose();

        PanelSupervisor[] supervisors;
        lock (_gate)
        {
            supervisors = [.. _supervisors.Values];
            _supervisors.Clear();
        }

        foreach (var supervisor in supervisors)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies new configuration, restarting only the panels whose settings
    /// actually changed.
    /// </summary>
    /// <remarks>
    /// Restarting every panel on any configuration change would make editing one
    /// panel's theme flash the others, which looks like a bug even though
    /// nothing is wrong.
    /// </remarks>
    public async ValueTask ApplyAsync(OmniPanelOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var previous = _options;
        _options = options;

        List<PanelSupervisor> stale = [];
        lock (_gate)
        {
            foreach (var (key, supervisor) in _supervisors)
            {
                if (previous.AssignmentFor(supervisor.Panel) != options.AssignmentFor(supervisor.Panel))
                {
                    stale.Add(supervisor);
                }

                _ = key;
            }

            foreach (var supervisor in stale)
            {
                _supervisors.Remove(supervisor.Panel.StableKey);
            }
        }

        foreach (var supervisor in stale)
        {
            _logger.LogInformation("Restarting {Panel} because its settings changed.", supervisor.Status.DisplayName);
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one discovery pass and starts or stops supervisors to match.</summary>
    public async ValueTask ReconcileAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DetectedPanel> panels;
        try
        {
            panels = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Panel discovery failed; the previous device list is being kept.");
            return;
        }

        var present = panels.ToDictionary(p => p.StableKey, StringComparer.Ordinal);

        List<PanelSupervisor> departed = [];
        List<PanelSupervisor> arrived = [];

        lock (_gate)
        {
            foreach (var key in _supervisors.Keys.ToList())
            {
                if (!present.ContainsKey(key))
                {
                    departed.Add(_supervisors[key]);
                    _supervisors.Remove(key);
                }
            }

            foreach (var (key, panel) in present)
            {
                if (_supervisors.ContainsKey(key))
                {
                    continue;
                }

                var supervisor = new PanelSupervisor(
                    panel,
                    _options.AssignmentFor(panel),
                    new DiscoveryConnectionFactory(_discovery),
                    _renderers,
                    _bindings,
                    _loggers.CreateLogger<PanelSupervisor>(),
                    _time);

                _supervisors[key] = supervisor;
                arrived.Add(supervisor);
            }
        }

        foreach (var supervisor in departed)
        {
            _logger.LogInformation("{Panel} was unplugged.", supervisor.Status.DisplayName);
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var supervisor in arrived)
        {
            _logger.LogInformation("{Panel} appeared.", supervisor.Status.DisplayName);
            supervisor.Start();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.DiscoveryIntervalSeconds, 0.5d, 300d));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _time, cancellationToken).ConfigureAwait(false);
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private sealed class DiscoveryConnectionFactory : IPanelConnectionFactory
    {
        private readonly PanelDiscoveryService _discovery;

        internal DiscoveryConnectionFactory(PanelDiscoveryService discovery) => _discovery = discovery;

        public ValueTask<IPanelConnection> OpenAsync(
            DetectedPanel panel,
            PanelOpenOptions options,
            CancellationToken cancellationToken = default) =>
            _discovery.OpenAsync(panel, options, cancellationToken);
    }
}
