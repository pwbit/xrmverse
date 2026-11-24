using System.Globalization;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Metadata;
using Xrmd.Query.Sdk;

namespace Xrmd.Query.Sql;

/// <summary>
/// The definition-time check of the SQL of a formula column (plan 2.12c, final review C1, I1, N1): the column is compiled as a
/// query reading it would compile it, and refused in our words when its SQL would be longer than 300,000 characters, hold more
/// than 250 applies (intermediate values) or nest <c>CASE</c> more than 10 deep, rather than failing every query that reads it
/// (out of memory, minutes of compilation then SQL Server error 8621, error 125). The column and the computed columns it chains
/// are counted together, as a query reading it counts them.
/// </summary>
public static partial class FormulaSqlCheck
{
    /// <param name="entity">The table of the column (with the column, as it is being defined).</param>
    /// <param name="attribute">The formula column.</param>
    /// <param name="entities">Finds a table by logical name (the table being changed included).</param>
    /// <param name="computed">Translates the column and the computed columns it reads.</param>
    /// <param name="dialect">The SQL the column is compiled in (plan 2.7a).</param>
    /// <exception cref="XrmdValidationException">The SQL of the column is too large, holds too many applies or nests too deeply.</exception>
    /// <remarks>Each table is passed to <paramref name="computed"/> as a fresh SDK conversion, once per call: the tables may still change.</remarks>
    public static void Check(TableDefinition entity, ColumnDefinition attribute, Func<string, TableDefinition?> entities, IComputedColumns computed,
        ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(computed);
        Run(entity, attribute, entities, SdkQueryCallbacks.Fresh(computed, entities), dialect);
    }

    /// <summary><see cref="Check(TableDefinition, ColumnDefinition, Func{string, TableDefinition?}, IComputedColumns, ISqlDialect)"/> with a translation of the computed columns.</summary>
    private static void Run(TableDefinition entity, ColumnDefinition attribute, Func<string, TableDefinition?> entities, ComputedTranslation translate,
        ISqlDialect dialect)
    {
        const string row = "t0";
        var sql = new ComputedSql(new QueryParameters(), entities, translate, dialect);
        var value = sql.Expression(row, entity, attribute, translate(entity, attribute), 1);
        var applies = sql.Applies(row);
        var where = $"The formula column '{entity.LogicalName}.{attribute.LogicalName}' is too complex to compute: its SQL would";
        if (value.Length + applies.Length > ComputedSql.MaxSqlLength)
            throw new XrmdValidationException(
                $"{where} exceed {ComputedSql.MaxSqlLength.ToString("N0", CultureInfo.InvariantCulture)} characters; simplify the formula.");
        if (Math.Max(ComputedSql.CaseNesting(value), ComputedSql.CaseNesting(applies)) > ComputedSql.MaxCaseNesting)
            throw new XrmdValidationException($"{where} nest conditions more than {ComputedSql.MaxCaseNesting} deep; simplify the formula.");
    }
}
