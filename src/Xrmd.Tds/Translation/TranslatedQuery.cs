using Xrmd.Query.Model;

namespace Xrmd.Tds.Translation;

/// <summary>What a SQL batch asks for: a <see cref="SystemQuery"/>, a <see cref="NoOp"/> or a <see cref="TranslatedQuery"/>.</summary>
public abstract record TranslatedBatch;

/// <summary>
/// A batch of <c>SET</c> options only (ruling T6: accepted and ignored), or an empty batch. It returns no result set.
/// </summary>
public sealed record NoOp : TranslatedBatch
{
    public static NoOp Instance { get; } = new();
}

/// <summary>The system queries the endpoint answers with canned values (ruling T9).</summary>
public enum SystemQueryKind
{
    /// <summary><c>SELECT @@VERSION</c>.</summary>
    Version,

    /// <summary><c>SELECT SERVERPROPERTY('…')</c>; the argument is one of <see cref="SqlTranslator.ServerProperties"/>.</summary>
    ServerProperty,

    /// <summary><c>SELECT DB_NAME()</c>.</summary>
    DatabaseName,

    /// <summary><c>SELECT @@SPID</c>.</summary>
    Spid,

    /// <summary><c>INFORMATION_SCHEMA.TABLES</c>; the argument is the <c>TABLE_NAME</c> filter, if any.</summary>
    Tables,

    /// <summary><c>INFORMATION_SCHEMA.COLUMNS</c>; the argument is the <c>TABLE_NAME</c> filter, if any.</summary>
    Columns,
}

/// <summary>
/// A system query (ruling T9).
/// </summary>
/// <param name="Argument">The <c>SERVERPROPERTY</c> name in its canonical case, or the <c>TABLE_NAME</c> filter of an
/// <c>INFORMATION_SCHEMA</c> view (null: every table).</param>
/// <param name="Columns">The result columns. For the scalar kinds, one name: the alias, or an empty string (SQL Server returns
/// such a column without a name). For the <c>INFORMATION_SCHEMA</c> views, the selected view columns in their canonical
/// case (see <see cref="SqlTranslator.InformationSchemaTablesColumns"/> and
/// <see cref="SqlTranslator.InformationSchemaColumnsColumns"/>), or null for <c>*</c> (every column of the view).</param>
public sealed record SystemQuery(SystemQueryKind Kind, string? Argument, IReadOnlyList<string>? Columns = null) : TranslatedBatch;

/// <summary>
/// A <c>SELECT</c> translated to the query model. <see cref="Columns"/> are the result set's columns in select-list order.
/// </summary>
public sealed record TranslatedQuery(QueryModel Model, IReadOnlyList<OutputColumn> Columns) : TranslatedBatch;

/// <summary>What a result column holds (ruling T7).</summary>
public enum OutputValue
{
    /// <summary>The attribute's value (a lookup's GUID, a choice's integer).</summary>
    Raw,

    /// <summary><c>&lt;lookup&gt;name</c>: the primary name of the record the lookup points to.</summary>
    Name,

    /// <summary><c>&lt;choice&gt;name</c>: the label of the choice, state or status value in the user's language.</summary>
    Label,

    /// <summary>A column of an aggregate query: an aggregate or a group-by value.</summary>
    Aggregate,
}

/// <summary>A result column of a <see cref="TranslatedQuery"/>.</summary>
/// <param name="Name">The column name the client sees: the alias, or the column name as written in the select list (in its
/// logical case for a column, an empty string for an aggregate without alias, as SQL Server does).</param>
/// <param name="EntityAlias">The table the column comes from, by its exposed name in the query (the SQL alias, or the table's
/// logical name when it has none). Every link of the model has that name as <see cref="LinkNode.Alias"/>; the root has no
/// model alias, and its exposed name is the one no link has.</param>
/// <param name="Attribute">The logical name of the attribute (for <see cref="OutputValue.Name"/> and
/// <see cref="OutputValue.Label"/>, the lookup or choice attribute, without the <c>name</c> suffix). For
/// <see cref="OutputValue.Aggregate"/>, the alias of the model's <see cref="ColumnNode"/>, which names the result value.</param>
/// <param name="FromStar">The column comes from the expansion of a <c>*</c>: the query handler leaves it out when the caller
/// cannot read it (a secured column, ruling T7), where a column named in the select list comes back null.</param>
public sealed record OutputColumn(string Name, string EntityAlias, string Attribute, OutputValue Value, bool FromStar = false);
