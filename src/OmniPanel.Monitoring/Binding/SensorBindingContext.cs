using OmniPanel.Expressions;
using OmniPanel.Monitoring.Hub;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Binding;

/// <summary>
/// Adapts a <see cref="SensorHub"/> (or any snapshot + alias map) to
/// <see cref="IBindingContext"/> for the expression engine. Reads are cheap on
/// the render thread: the hub swaps an immutable snapshot rather than locking.
/// </summary>
public sealed class SensorBindingContext : IBindingContext
{
    private readonly Func<SensorSnapshot> _snapshot;
    private readonly Func<IReadOnlyDictionary<string, SensorId>> _aliases;
    private readonly Func<string, TimeSpan, IReadOnlyList<double>> _history;
    private readonly IReadOnlyDictionary<string, BindingValue> _variables;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a context bound to a live <see cref="SensorHub"/>.</summary>
    public SensorBindingContext(
        SensorHub hub,
        IReadOnlyDictionary<string, BindingValue>? variables = null,
        Func<DateTimeOffset>? clock = null)
        : this(
            () => hub.Current,
            () => hub.Aliases,
            hub.ResolveHistory,
            variables,
            clock)
    {
        ArgumentNullException.ThrowIfNull(hub);
    }

    /// <summary>
    /// Creates a context from explicit delegates — used by tests that do not
    /// want to spin up a full hub.
    /// </summary>
    public SensorBindingContext(
        Func<SensorSnapshot> snapshot,
        Func<IReadOnlyDictionary<string, SensorId>> aliases,
        Func<string, TimeSpan, IReadOnlyList<double>>? history = null,
        IReadOnlyDictionary<string, BindingValue>? variables = null,
        Func<DateTimeOffset>? clock = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        _history = history ?? ((_, _) => []);
        _variables = variables ?? new Dictionary<string, BindingValue>(StringComparer.OrdinalIgnoreCase);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public DateTimeOffset Now => _clock();

    /// <inheritdoc />
    public BindingValue ResolveSensor(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return BindingValue.Missing;
        }

        var snapshot = _snapshot();
        SensorReading? reading = null;

        if (WellKnownSensors.IsAlias(reference))
        {
            var aliases = _aliases();
            if (aliases.TryGetValue(reference, out var target) && snapshot.TryGet(target, out var aliased))
            {
                reading = aliased;
            }
        }
        else if (SensorId.TryParse(reference, out var id) && snapshot.TryGet(id, out var raw))
        {
            reading = raw;
        }

        if (reading is not { } found || !found.HasValue)
        {
            return BindingValue.Missing;
        }

        return found.Kind == SensorKind.Text
            ? BindingValue.FromText(found.Text)
            : BindingValue.FromNumber(found.Value!.Value);
    }

    /// <inheritdoc />
    public BindingValue ResolveVariable(string name) =>
        _variables.TryGetValue(name, out var value) ? value : BindingValue.Missing;

    /// <inheritdoc />
    public IReadOnlyList<double> ResolveHistory(string reference, TimeSpan window) =>
        _history(reference, window);
}
