namespace Panelith.Engine.Tests;

public sealed class FrameClockTests
{
    [Fact]
    public void StartsAtTheRequestedRate()
    {
        var clock = new FrameClock(targetFramesPerSecond: 20);

        Assert.Equal(20, clock.TargetFramesPerSecond);
        Assert.Equal(20, clock.CurrentFramesPerSecond, precision: 3);
    }

    [Fact]
    public void BacksOffWhenCyclesTakeLongerThanTheBudget()
    {
        // Asking a serial panel for 30 fps does not make it faster; it makes the
        // writes queue and the process busy. The clock has to notice.
        var clock = new FrameClock(targetFramesPerSecond: 30, minimumFramesPerSecond: 0.5);

        for (var i = 0; i < 40; i++)
        {
            clock.RecordFrame(cycleMilliseconds: 500);
        }

        Assert.True(
            clock.CurrentFramesPerSecond < 5,
            $"Expected the clock to back off well below 5 fps but it settled at {clock.CurrentFramesPerSecond:0.##}.");
    }

    [Fact]
    public void NeverBacksOffBelowTheFloor()
    {
        // A panel must never look frozen, however slow the link.
        var clock = new FrameClock(targetFramesPerSecond: 30, minimumFramesPerSecond: 2);

        for (var i = 0; i < 200; i++)
        {
            clock.RecordFrame(cycleMilliseconds: 60_000);
        }

        Assert.True(clock.CurrentFramesPerSecond >= 2 - 1e-9);
    }

    [Fact]
    public void RecoversTowardsTheTargetWhenCyclesGetCheapAgain()
    {
        var clock = new FrameClock(targetFramesPerSecond: 30, minimumFramesPerSecond: 0.5);

        for (var i = 0; i < 20; i++)
        {
            clock.RecordFrame(cycleMilliseconds: 400);
        }

        var backedOff = clock.CurrentFramesPerSecond;

        for (var i = 0; i < 400; i++)
        {
            clock.RecordFrame(cycleMilliseconds: 1);
        }

        Assert.True(clock.CurrentFramesPerSecond > backedOff);
        Assert.Equal(30, clock.CurrentFramesPerSecond, precision: 1);
    }

    [Fact]
    public void NeverSchedulesAFrameInThePastAfterALongStall()
    {
        // After a suspend the deadline can be minutes behind. Catching up by
        // firing a burst would only deepen the backlog.
        var clock = new FrameClock(targetFramesPerSecond: 10);

        clock.RecordFrame(cycleMilliseconds: 120_000);

        Assert.True(clock.TimeUntilNextFrame >= TimeSpan.Zero);
    }

    [Fact]
    public void ResetReturnsToTheRequestedRate()
    {
        var clock = new FrameClock(targetFramesPerSecond: 25, minimumFramesPerSecond: 1);

        for (var i = 0; i < 20; i++)
        {
            clock.RecordFrame(cycleMilliseconds: 900);
        }

        clock.Reset();

        Assert.Equal(25, clock.CurrentFramesPerSecond, precision: 3);
        Assert.Equal(0, clock.MeasuredFramesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsANonPositiveTarget(double target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameClock(target));

    [Fact]
    public void RejectsAFloorAboveTheTarget() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameClock(10, minimumFramesPerSecond: 20));

    [Fact]
    public async Task WaitingReturnsPromptlyWhenAFrameIsAlreadyDue()
    {
        var clock = new FrameClock(targetFramesPerSecond: 1000);

        await clock.WaitAsync(TestContext.Current.CancellationToken);
        await clock.WaitAsync(TestContext.Current.CancellationToken);
    }
}
