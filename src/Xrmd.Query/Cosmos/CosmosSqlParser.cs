using System.Globalization;
using System.Text;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Query.Cosmos;

/// <summary>
/// Hand-written tokenizer and recursive-descent parser for the supported subset of the Cosmos DB SQL dialect accepted by
/// <c>ExecuteCosmosSqlQuery</c> (plan #78, rulings J1 and J2). Every recognised construct outside the subset is refused
/// with <c>ExecuteCosmosSqlQuery: &lt;construct&gt; is not supported by xrmverse.</c>; anything else that does not parse is a
/// syntax error with its 0-based character offset. Both are <see cref="XrmdFaultException.InvalidArgument"/>.
/// </summary>
/// <remarks>
/// Grammar (keywords are case-insensitive):
/// <code>
/// query   := SELECT item (',' item)* FROM alias [WHERE or] [ORDER BY path [ASC|DESC]]
/// item    := path [[AS] name]
/// or      := and (OR and)*        and := not (AND not)*        not := NOT not | primary
/// primary := '(' or ')' | IS_DEFINED '(' path ')' | operand cmp operand      (exactly one side is a path)
/// operand := path | string | number | TRUE | FALSE | NULL | @parameter
/// </code>
/// Operator precedence is NOT, AND, OR. Nesting of parentheses and NOT is limited by <see cref="QueryLimits.MaxFilterDepth"/>.
/// </remarks>
public static class CosmosSqlParser
{
    /// <summary>Maximum AND/OR operators in one query; keeps the tree (and the recursion over it) small.</summary>
    private const int MaxConnectives = 1000;

    public static CosmosQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(Tokenizer.Tokenize(text)).ParseQuery();
    }

    private static XrmdFaultException Syntax(int position, string detail) =>
        XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: syntax error at position {position}: {detail}.");

    private static XrmdFaultException Unsupported(string construct) =>
        XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: {construct} is not supported by xrmverse.");

    private enum TokenKind { Identifier, Number, String, Parameter, Punctuation, Error, End }

    private sealed record Token(TokenKind Kind, string Text, int Position, object? Value = null)
    {
        public bool IsKeyword(string keyword) =>
            Kind == TokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

        public bool IsPunct(string punct) => Kind == TokenKind.Punctuation && Text == punct;

        /// <summary>An identifier that is not a reserved word of the dialect (a property, alias or function name).</summary>
        public bool IsName => Kind == TokenKind.Identifier && !Keywords.Contains(Text);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "AS", "ASC", "DESC", "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "UNDEFINED",
        "VALUE", "DISTINCT", "TOP", "OFFSET", "LIMIT", "GROUP", "JOIN", "IN", "EXISTS", "BETWEEN", "LIKE",
    };

    private static class Tokenizer
    {
        public static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                var start = i;
                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$')) i++;
                    tokens.Add(new Token(TokenKind.Identifier, s[start..i], start));
                }
                else if (c == '@')
                {
                    i++;
                    if (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_'))
                    {
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                        tokens.Add(new Token(TokenKind.Parameter, s[start..i], start));
                    }
                    else
                    {
                        tokens.Add(Error(start, "a parameter name must follow '@'"));
                        break;
                    }
                }
                else if (c >= '0' && c <= '9')
                {
                    var token = ReadNumber(s, ref i);
                    tokens.Add(token);
                    if (token.Kind == TokenKind.Error) break;
                }
                else if (c == '\'' || c == '"')
                {
                    var token = ReadString(s, ref i);
                    tokens.Add(token);
                    if (token.Kind == TokenKind.Error) break;
                }
                else
                {
                    var two = i + 1 < s.Length ? s.Substring(i, 2) : null;
                    if (two is "!=" or "<>" or "<=" or ">=" or "||" or "??")
                    {
                        tokens.Add(new Token(TokenKind.Punctuation, two, start));
                        i += 2;
                    }
                    else if (".,()[]{}=<>+-*/%?:".Contains(c))
                    {
                        tokens.Add(new Token(TokenKind.Punctuation, c.ToString(), start));
                        i++;
                    }
                    else
                    {
                        tokens.Add(Error(start, $"unexpected character '{c}'"));
                        break;
                    }
                }
            }

            tokens.Add(new Token(TokenKind.End, "", s.Length));
            return tokens;
        }

        private static Token Error(int position, string detail) => new(TokenKind.Error, detail, position);

        private static Token ReadNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i + 1 < s.Length && s[i] == '.' && char.IsAsciiDigit(s[i + 1]))
            {
                i++;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            }

            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                var j = i + 1;
                if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                if (j >= s.Length || !char.IsAsciiDigit(s[j])) return Error(start, "malformed number: the exponent has no digits");
                while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                i = j;
            }

            if (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_')) return Error(start, "malformed number");
            var text = s[start..i];
            // Underflow (1e-400) parses to 0; a non-zero mantissa that yields 0 is out of range, never a silent zero.
            var nonZeroMantissa = text.TakeWhile(ch => ch is not ('e' or 'E')).Any(ch => ch is >= '1' and <= '9');
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !(value == 0m && nonZeroMantissa)
                ? new Token(TokenKind.Number, text, start, value)
                : Error(start, "the number is out of range");
        }

        private static bool HasUnpairedSurrogate(string v)
        {
            for (var k = 0; k < v.Length; k++)
            {
                if (char.IsHighSurrogate(v[k]) && k + 1 < v.Length && char.IsLowSurrogate(v[k + 1])) k++;
                else if (char.IsSurrogate(v[k])) return true;
            }

            return false;
        }

        private static Token ReadString(string s, ref int i)
        {
            var start = i;
            var quote = s[i++];
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == quote)
                {
                    var value = sb.ToString();
                    return HasUnpairedSurrogate(value)
                        ? Error(start, "the string contains an unpaired surrogate")
                        : new Token(TokenKind.String, s[start..i], start, value);
                }

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                var e = s[i++];
                switch (e)
                {
                    case '\'': case '"': case '\\': case '/': sb.Append(e); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !ushort.TryParse(s.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                            || s.AsSpan(i, 4).ContainsAny('+', '-', ' '))
                            return Error(start, "malformed \\u escape in a string");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        return Error(start, $"unknown escape '\\{e}' in a string");
                }
            }

            return Error(start, "unterminated string");
        }
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _connectives;

        private Token Peek(int ahead = 0) => tokens[Math.Min(_index + ahead, tokens.Count - 1)];

        private Token Next() => tokens[Math.Min(_index++, tokens.Count - 1)];

        private XrmdFaultException Unexpected(Token t, string expected)
        {
            if (t.Kind == TokenKind.Error) return Syntax(t.Position, t.Text);
            var found = t.Kind == TokenKind.End ? "the end of the query" : $"'{t.Text}'";
            return Syntax(t.Position, $"expected {expected} but found {found}");
        }

        private Token ExpectKeyword(string keyword)
        {
            var t = Peek();
            if (!t.IsKeyword(keyword)) throw Unexpected(t, keyword);
            return Next();
        }

        private Token ExpectPunct(string punct)
        {
            var t = Peek();
            if (!t.IsPunct(punct)) throw Unexpected(t, $"'{punct}'");
            return Next();
        }

        public CosmosQuery ParseQuery()
        {
            ExpectKeyword("SELECT");
            RefuseSelectModifier();

            var items = new List<(CosmosSelectItem Item, Token Root)>();
            do
            {
                var (path, root) = ParsePath();
                items.Add((new CosmosSelectItem(path, ParseAlias()), root));
            }
            while (Peek().IsPunct(",") && Next() is not null);

            ExpectKeyword("FROM");
            if (Peek().IsPunct("(")) throw Unsupported("a subquery");
            var aliasToken = Peek();
            if (!aliasToken.IsName) throw Unexpected(aliasToken, "the source alias");
            var alias = Next().Text;
            foreach (var (_, root) in items) RequireAlias(root, alias);
            RefuseTrailingClause();

            CosmosExpr? where = null;
            if (Peek().IsKeyword("WHERE"))
            {
                Next();
                where = ParseOr(0, alias);
                RefuseTrailingClause();
            }

            CosmosOrder? order = null;
            if (Peek().IsKeyword("ORDER"))
            {
                Next();
                ExpectKeyword("BY");
                var (path, root) = ParsePath();
                RequireAlias(root, alias);
                var descending = false;
                if (Peek().IsKeyword("DESC")) { Next(); descending = true; }
                else if (Peek().IsKeyword("ASC")) Next();
                if (Peek().IsPunct(",")) throw Unsupported("ORDER BY with more than one item");
                order = new CosmosOrder(path, descending);
                RefuseTrailingClause();
            }

            if (Peek().Kind != TokenKind.End) throw Unexpected(Peek(), "the end of the query");
            return new CosmosQuery(alias, items.Select(i => i.Item).ToList(), where, order);
        }

        private void RefuseSelectModifier()
        {
            var t = Peek();
            if (t.IsPunct("*")) throw Unsupported("SELECT *");
            if (t.IsKeyword("VALUE")) throw Unsupported("SELECT VALUE");
            if (t.IsKeyword("DISTINCT")) throw Unsupported("DISTINCT");
            if (t.IsKeyword("TOP")) throw Unsupported("TOP");
        }

        private void RefuseTrailingClause()
        {
            var t = Peek();
            if (t.IsKeyword("JOIN")) throw Unsupported("JOIN");
            if (t.IsKeyword("GROUP")) throw Unsupported("GROUP BY");
            if (t.IsKeyword("OFFSET")) throw Unsupported("OFFSET LIMIT");
        }

        /// <summary>An alias after a select item: <c>AS name</c>, or a bare name that is followed by ',' or FROM.</summary>
        private string? ParseAlias()
        {
            var t = Peek();
            if (t.IsKeyword("AS"))
            {
                Next();
                var name = Peek();
                if (!name.IsName) throw Unexpected(name, "an alias");
                return Next().Text;
            }

            if (t.IsName && (Peek(1).IsPunct(",") || Peek(1).IsKeyword("FROM"))) return Next().Text;
            return null;
        }

        private static void RequireAlias(Token root, string alias)
        {
            if (root.Text != alias) throw Syntax(root.Position, $"'{root.Text}' is not the source alias '{alias}'");
        }

        private CosmosPath ParsePathOf(string alias)
        {
            var (path, root) = ParsePath();
            RequireAlias(root, alias);
            return path;
        }

        /// <summary>Parses <c>root(.name | ["name"] | [n])*</c>; a function call is refused here with its name.</summary>
        private (CosmosPath Path, Token Root) ParsePath()
        {
            var root = Peek();
            if (root.IsPunct("{") || root.IsPunct("[")) throw Unsupported("an object or array literal");
            if (!root.IsName) throw Unexpected(root, "a property path");

            var j = _index + 1;
            var dotted = new StringBuilder(root.Text);
            while (tokens[j].IsPunct(".") && tokens[j + 1].Kind == TokenKind.Identifier)
            {
                dotted.Append('.').Append(tokens[j + 1].Text);
                j += 2;
            }

            if (tokens[j].IsPunct("("))
            {
                var name = root.Text.Equals("udf", StringComparison.OrdinalIgnoreCase) ? dotted.ToString() : dotted.ToString().ToUpperInvariant();
                throw Unsupported($"function {name}");
            }

            Next();
            var steps = new List<CosmosPathStep>();
            while (true)
            {
                var t = Peek();
                if (t.IsPunct("."))
                {
                    Next();
                    var name = Peek();
                    if (name.Kind != TokenKind.Identifier) throw Unexpected(name, "a property name");
                    Next();
                    steps.Add(new PropertyStep(name.Text));
                }
                else if (t.IsPunct("["))
                {
                    Next();
                    var key = Peek();
                    if (key.Kind == TokenKind.String)
                    {
                        Next();
                        steps.Add(new PropertyStep((string)key.Value!));
                    }
                    else if (key.Kind == TokenKind.Number && key.Text.All(char.IsAsciiDigit) && int.TryParse(key.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        Next();
                        steps.Add(new IndexStep(index));
                    }
                    else if (key.Kind == TokenKind.Number)
                        throw Syntax(key.Position, "an array index must be a non-negative integer literal");
                    else
                        throw Unexpected(key, "a quoted property name or a non-negative integer index");
                    ExpectPunct("]");
                }
                else
                    break;

                if (steps.Count == 1 && steps[0] is not PropertyStep { Name: "props" }) throw Unsupported("a property outside c.props");
            }

            if (steps.Count < 2 || steps[0] is not PropertyStep { Name: "props" }) throw Unsupported("a property outside c.props");
            return (new CosmosPath(steps), root);
        }

        private CosmosExpr ParseOr(int depth, string alias)
        {
            var left = ParseAnd(depth, alias);
            while (Peek().IsKeyword("OR"))
            {
                Next();
                CountConnective();
                left = new Or(left, ParseAnd(depth, alias));
            }

            return left;
        }

        private CosmosExpr ParseAnd(int depth, string alias)
        {
            var left = ParseNot(depth, alias);
            while (Peek().IsKeyword("AND"))
            {
                Next();
                CountConnective();
                left = new And(left, ParseNot(depth, alias));
            }

            return left;
        }

        private void CountConnective()
        {
            if (++_connectives > MaxConnectives)
                throw XrmdFaultException.InvalidArgument($"ExecuteCosmosSqlQuery: the filter has more than {MaxConnectives} AND/OR operators.");
        }

        private static void CheckDepth(int depth)
        {
            if (depth > QueryLimits.MaxFilterDepth)
                throw new XrmdValidationException($"Filters are nested more than {QueryLimits.MaxFilterDepth} levels deep.");
        }

        private CosmosExpr ParseNot(int depth, string alias)
        {
            if (!Peek().IsKeyword("NOT")) return ParsePrimary(depth, alias);
            Next();
            CheckDepth(depth + 1);
            return new Not(ParseNot(depth + 1, alias));
        }

        private CosmosExpr ParsePrimary(int depth, string alias)
        {
            var t = Peek();
            if (t.IsPunct("("))
            {
                Next();
                if (Peek().IsKeyword("SELECT")) throw Unsupported("a subquery");
                CheckDepth(depth + 1);
                var inner = ParseOr(depth + 1, alias);
                ExpectPunct(")");
                return inner;
            }

            if (t.IsKeyword("EXISTS")) throw Unsupported("EXISTS");

            if (t.IsName && t.Text.Equals("IS_DEFINED", StringComparison.OrdinalIgnoreCase) && Peek(1).IsPunct("("))
            {
                Next();
                Next();
                var path = ParsePathOf(alias);
                ExpectPunct(")");
                return new IsDefined(path);
            }

            var start = t;
            var left = ParseOperand(alias);
            RefuseOperatorAfterOperand();
            var opToken = Peek();
            var op = opToken.Kind == TokenKind.Punctuation ? opToken.Text switch
            {
                "=" => CosmosComparisonOp.Equal,
                "!=" or "<>" => CosmosComparisonOp.NotEqual,
                "<" => CosmosComparisonOp.Less,
                "<=" => CosmosComparisonOp.LessEqual,
                ">" => CosmosComparisonOp.Greater,
                ">=" => CosmosComparisonOp.GreaterEqual,
                _ => (CosmosComparisonOp?)null,
            } : null;
            if (op is null) throw Unexpected(opToken, "a comparison operator");
            Next();
            var right = ParseOperand(alias);
            RefuseOperatorAfterOperand();

            if (left is PathOperand && right is PathOperand) throw Unsupported("a comparison between two properties");
            if (left is not PathOperand && right is not PathOperand)
                throw Unsupported("a comparison without a property");
            return new Comparison(left, op.Value, right);
        }

        private CosmosOperand ParseOperand(string alias)
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.String:
                    Next();
                    return new LiteralOperand((string)t.Value!);
                case TokenKind.Number:
                    Next();
                    return new LiteralOperand((decimal)t.Value!);
                case TokenKind.Parameter:
                    Next();
                    return new ParameterOperand(t.Text);
                case TokenKind.Punctuation when t.Text is "-" or "+":
                    {
                        var number = Peek(1);
                        if (number.Kind != TokenKind.Number) throw Unexpected(number, "a number");
                        Next();
                        Next();
                        var value = (decimal)number.Value!;
                        return new LiteralOperand(t.Text == "-" ? -value : value);
                    }
                case TokenKind.Punctuation when t.Text is "{" or "[":
                    throw Unsupported("an object or array literal");
                case TokenKind.Identifier when t.IsKeyword("TRUE"):
                    Next();
                    return new LiteralOperand(true);
                case TokenKind.Identifier when t.IsKeyword("FALSE"):
                    Next();
                    return new LiteralOperand(false);
                case TokenKind.Identifier when t.IsKeyword("NULL"):
                    Next();
                    return new LiteralOperand(null);
                case TokenKind.Identifier when t.IsKeyword("UNDEFINED"):
                    throw Unsupported("undefined");
                case TokenKind.Identifier when t.IsKeyword("EXISTS"):
                    throw Unsupported("EXISTS");
                default:
                    return new PathOperand(ParsePathOf(alias));
            }
        }

        /// <summary>Refuses the operators and keywords that could only continue an expression outside the subset.</summary>
        private void RefuseOperatorAfterOperand()
        {
            var t = Peek();
            if (t.Kind == TokenKind.Punctuation)
            {
                switch (t.Text)
                {
                    case "+" or "-" or "*" or "/" or "%": throw Unsupported("an arithmetic operator");
                    case "||": throw Unsupported("string concatenation (||)");
                    case "??": throw Unsupported("the ?? operator");
                    case "?" or ":": throw Unsupported("the ?: operator");
                }
            }

            var kw = t.IsKeyword("NOT") ? Peek(1) : t;
            if (kw.IsKeyword("IN")) throw Unsupported("IN");
            if (kw.IsKeyword("BETWEEN")) throw Unsupported("BETWEEN");
            if (kw.IsKeyword("LIKE")) throw Unsupported("LIKE");
        }
    }
}
