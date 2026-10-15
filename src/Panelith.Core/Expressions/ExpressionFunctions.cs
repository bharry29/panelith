using System.Globalization;

namespace Panelith.Expressions;

/// <summary>The functions available inside theme binding expressions.</summary>
public static class ExpressionFunctions
{
    /// <summary>Every function name, for editor completion and validation.</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "abs", "min", "max", "avg", "sum", "round", "floor", "ceil", "clamp",
        "if", "coalesce", "sqrt", "pow", "sign", "lerp", "map",
        "avgover", "minover", "maxover",
        "upper", "lower", "trim", "len", "concat",
        "has", "isnum", "istext",
    };

    /// <summary>Invokes a function by name.</summary>
    /// <exception cref="ExpressionException">The function is unknown or was given the wrong number of arguments.</exception>
    public static BindingValue Invoke(string name, IReadOnlyList<ExpressionNode> arguments, IBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);

        switch (name.ToLowerInvariant())
        {
            // Evaluated lazily so that the untaken branch cannot fail or cost
            // anything, which matters for `if(has(@gpu.temp), @gpu.temp, 0)`.
            case "if":
                Require(name, arguments, 3);
                return arguments[0].Evaluate(context).AsBoolean()
                    ? arguments[1].Evaluate(context)
                    : arguments[2].Evaluate(context);

            case "coalesce":
            {
                RequireAtLeast(name, arguments, 1);
                foreach (var argument in arguments)
                {
                    var value = argument.Evaluate(context);
                    if (value.HasValue)
                    {
                        return value;
                    }
                }

                return BindingValue.Missing;
            }

            case "has":
                Require(name, arguments, 1);
                return BindingValue.FromBoolean(arguments[0].Evaluate(context).HasValue);

            case "avgover":
            case "minover":
            case "maxover":
                return OverWindow(name, arguments, context);
        }

        var values = new BindingValue[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            values[i] = arguments[i].Evaluate(context);
        }

        return InvokeStrict(name, values);
    }

    private static BindingValue InvokeStrict(string name, ReadOnlySpan<BindingValue> values)
    {
        switch (name.ToLowerInvariant())
        {
            case "isnum":
                return BindingValue.FromBoolean(values.Length == 1 && values[0].Kind == BindingValueKind.Number);
            case "istext":
                return BindingValue.FromBoolean(values.Length == 1 && values[0].Kind == BindingValueKind.Text);
            case "upper":
                return values.Length == 1 ? BindingValue.FromText(values[0].ToString().ToUpperInvariant()) : BindingValue.Missing;
            case "lower":
                return values.Length == 1 ? BindingValue.FromText(values[0].ToString().ToLowerInvariant()) : BindingValue.Missing;
            case "trim":
                return values.Length == 1 ? BindingValue.FromText(values[0].ToString().Trim()) : BindingValue.Missing;
            case "len":
                return values.Length == 1 ? BindingValue.FromNumber(values[0].ToString().Length) : BindingValue.Missing;
            case "concat":
            {
                var builder = new System.Text.StringBuilder();
                foreach (var value in values)
                {
                    builder.Append(value.ToString());
                }

                return BindingValue.FromText(builder.ToString());
            }
        }

        Span<double> numbers = values.Length <= 8 ? stackalloc double[values.Length] : new double[values.Length];
        var count = 0;
        foreach (var value in values)
        {
            if (value.AsNumber() is { } number)
            {
                numbers[count++] = number;
            }
        }

        var present = numbers[..count];

        return name.ToLowerInvariant() switch
        {
            // Aggregates skip missing inputs, so `max(@cpu.temp, @gpu.temp)` on a
            // machine with no GPU still reports the CPU temperature.
            "min" => count == 0 ? BindingValue.Missing : BindingValue.FromNumber(Min(present)),
            "max" => count == 0 ? BindingValue.Missing : BindingValue.FromNumber(Max(present)),
            "sum" => count == 0 ? BindingValue.Missing : BindingValue.FromNumber(Sum(present)),
            "avg" => count == 0 ? BindingValue.Missing : BindingValue.FromNumber(Sum(present) / count),
            "abs" => count >= 1 ? BindingValue.FromNumber(Math.Abs(present[0])) : BindingValue.Missing,
            "sqrt" => count >= 1 && present[0] >= 0d ? BindingValue.FromNumber(Math.Sqrt(present[0])) : BindingValue.Missing,
            "sign" => count >= 1 ? BindingValue.FromNumber(Math.Sign(present[0])) : BindingValue.Missing,
            "floor" => count >= 1 ? BindingValue.FromNumber(Math.Floor(present[0])) : BindingValue.Missing,
            "ceil" => count >= 1 ? BindingValue.FromNumber(Math.Ceiling(present[0])) : BindingValue.Missing,
            "pow" => count >= 2 ? BindingValue.FromNumber(Math.Pow(present[0], present[1])) : BindingValue.Missing,
            "round" => count switch
            {
                >= 2 => BindingValue.FromNumber(Math.Round(present[0], Math.Clamp((int)present[1], 0, 15), MidpointRounding.AwayFromZero)),
                1 => BindingValue.FromNumber(Math.Round(present[0], MidpointRounding.AwayFromZero)),
                _ => BindingValue.Missing,
            },
            "clamp" => count >= 3 ? BindingValue.FromNumber(Math.Clamp(present[0], present[1], present[2])) : BindingValue.Missing,
            "lerp" => count >= 3 ? BindingValue.FromNumber(present[0] + ((present[1] - present[0]) * present[2])) : BindingValue.Missing,
            "map" => count >= 5 ? BindingValue.FromNumber(Map(present)) : BindingValue.Missing,
            _ => throw new ExpressionException($"Unknown function \"{name}\".", name, -1),
        };
    }

    /// <summary>
    /// Re-maps a value from one range to another, clamped to the output range.
    /// </summary>
    /// <remarks>
    /// Used by themes to drive a gauge from a sensor whose natural range is
    /// unhelpful, for example mapping a 300-2200 RPM fan onto 0-100.
    /// </remarks>
    private static double Map(ReadOnlySpan<double> a)
    {
        var (value, inMin, inMax, outMin, outMax) = (a[0], a[1], a[2], a[3], a[4]);
        var span = inMax - inMin;
        if (Math.Abs(span) < double.Epsilon)
        {
            return outMin;
        }

        var t = Math.Clamp((value - inMin) / span, 0d, 1d);
        return outMin + ((outMax - outMin) * t);
    }

    private static BindingValue OverWindow(string name, IReadOnlyList<ExpressionNode> arguments, IBindingContext context)
    {
        RequireAtLeast(name, arguments, 1);

        if (arguments[0] is not SensorNode sensor)
        {
            throw new ExpressionException(
                $"{name}() expects a sensor reference as its first argument, for example {name}(@cpu.temp, 60).",
                name,
                -1);
        }

        var seconds = arguments.Count >= 2 ? arguments[1].Evaluate(context).AsNumber() ?? 60d : 60d;
        var history = context.ResolveHistory(sensor.Reference, TimeSpan.FromSeconds(Math.Max(1d, seconds)));
        if (history.Count == 0)
        {
            return context.ResolveSensor(sensor.Reference);
        }

        return name.ToLowerInvariant() switch
        {
            "avgover" => BindingValue.FromNumber(history.Average()),
            "minover" => BindingValue.FromNumber(history.Min()),
            "maxover" => BindingValue.FromNumber(history.Max()),
            _ => BindingValue.Missing,
        };
    }

    private static double Min(ReadOnlySpan<double> values)
    {
        var result = values[0];
        foreach (var value in values)
        {
            result = Math.Min(result, value);
        }

        return result;
    }

    private static double Max(ReadOnlySpan<double> values)
    {
        var result = values[0];
        foreach (var value in values)
        {
            result = Math.Max(result, value);
        }

        return result;
    }

    private static double Sum(ReadOnlySpan<double> values)
    {
        var result = 0d;
        foreach (var value in values)
        {
            result += value;
        }

        return result;
    }

    private static void Require(string name, IReadOnlyList<ExpressionNode> arguments, int count)
    {
        if (arguments.Count != count)
        {
            throw new ExpressionException(
                string.Create(CultureInfo.InvariantCulture, $"{name}() takes {count} arguments but was given {arguments.Count}."),
                name,
                -1);
        }
    }

    private static void RequireAtLeast(string name, IReadOnlyList<ExpressionNode> arguments, int count)
    {
        if (arguments.Count < count)
        {
            throw new ExpressionException(
                string.Create(CultureInfo.InvariantCulture, $"{name}() takes at least {count} arguments but was given {arguments.Count}."),
                name,
                -1);
        }
    }
}
