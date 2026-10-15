using RigGlass.Expressions;
using RigGlass.Sensors;

namespace RigGlass.Tests.Expressions;

/// <summary>
/// An in-memory binding context, so expression behaviour can be asserted
/// without a sensor backend.
/// </summary>
internal sealed class TestBindingContext : IBindingContext, ISensorKindProvider
{
    private readonly Dictionary<string, BindingValue> _sensors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SensorKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double[]> _history = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public TestBindingContext WithNumber(string reference, double value, SensorKind kind = SensorKind.Unknown)
    {
        _sensors[reference] = BindingValue.FromNumber(value);
        _kinds[reference] = kind;
        return this;
    }

    public TestBindingContext WithText(string reference, string value)
    {
        _sensors[reference] = BindingValue.FromText(value);
        _kinds[reference] = SensorKind.Text;
        return this;
    }

    public TestBindingContext WithVariable(string name, string expression)
    {
        _variables[name] = expression;
        return this;
    }

    public TestBindingContext WithHistory(string reference, params double[] values)
    {
        _history[reference] = values;
        return this;
    }

    public BindingValue ResolveSensor(string reference) =>
        _sensors.TryGetValue(reference, out var value) ? value : BindingValue.Missing;

    public BindingValue ResolveVariable(string name) =>
        _variables.TryGetValue(name, out var expression)
            ? BindingExpression.Parse(expression).Evaluate(this)
            : BindingValue.Missing;

    public IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) =>
        _history.TryGetValue(reference, out var values) ? values : [];

    public SensorKind KindOf(string reference) =>
        _kinds.TryGetValue(reference, out var kind) ? kind : SensorKind.Unknown;
}
