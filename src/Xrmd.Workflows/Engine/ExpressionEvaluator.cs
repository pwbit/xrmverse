using System.Globalization;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Engine;

/// <summary>
/// Evaluates the values of the step model (W16) to SDK values: <c>string</c>, <c>int</c>, <c>decimal</c>, <c>double</c>,
/// <c>bool</c>, <c>DateTime</c>, <c>Guid</c>, <see cref="Money"/>, <see cref="OptionSetValue"/>, <see cref="EntityReference"/>,
/// <see cref="EntityCollection"/> or null. Nothing else is evaluated: a value the run cannot treat faithfully fails explicitly
/// with a 400 <see cref="ErrorCodes.InvalidArgument"/> fault naming what it met.
/// </summary>
/// <remarks>
/// Rules of the run that are ours until checklist 1g confirms them: arithmetic with a null operand is null; a date plus or minus a
/// number moves it by that many days; the result of arithmetic keeps the type of its first operand (an Integer column stays an int, a
/// BigInt a long, a Money column a Money), an Integer or BigInt result that is not a whole number fails, and a result out of the range of
/// its type (or a date out of the calendar) fails; concatenation of only null parts is null; concatenation takes text and the name of a
/// reference only (Dataverse formats the other types with the user's settings, which xrmverse does not replicate).
/// </remarks>
public static class ExpressionEvaluator
{
    private const string EntityReferenceType = "Microsoft.Xrm.Sdk.EntityReference";
    private const string EntityCollectionType = "Microsoft.Xrm.Sdk.EntityCollection";

    /// <summary>
    /// The value of <paramref name="e"/>. <paramref name="read"/> returns a column of a record of the run (null when the record or
    /// the column is empty).
    /// </summary>
    /// <exception cref="XrmdFaultException">400: a value of a type the expression cannot take (the message names it), and the faults of
    /// <paramref name="read"/>.</exception>
    public static async Task<object?> Evaluate(ValueExpr e, Func<RecordRef, string, CancellationToken, Task<object?>> read, WorkflowRun run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(run);
        switch (e)
        {
            case ValueExpr.Literal literal:
                return Literal(literal);
            case ValueExpr.AttributeOf attribute:
            {
                var value = await read(attribute.Record, attribute.Attribute, ct);
                if (attribute.TargetType is { } target && value is not null && TypeName(value) != target)
                    throw Fault($"The workflow reads the column '{attribute.Attribute}' as {target}, but its value is of type {TypeName(value)}. " +
                                "xrmverse converts no other type.");
                return value;
            }
            case ValueExpr.Conversion conversion:
                return Convert(conversion, await Evaluate(conversion.Operand, read, run, ct));
            case ValueExpr.Arithmetic arithmetic:
            {
                var operands = new List<object?>(arithmetic.Operands.Count);
                foreach (var operand in arithmetic.Operands)
                    operands.Add(await Evaluate(operand, read, run, ct));
                return Arithmetic(arithmetic.Operator, operands);
            }
            case ValueExpr.Concat concat:
            {
                // Only null parts: null (a create leaves the column out), not an empty text.
                var text = new System.Text.StringBuilder();
                var any = false;
                foreach (var part in concat.Parts)
                    if (await Evaluate(part, read, run, ct) is { } value)
                    {
                        any = true;
                        text.Append(Text(value));
                    }
                return any ? text.ToString() : null;
            }
            case ValueExpr.FirstNonNull first:
                foreach (var operand in first.Operands)
                    if (await Evaluate(operand, read, run, ct) is { } value)
                        return value;
                return null;
            case ValueExpr.ExecutionTime:
                return run.Now;
            case ValueExpr.VariableRef variable:
                return run.Variables.TryGetValue(variable.Name, out var bound) ? bound : null;
            case ValueExpr.ActionOutput output:
                return run.Variables.TryGetValue(output.Variable, out var outputs) && outputs is IReadOnlyDictionary<string, object?> results
                    ? results.GetValueOrDefault(output.Output)
                    : throw Fault($"The output '{output.Output}' of the action step '{output.Variable}' is read before that step ran.");
            case ValueExpr.Cast cast:
                return Cast(cast.ClrType, await Evaluate(cast.Operand, read, run, ct));
            default:
                throw Fault($"The value '{e}' cannot be evaluated by xrmverse.");
        }
    }

    /// <summary>The 400 fault of a step value or condition xrmverse cannot evaluate faithfully.</summary>
    internal static XrmdFaultException Fault(string message) => XrmdFaultException.InvalidArgument(message);

    /// <summary>The full CLR type name of a value, as a <c>TargetType</c> or <c>DirectCast</c> names it.</summary>
    internal static string TypeName(object value) => value.GetType().FullName ?? value.GetType().Name;

    private static object? Literal(ValueExpr.Literal literal) => (literal.Type, literal.Value) switch
    {
        (_, null) => null,
        ("Money", decimal money) => new Money(money),
        ("OptionSetValue", int option) => new OptionSetValue(option),
        ("EntityReference", EntityRefValue reference) => new EntityReference(reference.LogicalName, reference.Id),
        ("String" or "Integer" or "Decimal" or "Double" or "Boolean" or "DateTime" or "Guid", var value) => value,
        _ => throw Fault($"The literal of type {literal.Type} ({literal.Value}) cannot be evaluated by xrmverse."),
    };

    /// <summary>The two conversions a <c>TargetType</c> asks for (WorkflowDefinition, <see cref="ValueExpr.Conversion"/>).</summary>
    private static object? Convert(ValueExpr.Conversion conversion, object? value)
    {
        if (value is null) return null;
        switch (conversion.From, conversion.To)
        {
            case ("System.Guid", EntityReferenceType) when conversion.Entity is { Length: > 0 } entity:
                return value is Guid id ? new EntityReference(entity, id) : throw ConversionFault(conversion, value);
            case (EntityReferenceType, EntityCollectionType):
                return value is EntityReference party
                    ? new EntityCollection([new Entity(ActivitySchema.Party) { [ActivitySchema.PartyId] = party }])
                    : throw ConversionFault(conversion, value);
            default:
                throw Fault($"Converting {conversion.From} to {conversion.To} is not supported by xrmverse.");
        }
    }

    private static XrmdFaultException ConversionFault(ValueExpr.Conversion conversion, object value) =>
        Fault($"The workflow converts a {conversion.From} to {conversion.To}, but the value is of type {TypeName(value)}.");

    /// <summary><c>DirectCast(value, type)</c>: the value unchanged, of that type; a null value type is its default (as in Visual Basic).</summary>
    private static object? Cast(string clrType, object? value)
    {
        if (value is null)
            return clrType switch
            {
                "System.Decimal" => 0m,
                "System.Int32" => 0,
                "System.Double" => 0d,
                "System.Boolean" => false,
                "System.DateTime" => default(DateTime),
                "System.Guid" => Guid.Empty,
                "System.String" or EntityReferenceType or EntityCollectionType or "Microsoft.Xrm.Sdk.Money" or "Microsoft.Xrm.Sdk.OptionSetValue" => null,
                _ => throw Fault($"The workflow casts an empty value to {clrType}, whose default xrmverse does not know."),
            };
        return TypeName(value) == clrType
            ? value
            : throw Fault($"The workflow casts a value of type {TypeName(value)} to {clrType}: xrmverse converts no type.");
    }

    // ---- arithmetic -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Add</c> and <c>Increment</c> add the operands to the first, <c>Subtract</c> and <c>Decrement</c> take them from it,
    /// <c>Multiply</c> multiplies it by them, <c>Set</c> is its one operand. A null operand makes the result null.
    /// </summary>
    private static object? Arithmetic(string op, IReadOnlyList<object?> operands)
    {
        try
        {
            return Compute(op, operands);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            throw Fault($"The result of {op} is out of range of its type ({TypeName(operands.First(o => o is not null)!)}).");
        }
    }

    private static object? Compute(string op, IReadOnlyList<object?> operands)
    {
        if (op == "Set")
            return operands.Count == 1 ? operands[0] : throw Fault($"Set takes one value, not {operands.Count}.");
        if (op is not ("Add" or "Increment" or "Subtract" or "Decrement" or "Multiply"))
            throw Fault($"The arithmetic operator '{op}' is not supported by xrmverse.");
        if (operands.Count == 0 || (op is "Increment" or "Decrement" && operands.Count < 2))
            throw Fault($"{op} needs {(op is "Increment" or "Decrement" ? "a value and the amount to apply" : "a value")}.");
        if (operands.Any(o => o is null))
            return null;

        var first = operands[0]!;
        if (first is DateTime date)
        {
            if (op == "Multiply")
                throw Fault("Multiplying a date is not supported.");
            var sign = op is "Subtract" or "Decrement" ? -1 : 1;
            foreach (var operand in operands.Skip(1))
                date = date.AddDays(sign * (double)Number(op, operand!));
            return date;
        }

        if (first is double d)
        {
            foreach (var operand in operands.Skip(1))
                d = Apply(op, d, (double)Number(op, operand!));
            return double.IsFinite(d) ? d : throw new OverflowException();
        }

        var result = Number(op, first);
        foreach (var operand in operands.Skip(1))
            result = Apply(op, result, Number(op, operand!));
        return first switch
        {
            Money => new Money(result),
            long when result == decimal.Truncate(result) => (long)result,
            long => throw Fault($"The result {result.ToString(CultureInfo.InvariantCulture)} of {op} is not a whole System.Int64."),
            int when result == decimal.Truncate(result) && result is >= int.MinValue and <= int.MaxValue => (int)result,
            int => throw Fault($"The result {result.ToString(CultureInfo.InvariantCulture)} of {op} is not a whole System.Int32."),
            _ => result,
        };
    }

    private static decimal Apply(string op, decimal a, decimal b) => op switch
    {
        "Subtract" or "Decrement" => a - b,
        "Multiply" => a * b,
        _ => a + b,
    };

    private static double Apply(string op, double a, double b) => op switch
    {
        "Subtract" or "Decrement" => a - b,
        "Multiply" => a * b,
        _ => a + b,
    };

    private static decimal Number(string op, object value) => value switch
    {
        int i => i,
        long l => l,
        decimal m => m,
        double d => (decimal)d,
        Money money => money.Value,
        _ => throw Fault($"{op} takes numbers and dates; a value of type {TypeName(value)} is not supported."),
    };

    // ---- text -------------------------------------------------------------------------------------------------------------

    private static string Text(object? value) => value switch
    {
        null => "",
        string s => s,
        EntityReference { Name: { } name } => name,
        EntityReference reference => throw Fault($"The name of the {reference.LogicalName} record {reference.Id} is not known, so it cannot be put in a text."),
        _ => throw Fault($"Putting a value of type {TypeName(value)} in a text is not supported by xrmverse: only text and the name of a record are."),
    };
}
