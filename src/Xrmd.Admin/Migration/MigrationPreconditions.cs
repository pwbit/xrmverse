using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The "no business data" precondition of an import (migration spec §7.2, plan M3 R7): the tables of the package that already hold
/// rows in the organization, leaving out the rows the organization is created with (its security tables, the organization row, the
/// base currency, the per-user settings): those are mapped onto the package's by the import (spec §7.3), not duplicated.
/// </summary>
public static class MigrationPreconditions
{
    /// <summary>Tables whose rows are created with the organization or its users, mapped by the import rather than refused.</summary>
    public static readonly IReadOnlySet<string> SeededTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "businessunit", "team", "systemuser", "teammembership", "systemuserroles", "teamroles", "role", "principalobjectaccess",
        "systemuserprofiles", "teamprofiles", "fieldsecurityprofile", "organization", "usersettings",
    };

    /// <summary>The tables among <paramref name="tables"/> that exist in the organization and hold business rows, sorted.</summary>
    public static async Task<List<string>> TablesWithDataAsync(DataSession s, IMetadataStore store, IEnumerable<string> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(store);
        var found = new List<string>();
        foreach (var table in tables.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (SeededTables.Contains(table) || store.GetEntity(table) is not { } m) continue;
            var pk = SystemAttributes.PrimaryIdName(m);
            // The base currency is created with the organization (plan 2.10): any other currency is business data.
            var where = table == "transactioncurrency"
                ? $" WHERE {s.Quote(pk)} NOT IN (SELECT basecurrencyid FROM {s.DataTable("organization")} WHERE basecurrencyid IS NOT NULL)"
                : "";
            try
            {
                // Plan 2.7b, Task 10: recoverable, so that the failure below leaves a PostgreSQL transaction usable.
                if (await s.RecoverableAsync(async () =>
                    {
                        await using var cmd = s.Command($"SELECT CASE WHEN EXISTS (SELECT 1 FROM {s.DataTable(m.LogicalName)}{where}) THEN 1 ELSE 0 END");
                        return await cmd.ExecuteScalarAsync(ct);
                    }, ct) is 1)
                    found.Add(table);
            }
            catch (DbException ex) when (s.Dialect.Classify(ex) == DbErrorKind.UndefinedObject)
            {
                // Invalid object name: a table the metadata knows but the database does not hold yet counts as empty.
            }
        }
        return found;
    }
}
