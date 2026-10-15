using System.Collections.Concurrent;
using System.Text;
using RigGlass.Sensors;

namespace RigGlass.Expressions;

/// <summary>
/// A text string with embedded bindings, such as
/// <c>CPU {@cpu.temp:0}&#176;C @ {@cpu.clock:clock}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A placeholder is <c>{expression}</c> or <c>{expression:format}</c>. Literal
/// braces are written doubled, as <c>{{</c> and <c>}}</c>.
/// </para>
/// <para>
/// Templates are parsed once per theme load. The parsed form keeps literal runs
/// and bindings separate so that rendering a frame is a walk over a small array
/// with no re-parsing and no regular expressions.
/// </para>
/// </remarks>
public sealed class TextTemplate
{
    private static readonly ConcurrentDictionary<string, TextTemplate> Cache = new(StringComparer.Ordinal);

    private readonly Segment[] _segments;

    private TextTemplate(string source, Segment[] segments)
    {
        Source = source;
        _segments = segments;
        Bindings = segments
            .Where(s => s.Expression is not null)
            .Select(s => s.Expression!)
            .ToArray();
        ReferencedSensors = Bindings
            .SelectMany(b => b.ReferencedSensors)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The template text this was parsed from.</summary>
    public string Source { get; }

    /// <summary>Every expression embedded in the template, in order.</summary>
    public IReadOnlyList<BindingExpression> Bindings { get; }

    /// <summary>Every sensor the template reads.</summary>
    public IReadOnlyList<string> ReferencedSensors { get; }

    /// <summary>Whether the template is a constant string with no bindings.</summary>
    public bool IsLiteral => Bindings.Count == 0;

    /// <summary>Parses a template, reusing a cached result for repeated text.</summary>
    /// <exception cref="ExpressionException">An embedded expression is malformed.</exception>
    public static TextTemplate Parse(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return Cache.GetOrAdd(string.Empty, static _ => new TextTemplate(string.Empty, []));
        }

        return Cache.GetOrAdd(source, static text =>
        {
            var segments = new List<Segment>();
            var literal = new StringBuilder();

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (ch == '{')
                {
                    if (i + 1 < text.Length && text[i + 1] == '{')
                    {
                        literal.Append('{');
                        i++;
                        continue;
                    }

                    var close = FindClosingBrace(text, i);
                    if (close < 0)
                    {
                        throw new ExpressionException("Unclosed '{' in text. Write '{{' for a literal brace.", text, i);
                    }

                    if (literal.Length > 0)
                    {
                        segments.Add(Segment.Literal(literal.ToString()));
                        literal.Clear();
                    }

                    var body = text[(i + 1)..close];
                    segments.Add(ParsePlaceholder(text, body, i + 1));
                    i = close;
                    continue;
                }

                if (ch == '}' && i + 1 < text.Length && text[i + 1] == '}')
                {
                    literal.Append('}');
                    i++;
                    continue;
                }

                literal.Append(ch);
            }

            if (literal.Length > 0)
            {
                segments.Add(Segment.Literal(literal.ToString()));
            }

            return new TextTemplate(text, [.. segments]);
        });
    }

    /// <summary>Parses a template, returning <see langword="false"/> instead of throwing.</summary>
    public static bool TryParse(string? source, out TextTemplate? template, out string? error)
    {
        try
        {
            template = Parse(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            template = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Renders the template against a context.</summary>
    public string Render(IBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_segments.Length == 0)
        {
            return string.Empty;
        }

        if (_segments is [{ Expression: null } only])
        {
            return only.Text!;
        }

        var builder = new StringBuilder(Source.Length + 16);
        foreach (var segment in _segments)
        {
            if (segment.Expression is null)
            {
                builder.Append(segment.Text);
                continue;
            }

            var value = segment.Expression.Evaluate(context);
            var kind = segment.Kind ?? InferKind(segment.Expression, context);
            builder.Append(ValueFormatter.Format(value, segment.Format, kind));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The first sensor referenced anywhere in the template, which is what
    /// threshold colouring falls back to when a widget names no explicit
    /// colour binding.
    /// </summary>
    public string? PrimarySensor => ReferencedSensors.Count > 0 ? ReferencedSensors[0] : null;

    /// <inheritdoc />
    public override string ToString() => Source;

    /// <summary>
    /// Works out which sensor kind a placeholder's default formatting should
    /// follow, by looking at the single sensor it reads.
    /// </summary>
    /// <remarks>
    /// Only a lone sensor reference gets a kind. Once arithmetic is involved the
    /// result's unit is no longer the sensor's unit -- dividing bytes by bytes
    /// yields a ratio, not a byte count -- so the author is expected to say what
    /// they want.
    /// </remarks>
    private static SensorKind InferKind(BindingExpression expression, IBindingContext context) =>
        expression.Root is SensorNode sensor && context is ISensorKindProvider provider
            ? provider.KindOf(sensor.Reference)
            : SensorKind.Unknown;

    private static int FindClosingBrace(string text, int openIndex)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits a placeholder body into expression and format on the last colon
    /// that is not inside brackets, quotes or parentheses.
    /// </summary>
    /// <remarks>
    /// The last colon rather than the first, because a conditional expression
    /// contains colons of its own: in
    /// <c>{@cpu.temp &gt; 80 ? 'hot' : 'ok'}</c> none of them introduce a format.
    /// </remarks>
    private static Segment ParsePlaceholder(string source, string body, int offset)
    {
        var depth = 0;
        var quote = '\0';
        var splitAt = -1;

        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ':' when depth == 0:
                    splitAt = i;
                    break;
            }
        }

        var expressionText = splitAt < 0 ? body : body[..splitAt];
        var format = splitAt < 0 ? null : body[(splitAt + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(expressionText))
        {
            throw new ExpressionException("Empty placeholder.", source, offset);
        }

        // A conditional whose branches are both strings has no numeric format,
        // so a trailing colon-looking fragment belongs to the expression.
        if (format is not null && !IsPlausibleFormat(format))
        {
            expressionText = body;
            format = null;
        }

        return Segment.Binding(BindingExpression.Parse(expressionText.Trim()), format);
    }

    /// <summary>
    /// Whether a fragment after a colon looks like a format specifier rather
    /// than the tail of a conditional expression.
    /// </summary>
    private static bool IsPlausibleFormat(string candidate)
    {
        if (candidate.Length == 0 || candidate.Length > 16)
        {
            return false;
        }

        if (ValueFormatter.NamedFormats.Contains(candidate))
        {
            return true;
        }

        foreach (var ch in candidate)
        {
            if (!char.IsAsciiDigit(ch) && ch is not ('0' or '#' or '.' or ',' or '%' or '+' or '-' or 'e' or 'E'))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Segment(string? Text, BindingExpression? Expression, string? Format, SensorKind? Kind)
    {
        public static Segment Literal(string text) => new(text, null, null, null);

        public static Segment Binding(BindingExpression expression, string? format) => new(null, expression, format, null);
    }
}

/// <summary>
/// Implemented by binding contexts that can report the kind of a sensor, so
/// that a placeholder with no explicit format still gets unit-aware output.
/// </summary>
public interface ISensorKindProvider
{
    /// <summary>The kind of the sensor named by <paramref name="reference"/>.</summary>
    SensorKind KindOf(string reference);
}
