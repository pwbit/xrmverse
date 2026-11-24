using Meziantou.Framework.Tds.Handler;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Tds.Translation;

namespace Xrmd.Tds.Execution;

/// <summary>
/// The canned answers of the system queries (plan 3.3, ruling T9): the version and server properties clients ask for on connect,
/// <c>DB_NAME()</c>, <c>@@SPID</c>, and the <c>INFORMATION_SCHEMA</c> views, which list the tables the caller holds a read privilege
/// on and the columns of ruling T7 it can read (no secured column it cannot read; <c>&lt;name&gt;name</c> for the name of a lookup
/// and the label of a choice; no file or image companion).
/// </summary>
public sealed class SystemQueries(IMetadataStore store, SecurityCache security, FieldSecurity fields)
{
    /// <summary>The SQL Server version the endpoint reports (SQL Server 2014 RTM, as Dataverse's endpoint does).</summary>
    public const string ProductVersion = "12.0.2000.8";

    /// <summary>The text of <c>@@VERSION</c>.</summary>
    public const string VersionText = "xrmverse SQL endpoint (read-only) - " + ProductVersion;

    /// <summary>The session id every connection reports: the endpoint has no SQL Server sessions.</summary>
    public const int Spid = 51;

    private const string Schema = "dbo";

    /// <summary>The answer to <paramref name="query"/> for <paramref name="caller"/>.</summary>
    /// <param name="database">The name <c>DB_NAME()</c> and <c>TABLE_CATALOG</c> report.</param>
    public async Task<TdsResultSet> AnswerAsync(SystemQuery query, Caller caller, string database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(caller);
        return query.Kind switch
        {
            SystemQueryKind.Version => Scalar(query, TdsColumnType.NVarChar, VersionText),
            SystemQueryKind.ServerProperty => query.Argument switch
            {
                "ProductVersion" => Scalar(query, TdsColumnType.NVarChar, ProductVersion),
                "Edition" => Scalar(query, TdsColumnType.NVarChar, "Enterprise Edition"),
                "EngineEdition" => Scalar(query, TdsColumnType.Int32, 3),
                "ProductLevel" => Scalar(query, TdsColumnType.NVarChar, "RTM"),
                _ => throw TdsSqlErrors.NotSupported($"SERVERPROPERTY('{query.Argument}')"),
            },
            SystemQueryKind.DatabaseName => Scalar(query, TdsColumnType.NVarChar, database),
            SystemQueryKind.Spid => Scalar(query, TdsColumnType.Int32, Spid),
            SystemQueryKind.Tables => View(query, SqlTranslator.InformationSchemaTablesColumns, TablesColumn,
                (await ReadableTablesAsync(caller, query.Argument, ct)).Select(t => TableRow(database, t))),
            SystemQueryKind.Columns => View(query, SqlTranslator.InformationSchemaColumnsColumns, ColumnsColumn,
                await ColumnRowsAsync(caller, database, query.Argument, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, "Unknown system query."),
        };
    }

    private static TdsResultSet Scalar(SystemQuery query, TdsColumnType type, object value)
    {
        var set = new TdsResultSet();
        set.Columns.Add(ResultSetBuilder.Column(query.Columns is [var name] ? name : "", 0, type));
        set.Rows.Add([value]);
        return set;
    }

    /// <summary>The selected columns of a view (all of them for <c>*</c>), in the order of the select list.</summary>
    private static TdsResultSet View(SystemQuery query, IReadOnlyList<string> all, Func<string, TdsColumnType> typeOf,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var selected = query.Columns ?? all;
        var set = new TdsResultSet();
        for (var i = 0; i < selected.Count; i++) set.Columns.Add(ResultSetBuilder.Column(selected[i], i, typeOf(selected[i])));
        // A missing value is null, never DBNull (see ResultSetBuilder).
        foreach (var row in rows) set.Rows.Add(selected.Select(c => row.GetValueOrDefault(c) switch { DBNull => null, var v => v }).ToArray());
        return set;
    }

    private static TdsColumnType TablesColumn(string column) => TdsColumnType.NVarChar;

    private static TdsColumnType ColumnsColumn(string column) => column switch
    {
        "ORDINAL_POSITION" or "CHARACTER_MAXIMUM_LENGTH" or "CHARACTER_OCTET_LENGTH" or "NUMERIC_PRECISION" or "NUMERIC_PRECISION_RADIX"
            or "NUMERIC_SCALE" or "DATETIME_PRECISION" => TdsColumnType.Int32,
        _ => TdsColumnType.NVarChar,
    };

    private static Dictionary<string, object?> TableRow(string database, EntityMetadata table) => new(StringComparer.Ordinal)
    {
        ["TABLE_CATALOG"] = database,
        ["TABLE_SCHEMA"] = Schema,
        ["TABLE_NAME"] = table.LogicalName,
        ["TABLE_TYPE"] = "BASE TABLE",
    };

    /// <summary>
    /// The tables <paramref name="caller"/> holds a read privilege on (every principal of it, as the executor demands), by logical
    /// name; only <paramref name="tableName"/> (ignoring case) when given.
    /// </summary>
    private async Task<List<EntityMetadata>> ReadableTablesAsync(Caller caller, string? tableName, CancellationToken ct)
    {
        var tables = store.GetAllEntities()
            .Where(t => tableName is null || string.Equals(t.LogicalName, tableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.LogicalName, StringComparer.Ordinal)
            .ToList();
        if (caller == Caller.Internal || security.IsUnrestricted(caller, await security.SystemUserIdAsync(ct))) return tables;
        var principals = await security.PrincipalsAsync(caller, ct);
        return tables.Where(t => principals.All(p => CanRead(p, t))).ToList();
    }

    /// <summary>As the read restriction decides it (<see cref="ReadSecurity"/>): intersect tables need no privilege; the audit table needs its privilege at Global.</summary>
    private static bool CanRead(SecurityProfile principal, EntityMetadata table)
    {
        if (table.IsIntersect == true) return true;
        var depth = principal.DepthOf(PrivilegeNames.For(AccessRights.ReadAccess, table));
        if (depth == DepthMask.None) return false;
        return table.LogicalName != AuditTable.Name || principal.IsSystemAdministrator || depth == DepthMask.Global;
    }

    private async Task<List<IReadOnlyDictionary<string, object?>>> ColumnRowsAsync(Caller caller, string database, string? tableName,
        CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var unrestricted = caller == Caller.Internal || security.IsUnrestricted(caller, await security.SystemUserIdAsync(ct));
        foreach (var table in await ReadableTablesAsync(caller, tableName, ct))
        {
            var unreadable = unrestricted ? null : await fields.ForAsync(caller, table.LogicalName, ct);
            var attributes = table.Attributes ?? [];
            var position = 0;
            foreach (var attribute in attributes.Where(SqlTranslator.IsExposed))
            {
                if (unreadable is not null && unreadable.UnreadableSecured.Contains(attribute.LogicalName)) continue;
                rows.Add(ColumnRow(database, table, attribute.LogicalName, ++position, attribute.LogicalName == table.PrimaryIdAttribute,
                    ResultSetBuilder.TypeOf(attribute), attribute));
                // The name of a lookup and the label of a choice (ruling T7), unless a real column has that name.
                var companion = attribute.LogicalName + SqlTranslator.NameSuffix;
                if (attribute is LookupAttributeMetadata or PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata
                    && !attributes.Any(a => string.Equals(a.LogicalName, companion, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(ColumnRow(database, table, companion, ++position, false, TdsColumnType.NVarChar, null));
            }
        }
        return rows;
    }

    private static Dictionary<string, object?> ColumnRow(string database, EntityMetadata table, string name, int position, bool key,
        TdsColumnType type, AttributeMetadata? attribute)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TABLE_CATALOG"] = database,
            ["TABLE_SCHEMA"] = Schema,
            ["TABLE_NAME"] = table.LogicalName,
            ["COLUMN_NAME"] = name,
            ["ORDINAL_POSITION"] = position,
            ["IS_NULLABLE"] = key ? "NO" : "YES",
        };
        switch (type)
        {
            case TdsColumnType.NVarChar:
                // nvarchar(max) for memos and names, as -1.
                var length = attribute switch
                {
                    StringAttributeMetadata { MaxLength: { } max } => max,
                    _ => -1,
                };
                row["DATA_TYPE"] = "nvarchar";
                row["CHARACTER_MAXIMUM_LENGTH"] = length;
                row["CHARACTER_OCTET_LENGTH"] = length < 0 ? -1 : length * 2;
                row["CHARACTER_SET_NAME"] = "UNICODE";
                break;
            case TdsColumnType.Int32:
                Numeric(row, "int", 10, 10, 0);
                break;
            case TdsColumnType.Int64:
                Numeric(row, "bigint", 19, 10, 0);
                break;
            case TdsColumnType.Decimal:
                // The precision of a decimal column is its number of decimal places (SQL: decimal(23, n)).
                Numeric(row, "decimal", 23, 10, (attribute as DecimalAttributeMetadata)?.Precision ?? 2);
                break;
            case TdsColumnType.Money:
                Numeric(row, "money", 19, 10, 4);
                break;
            case TdsColumnType.Double:
                Numeric(row, "float", 53, 2, null);
                break;
            case TdsColumnType.Boolean:
                row["DATA_TYPE"] = "bit";
                break;
            case TdsColumnType.DateTime2:
                row["DATA_TYPE"] = "datetime2";
                row["DATETIME_PRECISION"] = 7;
                break;
            case TdsColumnType.Guid:
                row["DATA_TYPE"] = "uniqueidentifier";
                break;
            default:
                row["DATA_TYPE"] = type.ToString().ToLowerInvariant();
                break;
        }
        return row;
    }

    private static void Numeric(Dictionary<string, object?> row, string type, int precision, int radix, int? scale)
    {
        row["DATA_TYPE"] = type;
        row["NUMERIC_PRECISION"] = precision;
        row["NUMERIC_PRECISION_RADIX"] = radix;
        row["NUMERIC_SCALE"] = scale;
    }
}
