using OmniPanel.Sensors;

namespace OmniPanel.Expressions;

/// <summary>
/// A recursive-descent parser for binding expressions.
/// </summary>
/// <remarks>
/// Precedence, loosest first: conditional, <c>||</c>, <c>&amp;&amp;</c>,
/// equality, comparison, additive, multiplicative, unary.
/// </remarks>
internal sealed class ExpressionParser
{
    private readonly string _source;
    private readonly List<Token> _tokens;
    private int _index;

    public ExpressionParser(string source, List<Token> tokens)
    {
        _source = source;
        _tokens = tokens;
    }

    public ExpressionNode ParseRoot()
    {
        var node = ParseConditional();
        if (Current.Type != TokenType.End)
        {
            throw new ExpressionException($"Unexpected \"{Current.Text}\".", _source, Current.Position);
        }

        return node;
    }

    private Token Current => _tokens[_index];

    private Token Advance() => _tokens[_index++];

    private bool Match(TokenType type)
    {
        if (Current.Type != type)
        {
            return false;
        }

        _index++;
        return true;
    }

    private void Expect(TokenType type, string expected)
    {
        if (!Match(type))
        {
            throw new ExpressionException($"Expected {expected}.", _source, Current.Position);
        }
    }

    private ExpressionNode ParseConditional()
    {
        var condition = ParseOr();
        if (!Match(TokenType.Question))
        {
            return condition;
        }

        var whenTrue = ParseConditional();
        Expect(TokenType.Colon, "':' to complete the conditional");
        var whenFalse = ParseConditional();
        return new ConditionalNode(condition, whenTrue, whenFalse);
    }

    private ExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenType.Or))
        {
            left = new BinaryNode("||", left, ParseAnd());
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseEquality();
        while (Match(TokenType.And))
        {
            left = new BinaryNode("&&", left, ParseEquality());
        }

        return left;
    }

    private ExpressionNode ParseEquality()
    {
        var left = ParseComparison();
        while (Current.Type is TokenType.Equal or TokenType.NotEqual)
        {
            var op = Advance().Type == TokenType.Equal ? "==" : "!=";
            left = new BinaryNode(op, left, ParseComparison());
        }

        return left;
    }

    private ExpressionNode ParseComparison()
    {
        var left = ParseAdditive();
        while (Current.Type is TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual)
        {
            var op = Advance().Type switch
            {
                TokenType.Less => "<",
                TokenType.LessEqual => "<=",
                TokenType.Greater => ">",
                _ => ">=",
            };
            left = new BinaryNode(op, left, ParseAdditive());
        }

        return left;
    }

    private ExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Type is TokenType.Plus or TokenType.Minus)
        {
            var op = Advance().Type == TokenType.Plus ? "+" : "-";
            left = new BinaryNode(op, left, ParseMultiplicative());
        }

        return left;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Type is TokenType.Star or TokenType.Slash or TokenType.Percent)
        {
            var op = Advance().Type switch
            {
                TokenType.Star => "*",
                TokenType.Slash => "/",
                _ => "%",
            };
            left = new BinaryNode(op, left, ParseUnary());
        }

        return left;
    }

    private ExpressionNode ParseUnary()
    {
        if (Current.Type is TokenType.Minus or TokenType.Not)
        {
            var op = Advance().Type == TokenType.Minus ? "-" : "!";
            return new UnaryNode(op, ParseUnary());
        }

        return ParsePrimary();
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Current;
        switch (token.Type)
        {
            case TokenType.Number:
                Advance();
                return new NumberNode(token.Number);

            case TokenType.String:
                Advance();
                return new StringNode(token.Text);

            case TokenType.SensorAlias:
            case TokenType.SensorId:
                Advance();
                return new SensorNode(token.Text);

            case TokenType.Variable:
                Advance();
                return new VariableNode(token.Text);

            case TokenType.LeftParen:
            {
                Advance();
                var inner = ParseConditional();
                Expect(TokenType.RightParen, "')'");
                return inner;
            }

            case TokenType.Identifier:
            {
                Advance();
                if (token.Text.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return new NumberNode(1d);
                }

                if (token.Text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return new NumberNode(0d);
                }

                if (Current.Type != TokenType.LeftParen)
                {
                    var hint = WellKnownSensors.All.ContainsKey(WellKnownSensors.AliasPrefix + token.Text)
                        ? $" Did you mean @{token.Text}?"
                        : " Sensor aliases start with '@', raw sensor ids go in [brackets], "
                          + "and theme variables start with '$'.";

                    throw new ExpressionException($"\"{token.Text}\" is not a value.{hint}", _source, token.Position);
                }

                if (!ExpressionFunctions.Names.Contains(token.Text))
                {
                    throw new ExpressionException($"Unknown function \"{token.Text}\".", _source, token.Position);
                }

                Advance();
                var arguments = new List<ExpressionNode>();
                if (Current.Type != TokenType.RightParen)
                {
                    do
                    {
                        arguments.Add(ParseConditional());
                    }
                    while (Match(TokenType.Comma));
                }

                Expect(TokenType.RightParen, $"')' to close {token.Text}()");
                return new FunctionNode(token.Text, arguments);
            }

            case TokenType.End:
                throw new ExpressionException("The expression ended unexpectedly.", _source, token.Position);

            default:
                throw new ExpressionException($"Unexpected \"{token.Text}\".", _source, token.Position);
        }
    }
}
