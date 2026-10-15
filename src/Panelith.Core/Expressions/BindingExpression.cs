using System.Collections.Concurrent;

namespace Panelith.Expressions;

/// <summary>
/// A parsed, reusable theme binding expression.
/// </summary>
/// <remarks>
/// Parsing happens once when a theme is loaded, never per frame. Instances are
/// immutable and safe to share across threads.
/// </remarks>
public sealed class BindingExpression
{
    private static readonly ConcurrentDictionary<string, BindingExpression> Cache = new(StringComparer.Ordinal);

    /// <summary>The longest expression parsed.</summary>
    public const int MaxLength = 4096;

    /// <summary>How many parsed expressions are kept for reuse.</summary>
    internal const int MaxCached = 4096;

    private BindingExpression(string source, ExpressionNode root)
    {
        Source = source;
        Root = root;
        ReferencedSensors = root.Descendants()
            .OfType<SensorNode>()
            .Select(n => n.Reference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ReferencedVariables = root.Descendants()
            .OfType<VariableNode>()
            .Select(n => n.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The expression text this was parsed from.</summary>
    public string Source { get; }

    /// <summary>The root of the parsed tree.</summary>
    public ExpressionNode Root { get; }

    /// <summary>
    /// Every sensor this expression reads, used to decide whether a frame needs
    /// redrawing and to warn about sensors missing on this machine.
    /// </summary>
    public IReadOnlyList<string> ReferencedSensors { get; }

    /// <summary>Every theme variable this expression reads.</summary>
    public IReadOnlyList<string> ReferencedVariables { get; }

    /// <summary>
    /// Parses an expression, reusing a cached tree when the same text has been
    /// parsed before.
    /// </summary>
    /// <exception cref="ExpressionException">The expression is malformed.</exception>
    public static BindingExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxLength)
        {
            throw new ExpressionException($"The expression is longer than {MaxLength} characters.", source[..64], 0);
        }

        if (Cache.Count >= MaxCached)
        {
            // Themes use a few hundred distinct expressions; only something
            // generating text endlessly gets here, so start over rather than grow.
            Cache.Clear();
        }

        return Cache.GetOrAdd(source, static text =>
        {
            var tokens = new ExpressionLexer(text).Tokenize();
            var parser = new ExpressionParser(text, tokens);
            return new BindingExpression(text, parser.ParseRoot());
        });
    }

    /// <summary>Parses an expression, returning <see langword="false"/> instead of throwing.</summary>
    public static bool TryParse(string? source, out BindingExpression? expression, out string? error)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            expression = null;
            error = "The expression is empty.";
            return false;
        }

        try
        {
            expression = Parse(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Evaluates the expression.</summary>
    public BindingValue Evaluate(IBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Root.Evaluate(context);
    }

    /// <inheritdoc />
    public override string ToString() => Source;
}
