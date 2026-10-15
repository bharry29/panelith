using System.Globalization;

namespace OmniPanel.Expressions;

/// <summary>What an expression evaluated to.</summary>
public enum BindingValueKind
{
    /// <summary>
    /// The value could not be produced: the sensor does not exist on this
    /// machine, or exists but is currently unreadable.
    /// </summary>
    /// <remarks>
    /// Missing is a first-class result rather than an error. A theme written for
    /// a desktop with a discrete GPU must still render on a laptop without one,
    /// so a missing sensor renders as a placeholder and the rest of the frame
    /// draws normally.
    /// </remarks>
    Missing = 0,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A string.</summary>
    Text,

    /// <summary>A boolean.</summary>
    Boolean,
}

/// <summary>
/// The result of evaluating a theme binding expression.
/// </summary>
public readonly record struct BindingValue
{
    private BindingValue(BindingValueKind kind, double number, string? text, bool boolean)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Boolean = boolean;
    }

    /// <summary>A value that is not available.</summary>
    public static BindingValue Missing { get; } = new(BindingValueKind.Missing, double.NaN, null, false);

    /// <summary>Which kind of value this is.</summary>
    public BindingValueKind Kind { get; }

    /// <summary>The numeric value, when <see cref="Kind"/> is <see cref="BindingValueKind.Number"/>.</summary>
    public double Number { get; }

    /// <summary>The string value, when <see cref="Kind"/> is <see cref="BindingValueKind.Text"/>.</summary>
    public string? Text { get; }

    /// <summary>The boolean value, when <see cref="Kind"/> is <see cref="BindingValueKind.Boolean"/>.</summary>
    public bool Boolean { get; }

    /// <summary>Whether a value is present.</summary>
    public bool HasValue => Kind != BindingValueKind.Missing;

    /// <summary>Wraps a number.</summary>
    public static BindingValue FromNumber(double value) =>
        double.IsNaN(value) ? Missing : new BindingValue(BindingValueKind.Number, value, null, false);

    /// <summary>Wraps a string.</summary>
    public static BindingValue FromText(string? value) =>
        value is null ? Missing : new BindingValue(BindingValueKind.Text, double.NaN, value, false);

    /// <summary>Wraps a boolean.</summary>
    public static BindingValue FromBoolean(bool value) =>
        new(BindingValueKind.Boolean, value ? 1d : 0d, null, value);

    /// <summary>
    /// This value as a number, or <see langword="null"/> when it cannot be read
    /// as one. Booleans become 1 and 0; numeric strings are parsed.
    /// </summary>
    public double? AsNumber() => Kind switch
    {
        BindingValueKind.Number => Number,
        BindingValueKind.Boolean => Boolean ? 1d : 0d,
        BindingValueKind.Text when double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// This value's truthiness. Numbers are true when non-zero, strings when
    /// non-empty, and a missing value is always false.
    /// </summary>
    public bool AsBoolean() => Kind switch
    {
        BindingValueKind.Boolean => Boolean,
        BindingValueKind.Number => Number != 0d,
        BindingValueKind.Text => !string.IsNullOrEmpty(Text),
        _ => false,
    };

    /// <summary>This value rendered with invariant culture, or an empty string when missing.</summary>
    public override string ToString() => Kind switch
    {
        BindingValueKind.Number => Number.ToString("0.###", CultureInfo.InvariantCulture),
        BindingValueKind.Text => Text ?? string.Empty,
        BindingValueKind.Boolean => Boolean ? "true" : "false",
        _ => string.Empty,
    };
}
