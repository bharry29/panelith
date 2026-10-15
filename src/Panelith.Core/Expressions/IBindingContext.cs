namespace Panelith.Expressions;

/// <summary>
/// Supplies the values an expression refers to.
/// </summary>
/// <remarks>
/// Deliberately small: the expression engine knows nothing about sensors,
/// themes or panels, which keeps it testable in isolation and lets the theme
/// editor evaluate expressions against made-up data.
/// </remarks>
public interface IBindingContext
{
    /// <summary>
    /// Resolves a sensor reference, which is either an alias such as
    /// <c>@cpu.temp</c> or a raw sensor id such as
    /// <c>cpu/0/temperature/package</c>.
    /// </summary>
    BindingValue ResolveSensor(string reference);

    /// <summary>Resolves a theme variable by name, without its <c>$</c>.</summary>
    BindingValue ResolveVariable(string name);

    /// <summary>
    /// Recent history of a sensor reference, newest last, used by
    /// <c>avg</c>, <c>min</c> and <c>max</c> over a time window and by graph
    /// widgets.
    /// </summary>
    /// <param name="reference">The sensor reference.</param>
    /// <param name="window">How far back to look.</param>
    IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) => [];

    /// <summary>
    /// Like <see cref="ResolveHistory"/>, with the time each sample was taken,
    /// so graphs can place points by time and scroll smoothly between samples.
    /// </summary>
    IReadOnlyList<HistorySample> ResolveHistorySamples(string reference, TimeSpan window) => [];

    /// <summary>The time the current frame represents.</summary>
    DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>One recorded value of a sensor.</summary>
/// <param name="Timestamp">When it was read.</param>
/// <param name="Value">The value.</param>
public readonly record struct HistorySample(DateTimeOffset Timestamp, double Value);

/// <summary>
/// A binding context backed by periodic sensor readings, which can say when the
/// latest reading was taken. Motion smoothing uses this to know when a new
/// target arrives and how often readings come.
/// </summary>
public interface ISampledBindingContext
{
    /// <summary>When the current readings were taken.</summary>
    DateTimeOffset SampleTime { get; }
}

/// <summary>A binding context that smooths values, and can hand out the unsmoothed ones.</summary>
public interface ISmoothedBindingContext
{
    /// <summary>The same values without smoothing.</summary>
    IBindingContext Raw { get; }
}

/// <summary>A context that resolves nothing, for parsing and validation.</summary>
public sealed class EmptyBindingContext : IBindingContext
{
    /// <summary>The shared instance.</summary>
    public static EmptyBindingContext Instance { get; } = new();

    private EmptyBindingContext()
    {
    }

    /// <inheritdoc />
    public BindingValue ResolveSensor(string reference) => BindingValue.Missing;

    /// <inheritdoc />
    public BindingValue ResolveVariable(string name) => BindingValue.Missing;
}
