using System.Globalization;
using System.Text;

namespace Panelith.Expressions;

/// <summary>The lexical categories of a binding expression.</summary>
internal enum TokenType
{
    End,
    Number,
    String,
    Identifier,
    SensorAlias,
    SensorId,
    Variable,
    LeftParen,
    RightParen,
    Comma,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Equal,
    NotEqual,
    And,
    Or,
    Not,
    Question,
    Colon,
}

/// <summary>One token of a binding expression.</summary>
internal readonly record struct Token(TokenType Type, string Text, double Number, int Position);

/// <summary>Raised when an expression cannot be parsed or evaluated.</summary>
public sealed class ExpressionException : Exception
{
    /// <summary>Creates an expression error.</summary>
    public ExpressionException(string message, string expression, int position)
        : base(BuildMessage(message, expression, position))
    {
        Expression = expression;
        Position = position;
    }

    /// <summary>The expression that failed.</summary>
    public string Expression { get; }

    /// <summary>Zero-based character offset the failure was detected at.</summary>
    public int Position { get; }

    /// <remarks>
    /// Theme authors hit these errors while editing, so the message points at
    /// the offending character rather than merely naming the expression.
    /// </remarks>
    private static string BuildMessage(string message, string expression, int position)
    {
        var caret = position >= 0 && position <= expression.Length
            ? $"\n  {expression}\n  {new string(' ', position)}^"
            : $" in \"{expression}\"";
        return message + caret;
    }
}

/// <summary>
/// Turns binding expression text into tokens.
/// </summary>
/// <remarks>
/// Sensor references come in two spellings because the two cases have different
/// needs. An alias such as <c>@cpu.temp</c> is short and is what themes should
/// normally use. A raw id is written in brackets, <c>[cpu/0/temperature/package]</c>,
/// because ids contain <c>/</c> and would otherwise be indistinguishable from
/// division.
/// </remarks>
internal sealed class ExpressionLexer
{
    private readonly string _text;
    private int _index;

    public ExpressionLexer(string text) => _text = text;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Type == TokenType.End)
            {
                return tokens;
            }
        }
    }

    private Token Next()
    {
        while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
        {
            _index++;
        }

        if (_index >= _text.Length)
        {
            return new Token(TokenType.End, string.Empty, 0d, _index);
        }

        var start = _index;
        var ch = _text[_index];

        switch (ch)
        {
            case '(': _index++; return new Token(TokenType.LeftParen, "(", 0d, start);
            case ')': _index++; return new Token(TokenType.RightParen, ")", 0d, start);
            case ',': _index++; return new Token(TokenType.Comma, ",", 0d, start);
            case '+': _index++; return new Token(TokenType.Plus, "+", 0d, start);
            case '-': _index++; return new Token(TokenType.Minus, "-", 0d, start);
            case '*': _index++; return new Token(TokenType.Star, "*", 0d, start);
            case '/': _index++; return new Token(TokenType.Slash, "/", 0d, start);
            case '%': _index++; return new Token(TokenType.Percent, "%", 0d, start);
            case '?': _index++; return new Token(TokenType.Question, "?", 0d, start);
            case ':': _index++; return new Token(TokenType.Colon, ":", 0d, start);
            case '<':
                _index++;
                if (Peek() == '=')
                {
                    _index++;
                    return new Token(TokenType.LessEqual, "<=", 0d, start);
                }

                return new Token(TokenType.Less, "<", 0d, start);
            case '>':
                _index++;
                if (Peek() == '=')
                {
                    _index++;
                    return new Token(TokenType.GreaterEqual, ">=", 0d, start);
                }

                return new Token(TokenType.Greater, ">", 0d, start);
            case '=':
                _index++;
                if (Peek() == '=')
                {
                    _index++;
                }

                // A single '=' is accepted as equality: theme authors write it
                // constantly and there is no assignment operator to confuse it with.
                return new Token(TokenType.Equal, "==", 0d, start);
            case '!':
                _index++;
                if (Peek() == '=')
                {
                    _index++;
                    return new Token(TokenType.NotEqual, "!=", 0d, start);
                }

                return new Token(TokenType.Not, "!", 0d, start);
            case '&':
                _index++;
                if (Peek() == '&')
                {
                    _index++;
                }

                return new Token(TokenType.And, "&&", 0d, start);
            case '|':
                _index++;
                if (Peek() == '|')
                {
                    _index++;
                }

                return new Token(TokenType.Or, "||", 0d, start);
            case '\'' or '"':
                return ReadString(ch);
            case '[':
                return ReadBracketedSensorId();
            case '@':
                return ReadAlias();
            case '$':
                return ReadVariable();
        }

        if (char.IsAsciiDigit(ch) || (ch == '.' && char.IsAsciiDigit(Peek())))
        {
            return ReadNumber();
        }

        if (char.IsAsciiLetter(ch) || ch == '_')
        {
            return ReadIdentifier();
        }

        throw new ExpressionException($"Unexpected character '{ch}'.", _text, start);
    }

    private char Peek() => _index < _text.Length ? _text[_index] : '\0';

    private Token ReadNumber()
    {
        var start = _index;
        while (_index < _text.Length && (char.IsAsciiDigit(_text[_index]) || _text[_index] == '.'))
        {
            _index++;
        }

        // Allow a trailing unit suffix on literals so that thresholds can be
        // written the way a user thinks of them, as in "@cpu.temp > 80c".
        while (_index < _text.Length && char.IsAsciiLetter(_text[_index]))
        {
            _index++;
        }

        var raw = _text[start.._index];
        var numeric = raw.TrimEnd(ch => char.IsAsciiLetter(ch));
        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new ExpressionException($"\"{raw}\" is not a valid number.", _text, start);
        }

        return new Token(TokenType.Number, raw, value, start);
    }

    private Token ReadString(char quote)
    {
        var start = _index;
        _index++;
        var builder = new StringBuilder();
        while (_index < _text.Length && _text[_index] != quote)
        {
            if (_text[_index] == '\\' && _index + 1 < _text.Length)
            {
                _index++;
            }

            builder.Append(_text[_index]);
            _index++;
        }

        if (_index >= _text.Length)
        {
            throw new ExpressionException("Unterminated string literal.", _text, start);
        }

        _index++;
        return new Token(TokenType.String, builder.ToString(), 0d, start);
    }

    private Token ReadBracketedSensorId()
    {
        var start = _index;
        _index++;
        var builder = new StringBuilder();
        while (_index < _text.Length && _text[_index] != ']')
        {
            builder.Append(_text[_index]);
            _index++;
        }

        if (_index >= _text.Length)
        {
            throw new ExpressionException("Unterminated sensor reference: expected ']'.", _text, start);
        }

        _index++;
        var id = builder.ToString().Trim();
        if (id.Length == 0)
        {
            throw new ExpressionException("Empty sensor reference.", _text, start);
        }

        return new Token(TokenType.SensorId, id, 0d, start);
    }

    private Token ReadAlias()
    {
        var start = _index;
        _index++;
        while (_index < _text.Length && (char.IsAsciiLetterOrDigit(_text[_index]) || _text[_index] is '.' or '_'))
        {
            _index++;
        }

        var alias = _text[start.._index];
        if (alias.Length <= 1)
        {
            throw new ExpressionException("Expected a sensor alias after '@', for example @cpu.temp.", _text, start);
        }

        return new Token(TokenType.SensorAlias, alias, 0d, start);
    }

    private Token ReadVariable()
    {
        var start = _index;
        _index++;
        while (_index < _text.Length && (char.IsAsciiLetterOrDigit(_text[_index]) || _text[_index] == '_'))
        {
            _index++;
        }

        var name = _text[(start + 1).._index];
        if (name.Length == 0)
        {
            throw new ExpressionException("Expected a variable name after '$'.", _text, start);
        }

        return new Token(TokenType.Variable, name, 0d, start);
    }

    private Token ReadIdentifier()
    {
        var start = _index;

        // Dots are consumed here so that a bare "cpu.temp" -- an alias written
        // without its '@', which is the single most common authoring mistake --
        // arrives at the parser as one identifier and gets an error message
        // explaining the right syntax, rather than "unexpected character '.'".
        while (_index < _text.Length && (char.IsAsciiLetterOrDigit(_text[_index]) || _text[_index] is '_' or '.'))
        {
            _index++;
        }

        var text = _text[start.._index];
        return text.ToLowerInvariant() switch
        {
            "and" => new Token(TokenType.And, "&&", 0d, start),
            "or" => new Token(TokenType.Or, "||", 0d, start),
            "not" => new Token(TokenType.Not, "!", 0d, start),
            _ => new Token(TokenType.Identifier, text, 0d, start),
        };
    }
}

file static class SpanExtensions
{
    public static string TrimEnd(this string value, Func<char, bool> predicate)
    {
        var end = value.Length;
        while (end > 0 && predicate(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }
}
