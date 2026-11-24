using System.Text;
using Xrmd.Model.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// Generates DDL for the <c>data</c> schema in the SQL of a dialect (plan 2.7a, Task 2). Every identifier is validated with
/// <see cref="LogicalNameValidator"/> before being quoted, so no caller-controlled text reaches the SQL unchecked.
/// </summary>
public static partial class DdlGenerator
{
    public const string DataSchema = "data";

    /// <summary>
    /// <c>CREATE TABLE [data].[logicalname] (...)</c>; the attribute list is first completed with the primary key,
    /// system columns and money <c>_base</c> attributes (see <see cref="SystemAttributes.Complete"/>). On PostgreSQL the
    /// statement is followed by the <c>versionnumber</c> trigger (<see cref="ISqlDialect.RowVersionTrigger"/>, plan 2.7a, ruling P4),
    /// and the constraint name is cut to the identifier length (<see cref="ISqlDialect.ObjectName"/>).
    /// </summary>
    public static string CreateTable(TableDefinition e, IEnumerable<ColumnDefinition> attrs, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(e.LogicalName, "entity");
        // Fix round 1 (review I1): a name the engine would cut (PostgreSQL: 63 bytes) is refused before any DDL runs.
        d.EnsureIdentifier(e.LogicalName!, "table");
        var primaryId = SystemAttributes.PrimaryIdName(e);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        var rowVersion = false;

        foreach (var attribute in SystemAttributes.Complete(e, attrs))
        {
            LogicalNameValidator.EnsureValid(attribute.LogicalName, "attribute");
            foreach (var column in TypeMapping.ToColumns(attribute))
            {
                if (!seen.Add(column.Name))
                    throw new XrmdValidationException($"Column '{column.Name}' is defined twice on entity '{e.LogicalName}'.");
                d.EnsureIdentifier(column.Name, "column");
                rowVersion |= column.IsRowVersion;
                lines.Add(column.Name == primaryId
                    ? $"{d.Quote(column.Name)} {column.SqlType(d)} NOT NULL CONSTRAINT {d.Quote(d.ObjectName("PK_" + e.LogicalName))} PRIMARY KEY"
                    : ColumnSql(column, d));
            }
        }

        var sql = new StringBuilder()
            .Append("CREATE TABLE ").Append(Table(e.LogicalName!, d)).Append(" (\n    ")
            .AppendJoin(",\n    ", lines)
            .Append("\n);");
        // Plan 2.7a, ruling P4: on PostgreSQL a trigger sets versionnumber (nothing on SQL Server, where it is a rowversion).
        if (rowVersion && d.RowVersionTrigger(DataSchema, e.LogicalName!) is { Length: > 0 } trigger) sql.Append('\n').Append(trigger);
        return sql.ToString();
    }

    /// <summary>
    /// The table of a many-to-many intersect entity: <see cref="CreateTable"/>, then a unique index on the pair of columns
    /// <paramref name="first"/>, <paramref name="second"/> (one row per associated pair).
    /// </summary>
    public static string CreateIntersectTable(TableDefinition e, IEnumerable<ColumnDefinition> attrs, string first, string second, ISqlDialect d)
    {
        LogicalNameValidator.EnsureValid(first, "attribute");
        LogicalNameValidator.EnsureValid(second, "attribute");
        return CreateTable(e, attrs, d)
               + $"\nCREATE UNIQUE INDEX {d.Quote(d.ObjectName($"UQ_{e.LogicalName}_pair"))} ON {Table(e.LogicalName!, d)} ({d.Quote(first)}, {d.Quote(second)});";
    }

    /// <summary><c>ALTER TABLE [data].[entity] ADD ...</c> with all the attribute's columns.</summary>
    public static string AddColumn(string entityLogicalName, ColumnDefinition a, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");

        var columns = TypeMapping.ToColumns(a);
        if (columns.Count == 0)
            throw new XrmdValidationException($"Attribute '{a.LogicalName}' is stored by another attribute and has no column of its own.");

        // Fix round 1 (review I1): every column, the derived ones (<attr>_base, <attr>type) included.
        foreach (var c in columns) d.EnsureIdentifier(c.Name, "column");
        return d.AddColumns(Table(entityLogicalName, d), columns.Select(c => ColumnSql(c, d)).ToList());
    }

    /// <summary>
    /// <c>ALTER TABLE [data].[entity] DROP COLUMN ...</c> with all the attribute's columns (money <c>_base</c>,
    /// polymorphic lookup <c>type</c> included).
    /// </summary>
    public static string DropColumn(string entityLogicalName, ColumnDefinition a, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");

        var columns = TypeMapping.ToColumns(a);
        if (columns.Count == 0)
            throw new XrmdValidationException($"Attribute '{a.LogicalName}' is stored by another attribute and has no column of its own.");

        return d.DropColumns(Table(entityLogicalName, d), columns.Select(c => d.Quote(c.Name)).ToList());
    }

    /// <summary>
    /// <c>ALTER TABLE [data].[entity] ALTER COLUMN ...</c> giving the attribute's own column the type
    /// <see cref="TypeMapping.ToSql"/> maps it to now (e.g. a String whose MaxLength grew).
    /// </summary>
    public static string AlterColumn(string entityLogicalName, ColumnDefinition a, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        LogicalNameValidator.EnsureValid(a.LogicalName, "attribute");
        return d.AlterColumnType(Table(entityLogicalName, d), d.Quote(a.LogicalName!), TypeMapping.ToSql(a, d));
    }

    /// <summary>
    /// <c>CREATE [UNIQUE] INDEX</c> <paramref name="index"/> (its <see cref="ISqlDialect.ObjectName"/>) on the data table
    /// <paramref name="entityLogicalName"/>, without a terminator (for <see cref="ISqlDialect.When"/>; plan 2.7a, Task 4):
    /// <paramref name="columns"/> and <paramref name="filter"/> are SQL as the caller writes them.
    /// </summary>
    public static string CreateIndex(string entityLogicalName, string index, string columns, ISqlDialect d, bool unique = false, string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        return $"CREATE {(unique ? "UNIQUE " : "")}INDEX {d.Quote(d.ObjectName(index))} ON {Table(entityLogicalName, d)} ({columns})" +
               (filter is null ? "" : " WHERE " + filter);
    }

    /// <summary>
    /// The column list of a unique (alternate) key for <see cref="ISqlDialect.CreateIndexIfMissing"/> or <see cref="CreateIndex"/>
    /// (plan 2.7b, ruling Q1, #299): each column quoted, a text column (<paramref name="columns"/>' <c>Text</c>) as its text key
    /// (<see cref="ISqlDialect.TextKey"/>), so that the key refuses values that differ only by case, trailing spaces, width or kana
    /// type, as SQL Server's <c>_CI_AS</c> does. On SQL Server the quoted columns themselves.
    /// </summary>
    public static string KeyColumns(ISqlDialect d, IEnumerable<(string Column, bool Text)> columns)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(columns);
        return string.Join(", ", columns.Select(c =>
        {
            LogicalNameValidator.EnsureValid(c.Column, "attribute");
            return c.Text ? d.TextKey(d.Quote(c.Column)) : d.Quote(c.Column);
        }));
    }

    /// <summary><c>DROP TABLE [data].[entity]</c>.</summary>
    public static string DropTable(string entityLogicalName, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        return $"DROP TABLE {Table(entityLogicalName, d)};";
    }

    /// <summary>The quoted, schema-qualified name of the data table of <paramref name="entityLogicalName"/>: <c>[data].[entity]</c>.</summary>
    public static string Table(string entityLogicalName, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return $"{d.Quote(DataSchema)}.{d.Quote(entityLogicalName)}";
    }

    private static string ColumnSql(SqlColumn c, ISqlDialect d) =>
        c.IsRowVersion ? d.RowVersionColumn(c.Name) : $"{d.Quote(c.Name)} {c.SqlType(d)} NULL";
}
