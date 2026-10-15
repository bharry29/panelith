namespace OmniPanel.Sensors;

/// <summary>
/// The unit a <see cref="SensorReading"/> value is expressed in.
/// </summary>
/// <remarks>
/// Every source normalises to the canonical unit for its <see cref="SensorKind"/>
/// so that themes never have to know which backend produced a value. Themes may
/// still ask for a different unit at format time (for example bytes rendered as
/// gibibytes); that conversion lives in <see cref="UnitConversion"/>.
/// </remarks>
public enum SensorUnit
{
    /// <summary>Dimensionless.</summary>
    None = 0,

    /// <summary>Degrees Celsius.</summary>
    Celsius,

    /// <summary>Percent, 0-100.</summary>
    Percent,

    /// <summary>Megahertz.</summary>
    Megahertz,

    /// <summary>Watts.</summary>
    Watts,

    /// <summary>Volts.</summary>
    Volts,

    /// <summary>Amperes.</summary>
    Amperes,

    /// <summary>Revolutions per minute.</summary>
    Rpm,

    /// <summary>Bytes.</summary>
    Bytes,

    /// <summary>Bytes per second.</summary>
    BytesPerSecond,

    /// <summary>Seconds.</summary>
    Seconds,

    /// <summary>Frames per second.</summary>
    FramesPerSecond,
}

/// <summary>
/// Maps a <see cref="SensorKind"/> to the unit every source must normalise to.
/// </summary>
public static class SensorKindDefaults
{
    /// <summary>The canonical unit for <paramref name="kind"/>.</summary>
    public static SensorUnit UnitFor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => SensorUnit.Celsius,
        SensorKind.Load or SensorKind.Control => SensorUnit.Percent,
        SensorKind.Clock => SensorUnit.Megahertz,
        SensorKind.Power => SensorUnit.Watts,
        SensorKind.Voltage => SensorUnit.Volts,
        SensorKind.Current => SensorUnit.Amperes,
        SensorKind.Fan => SensorUnit.Rpm,
        SensorKind.Data => SensorUnit.Bytes,
        SensorKind.Throughput => SensorUnit.BytesPerSecond,
        SensorKind.Duration => SensorUnit.Seconds,
        SensorKind.FrameRate => SensorUnit.FramesPerSecond,
        _ => SensorUnit.None,
    };

    /// <summary>
    /// The range a gauge should span by default for <paramref name="kind"/>, or
    /// <see langword="null"/> when the kind has no natural bounds and the theme
    /// must supply them.
    /// </summary>
    public static (double Min, double Max)? DefaultRangeFor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => (0d, 100d),
        SensorKind.Load or SensorKind.Control => (0d, 100d),
        SensorKind.FrameRate => (0d, 240d),
        _ => null,
    };

    /// <summary>The number of fraction digits normally shown for <paramref name="kind"/>.</summary>
    public static int DefaultPrecisionFor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature or SensorKind.Load or SensorKind.Control => 0,
        SensorKind.Clock or SensorKind.Fan or SensorKind.Count => 0,
        SensorKind.Power or SensorKind.FrameRate => 1,
        SensorKind.Voltage or SensorKind.Current => 3,
        _ => 1,
    };
}
