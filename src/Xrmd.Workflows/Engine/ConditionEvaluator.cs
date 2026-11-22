using Microsoft.Xrm.Sdk;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Engine;

/// <summary>
/// Evaluates the conditions of the step model (W16): the <c>ConditionOperator</c>s of <see cref="ConditionExpr.Compare"/> and the
/// logical And, Or and Not. Operands are compared by kind: numbers (Integer, Decimal, Double, Money, and the value of an option set),
/// dates, text, booleans and record ids (a <see cref="EntityReference"/> by its id). Operands of two kinds, or an operator on a kind it
/// does not take, fail explicitly (400).
/// </summary>
/// <remarks>
/// Rules of the run that are ours until checklist 1g confirms them: text comparisons ignore case (as the database's collation does);
/// an empty text is null; Equal with a null operand is false and NotEqual true; the ordering operators and the text operators are
/// false on a null operand, and DoesNotContain true.
/// </remarks>
public static class ConditionEvaluator
{
    /// <summary>Whether <paramref name="c"/> holds.</summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400: an operator on values it does not take, and the faults of the values.</exception>
    public static async Task<bool> EvaluateAsync(ConditionExpr c, Func<RecordRef, string, CancellationToken, Task<object?>> read, WorkflowRun run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        switch (c)
        {
            case ConditionExpr.And and:
                return await EvaluateAsync(and.Left, read, run, ct) && await EvaluateAsync(and.Right, read, run, ct);
            case ConditionExpr.Or or:
                return await EvaluateAsync(or.Left, read, run, ct) || await EvaluateAsync(or.Right, read, run, ct);
            case ConditionExpr.Not not:
                return !await EvaluateAsync(not.Operand, read, run, ct);
            case ConditionExpr.Compare compare:
            {
                var left = Normalize(compare.Operator, await ExpressionEvaluator.Evaluate(compare.Left, read, run, ct));
                var right = compare.Right is null ? null : Normalize(compare.Operator, await ExpressionEvaluator.Evaluate(compare.Right, read, run, ct));
                return Compare(compare.Operator, left, right, compare.Right is not null);
            }
            default:
                throw ExpressionEvaluator.Fault($"The condition '{c}' cannot be evaluated by xrmverse.");
        }
    }

    private static bool Compare(string op, object? left, object? right, bool hasRight)
    {
        switch (op)
        {
            case "Null":
                return left is null;
            case "NotNull":
                return left is not null;
        }
        if (!hasRight)
            throw ExpressionEvaluator.Fault($"The condition operator '{op}' needs a value to compare with.");

        switch (op)
        {
            case "Equal":
                return left is not null && right is not null && Order(op, left, right, ordered: false) == 0;
            case "NotEqual":
                return left is null || right is null || Order(op, left, right, ordered: false) != 0;
            case "GreaterThan":
                return left is not null && right is not null && Order(op, left, right, ordered: true) > 0;
            case "GreaterEqual":
                return left is not null && right is not null && Order(op, left, right, ordered: true) >= 0;
            case "LessThan":
                return left is not null && right is not null && Order(op, left, right, ordered: true) < 0;
            case "LessEqual":
                return left is not null && right is not null && Order(op, left, right, ordered: true) <= 0;
            case "Contains" or "DoesNotContain" or "BeginsWith" or "EndsWith":
            {
                if (left is not null and not string || right is not null and not string)
                    throw ExpressionEvaluator.Fault(
                        $"The condition operator '{op}' compares text; it was given {Kind(left)} and {Kind(right)}.");
                var holds = left is string l && right is string r && op switch
                {
                    "BeginsWith" => l.StartsWith(r, StringComparison.OrdinalIgnoreCase),
                    "EndsWith" => l.EndsWith(r, StringComparison.OrdinalIgnoreCase),
                    _ => l.Contains(r, StringComparison.OrdinalIgnoreCase),
                };
                return op == "DoesNotContain" ? !holds : holds;
            }
            default:
                throw ExpressionEvaluator.Fault($"The condition operator '{op}' is not supported by xrmverse.");
        }
    }

    /// <summary>The order of two non-null normalized operands of the same kind; dates and numbers only when <paramref name="ordered"/>.</summary>
    private static int Order(string op, object left, object right, bool ordered) => (left, right) switch
    {
        (decimal a, decimal b) => a.CompareTo(b),
        (DateTime a, DateTime b) => a.CompareTo(b),
        (string a, string b) when !ordered => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
        (bool a, bool b) when !ordered => a.CompareTo(b),
        (Guid a, Guid b) when !ordered => a.CompareTo(b),
        _ => throw ExpressionEvaluator.Fault($"The condition operator '{op}' cannot compare {Kind(left)} with {Kind(right)}."),
    };

    /// <summary>An operand as one comparable kind: decimal, DateTime (UTC), string (null when empty), bool or Guid.</summary>
    private static object? Normalize(string op, object? value) => value switch
    {
        null => null,
        string { Length: 0 } => null,
        string s => s,
        int i => (decimal)i,
        long l => (decimal)l,
        decimal d => d,
        double f => (decimal)f,
        Money m => m.Value,
        OptionSetValue o => (decimal)o.Value,
        bool b => b,
        DateTime { Kind: DateTimeKind.Local } t => t.ToUniversalTime(),
        DateTime { Kind: DateTimeKind.Unspecified } t => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        DateTime t => t,
        Guid g => g,
        EntityReference r => r.Id,
        _ => throw ExpressionEvaluator.Fault(
            $"The condition operator '{op}' cannot take a value of type {ExpressionEvaluator.TypeName(value)}."),
    };

    private static string Kind(object? value) => value switch
    {
        null => "an empty value",
        decimal => "a number",
        DateTime => "a date",
        string => "a text",
        bool => "a yes/no value",
        Guid => "a record",
        _ => ExpressionEvaluator.TypeName(value),
    };
}
