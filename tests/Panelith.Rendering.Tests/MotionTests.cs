using Panelith.Expressions;
using Panelith.Rendering.Motion;
using Panelith.Sensors;

namespace Panelith.Rendering;

public sealed class MotionTests
{
    private const string Sensor = "@cpu.temp";

    [Fact]
    public void EaseOutRunsFromZeroToOneAndOnlyRises()
    {
        Assert.Equal(0d, MotionBindingContext.EaseOut(0d));
        Assert.Equal(1d, MotionBindingContext.EaseOut(1d));
        Assert.Equal(1d, MotionBindingContext.EaseOut(5d));

        var previous = 0d;
        for (var t = 0.05; t <= 1d; t += 0.05)
        {
            var eased = MotionBindingContext.EaseOut(t);
            Assert.True(eased > previous);
            Assert.True(eased >= t, "ease-out should lead a straight line");
            previous = eased;
        }
    }

    [Fact]
    public void NoisyKindsAreFilteredHarder()
    {
        Assert.True(MotionBindingContext.FilterStrength(SensorKind.Load) < MotionBindingContext.FilterStrength(SensorKind.Temperature));
        Assert.Equal(1d, MotionBindingContext.FilterStrength(SensorKind.Unknown));
    }

    [Fact]
    public void ANewReadingGlidesInsteadOfJumping()
    {
        var (time, inner, motion) = Create(SensorKind.Unknown, 40);
        Assert.Equal(40d, Read(motion, inner));

        inner.WithNumber(Sensor, 80, SensorKind.Unknown);
        Assert.Equal(40d, Read(motion, inner), 3);

        // Halfway through the move, ease-out is well past halfway.
        time.Advance(TimeSpan.FromSeconds(0.85 / 2));
        var middle = Read(motion, inner);
        Assert.InRange(middle, 60d, 80d);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(80d, Read(motion, inner), 6);
    }

    [Fact]
    public void AReadingMidMoveStartsFromWhatIsOnScreen()
    {
        var (time, inner, motion) = Create(SensorKind.Unknown, 0);
        Read(motion, inner);
        inner.WithNumber(Sensor, 100, SensorKind.Unknown);
        Read(motion, inner);
        time.Advance(TimeSpan.FromSeconds(0.2));
        var shown = Read(motion, inner);

        inner.WithNumber(Sensor, 0, SensorKind.Unknown);
        Assert.Equal(shown, Read(motion, inner), 6);
    }

    [Fact]
    public void LoadIsFilteredTowardTheNewValue()
    {
        var (time, inner, motion) = Create(SensorKind.Load, 0);
        Read(motion, inner);
        inner.WithNumber(Sensor, 100, SensorKind.Load);
        Read(motion, inner);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(100d * MotionBindingContext.FilterStrength(SensorKind.Load), Read(motion, inner), 6);
    }

    [Fact]
    public void DisabledAndTextValuesPassStraightThrough()
    {
        var (_, inner, motion) = Create(SensorKind.Unknown, 10);
        Read(motion, inner);
        motion.Enabled = false;
        inner.WithNumber(Sensor, 90, SensorKind.Unknown);
        Assert.Equal(90d, Read(motion, inner));

        motion.Enabled = true;
        inner.WithText(Sensor, "hello");
        motion.Reset(inner);
        Assert.Equal("hello", motion.ResolveSensor(Sensor).Text);
    }

    [Fact]
    public void ThemesAndWidgetsCanOptOut()
    {
        var (_, inner, motion) = Create(SensorKind.Unknown, 10);
        Read(motion, inner);
        inner.WithNumber(Sensor, 90, SensorKind.Unknown);
        motion.Reset(inner);

        var bindings = new ThemeBindingContext(new Themes.Theme());
        bindings.Reset(motion);
        Assert.Equal(10d, bindings.ResolveSensor(Sensor).Number, 3);
        bindings.UseRaw = true;
        Assert.Equal(90d, bindings.ResolveSensor(Sensor).Number);
    }

    private static (ManualTime Time, TestBindingContext Inner, MotionBindingContext Motion) Create(SensorKind kind, double value)
    {
        var time = new ManualTime();
        var inner = new TestBindingContext().WithNumber(Sensor, value, kind);
        var motion = new MotionBindingContext(time);
        return (time, inner, motion);
    }

    private static double Read(MotionBindingContext motion, IBindingContext inner)
    {
        motion.Reset(inner);
        return motion.ResolveSensor(Sensor).Number;
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
