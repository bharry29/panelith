using RigGlass.Expressions;
using RigGlass.Sensors;
using RigGlass.Themes;

namespace RigGlass.Rendering;

/// <summary>
/// Reusable wrapper so theme <c>$variables</c> resolve against the document that
/// owns them. The outer context is swapped each frame without allocating.
/// </summary>
internal sealed class ThemeBindingContext : IBindingContext, ISensorKindProvider
{
    private readonly Theme _theme;
    private IBindingContext _inner = EmptyBindingContext.Instance;

    public ThemeBindingContext(Theme theme)
    {
        _theme = theme;
    }

    public void Reset(IBindingContext inner) => _inner = inner;

    public DateTimeOffset Now => _inner.Now;

    public BindingValue ResolveSensor(string reference) => _inner.ResolveSensor(reference);

    public BindingValue ResolveVariable(string name)
    {
        if (_theme.Variables is not null
            && _theme.Variables.TryGetValue(name, out var expression)
            && BindingExpression.TryParse(expression, out var parsed, out _))
        {
            return parsed!.Evaluate(this);
        }

        return _inner.ResolveVariable(name);
    }

    public IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) =>
        _inner.ResolveHistory(reference, window);

    public SensorKind KindOf(string reference) =>
        _inner is ISensorKindProvider provider
            ? provider.KindOf(reference)
            : WellKnownSensors.All.TryGetValue(reference, out var kind) ? kind : SensorKind.Unknown;
}
