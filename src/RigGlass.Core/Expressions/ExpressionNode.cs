namespace RigGlass.Expressions;

/// <summary>A node of a parsed binding expression.</summary>
public abstract record ExpressionNode
{
    /// <summary>Evaluates this node against a context.</summary>
    public abstract BindingValue Evaluate(IBindingContext context);

    /// <summary>This node and all of its descendants.</summary>
    public virtual IEnumerable<ExpressionNode> Descendants() => [this];
}

/// <summary>A literal number.</summary>
/// <param name="Value">The number.</param>
public sealed record NumberNode(double Value) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) => BindingValue.FromNumber(Value);
}

/// <summary>A literal string.</summary>
/// <param name="Value">The string.</param>
public sealed record StringNode(string Value) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) => BindingValue.FromText(Value);
}

/// <summary>A reference to a sensor, by alias or by raw id.</summary>
/// <param name="Reference">The alias (including its <c>@</c>) or the raw sensor id.</param>
public sealed record SensorNode(string Reference) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) => context.ResolveSensor(Reference);
}

/// <summary>A reference to a theme variable.</summary>
/// <param name="Name">Variable name, without the leading <c>$</c>.</param>
public sealed record VariableNode(string Name) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) => context.ResolveVariable(Name);
}

/// <summary>A unary operation.</summary>
/// <param name="Operator">Either <c>-</c> or <c>!</c>.</param>
/// <param name="Operand">The operand.</param>
public sealed record UnaryNode(string Operator, ExpressionNode Operand) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context)
    {
        var value = Operand.Evaluate(context);
        return Operator switch
        {
            "!" => BindingValue.FromBoolean(!value.AsBoolean()),
            "-" => value.AsNumber() is { } n ? BindingValue.FromNumber(-n) : BindingValue.Missing,
            _ => BindingValue.Missing,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<ExpressionNode> Descendants() => [this, .. Operand.Descendants()];
}

/// <summary>A binary operation.</summary>
/// <param name="Operator">The operator symbol.</param>
/// <param name="Left">Left operand.</param>
/// <param name="Right">Right operand.</param>
public sealed record BinaryNode(string Operator, ExpressionNode Left, ExpressionNode Right) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context)
    {
        // Logical operators short-circuit, so that `@gpu.temp > 0 && @gpu.temp < 50`
        // does not evaluate the right side on a machine with no GPU sensor.
        if (Operator == "&&")
        {
            return BindingValue.FromBoolean(Left.Evaluate(context).AsBoolean() && Right.Evaluate(context).AsBoolean());
        }

        if (Operator == "||")
        {
            return BindingValue.FromBoolean(Left.Evaluate(context).AsBoolean() || Right.Evaluate(context).AsBoolean());
        }

        var left = Left.Evaluate(context);
        var right = Right.Evaluate(context);

        if (Operator is "==" or "!=")
        {
            var equal = Equals(left, right);
            return BindingValue.FromBoolean(Operator == "==" ? equal : !equal);
        }

        if (Operator == "+" && (left.Kind == BindingValueKind.Text || right.Kind == BindingValueKind.Text))
        {
            return BindingValue.FromText(left.ToString() + right.ToString());
        }

        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
        {
            return BindingValue.Missing;
        }

        return Operator switch
        {
            "+" => BindingValue.FromNumber(a + b),
            "-" => BindingValue.FromNumber(a - b),
            "*" => BindingValue.FromNumber(a * b),

            // Division by zero yields Missing rather than infinity: a theme
            // computing "used / total * 100" before the total is known should
            // show a placeholder, not "Infinity%".
            "/" => b == 0d ? BindingValue.Missing : BindingValue.FromNumber(a / b),
            "%" => b == 0d ? BindingValue.Missing : BindingValue.FromNumber(a % b),
            "<" => BindingValue.FromBoolean(a < b),
            "<=" => BindingValue.FromBoolean(a <= b),
            ">" => BindingValue.FromBoolean(a > b),
            ">=" => BindingValue.FromBoolean(a >= b),
            _ => BindingValue.Missing,
        };
    }

    private static bool Equals(BindingValue left, BindingValue right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        if (left.Kind == BindingValueKind.Text || right.Kind == BindingValueKind.Text)
        {
            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return left.AsNumber() is { } a && right.AsNumber() is { } b && Math.Abs(a - b) < 1e-9d;
    }

    /// <inheritdoc />
    public override IEnumerable<ExpressionNode> Descendants() =>
        [this, .. Left.Descendants(), .. Right.Descendants()];
}

/// <summary>A conditional expression.</summary>
/// <param name="Condition">The condition.</param>
/// <param name="WhenTrue">Result when the condition is truthy.</param>
/// <param name="WhenFalse">Result otherwise.</param>
public sealed record ConditionalNode(ExpressionNode Condition, ExpressionNode WhenTrue, ExpressionNode WhenFalse) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) =>
        Condition.Evaluate(context).AsBoolean() ? WhenTrue.Evaluate(context) : WhenFalse.Evaluate(context);

    /// <inheritdoc />
    public override IEnumerable<ExpressionNode> Descendants() =>
        [this, .. Condition.Descendants(), .. WhenTrue.Descendants(), .. WhenFalse.Descendants()];
}

/// <summary>A call to a built-in function.</summary>
/// <param name="Name">Function name, lowercased.</param>
/// <param name="Arguments">Evaluated left to right, except for <c>if</c> and <c>coalesce</c>.</param>
public sealed record FunctionNode(string Name, IReadOnlyList<ExpressionNode> Arguments) : ExpressionNode
{
    /// <inheritdoc />
    public override BindingValue Evaluate(IBindingContext context) =>
        ExpressionFunctions.Invoke(Name, Arguments, context);

    /// <inheritdoc />
    public override IEnumerable<ExpressionNode> Descendants() =>
        [this, .. Arguments.SelectMany(a => a.Descendants())];
}
