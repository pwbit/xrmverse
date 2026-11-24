using Xrmd.Model.Metadata;
using Xrmd.Query.Model;

namespace Xrmd.Query;

/// <summary>The read restriction of the caller, compiled into the query (never applied to rows afterwards).</summary>
public interface IQuerySecurity
{
    /// <summary>
    /// The filter the caller's readable records of <paramref name="table"/> satisfy, compiled in the scope of that entity
    /// (conditions without an entity alias refer to it); null: no restriction.
    /// </summary>
    FilterNode? ReadFilter(TableDefinition table);

    /// <summary>
    /// The secured columns of <paramref name="table"/> the caller cannot read through its field security profiles (plan 14,
    /// ruling F3); empty when field security does not restrict it. The compiler refuses them in a condition, an order, an aggregate
    /// or a link join (403 0x8004F504) and marks them in the projection (<see cref="Sql.ResultColumn.Unreadable"/>), so that the
    /// executor returns them as null unless a field share grants them on the row.
    /// </summary>
    IReadOnlySet<string> UnreadableColumns(TableDefinition table);
}
