using RigGlass.Devices;
using RigGlass.Engine.Configuration;
using RigGlass.Engine.Tests.Fakes;

namespace RigGlass.Engine.Tests;

/// <summary>
/// The supervisor is the part of RigGlass that has to be right about failure.
/// These tests pin the behaviour users notice: a panel that comes back after
/// suspend, a panel another application is holding, and a panel the system will
/// not let us open at all.
/// </summary>
public sealed class PanelSupervisorTests
{
    [Fact]
    public async Task DrivesAPanelAndReportsItHealthy()
    {
        var connections = new ScriptedConnectionFactory();
        await using var supervisor = Create(connections, out _);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.FramesSent >= 3);

        Assert.Equal(PanelRuntimeState.Running, supervisor.Status.State);
        Assert.True(supervisor.Status.IsHealthy);
        Assert.Equal((320, 240), supervisor.Status.FrameSize);
        Assert.Null(supervisor.Status.LastError);
    }

    [Fact]
    public async Task AppliesTheConfiguredOrientationBrightnessAndQuality()
    {
        var connections = new ScriptedConnectionFactory();
        await using var supervisor = Create(
            connections,
            out _,
            new PanelAssignment
            {
                Key = "k",
                Theme = "starter",
                Orientation = PanelOrientation.Landscape,
                Brightness = 42,
                JpegQuality = 70,
                FlipHorizontally = true,
            });

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.State == PanelRuntimeState.Running);

        var connection = Assert.Single(connections.Opened);
        Assert.Equal(PanelOrientation.Landscape, connection.Options.Orientation);
        Assert.Equal(42, connection.Options.Brightness);
        Assert.Equal(70, connection.Options.JpegQuality);
        Assert.True(connection.Options.FlipHorizontally);

        // A quarter turn swaps the frame the theme is rendered at.
        Assert.Equal((240, 320), supervisor.Status.FrameSize);
    }

    [Fact]
    public async Task SkipsFramesThatWouldNotChangeAnythingOnScreen()
    {
        // A static theme is the common case - a clock and a couple of readouts
        // that only move once a second - and pushing an identical frame costs
        // the full transfer for no visible benefit.
        var connections = new ScriptedConnectionFactory();
        var renderers = new FakeRendererFactory { Animate = false };
        await using var supervisor = Create(connections, renderers);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.FramesSkipped >= 3);

        Assert.Equal(1, supervisor.Status.FramesSent);
        Assert.True(supervisor.Status.FramesSkipped >= 3);
    }

    [Fact]
    public async Task ReconnectsAfterThePanelDisappearsAndComesBack()
    {
        // This is the suspend-and-resume path: the panel vanishes mid-transfer,
        // and the user's expectation is that it lights up again on its own.
        var connections = new ScriptedConnectionFactory();
        connections.Configure = connection =>
        {
            if (connections.Opened.Count == 0)
            {
                connection.FailOnFrame = (3, new PanelConnectionException("gone", PanelFailureReason.Disconnected));
            }
        };

        await using var supervisor = Create(connections, out _);

        supervisor.Start();
        await WaitForAsync(supervisor, _ => connections.Attempts >= 2, TimeSpan.FromSeconds(20));

        Assert.True(connections.Attempts >= 2);
    }

    [Fact]
    public async Task TellsTheUserToCloseTheVendorToolWhenThePanelIsBusy()
    {
        var connections = new ScriptedConnectionFactory()
            .FailingWith(new PanelConnectionException("in use", PanelFailureReason.Busy));

        await using var supervisor = Create(connections, out _);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.State == PanelRuntimeState.Retrying);

        Assert.Equal(PanelRuntimeState.Retrying, supervisor.Status.State);
        Assert.Contains("vendor software", supervisor.Status.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopsRetryingWhenThePanelCanNeverBeOpened()
    {
        // Access denied needs a one-time permissions fix. Retrying forever would
        // bury the one message that tells the user how to make it work.
        var connections = new ScriptedConnectionFactory()
            .FailingWith(new PanelConnectionException("no permission", PanelFailureReason.AccessDenied));

        await using var supervisor = Create(connections, out _);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.State == PanelRuntimeState.Failed);

        Assert.Equal(PanelRuntimeState.Failed, supervisor.Status.State);
        Assert.NotNull(supervisor.Status.Remedy);
        Assert.Equal(1, connections.Attempts);
    }

    [Fact]
    public async Task StopsAndExplainsWhenTheThemeCannotBePrepared()
    {
        var connections = new ScriptedConnectionFactory();
        var renderers = new FakeRendererFactory { FailWith = "widget \"cpu\" has no binding" };
        await using var supervisor = Create(connections, renderers);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.State == PanelRuntimeState.Failed);

        Assert.Contains("no binding", supervisor.Status.LastError, StringComparison.Ordinal);
        Assert.Contains("theme validate", supervisor.Status.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNothingWhenThePanelIsDisabled()
    {
        var connections = new ScriptedConnectionFactory();
        await using var supervisor = Create(
            connections,
            out _,
            new PanelAssignment { Key = "k", Theme = "starter", Enabled = false });

        supervisor.Start();

        Assert.Equal(PanelRuntimeState.Disabled, supervisor.Status.State);
        Assert.Equal(0, connections.Attempts);
    }

    [Fact]
    public async Task StopsCleanlyAndReleasesTheConnection()
    {
        var connections = new ScriptedConnectionFactory();
        var supervisor = Create(connections, out _);

        supervisor.Start();
        await WaitForAsync(supervisor, s => s.State == PanelRuntimeState.Running);

        await supervisor.StopAsync();

        Assert.Equal(PanelRuntimeState.Stopped, supervisor.Status.State);
        Assert.False(Assert.Single(connections.Opened).IsConnected);
    }

    private static PanelSupervisor Create(
        ScriptedConnectionFactory connections,
        out FakeRendererFactory renderers,
        PanelAssignment? assignment = null)
    {
        renderers = new FakeRendererFactory();
        return Create(connections, renderers, assignment);
    }

    private static PanelSupervisor Create(
        ScriptedConnectionFactory connections,
        FakeRendererFactory renderers,
        PanelAssignment? assignment = null) =>
        new(
            TestPanel.Create(),
            assignment ?? new PanelAssignment { Key = "k", Theme = "starter", FramesPerSecond = 60 },
            connections,
            renderers,
            new SilentBindingContext());

    /// <summary>
    /// Polls a condition with a deadline.
    /// </summary>
    /// <remarks>
    /// The supervisor runs on its own task by design, so the alternative would
    /// be to expose a test-only synchronisation hook, which would let the
    /// production path and the tested path diverge.
    /// </remarks>
    private static async Task WaitForAsync(
        PanelSupervisor supervisor,
        Func<PanelRuntimeStatus, bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition(supervisor.Status))
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The condition was never met. Final status: {supervisor.Status.Describe()}");
    }
}
