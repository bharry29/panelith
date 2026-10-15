using RigGlass.Devices;
using RigGlass.Engine.Rendering;
using RigGlass.Expressions;
using RigGlass.Imaging;

namespace RigGlass.Engine.Tests.Fakes;

/// <summary>A renderer that fills the frame with a chosen byte.</summary>
internal sealed class SolidColourRenderer : IPanelRenderer
{
    private byte _value;

    internal SolidColourRenderer(int width, int height, byte value = 0x40)
    {
        Size = (width, height);
        _value = value;
    }

    public (int Width, int Height) Size { get; }

    /// <summary>How many frames have been drawn.</summary>
    internal int RenderCount { get; private set; }

    /// <summary>Whether the content changes on every frame.</summary>
    internal bool Animate { get; set; }

    public void RenderInto(FrameBuffer target, IBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(target);

        RenderCount++;
        target.Span.Fill(_value);

        if (Animate)
        {
            _value = unchecked((byte)(_value + 1));
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Hands out <see cref="SolidColourRenderer"/> instances.</summary>
internal sealed class FakeRendererFactory : IPanelRendererFactory
{
    /// <summary>Every renderer created, in order.</summary>
    internal List<SolidColourRenderer> Created { get; } = [];

    /// <summary>Theme ids requested, in order.</summary>
    internal List<string> RequestedThemes { get; } = [];

    /// <summary>When set, creating a renderer fails with this message.</summary>
    internal string? FailWith { get; set; }

    /// <summary>Whether created renderers animate.</summary>
    internal bool Animate { get; set; } = true;

    public IPanelRenderer Create(
        string themeId,
        int width,
        int height,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        RequestedThemes.Add(themeId);

        if (FailWith is { } message)
        {
            throw new PanelRenderException(message);
        }

        var renderer = new SolidColourRenderer(width, height) { Animate = Animate };
        Created.Add(renderer);
        return renderer;
    }
}

/// <summary>A panel connection that counts frames and can be told to fail.</summary>
internal sealed class FakePanelConnection : IPanelConnection
{
    private int _frames;

    internal FakePanelConnection(PanelDescriptor descriptor, PanelLocation location, PanelOpenOptions options)
    {
        Descriptor = descriptor;
        Location = location;
        Options = options;
        FrameSize = descriptor.RenderSize(options.Orientation);
    }

    public PanelDescriptor Descriptor { get; }

    public PanelLocation Location { get; }

    public bool IsConnected { get; private set; } = true;

    public (int Width, int Height) FrameSize { get; }

    /// <summary>The options this connection was opened with.</summary>
    internal PanelOpenOptions Options { get; }

    /// <summary>How many frames were accepted.</summary>
    internal int Frames => Volatile.Read(ref _frames);

    /// <summary>When set, the nth frame fails with this exception.</summary>
    internal (int Frame, PanelConnectionException Error)? FailOnFrame { get; set; }

    public ValueTask SendFrameAsync(FrameBuffer frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        var index = Interlocked.Increment(ref _frames);

        if (FailOnFrame is { } failure && index >= failure.Frame)
        {
            IsConnected = false;
            throw failure.Error;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SetBrightnessAsync(int percent, CancellationToken cancellationToken = default)
    {
        Brightness = percent;
        return ValueTask.CompletedTask;
    }

    /// <summary>The brightness last requested.</summary>
    internal int? Brightness { get; private set; }

    public ValueTask SetScreenPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens <see cref="FakePanelConnection"/> instances on a script.</summary>
internal sealed class ScriptedConnectionFactory : IPanelConnectionFactory
{
    private readonly Queue<PanelConnectionException> _failures = new();

    /// <summary>Every connection opened, in order.</summary>
    internal List<FakePanelConnection> Opened { get; } = [];

    /// <summary>How many times an open was attempted.</summary>
    internal int Attempts { get; private set; }

    /// <summary>Configures the nth connection once it is opened.</summary>
    internal Action<FakePanelConnection>? Configure { get; set; }

    /// <summary>Makes the next open attempt fail.</summary>
    internal ScriptedConnectionFactory FailingWith(params PanelConnectionException[] failures)
    {
        foreach (var failure in failures)
        {
            _failures.Enqueue(failure);
        }

        return this;
    }

    public ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        Attempts++;

        if (_failures.Count > 0)
        {
            throw _failures.Dequeue();
        }

        var connection = new FakePanelConnection(panel.Descriptor, panel.Location, options);
        Configure?.Invoke(connection);
        Opened.Add(connection);
        return ValueTask.FromResult<IPanelConnection>(connection);
    }
}

/// <summary>A binding context that resolves nothing, which themes must tolerate.</summary>
internal sealed class SilentBindingContext : IBindingContext
{
    public BindingValue ResolveSensor(string reference) => BindingValue.Missing;

    public BindingValue ResolveVariable(string name) => BindingValue.Missing;
}

/// <summary>Builds a detected panel for tests.</summary>
internal static class TestPanel
{
    internal static DetectedPanel Create(
        string profileId = "test.panel",
        int width = 320,
        int height = 240,
        double maxFps = 10,
        string serial = "TEST-1") =>
        new(
            new PanelDescriptor(
                profileId,
                "Test",
                "Panel",
                width,
                height,
                PanelTransportKind.UsbBulk,
                PanelPixelFormat.Rgb565BigEndian,
                PanelCapabilities.Minimal with { MaxFramesPerSecond = maxFps, SupportsBrightness = true }),
            PanelLocation.Usb(PanelTransportKind.UsbBulk, 0x1234, 0x5678, 1, 2, serialNumber: serial),
            "test.driver",
            PanelMatchConfidence.Confirmed);
}
