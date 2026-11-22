using System.Globalization;
using System.Text;

namespace Xrmd.Workflows.Xaml;

/// <summary>
/// The syntax tree of the small subset of Visual Basic the classic designer writes into XAML attribute values
/// (<c>[...]</c>). It is only a syntax: <see cref="XamlWorkflowParser"/> maps the shapes it knows to steps and values and
/// turns everything else into an <see cref="UnsupportedStep"/>. Nothing here is ever evaluated as VB (W16).
/// </summary>
internal abstract record VbExpr
{
    /// <summary>An identifier or dotted name, e.g. <c>CreateStep1_1</c> or <c>Microsoft.Xrm.Sdk.OperationStatus.Canceled</c>.</summary>
    public sealed record Name(string Value) : VbExpr;

    public sealed record Str(string Value) : VbExpr;

    /// <summary>A numeric literal, kept as written (invariant culture).</summary>
    public sealed record Num(string Text) : VbExpr;

    public sealed record Bool(bool Value) : VbExpr;

    public sealed record Nothing : VbExpr;

    /// <summary>A call or indexer: <c>Target(args)</c>, e.g. <c>InputEntities("primaryEntity")</c> or <c>String.Concat(a, b)</c>.</summary>
    public sealed record Call(VbExpr Target, IReadOnlyList<VbExpr> Args) : VbExpr;

    /// <summary>A member access on a non-name expression, e.g. <c>InputEntities("primaryEntity").Id</c>.</summary>
    public sealed record Member(VbExpr Target, string MemberName) : VbExpr;

    /// <summary><c>New Type(args)</c>.</summary>
    public sealed record New(string Type, IReadOnlyList<VbExpr> Args) : VbExpr;

    /// <summary><c>New Type() { elements }</c>, e.g. <c>New Object() { ... }</c>.</summary>
    public sealed record NewArray(string Type, IReadOnlyList<VbExpr> Elements) : VbExpr;
}

/// <summary>Parses the VB subset of <see cref="VbExpr"/>. Anything outside it throws <see cref="FormatException"/>.</summary>
internal static class ExpressionParser
{
    /// <summary>
    /// The expression inside an attribute or argument value written as <c>[expr]</c>, or null when the value is not
    /// bracketed (then it is a plain literal for XAML).
    /// </summary>
    public static string? Bracketed(string? text)
    {
        if (text is null)
            return null;
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : null;
    }

    /// <summary>The deepest nesting of calls, arguments and array elements accepted.</summary>
    public const int MaxDepth = 64;

    public static VbExpr Parse(string text)
    {
        var parser = new Parser(Tokenize(text), text);
        var expr = parser.ParseExpression();
        parser.ExpectEnd();
        return expr;
    }

    private enum Kind { Ident, Str, Num, Punct, End }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new FormatException("unterminated string literal");
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                tokens.Add(new Token(Kind.Str, sb.ToString()));
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && PreviousAllowsSign(tokens)))
            {
                var start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                    i++;
                tokens.Add(new Token(Kind.Num, text[start..i]));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new Token(Kind.Ident, text[start..i]));
            }
            else if (c is '(' or ')' or '{' or '}' or ',' or '.')
            {
                tokens.Add(new Token(Kind.Punct, c.ToString()));
                i++;
            }
            else
            {
                throw new FormatException($"unexpected character '{c}'");
            }
        }
        tokens.Add(new Token(Kind.End, ""));
        return tokens;
    }

    private static bool PreviousAllowsSign(List<Token> tokens) =>
        tokens.Count == 0 || tokens[^1] is { Kind: Kind.Punct, Text: "(" or "{" or "," };

    private sealed class Parser(List<Token> tokens, string text)
    {
        private int _pos;
        private int _depth;

        private Token Peek => tokens[_pos];

        public void ExpectEnd()
        {
            if (Peek.Kind != Kind.End)
                throw new FormatException($"unexpected '{Peek.Text}' in '{text}'");
        }

        public VbExpr ParseExpression()
        {
            if (++_depth > MaxDepth)
                throw new FormatException($"the expression is nested deeper than {MaxDepth} levels");
            try
            {
                return ParsePostfix();
            }
            finally
            {
                _depth--;
            }
        }

        private VbExpr ParsePostfix()
        {
            var expr = ParsePrimary();
            while (true)
            {
                if (IsPunct("("))
                {
                    expr = new VbExpr.Call(expr, ParseArgs("(", ")"));
                }
                else if (IsPunct("."))
                {
                    _pos++;
                    var member = ExpectIdent();
                    expr = expr is VbExpr.Name name ? new VbExpr.Name(name.Value + "." + member) : new VbExpr.Member(expr, member);
                }
                else
                {
                    return expr;
                }
            }
        }

        private VbExpr ParsePrimary()
        {
            var token = Peek;
            switch (token.Kind)
            {
                case Kind.Str:
                    _pos++;
                    return new VbExpr.Str(token.Text);
                case Kind.Num:
                    _pos++;
                    return new VbExpr.Num(token.Text);
                case Kind.Ident when token.Text.Equals("True", StringComparison.OrdinalIgnoreCase):
                    _pos++;
                    return new VbExpr.Bool(true);
                case Kind.Ident when token.Text.Equals("False", StringComparison.OrdinalIgnoreCase):
                    _pos++;
                    return new VbExpr.Bool(false);
                case Kind.Ident when token.Text.Equals("Nothing", StringComparison.OrdinalIgnoreCase):
                    _pos++;
                    return new VbExpr.Nothing();
                case Kind.Ident when token.Text.Equals("New", StringComparison.OrdinalIgnoreCase):
                    _pos++;
                    return ParseNew();
                case Kind.Ident:
                    _pos++;
                    return new VbExpr.Name(token.Text);
                default:
                    throw new FormatException($"unexpected '{token.Text}' in '{text}'");
            }
        }

        private VbExpr ParseNew()
        {
            var type = ExpectIdent();
            while (IsPunct("."))
            {
                _pos++;
                type += "." + ExpectIdent();
            }
            var args = IsPunct("(") ? ParseArgs("(", ")") : [];
            if (IsPunct("{"))
            {
                if (args.Count != 0)
                    throw new FormatException($"array bounds are not supported in '{text}'");
                return new VbExpr.NewArray(type, ParseArgs("{", "}"));
            }
            return new VbExpr.New(type, args);
        }

        private List<VbExpr> ParseArgs(string open, string close)
        {
            ExpectPunct(open);
            var args = new List<VbExpr>();
            if (IsPunct(close))
            {
                _pos++;
                return args;
            }
            while (true)
            {
                args.Add(ParseExpression());
                if (IsPunct(","))
                {
                    _pos++;
                    continue;
                }
                ExpectPunct(close);
                return args;
            }
        }

        private bool IsPunct(string p) => Peek.Kind == Kind.Punct && Peek.Text == p;

        private void ExpectPunct(string p)
        {
            if (!IsPunct(p))
                throw new FormatException($"expected '{p}' in '{text}'");
            _pos++;
        }

        private string ExpectIdent()
        {
            if (Peek.Kind != Kind.Ident)
                throw new FormatException($"expected a name in '{text}'");
            return tokens[_pos++].Text;
        }
    }

    /// <summary>An invariant-culture number of a <see cref="VbExpr.Num"/> or a string.</summary>
    public static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
