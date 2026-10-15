namespace OmniPanel.Expressions;

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

    /// <summary>The time the current frame represents.</summary>
    DateTimeOffset Now => DateTimeOffset.Now;
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
