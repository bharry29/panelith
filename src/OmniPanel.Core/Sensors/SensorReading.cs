namespace OmniPanel.Sensors;

/// <summary>
/// One sensor and its most recent value.
/// </summary>
/// <param name="Id">Stable identifier used by theme bindings.</param>
/// <param name="DisplayName">Short human-readable label, for pickers and tooltips.</param>
/// <param name="Kind">The quantity measured.</param>
/// <param name="Value">
/// The value in the canonical unit for <paramref name="Kind"/>, or
/// <see langword="null"/> when the sensor is present but currently unreadable.
/// </param>
/// <param name="Text">
/// The value of a <see cref="SensorKind.Text"/> sensor. Always
/// <see langword="null"/> for numeric kinds.
/// </param>
/// <param name="Minimum">Lower bound for gauges, when the source knows one.</param>
/// <param name="Maximum">Upper bound for gauges, when the source knows one.</param>
/// <param name="SourceId">Identifier of the <see cref="ISensorSource"/> that produced this reading.</param>
public readonly record struct SensorReading(
    SensorId Id,
    string DisplayName,
    SensorKind Kind,
    double? Value,
    string? Text = null,
    double? Minimum = null,
    double? Maximum = null,
    string? SourceId = null)
{
    /// <summary>The canonical unit of <see cref="Value"/>.</summary>
    public SensorUnit Unit => SensorKindDefaults.UnitFor(Kind);

    /// <summary>Whether this reading currently carries usable data.</summary>
    public bool HasValue => Kind == SensorKind.Text ? Text is not null : Value is { } v && !double.IsNaN(v);

    /// <summary>
    /// The effective gauge range: the source's bounds when it supplied them,
    /// otherwise the default range for the kind, otherwise <c>(0, 100)</c>.
    /// </summary>
    public (double Min, double Max) EffectiveRange
    {
        get
        {
            var fallback = SensorKindDefaults.DefaultRangeFor(Kind) ?? (0d, 100d);
            return (Minimum ?? fallback.Min, Maximum ?? fallback.Max);
        }
    }

    /// <summary>
    /// This reading's value as a fraction of <see cref="EffectiveRange"/>,
    /// clamped to <c>[0, 1]</c>. Returns <c>0</c> when there is no value or the
    /// range is degenerate.
    /// </summary>
    public double NormalizedValue
    {
        get
        {
            if (Value is not { } value || double.IsNaN(value))
            {
                return 0d;
            }

            var (min, max) = EffectiveRange;
            var span = max - min;
            return Math.Abs(span) < double.Epsilon
                ? 0d
                : Math.Clamp((value - min) / span, 0d, 1d);
        }
    }

    /// <summary>Creates a numeric reading.</summary>
    public static SensorReading Numeric(
        SensorId id,
        string displayName,
        SensorKind kind,
        double? value,
        double? minimum = null,
        double? maximum = null,
        string? sourceId = null) =>
        new(id, displayName, kind, value, Text: null, minimum, maximum, sourceId);

    /// <summary>Creates a textual reading.</summary>
    public static SensorReading Textual(
        SensorId id,
        string displayName,
        string? text,
        string? sourceId = null) =>
        new(id, displayName, SensorKind.Text, Value: null, text, Minimum: null, Maximum: null, sourceId);
}
