using System.Data.Common;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Query.Sql;

/// <summary>What a result column carries.</summary>
public enum ResultKind
{
    /// <summary>The attribute's own column (for lookups: the id).</summary>
    Value,
    /// <summary>Object type code of an Owner/Customer lookup (<c>{name}type</c>).</summary>
    LookupType,
    /// <summary>Primary name of the lookup target.</summary>
    LookupName,
    Aggregate,
    TotalCount,
    /// <summary>
    /// The primary key of a link-entity scope that selects an unreadable secured column (<see cref="ResultColumn.Unreadable"/>):
    /// the record the field shares are looked up for; not a value of the result.
    /// </summary>
    RecordId,
    /// <summary>
    /// The <c>objectid</c> or <c>objecttypecode</c> of an <c>audit</c> row whose unreadable <c>changedata</c> is selected (same
    /// result name): the audited record and table, whose unreadable secured columns the executor removes from the change data.
    /// </summary>
    AuditTarget,
    /// <summary>
    /// The <c>transactioncurrencyid</c> of a scope that selects a money value in its record's currency (plan 2.10, ruling X11): the
    /// currency the executor formats that scope's money with; not a value of the result. None in an aggregate or distinct query.
    /// </summary>
    Currency,
}

/// <summary>A column of the compiled SELECT list.</summary>
/// <param name="SqlAlias">Alias in the SQL text (<c>c0..cn</c>).</param>
/// <param name="EntityAlias">Link-entity alias; null for the root entity.</param>
/// <param name="EntityName">Logical name of the entity the column belongs to.</param>
/// <param name="Attribute">The attribute; null for computed columns (total count).</param>
/// <param name="ResultName">Name in the result: <c>pb_name</c>, <c>u.fullname</c> or the column/aggregate alias.</param>
/// <param name="Unreadable">A secured column the caller cannot read through its profiles
/// (<see cref="IQuerySecurity.UnreadableColumns"/>): its value is returned only on the rows a field share grants it on, null
/// elsewhere. In a distinct query it is not read at all (<c>NULL</c>).</param>
public sealed record ResultColumn(string SqlAlias, string? EntityAlias, string EntityName,
    AttributeMetadata? Attribute, ResultKind Kind, string ResultName, bool Unreadable = false);

/// <summary>
/// T-SQL produced by <see cref="SqlQueryCompiler"/>. Every value is in <see cref="Parameters"/>; every table/column name
/// comes from metadata.
/// </summary>
/// <remarks>
/// Without <c>top</c> the query reads <see cref="PageSize"/> + 1 rows (the extra row means more records): page 1 with
/// <c>TOP (@top)</c>, later pages with the keyset predicate and <c>TOP</c>, or <c>OFFSET/FETCH</c>. With <c>top</c>
/// paging does not apply. The total record count is a <see cref="ResultKind.TotalCount"/> column
/// (<c>COUNT(*) OVER()</c>) when present; otherwise, if requested, it is <see cref="CountSql"/>.
/// </remarks>
/// <param name="PageSize">Requested page size (5000 when not set); not applied when <c>top</c> is set.</param>
/// <param name="UsesKeyset">True when this page is read after the paging cookie's <c>last</c> id (keyset on the root
/// primary key) instead of OFFSET.</param>
/// <param name="CountSql">Scalar query run with the same <see cref="Parameters"/> (unused ones are harmless).
/// Aggregate queries: the <c>TOP 50001</c> record-limit probe over the base rows (compare with 50,000). Other
/// queries: the total record count when it cannot be a window column (keyset paging or distinct); null otherwise.</param>
public sealed record CompiledQuery(string Sql, IReadOnlyList<DbParameter> Parameters, IReadOnlyList<ResultColumn> Columns,
    EntityMetadata Root, int PageSize, int PageNumber, bool UsesKeyset, string? CountSql);
