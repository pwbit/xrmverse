namespace Xrmd.Licenses;

/// <summary>
/// A parsed SPDX license expression: <c>id</c>, <c>id WITH exception</c>, <c>a AND b</c>, <c>a OR b</c>, with parentheses.
/// AND binds tighter than OR. Operators are accepted in any case.
/// </summary>
public abstract record SpdxExpression
{
    public sealed record License(string Id, string? Exception) : SpdxExpression;

    public sealed record And(SpdxExpression Left, SpdxExpression Right) : SpdxExpression;

    public sealed record Or(SpdxExpression Left, SpdxExpression Right) : SpdxExpression;

    /// <summary>All license leaves of the expression, left to right.</summary>
    public IEnumerable<License> Licenses()
    {
        switch (this)
        {
            case License leaf:
                yield return leaf;
                break;
            case And and:
                foreach (var l in and.Left.Licenses().Concat(and.Right.Licenses())) yield return l;
                break;
            case Or or:
                foreach (var l in or.Left.Licenses().Concat(or.Right.Licenses())) yield return l;
                break;
        }
    }

    /// <summary>Parses an expression; returns null when it is not a valid SPDX expression.</summary>
    public static SpdxExpression? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tokens = Tokenize(text);
        if (tokens is null)
        {
            return null;
        }

        var position = 0;
        var result = ParseOr(tokens, ref position);
        return result is not null && position == tokens.Count ? result : null;
    }

    private static List<string>? Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c is '(' or ')')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (IsIdChar(c))
            {
                var start = i;
                while (i < text.Length && IsIdChar(text[i])) i++;
                tokens.Add(text[start..i]);
            }
            else
            {
                return null;
            }
        }

        return tokens;
    }

    private static bool IsIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or ':';

    private static bool IsOperator(string token, string op) => string.Equals(token, op, StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyword(string token) =>
        IsOperator(token, "AND") || IsOperator(token, "OR") || IsOperator(token, "WITH") || token is "(" or ")";

    private static SpdxExpression? ParseOr(List<string> tokens, ref int position)
    {
        var left = ParseAnd(tokens, ref position);
        while (left is not null && position < tokens.Count && IsOperator(tokens[position], "OR"))
        {
            position++;
            var right = ParseAnd(tokens, ref position);
            left = right is null ? null : new Or(left, right);
        }

        return left;
    }

    private static SpdxExpression? ParseAnd(List<string> tokens, ref int position)
    {
        var left = ParsePrimary(tokens, ref position);
        while (left is not null && position < tokens.Count && IsOperator(tokens[position], "AND"))
        {
            position++;
            var right = ParsePrimary(tokens, ref position);
            left = right is null ? null : new And(left, right);
        }

        return left;
    }

    private static SpdxExpression? ParsePrimary(List<string> tokens, ref int position)
    {
        if (position >= tokens.Count)
        {
            return null;
        }

        if (tokens[position] == "(")
        {
            position++;
            var inner = ParseOr(tokens, ref position);
            if (inner is null || position >= tokens.Count || tokens[position] != ")")
            {
                return null;
            }

            position++;
            return inner;
        }

        var id = tokens[position];
        if (IsKeyword(id))
        {
            return null;
        }

        position++;
        if (position < tokens.Count && IsOperator(tokens[position], "WITH"))
        {
            position++;
            if (position >= tokens.Count || IsKeyword(tokens[position]))
            {
                return null;
            }

            return new License(id, tokens[position++]);
        }

        return new License(id, null);
    }
}
