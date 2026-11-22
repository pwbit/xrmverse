using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 2.13, task 1: which tables can be tracked (ruling C2) and the guard that keeps the list of ineligible tables honest.
/// </summary>
public class ChangeTrackingSchemaTests
{
    /// <summary>Sets a property the SDK exposes read-only (the server sets it).</summary>
    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    [Fact]
    public void Constants_are_the_Dataverse_values()
    {
        ChangeTrackingSchema.DefaultExpireDays.Should().Be(7);
        ChangeTrackingSchema.MaxExpireDays.Should().Be(365);
        ChangeTrackingSchema.IneligibleTables.Should().BeEquivalentTo(
            "audit", "principalobjectaccess", "principalobjectattributeaccess", "fileattachment", "solutioncomponent", "fieldpermission",
            "sdkmessageprocessingstepimage", "savedquery", "systemform", "privilege", "sdkmessagefilter", "sdkmessageprocessingstep",
            "activitypointer", "activityparty",
            "duplicaterule", "duplicaterulecondition", "duplicaterecord", "asyncoperation", "appmodulecomponent", "savedqueryvisualization",
            "postregarding");
        ChangeTrackingSchema.IndexName("account").Should().Be("IX_account_versionnumber");
    }

    [Fact]
    public void CanBeEnabled_is_false_for_intersect_and_ineligible_tables()
    {
        ChangeTrackingSchema.CanBeEnabled(new EntityMetadata { LogicalName = "account" }).Should().BeTrue();
        ChangeTrackingSchema.CanBeEnabled(new EntityMetadata { LogicalName = "new_thing" }).Should().BeTrue();
        foreach (var table in ChangeTrackingSchema.IneligibleTables)
            ChangeTrackingSchema.CanBeEnabled(new EntityMetadata { LogicalName = table }).Should().BeFalse(table);
        var intersect = new EntityMetadata { LogicalName = "new_a_b" };
        Set(intersect, nameof(EntityMetadata.IsIntersect), true);
        ChangeTrackingSchema.CanBeEnabled(intersect).Should().BeFalse();
    }

    // ---- the C2 guard -----------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The interpolated table names of the raw deletes, resolved to the constant they hold.</summary>
    private static readonly Dictionary<string, string> Constants = new(StringComparer.Ordinal)
    {
        ["{FieldSecurityTables.Permission}"] = FieldSecurityTables.Permission,
        ["{FieldSecurityTables.Share}"] = FieldSecurityTables.Share,
        ["{AuditTable.Name}"] = AuditTable.Name,
        // FileColumnService: private const string Attachment = FileColumns.AttachmentEntity.
        ["{Attachment}"] = FileColumns.AttachmentEntity,
        ["{ActivitySchema.Pointer}"] = ActivitySchema.Pointer,
        ["{ActivitySchema.Party}"] = ActivitySchema.Party,
        ["{DuplicateDetectionSchema.Rule}"] = DuplicateDetectionSchema.Rule,
        ["{DuplicateDetectionSchema.Condition}"] = DuplicateDetectionSchema.Condition,
        ["{DuplicateDetectionSchema.Record}"] = DuplicateDetectionSchema.Record,
        ["{PostSchema.Regarding}"] = PostSchema.Regarding,
    };

    /// <summary>Raw deletes whose target is not a table name, by (file, target), with what they delete from.</summary>
    private static readonly Dictionary<(string File, string Target), string?> Whitelisted = new()
    {
        // DeleteAssociationAsync: the intersect table of a many-to-many relationship (never tracked).
        [("SecurityAdministration.cs", "{entity.LogicalName}")] = Intersect,
        // The role's privileges: `table` is the roleprivileges intersect (never tracked).
        [("SecurityAdministration.cs", "{table}")] = Intersect,
        // A CTE over data.principalobjectattributeaccess (the field shares), which is in IneligibleTables.
        [("SecurityBootstrap.cs", "ranked")] = FieldSecurityTables.Share,
        // meta.hierarchy_security_exclusion: a meta table, not a data table.
        [("SecurityCache.cs", "{ExclusionTable}")] = null,
        // meta.component_layer and meta.solution_rank (plan 2.6): meta tables, not data tables.
        [("ComponentLayerStore.cs", "{Layers}")] = null,
        [("ComponentLayerStore.cs", "{Ranks}")] = null,
        // Plan 2.7a, Task 6: the same meta tables, named through the dialect (Layers(s), and `table` for the ranks).
        [("ComponentLayerStore.cs", "{Layers(s)}")] = null,
        [("ComponentLayerStore.cs", "{table}")] = null,
        // Plan 2.7a, Task 6: the purge's batch delete, `table` the audit table through the dialect.
        [("AuditReader.cs", "{table}")] = AuditTable.Name,
        // The import replaces an app module's components and role links (plan 3.5 UI-1a): `table` is appmodulecomponent
        // (in IneligibleTables) or the appmoduleroles intersect.
        [("SolutionImporter.cs", "{table}")] = AppModuleSchema.Component,
        // meta.workflow_wait (plan 2.14): the waits of background workflow jobs, a meta table, not a data table.
        [("WorkflowWaits.cs", "{Table}")] = null,
        // meta.serviceendpoint_secret (plan 3.2): a meta table, not a data table.
        [("WebhookSecrets.cs", "{SecretTable}")] = null,
    };

    private const string Intersect = "(intersect)";

    private static readonly Regex RawDelete = new(@"DELETE\s+(?:TOP\s*\([^)]*\)\s+)?(?:\w+\s+)?FROM\s+(?<target>\{\w+\.DataTable\([^)]*\)\}|[^\s;""]+)", RegexOptions.Compiled);

    private static readonly Regex DataTableCall = new(@"^\{\w+\.DataTable\((?<t>[^)]*)\)\}$", RegexOptions.Compiled);

    private static HashSet<string> SeededIntersects(string root) =>
        Directory.GetFiles(Path.Combine(root, "fixtures", "seed"), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f)))
            .Where(n => n?["IsIntersect"]?.GetValue<bool>() == true)
            .Select(n => n!["LogicalName"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("DELETE TOP (500) FROM data.savedquery WHERE x = 1", "data.savedquery")]
    [InlineData("DELETE TOP(@n) FROM [{DdlGenerator.DataSchema}].[audit]", "[{DdlGenerator.DataSchema}].[audit]")]
    [InlineData("DELETE s FROM data.sdkmessageprocessingstep AS s", "data.sdkmessageprocessingstep")]
    [InlineData("DELETE FROM {Table}{s.Dialect.ReturningInline(returned, RowImage.Deleted)} WHERE", "{Table}{s.Dialect.ReturningInline(returned,")]
    public void Raw_delete_pattern_matches_TOP_and_alias_forms(string sql, string target) =>
        RawDelete.Match(sql).Groups["target"].Value.Should().Be(target);

    [Fact]
    public void Raw_deletes_outside_core_only_touch_ineligible_tables()
    {
        var root = RepoRoot();
        var intersects = SeededIntersects(root);
        intersects.Should().Contain("roleprivileges", "the seed's intersect tables are read from fixtures/seed");
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(f => Path.GetFileName(f) != "CoreOperations.cs")
            .ToList();

        var found = new List<string>();
        var offending = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (Match match in RawDelete.Matches(File.ReadAllText(file)))
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith('@') || target.StartsWith('#') || target.StartsWith("meta.", StringComparison.Ordinal)) continue;
                string? table = target;
                // Plan 2.7a, fix round 1: the stores name a data table through the dialect, {s.DataTable(X)}, and a returning
                // slot may follow the table name without a space (savedquery{d.ReturningInline(...)}).
                if (DataTableCall.Match(table) is { Success: true } call)
                    table = call.Groups["t"].Value is var t && t.StartsWith('"') ? t.Trim('"') : "{" + t + "}";
                else if (table[0] is not ('[' or '{') && table.IndexOf('{') is > 0 and var brace) table = table[..brace];
                // Plan 2.7b, Task 7: a placeholder table name followed by a returning slot ({Table}{s.Dialect.ReturningInline(...)}).
                else if (table[0] == '{' && table.IndexOf("}{", StringComparison.Ordinal) is > 0 and var slot) table = table[..(slot + 1)];
                foreach (var prefix in new[] { "[{DdlGenerator.DataSchema}].", "[data].", "data." })
                    if (table.StartsWith(prefix, StringComparison.Ordinal)) table = table[prefix.Length..];
                if (table.StartsWith('[') && table.EndsWith(']')) table = table[1..^1];
                table = Whitelisted.TryGetValue((name, table), out var listed) ? listed : Constants.GetValueOrDefault(table, table);
                if (table is null) continue;
                found.Add($"{name}: {table}");
                if (table == Intersect || intersects.Contains(table) || ChangeTrackingSchema.IneligibleTables.Contains(table))
                    continue;
                offending.Add($"{name}: DELETE FROM {target} ({table})");
            }
        }

        found.Should().Contain(["MetadataStore.cs: fieldpermission", "AuditReader.cs: audit", "FileColumnService.cs: fileattachment",
                "MetadataOperations.cs: savedquery", "SdkMessageCatalog.cs: sdkmessageprocessingstep"],
            "the scan finds the known raw deletes");
        offending.Should().BeEmpty("a raw delete outside CoreOperations bypasses the deletion log: add its table to " +
                                   "ChangeTrackingSchema.IneligibleTables or delete through CoreOperations");
    }
}
