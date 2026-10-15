using System.Globalization;
using Panelith.Sensors;

namespace Panelith.Expressions;

/// <summary>
/// Renders a <see cref="BindingValue"/> as display text.
/// </summary>
/// <remarks>
/// Format specifiers come in two flavours. A named specifier such as
/// <c>bytes</c> applies a unit-aware transformation that a bare number format
/// cannot express; anything else is handed to .NET numeric formatting, so
/// <c>0.0</c> and <c>#,##0</c> work as authors expect.
/// </remarks>
public static class ValueFormatter
{
    /// <summary>Shown in place of a value the machine cannot supply.</summary>
    public const string MissingPlaceholder = "--";

    /// <summary>The named format specifiers, for editor completion and validation.</summary>
    public static IReadOnlySet<string> NamedFormats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bytes", "bytes0", "bytes1", "bytes2",
        "rate", "rate0", "rate1",
        "clock", "clock0", "clock1", "clock2",
        "duration", "percent", "pct", "int", "auto",
        "hex", "bool", "onoff", "yesno",
    };

    /// <summary>
    /// Formats a value.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <param name="format">
    /// A named specifier, a .NET numeric format string, or
    /// <see langword="null"/> for a sensible default.
    /// </param>
    /// <param name="kind">
    /// The sensor kind the value came from, used to pick the default precision
    /// and to interpret <c>auto</c>.
    /// </param>
    public static string Format(BindingValue value, string? format = null, SensorKind kind = SensorKind.Unknown)
    {
        if (!value.HasValue)
        {
            return MissingPlaceholder;
        }

        if (value.Kind == BindingValueKind.Text)
        {
            return value.Text ?? string.Empty;
        }

        if (value.Kind == BindingValueKind.Boolean && string.IsNullOrEmpty(format))
        {
            return value.Boolean ? "true" : "false";
        }

        if (value.AsNumber() is not { } number)
        {
            return MissingPlaceholder;
        }

        if (string.IsNullOrEmpty(format))
        {
            return FormatDefault(number, kind);
        }

        return FormatNamed(number, format, kind) ?? FormatNumeric(number, format);
    }

    /// <summary>Formats a sensor reading using its own kind for defaults.</summary>
    public static string Format(SensorReading reading, string? format = null) =>
        reading.Kind == SensorKind.Text
            ? reading.Text ?? MissingPlaceholder
            : Format(BindingValue.FromNumber(reading.Value ?? double.NaN), format, reading.Kind);

    /// <summary>
    /// Formats a value together with the symbol of its unit, auto-scaling where
    /// the unit supports it.
    /// </summary>
    public static string FormatWithUnit(double number, SensorKind kind)
    {
        switch (kind)
        {
            case SensorKind.Data:
            {
                var (scaled, suffix) = UnitConversion.ScaleBytes(number);
                return $"{scaled.ToString(PrecisionFormat(ScaledPrecision(scaled)), CultureInfo.InvariantCulture)} {suffix}";
            }

            case SensorKind.Throughput:
            {
                var (scaled, suffix) = UnitConversion.ScaleRate(number);
                return $"{scaled.ToString(PrecisionFormat(ScaledPrecision(scaled)), CultureInfo.InvariantCulture)} {suffix}";
            }

            case SensorKind.Clock:
            {
                var (scaled, suffix) = UnitConversion.ScaleClock(number);
                return $"{scaled.ToString(PrecisionFormat(suffix == "GHz" ? 2 : 0), CultureInfo.InvariantCulture)} {suffix}";
            }

            case SensorKind.Duration:
                return UnitConversion.FormatDuration(number);

            default:
            {
                var text = FormatDefault(number, kind);
                var symbol = UnitConversion.SymbolFor(SensorKindDefaults.UnitFor(kind));
                if (symbol.Length == 0)
                {
                    return text;
                }

                // Degree and percent signs sit flush against the number by
                // typographic convention; word-like units take a space.
                return symbol is "\u00b0C" or "%" ? text + symbol : text + " " + symbol;
            }
        }
    }

    private static string FormatDefault(double number, SensorKind kind) => kind switch
    {
        SensorKind.Data or SensorKind.Throughput or SensorKind.Clock or SensorKind.Duration =>
            FormatWithUnit(number, kind),
        _ => number.ToString(PrecisionFormat(SensorKindDefaults.DefaultPrecisionFor(kind)), CultureInfo.InvariantCulture),
    };

    private static string? FormatNamed(double number, string format, SensorKind kind)
    {
        switch (format.ToLowerInvariant())
        {
            case "auto":
                return FormatWithUnit(number, kind);

            case "bytes":
            case "bytes0":
            case "bytes1":
            case "bytes2":
            {
                var (scaled, suffix) = UnitConversion.ScaleBytes(number);
                var digits = DigitsFromSuffix(format, "bytes", ScaledPrecision(scaled));
                return $"{scaled.ToString(PrecisionFormat(digits), CultureInfo.InvariantCulture)} {suffix}";
            }

            case "rate":
            case "rate0":
            case "rate1":
            {
                var (scaled, suffix) = UnitConversion.ScaleRate(number);
                var digits = DigitsFromSuffix(format, "rate", ScaledPrecision(scaled));
                return $"{scaled.ToString(PrecisionFormat(digits), CultureInfo.InvariantCulture)} {suffix}";
            }

            case "clock":
            case "clock0":
            case "clock1":
            case "clock2":
            {
                var (scaled, suffix) = UnitConversion.ScaleClock(number);
                var digits = DigitsFromSuffix(format, "clock", suffix == "GHz" ? 2 : 0);
                return $"{scaled.ToString(PrecisionFormat(digits), CultureInfo.InvariantCulture)} {suffix}";
            }

            case "duration":
                return UnitConversion.FormatDuration(number);

            case "percent":
            case "pct":
                return number.ToString("0", CultureInfo.InvariantCulture) + "%";

            case "int":
                return Math.Round(number, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

            case "hex":
                return "0x" + ((long)number).ToString("X", CultureInfo.InvariantCulture);

            case "bool":
                return number != 0d ? "true" : "false";

            case "onoff":
                return number != 0d ? "ON" : "OFF";

            case "yesno":
                return number != 0d ? "Yes" : "No";

            default:
                return null;
        }
    }

    private static string FormatNumeric(double number, string format)
    {
        try
        {
            return number.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // A bad format string in a theme should degrade to a readable number
            // rather than abort the frame.
            return number.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Keeps scaled values readable: show more decimals for small numbers, none
    /// for large ones, so that "1.5 GiB" and "512 MiB" both look right.
    /// </summary>
    private static int ScaledPrecision(double scaled) => Math.Abs(scaled) switch
    {
        >= 100d => 0,
        >= 10d => 1,
        _ => 2,
    };

    private static int DigitsFromSuffix(string format, string prefix, int fallback) =>
        format.Length > prefix.Length && char.IsAsciiDigit(format[^1])
            ? format[^1] - '0'
            : fallback;

    private static string PrecisionFormat(int digits) => digits switch
    {
        <= 0 => "0",
        1 => "0.0",
        2 => "0.00",
        3 => "0.000",
        _ => "0." + new string('0', Math.Min(digits, 9)),
    };
}
