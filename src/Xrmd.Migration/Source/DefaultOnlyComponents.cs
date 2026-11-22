using System.Text.Json;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Source;

/// <summary>
/// Forms, views, charts, security roles and processes made in the source (unmanaged) that no exported solution carries.
/// The migration does not export them (spec decision 12): each one becomes a <c>default-solution-component</c> warning.
/// </summary>
public static class DefaultOnlyComponents
{
    public const string WarningCode = "default-solution-component";

    private sealed record Kind(int ComponentType, string Label, string Query, string Id, string? TableColumn);

    private static readonly Kind[] Kinds =
    [
        new(60, "form", "systemforms?$select=formid,name,objecttypecode&$filter=ismanaged eq false", "formid", "objecttypecode"),
        new(26, "view", "savedqueries?$select=savedqueryid,name,returnedtypecode&$filter=ismanaged eq false", "savedqueryid", "returnedtypecode"),
        new(59, "chart", "savedqueryvisualizations?$select=savedqueryvisualizationid,name,primaryentitytypecode&$filter=ismanaged eq false", "savedqueryvisualizationid", "primaryentitytypecode"),
        new(20, "security role", "roles?$select=roleid,name&$filter=ismanaged eq false and _parentroleid_value eq null", "roleid", null),
        new(29, "process", "workflows?$select=workflowid,name,category,primaryentity&$filter=ismanaged eq false and type eq 1", "workflowid", "primaryentity"),
    ];

    public static async Task<IReadOnlyList<PackageWarning>> FindAsync(DataverseClient client, IEnumerable<Guid> exportedSolutionIds, CancellationToken ct)
    {
        var inSolutions = new HashSet<Guid>();
        var types = string.Join(" or ", Kinds.Select(k => $"componenttype eq {k.ComponentType}"));
        foreach (var id in exportedSolutionIds)
            foreach (var row in await client.GetAllAsync($"solutioncomponents?$select=objectid&$filter=_solutionid_value eq {id} and ({types})", ct))
                inSolutions.Add(row.GetProperty("objectid").GetGuid());

        var warnings = new List<PackageWarning>();
        foreach (var kind in Kinds)
        {
            foreach (var row in await client.GetAllAsync(kind.Query, ct))
            {
                if (inSolutions.Contains(row.GetProperty(kind.Id).GetGuid())) continue;
                var label = kind.ComponentType == 29 && row.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 2
                    ? "business rule" : kind.Label;
                var table = kind.TableColumn is not null && row.TryGetProperty(kind.TableColumn, out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() is { Length: > 0 } name && name != "none" ? $" on {name}" : "";
                warnings.Add(new PackageWarning(WarningCode,
                    $"The {label} '{row.GetProperty("name").GetString()}'{table} is not in an exported solution and is not migrated: add it to a solution and export again."));
            }
        }
        return warnings;
    }
}
