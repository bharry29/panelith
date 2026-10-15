using Panelith.Sensors;

namespace Panelith.Expressions;

/// <summary>
/// A recursive-descent parser for binding expressions.
/// </summary>
/// <remarks>
/// Precedence, loosest first: conditional, <c>||</c>, <c>&amp;&amp;</c>,
/// equality, comparison, additive, multiplicative, unary.
/// </remarks>
internal sealed class ExpressionParser
{
    /// <summary>How deeply an expression may nest, which bounds recursion while parsing and evaluating.</summary>
    public const int MaxDepth = 64;

    private readonly string _source;
    private readonly List<Token> _tokens;
    private readonly Dictionary<ExpressionNode, int> _depths = new(ReferenceEqualityComparer.Instance);
    private int _index;
    private int _recursion;

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
        Enter();
        try
        {
            var condition = ParseOr();
            if (!Match(TokenType.Question))
            {
                return condition;
            }

            var whenTrue = ParseConditional();
            Expect(TokenType.Colon, "':' to complete the conditional");
            var whenFalse = ParseConditional();
            return Track(new ConditionalNode(condition, whenTrue, whenFalse), condition, whenTrue, whenFalse);
        }
        finally
        {
            _recursion--;
        }
    }

    private void Enter()
    {
        if (++_recursion > MaxDepth)
        {
            throw new ExpressionException($"The expression nests more than {MaxDepth} levels deep.", _source, Current.Position);
        }
    }

    /// <summary>Records a node's depth, refusing trees deeper than <see cref="MaxDepth"/>.</summary>
    private ExpressionNode Track(ExpressionNode node, params ReadOnlySpan<ExpressionNode> children)
    {
        var depth = 1;
        foreach (var child in children)
        {
            depth = Math.Max(depth, (_depths.TryGetValue(child, out var d) ? d : 1) + 1);
        }

        if (depth > MaxDepth)
        {
            throw new ExpressionException($"The expression nests more than {MaxDepth} levels deep.", _source, Current.Position);
        }

        _depths[node] = depth;
        return node;
    }

    private ExpressionNode Binary(string op, ExpressionNode left, ExpressionNode right) =>
        Track(new BinaryNode(op, left, right), left, right);

    private ExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenType.Or))
        {
            left = Binary("||", left, ParseAnd());
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseEquality();
        while (Match(TokenType.And))
        {
            left = Binary("&&", left, ParseEquality());
        }

        return left;
    }

    private ExpressionNode ParseEquality()
    {
        var left = ParseComparison();
        while (Current.Type is TokenType.Equal or TokenType.NotEqual)
        {
            var op = Advance().Type == TokenType.Equal ? "==" : "!=";
            left = Binary(op, left, ParseComparison());
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
            left = Binary(op, left, ParseAdditive());
        }

        return left;
    }

    private ExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Type is TokenType.Plus or TokenType.Minus)
        {
            var op = Advance().Type == TokenType.Plus ? "+" : "-";
            left = Binary(op, left, ParseMultiplicative());
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
            left = Binary(op, left, ParseUnary());
        }

        return left;
    }

    private ExpressionNode ParseUnary()
    {
        if (Current.Type is TokenType.Minus or TokenType.Not)
        {
            var op = Advance().Type == TokenType.Minus ? "-" : "!";
            Enter();
            try
            {
                var operand = ParseUnary();
                return Track(new UnaryNode(op, operand), operand);
            }
            finally
            {
                _recursion--;
            }
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
                return Track(new FunctionNode(token.Text, arguments), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(arguments));
            }

            case TokenType.End:
                throw new ExpressionException("The expression ended unexpectedly.", _source, token.Position);

            default:
                throw new ExpressionException($"Unexpected \"{token.Text}\".", _source, token.Position);
        }
    }
}
