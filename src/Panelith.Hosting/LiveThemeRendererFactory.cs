using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Panelith.Engine.Rendering;
using Panelith.Expressions;
using Panelith.Imaging;
using Panelith.Rendering.Motion;

namespace Panelith.Hosting;

/// <summary>
/// Builds panel renderers whose theme can be changed while the panel keeps
/// running.
/// </summary>
/// <remarks>
/// <para>
/// The engine fixes a panel's theme when it opens the connection, so changing
/// the theme through configuration closes and reopens the USB device. That
/// takes a second or two and blanks the screen, which is fine for a config
/// edit but unusable for flicking through screens in a gallery or rotating
/// them on a timer. Renderers built here swap their theme between two frames
/// instead.
/// </para>
/// <para>
/// A theme that fails to load leaves the previous one on screen and is
/// reported through <see cref="ThemeFailed"/>; the panel never goes dark
/// because of a typo in a theme file.
/// </para>
/// </remarks>
public sealed class LiveThemeRendererFactory : IPanelRendererFactory
{
    private readonly ThemePanelRendererFactory _inner;
    private readonly ILogger _logger;
    private string? _activeTheme;
    private int _generation;
    private double _dim;
    private bool _motion = true;

    /// <summary>Creates the factory.</summary>
    public LiveThemeRendererFactory(ThemeLibrary library, ILogger<LiveThemeRendererFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _inner = new ThemePanelRendererFactory(library);
        _logger = logger as ILogger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The theme every panel shows, overriding the one in its configuration.
    /// Null means each panel uses its configured theme.
    /// </summary>
    public string? ActiveTheme => Volatile.Read(ref _activeTheme);

    /// <summary>The most recent frame drawn for any panel.</summary>
    public FrameMirror Mirror { get; } = new();

    /// <summary>Raised on the render thread when a theme could not be loaded.</summary>
    public event EventHandler<ThemeFailedEventArgs>? ThemeFailed;

    /// <summary>
    /// How much to darken every frame, from 0 (unchanged) to 1 (black). Panels
    /// such as the Trofeo LY have no brightness command, so this is the only
    /// way to make them quieter at night.
    /// </summary>
    public double Dim
    {
        get => Volatile.Read(ref _dim);
        set => Volatile.Write(ref _dim, Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// Whether values glide between readings, screens crossfade and dimming
    /// fades. When off, everything changes on the next frame.
    /// </summary>
    public bool Motion
    {
        get => Volatile.Read(ref _motion);
        set => Volatile.Write(ref _motion, value);
    }

    /// <summary>How long a crossfade between two screens lasts.</summary>
    public static TimeSpan CrossfadeDuration { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long dimming takes to go from fully bright to fully dark.</summary>
    public static TimeSpan DimFadeDuration { get; } = TimeSpan.FromMilliseconds(800);

    internal int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Mixes <paramref name="from"/> into <paramref name="into"/> in place:
    /// 0 leaves <paramref name="into"/> unchanged, 1 replaces it.
    /// </summary>
    public static void Blend(FrameBuffer into, FrameBuffer from, double amount)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(from);
        if (into.Width != from.Width || into.Height != from.Height)
        {
            throw new ArgumentException("Frames must be the same size to blend.", nameof(from));
        }

        if (amount <= 0)
        {
            return;
        }

        var target = into.Span;
        var source = from.ReadOnlySpan;
        if (amount >= 1)
        {
            source[..target.Length].CopyTo(target);
            return;
        }

        var weight = (int)Math.Round(amount * 256);
        for (var i = 0; i < target.Length; i++)
        {
            target[i] = (byte)(target[i] + (((source[i] - target[i]) * weight) >> 8));
        }
    }

    /// <summary>Moves a shown dim level toward its target, at most <paramref name="maximumStep"/> per call.</summary>
    public static double StepDim(double shown, double target, double maximumStep) =>
        shown < target ? Math.Min(target, shown + maximumStep) : Math.Max(target, shown - maximumStep);

    /// <summary>Darkens straight RGBA pixels in place, leaving alpha alone.</summary>
    public static void ApplyDim(FrameBuffer frame, double dim)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (dim <= 0)
        {
            return;
        }

        var span = frame.Span;
        if (dim >= 1)
        {
            for (var i = 0; i < span.Length; i += FrameBuffer.BytesPerPixel)
            {
                span[i] = 0;
                span[i + 1] = 0;
                span[i + 2] = 0;
            }

            return;
        }

        var keep = (int)Math.Round((1 - dim) * 256);
        for (var i = 0; i < span.Length; i += FrameBuffer.BytesPerPixel)
        {
            span[i] = (byte)((span[i] * keep) >> 8);
            span[i + 1] = (byte)((span[i + 1] * keep) >> 8);
            span[i + 2] = (byte)((span[i + 2] * keep) >> 8);
        }
    }

    /// <summary>Switches every running panel to a theme from its next frame.</summary>
    public void Show(string themeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);
        Volatile.Write(ref _activeTheme, themeId);
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Reloads the current theme from disk on every panel, after it was edited.</summary>
    public void Reload() => Interlocked.Increment(ref _generation);

    /// <inheritdoc />
    public IPanelRenderer Create(
        string themeId,
        int width,
        int height,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var generation = Generation;
        var theme = ActiveTheme ?? themeId;
        var inner = _inner.Create(theme, width, height, variables);
        return new LiveRenderer(this, themeId, inner, generation, width, height, variables);
    }

    private void ReportFailure(string themeId, Exception exception)
    {
        _logger.LogWarning("The theme \"{Theme}\" could not be shown: {Message}", themeId, exception.Message);
        ThemeFailed?.Invoke(this, new ThemeFailedEventArgs(themeId, exception));
    }

    private sealed class LiveRenderer : IPanelRenderer
    {
        private readonly LiveThemeRendererFactory _owner;
        private readonly string _configuredTheme;
        private readonly IReadOnlyDictionary<string, string>? _variables;
        private readonly MotionBindingContext _motion = new();
        private readonly TimeProvider _time = TimeProvider.System;
        private IPanelRenderer _current;
        private IPanelRenderer? _outgoing;
        private FrameBuffer? _scratch;
        private long _fadeStarted;
        private long _lastFrame;
        private double _shownDim = double.NaN;
        private int _generation;

        internal LiveRenderer(
            LiveThemeRendererFactory owner,
            string configuredTheme,
            IPanelRenderer current,
            int generation,
            int width,
            int height,
            IReadOnlyDictionary<string, string>? variables)
        {
            _owner = owner;
            _configuredTheme = configuredTheme;
            _current = current;
            _generation = generation;
            _variables = variables;
            Size = (width, height);
        }

        public (int Width, int Height) Size { get; }

        public void RenderInto(FrameBuffer target, IBindingContext context)
        {
            var generation = _owner.Generation;
            if (generation != _generation)
            {
                _generation = generation;
                SwapTo(_owner.ActiveTheme ?? _configuredTheme);
            }

            var motion = _owner.Motion;
            var now = _time.GetTimestamp();
            var frameSeconds = _lastFrame == 0 ? 0 : _time.GetElapsedTime(_lastFrame, now).TotalSeconds;
            _lastFrame = now;

            _motion.Enabled = motion;
            _motion.Reset(context);
            _current.RenderInto(target, _motion);
            Crossfade(target, now, motion);

            var dim = _owner.Dim;
            _shownDim = double.IsNaN(_shownDim) || !motion
                ? dim
                : StepDim(_shownDim, dim, frameSeconds / DimFadeDuration.TotalSeconds);
            ApplyDim(target, _shownDim);
            _owner.Mirror.Publish(target);
        }

        public void Dispose()
        {
            _current.Dispose();
            EndCrossfade();
        }

        private void Crossfade(FrameBuffer target, long now, bool motion)
        {
            if (_outgoing is null)
            {
                return;
            }

            var progress = _time.GetElapsedTime(_fadeStarted, now) / CrossfadeDuration;
            if (!motion || progress >= 1)
            {
                EndCrossfade();
                return;
            }

            if (_scratch is null || _scratch.Width != target.Width || _scratch.Height != target.Height)
            {
                _scratch?.Dispose();
                _scratch = FrameBuffer.Rent(target.Width, target.Height);
            }

            _outgoing.RenderInto(_scratch, _motion);
            Blend(target, _scratch, 1 - MotionBindingContext.EaseOut(progress));
        }

        private void EndCrossfade()
        {
            _outgoing?.Dispose();
            _outgoing = null;
            _scratch?.Dispose();
            _scratch = null;
        }

        private void SwapTo(string themeId)
        {
            IPanelRenderer next;
            try
            {
                next = _owner._inner.Create(themeId, Size.Width, Size.Height, _variables);
            }
            catch (PanelRenderException ex)
            {
                _owner.ReportFailure(themeId, ex);
                return;
            }

            var previous = _current;
            _current = next;
            if (_owner.Motion && _lastFrame != 0)
            {
                // A second swap mid-fade fades from the newest screen; the oldest one goes.
                _outgoing?.Dispose();
                _outgoing = previous;
                _fadeStarted = _time.GetTimestamp();
            }
            else
            {
                previous.Dispose();
            }
        }
    }
}

/// <summary>Describes a theme that could not be shown.</summary>
public sealed class ThemeFailedEventArgs(string themeId, Exception exception) : EventArgs
{
    /// <summary>The theme that was requested.</summary>
    public string ThemeId { get; } = themeId;

    /// <summary>Why it could not be loaded.</summary>
    public Exception Exception { get; } = exception;
}
