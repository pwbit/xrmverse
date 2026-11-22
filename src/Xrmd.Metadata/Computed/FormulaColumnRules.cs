using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.Metadata.Computed;

/// <summary>
/// The metadata rules of formula columns (<c>SourceType</c> 3, Power Fx, plan 2.12c Global Constraints and ruling F6). The column
/// type, the formula and its result type are checked by the translator (<see cref="IComputedColumns.Translate"/>); these rules add
/// what the formula alone does not say: the formats, the limits and the precision of the column, the behaviour of a date result,
/// and the read-only flags of a column the server computes.
/// </summary>
public static class FormulaColumnRules
{
    /// <summary>The precision a Decimal formula column takes when none is given (Learn: 0 to 10, default 2).</summary>
    public const int DefaultPrecision = 2;

    /// <summary>The most decimal places of a Decimal formula column.</summary>
    public const int MaxPrecision = 10;

    /// <summary>
    /// The highest constant instance of <c>Substitute</c>: the translator refuses a higher one, the SQL finds the instance-th match
    /// by one <c>CHARINDEX</c> per match before it.
    /// </summary>
    public const int MaxSubstituteInstance = 100;

    /// <summary>
    /// Checks the formula column <paramref name="a"/> of <paramref name="entity"/>, which is changed in place: a formula starting with
    /// <c>=</c> is refused (Dataverse's Web API refuses it; Q1 finding 8) and it is stored trimmed (F2), a Decimal column without a precision gets 2, the column is made read-only and not required,
    /// and its <c>SourceTypeMask</c> is set. <paramref name="current"/> is the stored column on an update: the translator then refuses
    /// a formula whose result type is not the column's (Learn; the type of a column never changes).
    /// </summary>
    /// <exception cref="XrmdValidationException">The definition is refused.</exception>
    public static void Validate(EntityMetadata entity, AttributeMetadata a, AttributeMetadata? current, Func<string, EntityMetadata?> entities,
        IComputedColumns? computed)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(entities);
        _ = current; // the rules are the same on create and on update: the translator compares the result with the column type.
        var where = $"'{entity.LogicalName}.{a.LogicalName}'";

        if (computed is null) throw new XrmdValidationException("Formula columns are not available on this server.");
        // A lookup reads its target's name from the stored primary name column (as for a calculated one, 2.12a final review I3).
        if (a.LogicalName == entity.PrimaryNameAttribute)
            throw new XrmdValidationException($"The primary name column of a table cannot be a formula column: {where}.");
        if (ComputedColumnRules.FormulaOf(a) is { } formula)
        {
            // Dataverse's Web API refuses a FormulaDefinition that starts with '=' (Q1 2026-11-19, finding 8); only a solution's
            // formula file writes it, and the import reads it without (FormulaFiles).
            if (formula.TrimStart().StartsWith('='))
                throw new XrmdValidationException($"The formula column {where} has a formula that starts with '=': give the formula " +
                    "without it (only a solution's formula file writes it with '=').");
            ComputedColumnRules.SetFormula(a, Normalize(formula));
        }

        // The column type, the functions, the result type (F6) and the refusals of F7 are the translator's; its definition-time
        // check also compiles the formula's SQL, refused when too large (final review C1).
        var definition = computed.Check(entity, a, entities);

        switch (a)
        {
            case StringAttributeMetadata s when s.Format is { } f && f != StringFormat.Text
                                                || s.FormatName is { Value: { } name } && name != StringFormatName.Text.Value:
                throw new XrmdValidationException($"A formula column of type String takes the format Text only: {where}.");
            case IntegerAttributeMetadata { Format: { } format } when format != IntegerFormat.None:
                throw new XrmdValidationException($"A formula column of type Integer takes the format None only: {where}.");
            case DecimalAttributeMetadata d:
                d.Precision ??= DefaultPrecision;
                if (d.Precision is < 0 or > MaxPrecision)
                    throw new XrmdValidationException(
                        $"The precision of the formula column {where} is {d.Precision}: a formula column takes 0 to {MaxPrecision}.");
                break;
            case DateTimeAttributeMetadata date:
                DemandBehavior(date, definition.DateBehavior, where);
                break;
        }
        if (HasLimits(a))
            throw new XrmdValidationException($"MinValue and MaxValue cannot be set on a formula column: {where}.");

        // A formula column is computed by the server: it is never written, and it cannot be required (Global Constraints).
        a.IsValidForCreate = false;
        a.IsValidForUpdate = false;
        a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        SdkProperty.Set(a, "SourceTypeMask", definition.SourceTypeMask);
    }

    /// <summary>A formula as stored: without surrounding whitespace and without one leading <c>=</c> (ruling F2).</summary>
    public static string Normalize(string formula)
    {
        ArgumentNullException.ThrowIfNull(formula);
        var text = formula.Trim();
        return text.StartsWith('=') ? text[1..].Trim() : text;
    }

    /// <summary>
    /// True when <paramref name="body"/> reads the current time: <c>Now()</c> and <c>UTCNow()</c> (<see cref="ComputedExpr.Now"/>),
    /// <c>UTCToday()</c> and <c>IsUTCToday()</c>. A rollup cannot aggregate such a formula column (Learn, ruling F9).
    /// </summary>
    public static bool ReadsCurrentTime(ComputedExpr body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Expr(body);

        static bool Expr(ComputedExpr? e) => e switch
        {
            ComputedExpr.Now => true,
            ComputedExpr.Function f => f.Name is "fx.UTCToday" or "fx.IsUTCToday" || f.Args.Any(Expr),
            ComputedExpr.Binary b => Expr(b.Left) || Expr(b.Right),
            ComputedExpr.Conditional c => c.Branches.Any(x => Condition(x.When) || Expr(x.Then)) || Expr(c.Otherwise),
            _ => false,
        };

        static bool Condition(ComputedCondition c) => c switch
        {
            ComputedCondition.Compare compare => Expr(compare.Left) || Expr(compare.Right),
            ComputedCondition.And and => Condition(and.Left) || Condition(and.Right),
            ComputedCondition.Or or => Condition(or.Left) || Condition(or.Right),
            ComputedCondition.Not not => Condition(not.Operand),
            _ => false,
        };
    }

    /// <summary>
    /// A date result fits the column's behaviour (controller ruling, Learn): a User local date (or <c>Now()</c>) needs a User local
    /// column; a Date only date (or <c>UTCToday()</c>) a Date only or Time zone independent column; a Time zone independent date
    /// (or <c>UTCNow()</c>) a Time zone independent column. A result without a behaviour (null) is not checked.
    /// </summary>
    private static void DemandBehavior(DateTimeAttributeMetadata column, string? result, string where)
    {
        if (result is null) return; // a result with no date behaviour (Blank()): nothing to compare
        var behavior = RollupColumnRules.BehaviorOf(column);
        var fits = result == DateTimeBehavior.UserLocal.Value ? behavior == DateTimeBehavior.UserLocal.Value
            : result == DateTimeBehavior.DateOnly.Value ? behavior != DateTimeBehavior.UserLocal.Value
            : behavior == DateTimeBehavior.TimeZoneIndependent.Value;
        if (!fits)
            throw new XrmdValidationException($"The formula of {where} gives a {result} date, but the column is {behavior}.");
    }

    /// <summary>A range narrower than the type's widest one (as for calculated columns; Float's is ±100,000,000,000).</summary>
    private static bool HasLimits(AttributeMetadata a) => a switch
    {
        DoubleAttributeMetadata d => d.MinValue is not null && d.MinValue != -WidestDouble || d.MaxValue is not null && d.MaxValue != WidestDouble,
        _ => ComputedColumnRules.HasLimits(a),
    };

    private const double WidestDouble = 100000000000d;
}
