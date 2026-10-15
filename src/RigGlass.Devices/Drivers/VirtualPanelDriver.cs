using Microsoft.Extensions.Logging;
using RigGlass.Devices.Encoding;
using RigGlass.Devices.Profiles;
using RigGlass.Imaging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// A panel that does not exist.
/// </summary>
/// <remarks>
/// <para>
/// Always present, at any resolution the caller asks for. This is what makes
/// the theme editor, the preview command and most of the test suite usable with
/// no hardware attached, and it is the first thing a new contributor can run.
/// </para>
/// <para>
/// Frames are kept in memory and, when a folder is configured, written out as
/// PNG files. That turns "does this theme look right on a 1280x480 panel?" into
/// a question anyone can answer without owning one.
/// </para>
/// </remarks>
public sealed class VirtualPanelDriver : IPanelDriver
{
    /// <summary>The driver id used in profiles.</summary>
    public const string DriverId = "virtual";

    private readonly PanelDriverContext _context;
    private readonly ILogger _logger;
    private readonly VirtualPanelOptions _options;

    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    /// <param name="options">How the virtual panel should behave.</param>
    public VirtualPanelDriver(PanelDriverContext context, VirtualPanelOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? new VirtualPanelOptions();
        _logger = context.LoggerFactory.CreateLogger<VirtualPanelDriver>();
    }

    /// <inheritdoc />
    public string Id => DriverId;

    /// <inheritdoc />
    public string DisplayName => "Virtual panel";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var detected = new List<DetectedPanel>();
        foreach (var profile in _context.Profiles.ForDriver(Id))
        {
            var descriptor = profile.ToDescriptor();
            if (_options.Width is { } width && _options.Height is { } height)
            {
                descriptor = descriptor with { Width = width, Height = height };
            }

            detected.Add(new DetectedPanel(
                descriptor,
                PanelLocation.Virtual(profile.Id),
                Id,
                PanelMatchConfidence.Confirmed));
        }

        return ValueTask.FromResult<IReadOnlyList<DetectedPanel>>(detected);
    }

    /// <inheritdoc />
    public ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.OutputDirectory is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        return ValueTask.FromResult<IPanelConnection>(new VirtualPanelConnection(
            panel.Descriptor,
            panel.Location,
            options,
            _options,
            _context.Codec,
            _logger));
    }
}

/// <summary>How the virtual panel behaves.</summary>
public sealed record VirtualPanelOptions
{
    /// <summary>Override width, or <see langword="null"/> to use the profile's.</summary>
    public int? Width { get; init; }

    /// <summary>Override height, or <see langword="null"/> to use the profile's.</summary>
    public int? Height { get; init; }

    /// <summary>Folder to write PNG frames into, or <see langword="null"/> to keep them in memory only.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// Whether every frame is written, or only the most recent one.
    /// </summary>
    /// <remarks>
    /// Overwriting one file is what a live preview wants; a numbered sequence is
    /// what recording an animation for a bug report wants.
    /// </remarks>
    public bool WriteEveryFrame { get; init; }

    /// <summary>Stop after this many frames, or <see langword="null"/> for no limit.</summary>
    public int? FrameLimit { get; init; }
}

/// <summary>An open connection to a virtual panel.</summary>
internal sealed class VirtualPanelConnection : IPanelConnection
{
    private readonly VirtualPanelOptions _options;
    private readonly IImageCodec _codec;
    private readonly ILogger _logger;
    private readonly PanelOrientation _orientation;
    private readonly bool _mirror;
    private readonly Lock _gate = new();
    private FrameBuffer? _lastFrame;
    private int _frameCount;
    private bool _disposed;

    internal VirtualPanelConnection(
        PanelDescriptor descriptor,
        PanelLocation location,
        PanelOpenOptions options,
        VirtualPanelOptions virtualOptions,
        IImageCodec codec,
        ILogger logger)
    {
        _options = virtualOptions;
        _codec = codec;
        _logger = logger;
        _orientation = options.Orientation;
        _mirror = options.FlipHorizontally;
        Descriptor = descriptor;
        Location = location;
        FrameSize = descriptor.RenderSize(options.Orientation);
    }

    public PanelDescriptor Descriptor { get; }

    public PanelLocation Location { get; }

    public bool IsConnected => !_disposed;

    public (int Width, int Height) FrameSize { get; }

    /// <summary>How many frames have been accepted.</summary>
    internal int FrameCount => Volatile.Read(ref _frameCount);

    /// <summary>Brightness last requested, for tests that assert on it.</summary>
    internal int Brightness { get; private set; } = 100;

    /// <summary>Whether the screen was last asked to be on.</summary>
    internal bool ScreenOn { get; private set; } = true;

    public ValueTask SendFrameAsync(FrameBuffer frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if ((frame.Width, frame.Height) != FrameSize)
        {
            throw new PanelConnectionException(
                $"This panel expects {FrameSize.Width}x{FrameSize.Height} frames, "
                + $"but a {frame.Width}x{frame.Height} frame was supplied.",
                PanelFailureReason.ProtocolError);
        }

        var index = Interlocked.Increment(ref _frameCount);

        var oriented = FrameOrientation.ApplyOrPassThrough(frame, _orientation, _mirror, out var rented);
        try
        {
            lock (_gate)
            {
                var copy = FrameBuffer.Rent(oriented.Width, oriented.Height);
                oriented.CopyTo(copy);
                _lastFrame?.Dispose();
                _lastFrame = copy;
            }

            if (_options.OutputDirectory is { Length: > 0 } directory)
            {
                var name = _options.WriteEveryFrame
                    ? $"frame-{index:D6}.png"
                    : "frame.png";

                File.WriteAllBytes(Path.Combine(directory, name), _codec.EncodePng(oriented));
            }
        }
        finally
        {
            if (rented)
            {
                oriented.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
    {
        Brightness = Math.Clamp(percent, 0, 100);
        _logger.LogDebug("Virtual panel brightness set to {Percent}%.", Brightness);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default)
    {
        ScreenOn = on;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        using var blank = FrameBuffer.RentCleared(FrameSize.Width, FrameSize.Height);
        return SendFrameAsync(blank, cancellationToken);
    }

    /// <summary>A copy of the most recent frame, or <see langword="null"/> if none has arrived.</summary>
    internal FrameBuffer? CaptureLastFrame()
    {
        lock (_gate)
        {
            if (_lastFrame is null)
            {
                return null;
            }

            var copy = FrameBuffer.Rent(_lastFrame.Width, _lastFrame.Height);
            _lastFrame.CopyTo(copy);
            return copy;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;

        lock (_gate)
        {
            _lastFrame?.Dispose();
            _lastFrame = null;
        }

        return ValueTask.CompletedTask;
    }
}
