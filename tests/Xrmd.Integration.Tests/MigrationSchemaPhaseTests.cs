using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The schema phase of an import (plan M4b task 1; spec §7.6 step 2, §7.9): the package's solutions imported in manifest order, the
/// plug-in steps and processes they bring left off and recorded, a failing solution named, and a resume that imports nothing twice.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationSchemaPhaseTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4b-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The two plug-in steps of the fixture solution, both enabled (StateCode 0) in its file.</summary>
    private static readonly Guid[] FixtureSteps = [Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21"), Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22")];

    private static TestProcess Process() => new("MigWf" + Guid.NewGuid().ToString("N")[..8], "background-wait", StateCode: 1);

    /// <summary>The fixture solution (steps) at order 1 and a solution with an activated process at order 2, added in reverse order.</summary>
    private string Package(TestProcess process) => Build(_root, p => p
        .Solution("xvmigwf", WorkflowSolutionBuilder.Build("xvmigwf", process), order: 2)
        .Solution(SolutionFixtureZip.FixtureName, SolutionFixtureZip.Build(), order: 1));

    private Task<object?> CountAsync(MigrationPhaseHarness h, string sql) => h.ScalarAsync(sql, ("@run", h.RunId));

    [Fact]
    public async Task Solutions_imported_in_manifest_order()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(Process()), null, new SchemaPhase());

        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename IN (N'XrmverseFixture', N'xvmigwf')")).Should().Be(2);
        (await TestSql.RowsAsync(h.ConnectionString, """
            SELECT entry FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'schema/solution/%' ORDER BY committedon, entry
            """, ("@run", h.RunId))).Select(r => (string)r[0]!).Should().Equal("schema/solution/XrmverseFixture", "schema/solution/xvmigwf");
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Imported_steps_and_workflows_stay_off_and_are_recorded()
    {
        var process = Process();
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package(process);
        await h.RunAsync(package, null, new SchemaPhase());

        async Task AssertOffAsync()
        {
            foreach (var step in FixtureSteps)
            {
                (await h.ScalarAsync("SELECT statecode FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(1);
                (await h.ScalarAsync("SELECT statuscode FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(2);
            }
            (await h.ScalarAsync("SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", process.Id))).Should().Be(0);
            (await h.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE parentworkflowid = @id AND type = 2", ("@id", process.Id))).Should().Be(0);
            (await CountAsync(h, "SELECT COUNT(*) FROM meta.migration_deactivated WHERE runid = @run")).Should().Be(3);
            (await h.ScalarAsync("SELECT sourcestate FROM meta.migration_deactivated WHERE runid = @run AND id = @id", ("@run", h.RunId), ("@id", FixtureSteps[0])))
                .Should().Be(0);
            (await h.ScalarAsync("SELECT sourcestate FROM meta.migration_deactivated WHERE runid = @run AND id = @id", ("@run", h.RunId), ("@id", process.Id)))
                .Should().Be(1);
        }

        await AssertOffAsync();
        var recorded = await h.Ledger.DeactivatedAsync(default);
        recorded.Select(d => d.Id).Should().BeEquivalentTo([.. FixtureSteps, process.Id]);
        recorded.Should().Contain(d => d.Table == "workflow" && d.Name == process.Name);

        // A crash after the imports but before they were marked: the solutions are imported again, and their logic switched off again.
        await h.ScalarAsync("DELETE FROM meta.migration_chunk WHERE runid = @run", ("@run", h.RunId));
        await h.RunAsync(package, null, new SchemaPhase());
        await AssertOffAsync();
    }

    [Fact]
    public async Task Imported_solution_not_switched_off_is_switched_off_at_finalize()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Package(Process()), null, new SchemaPhase());
        (await CountAsync(h, "SELECT COUNT(*) FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'schema/pending/%'")).Should().Be(2);

        // A cancel between a solution's import and its switch-off: imported (pending), logic still on, nothing recorded, not marked.
        await h.ScalarAsync("""
            DELETE FROM meta.migration_chunk WHERE runid = @run AND entry = 'schema/solution/XrmverseFixture';
            DELETE FROM meta.migration_deactivated WHERE runid = @run AND tablename = 'sdkmessageprocessingstep';
            UPDATE data.sdkmessageprocessingstep SET statecode = 0, statuscode = 1 WHERE sdkmessageprocessingstepid IN (@a, @b)
            """, ("@run", h.RunId), ("@a", FixtureSteps[0]), ("@b", FixtureSteps[1]));

        var off = await BusinessLogicSwitch.SwitchOffPendingAsync(h.App.Services, h.RunId, default);

        off.Should().Be(2);
        foreach (var step in FixtureSteps)
            (await h.ScalarAsync("SELECT statecode FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(1);
        (await CountAsync(h, "SELECT COUNT(*) FROM meta.migration_deactivated WHERE runid = @run AND tablename = 'sdkmessageprocessingstep'")).Should().Be(2);
        (await BusinessLogicSwitch.SwitchOffPendingAsync(h.App.Services, h.RunId, default)).Should().Be(0, "the solution is now marked");
    }

    [Fact]
    public async Task Failing_solution_fails_the_run_with_its_name()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Build(_root, p => p.Solution("xvbroken", "not a solution file"u8.ToArray()));
        var act = () => h.RunAsync(package, null, new SchemaPhase());
        (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message.Should().Contain("xvbroken");
        (await CountAsync(h, "SELECT COUNT(*) FROM meta.migration_chunk WHERE runid = @run AND entry LIKE 'schema/solution/%'")).Should().Be(0);
    }

    [Fact]
    public async Task Schema_resume_skips_imported_solutions()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = Package(Process());
        await h.RunAsync(package, null, new SchemaPhase());
        var jobs = await h.ScalarAsync("SELECT COUNT(*) FROM data.importjob");
        await h.RunAsync(package, null, new SchemaPhase());
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.importjob")).Should().Be(jobs);
    }

    // ---- schema delta (plan M4b task 2) ------------------------------------------------------------------------------

    private static JsonObject Label(string text) => new() { ["LocalizedLabels"] = new JsonArray(new JsonObject { ["Label"] = text, ["LanguageCode"] = 1033 }) };

    /// <summary>A delta in the shape the exporter writes: Web API metadata, with its annotations, under camelCase item keys.</summary>
    private static JsonObject Delta(string suffix) => new()
    {
        ["tables"] = new JsonArray(new JsonObject
        {
            ["table"] = $"new_mig{suffix}",
            ["metadata"] = new JsonObject
            {
                ["@odata.context"] = "https://contoso.example.com/api/data/v9.2/$metadata#EntityDefinitions/$entity",
                ["MetadataId"] = Guid.NewGuid().ToString(), ["LogicalName"] = $"new_mig{suffix}", ["SchemaName"] = $"new_Mig{suffix}",
                ["EntitySetName"] = $"new_mig{suffix}s", ["PrimaryIdAttribute"] = $"new_mig{suffix}id", ["PrimaryNameAttribute"] = "new_name",
                ["DisplayName"] = Label("Mig project"), ["DisplayCollectionName"] = Label("Mig projects"), ["OwnershipType"] = "UserOwned",
                ["IsActivity"] = false, ["TableType"] = "Standard", ["HasNotes"] = true, ["HasActivities"] = false,
                // Plan 4.1a: the source's queue setting travels with the table.
                ["IsValidForQueue"] = new JsonObject { ["Value"] = true, ["CanBeChanged"] = true, ["ManagedPropertyLogicalName"] = "canmodifyqueuesettings" },
            },
        }),
        ["columns"] = new JsonArray(
            new JsonObject
            {
                ["table"] = $"new_mig{suffix}", ["column"] = "new_name", ["type"] = "String",
                ["metadata"] = new JsonObject
                {
                    ["@odata.context"] = "x", ["@odata.type"] = "#Microsoft.Dynamics.CRM.StringAttributeMetadata", ["LogicalName"] = "new_name",
                    ["SchemaName"] = "new_Name", ["AttributeType"] = "String", ["MaxLength"] = 200, ["DisplayName"] = Label("Name"),
                    ["RequiredLevel"] = new JsonObject { ["Value"] = "ApplicationRequired" },
                },
            },
            new JsonObject
            {
                ["table"] = "account", ["column"] = $"new_code{suffix}", ["type"] = "String",
                ["metadata"] = new JsonObject
                {
                    ["@odata.type"] = "#Microsoft.Dynamics.CRM.StringAttributeMetadata", ["LogicalName"] = $"new_code{suffix}", ["SchemaName"] = $"new_Code{suffix}",
                    ["AttributeType"] = "String", ["MaxLength"] = 20, ["DisplayName"] = Label("Code"),
                },
            },
            new JsonObject
            {
                // A cast GET (…/Microsoft.Dynamics.CRM.PicklistAttributeMetadata) has no @odata.type: the item's type says it.
                ["table"] = "account", ["column"] = $"new_prio{suffix}", ["type"] = "Picklist",
                ["metadata"] = new JsonObject
                {
                    ["@odata.context"] = "x", ["LogicalName"] = $"new_prio{suffix}", ["SchemaName"] = $"new_Prio{suffix}", ["AttributeType"] = "Picklist",
                    ["DisplayName"] = Label("Priority"),
                    ["OptionSet"] = new JsonObject
                    {
                        ["MetadataId"] = Guid.NewGuid().ToString(), ["IsGlobal"] = false, ["OptionSetType"] = "Picklist",
                        ["Options"] = new JsonArray(new JsonObject { ["Value"] = 100000000, ["Label"] = Label("Low") }),
                    },
                },
            },
            new JsonObject
            {
                ["table"] = $"new_mig{suffix}", ["column"] = "new_accountid", ["type"] = "Lookup",
                ["metadata"] = new JsonObject
                {
                    ["@odata.type"] = "#Microsoft.Dynamics.CRM.LookupAttributeMetadata", ["LogicalName"] = "new_accountid", ["SchemaName"] = "new_AccountId",
                    ["AttributeType"] = "Lookup", ["DisplayName"] = Label("Account"), ["Targets"] = new JsonArray("account"),
                },
                ["relationship"] = new JsonObject
                {
                    ["SchemaName"] = $"new_account_new_mig{suffix}", ["ReferencedEntity"] = "account", ["ReferencedAttribute"] = "accountid",
                    ["ReferencingEntity"] = $"new_mig{suffix}", ["ReferencingAttribute"] = "new_accountid", ["RelationshipType"] = "OneToManyRelationship",
                },
            }),
        ["relationships"] = new JsonArray(new JsonObject
        {
            ["schemaName"] = $"new_mig{suffix}_contact",
            ["metadata"] = new JsonObject
            {
                ["@odata.context"] = "x", ["SchemaName"] = $"new_mig{suffix}_contact", ["Entity1LogicalName"] = $"new_mig{suffix}",
                ["Entity2LogicalName"] = "contact", ["IntersectEntityName"] = $"new_mig{suffix}_contact", ["RelationshipType"] = "ManyToManyRelationship",
            },
        }),
        ["optionValues"] = new JsonArray(new JsonObject
        {
            ["table"] = "account", ["column"] = $"new_prio{suffix}", ["value"] = 100000007,
            ["metadata"] = new JsonObject { ["Value"] = 100000007, ["Label"] = Label("Urgent") },
        }),
        ["statusValues"] = new JsonArray(new JsonObject
        {
            ["table"] = $"new_mig{suffix}", ["column"] = "statuscode", ["value"] = 100000001, ["state"] = 0,
            ["metadata"] = new JsonObject { ["Value"] = 100000001, ["State"] = 0, ["Label"] = Label("Waiting") },
        }),
    };

    private string DeltaPackage(JsonObject delta, JsonArray? unsupported = null) => Build(_root, p => p
        .File("schema/delta.json", Encoding.UTF8.GetBytes(delta.ToJsonString()))
        .File("schema/unsupported.json", Encoding.UTF8.GetBytes((unsupported ?? []).ToJsonString())));

    [Fact]
    public async Task Delta_creates_table_column_lookup_nn_and_option_values()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(DeltaPackage(Delta(suffix)), null, new SchemaPhase());

        var issues = await h.IssuesAsync();
        issues.Where(i => i.Code == "schema-item-failed").Should().BeEmpty();
        var store = h.App.Services.GetRequiredService<IMetadataStore>();
        var table = store.GetEntity($"new_mig{suffix}")!;
        table.Should().NotBeNull();
        table.PrimaryNameAttribute.Should().Be("new_name");
        table.IsValidForQueue!.Value.Should().BeTrue();
        table.Attributes.Should().Contain(a => a.LogicalName == "new_name" && ((StringAttributeMetadata)a).MaxLength == 200);
        table.Attributes.Should().Contain(a => a.LogicalName == "new_accountid" && a is LookupAttributeMetadata);
        var account = store.GetEntity("account")!;
        account.Attributes.Should().Contain(a => a.LogicalName == $"new_code{suffix}");
        var prio = (PicklistAttributeMetadata)account.Attributes.Single(a => a.LogicalName == $"new_prio{suffix}");
        prio.OptionSet.Options.Select(o => o.Value).Should().BeEquivalentTo(new int?[] { 100000000, 100000007 });
        store.GetRelationship($"new_mig{suffix}_contact").Should().BeOfType<ManyToManyRelationshipMetadata>();
        store.GetRelationship($"new_account_new_mig{suffix}").Should().BeOfType<OneToManyRelationshipMetadata>();
        // xrmverse has no InsertStatusValue: a status value is reported, not created.
        issues.Should().ContainSingle(i => i.Code == "unsupported" && i.Table == $"new_mig{suffix}" && i.Column == "statuscode");
    }

    [Fact]
    public async Task Local_option_named_after_its_option_set_added_to_the_column()
    {
        // M6 run: the Web API can name the local option set of a choice (account_new_prio…); there is no global set by that name.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var delta = Delta(suffix);
        ((JsonArray)delta["optionValues"]!)[0]!["optionSet"] = $"account_new_prio{suffix}";
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(DeltaPackage(delta), null, new SchemaPhase());

        (await h.IssuesAsync()).Where(i => i.Code == "schema-item-failed").Should().BeEmpty();
        var account = h.App.Services.GetRequiredService<IMetadataStore>().GetEntity("account")!;
        var prio = (PicklistAttributeMetadata)account.Attributes.Single(a => a.LogicalName == $"new_prio{suffix}");
        prio.OptionSet.Options.Select(o => o.Value).Should().BeEquivalentTo(new int?[] { 100000000, 100000007 });
    }

    [Fact]
    public async Task Existing_delta_item_is_skipped()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var delta = Delta(suffix);
        ((JsonArray)delta["columns"]!).Add(new JsonObject
        {
            ["table"] = "account", ["column"] = "name", ["type"] = "String",
            ["metadata"] = new JsonObject
            {
                ["@odata.type"] = "#Microsoft.Dynamics.CRM.StringAttributeMetadata", ["LogicalName"] = "name", ["SchemaName"] = "Name",
                ["AttributeType"] = "String", ["MaxLength"] = 160, ["DisplayName"] = Label("Account Name"),
            },
        });
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = DeltaPackage(delta);
        await h.RunAsync(package, null, new SchemaPhase());
        (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "schema-exists" && i.Table == "account" && i.Column == "name");

        // A crash before the items were marked: every item exists now, and is skipped, not failed.
        await h.ScalarAsync("DELETE FROM meta.migration_chunk WHERE runid = @run", ("@run", h.RunId));
        await h.RunAsync(package, null, new SchemaPhase());
        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Code == "schema-item-failed" || i.Severity == MigrationIssue.Error);
        issues.Count(i => i.Code == "schema-exists").Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task Unsupported_items_reported_and_strict_fails()
    {
        var unsupported = new JsonArray(new JsonObject { ["kind"] = "column", ["name"] = "account.new_total", ["reason"] = "rollup column outside the exported solutions: import it with its solution" });
        var package = Build(_root, p => p.File("schema/unsupported.json", Encoding.UTF8.GetBytes(unsupported.ToJsonString())));
        await using (var h = MigrationPhaseHarness.Start(fx))
        {
            await h.RunAsync(package, null, new SchemaPhase());
            (await h.IssuesAsync()).Should().ContainSingle(i => i.Code == "unsupported" && i.Severity == MigrationIssue.Warning
                && i.Message.Contains("account.new_total") && i.Message.Contains("rollup"));
        }
        await using (var h = MigrationPhaseHarness.Start(fx))
        {
            var act = () => h.RunAsync(package, new Dictionary<string, string> { ["strict"] = "true" }, new SchemaPhase());
            (await act.Should().ThrowAsync<MigrationPhaseException>()).Which.Message.Should().Contain("account.new_total");
        }
    }

    [Fact]
    public async Task Strict_fails_before_any_solution_is_imported()
    {
        var unsupported = new JsonArray(new JsonObject { ["kind"] = "column", ["name"] = "account.new_total", ["reason"] = "rollup column" });
        var package = Build(_root, p => p
            .Solution(SolutionFixtureZip.FixtureName, SolutionFixtureZip.Build())
            .File("schema/unsupported.json", Encoding.UTF8.GetBytes(unsupported.ToJsonString())));
        await using var h = MigrationPhaseHarness.Start(fx);
        var act = () => h.RunAsync(package, new Dictionary<string, string> { ["strict"] = "true" }, new SchemaPhase());
        await act.Should().ThrowAsync<MigrationPhaseException>();
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'XrmverseFixture'")).Should().Be(0);
    }

    [Fact]
    public async Task Delta_item_with_unreadable_metadata_is_reported_not_fatal()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var delta = Delta(suffix);
        ((JsonArray)delta["columns"]!).Add(new JsonObject
        {
            ["table"] = "account", ["column"] = $"new_bad{suffix}", ["type"] = "String",
            ["metadata"] = new JsonObject { ["LogicalName"] = $"new_bad{suffix}", ["SchemaName"] = $"new_Bad{suffix}", ["MaxLength"] = "not a number" },
        });
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(DeltaPackage(delta), null, new SchemaPhase());
        var issues = await h.IssuesAsync();
        issues.Should().ContainSingle(i => i.Code == "schema-item-failed" && i.Column == $"new_bad{suffix}");
        h.App.Services.GetRequiredService<IMetadataStore>().GetEntity($"new_mig{suffix}").Should().NotBeNull("the other items are applied");
    }

    // ---- carried custom columns (migration table mapping spec §8.3 R7, GAP-27 task 11) --------------------------------------

    private const string LeadMap = """
        version: 1
        tables:
          - source: lead
            target: xv_lead
            columns: { subject: xv_name }
          - source: xs_rival
            target: xv_rival
        relationships:
          - { source: leadcompetitors, target: xv_lead_xv_rival }
        """;

    private static readonly SourceCatalog LeadCatalog = new(
    [
        new SourceCatalogTable("lead", "leadid", 4, "user", false,
            [new("subject", "String", [], false), new("new_rating", "Picklist", [], true), new("new_score", "Integer", [], true)]),
        new SourceCatalogTable("xs_rival", "xs_rivalid", 10002, "organization", false, [new("xs_name", "String", [], true)]),
    ], []);

    /// <summary>The choice <c>lead.new_rating</c> as the export of a mapped table puts it in the delta (a cast GET, no <c>@odata.type</c>).</summary>
    private static JsonObject RatingColumn() => new()
    {
        ["table"] = "lead", ["column"] = "new_rating", ["type"] = "Picklist",
        ["metadata"] = new JsonObject
        {
            ["@odata.context"] = "x", ["LogicalName"] = "new_rating", ["SchemaName"] = "new_Rating", ["AttributeType"] = "Picklist",
            ["EntityLogicalName"] = "lead", ["DisplayName"] = Label("Rating"),
            ["OptionSet"] = new JsonObject
            {
                ["MetadataId"] = Guid.NewGuid().ToString(), ["IsGlobal"] = false, ["OptionSetType"] = "Picklist",
                ["Options"] = new JsonArray(
                    new JsonObject { ["Value"] = 100000000, ["Label"] = Label("Cold") },
                    new JsonObject { ["Value"] = 100000001, ["Label"] = Label("Hot") }),
            },
        },
    };

    private static JsonObject ScoreColumn() => new()
    {
        ["table"] = "lead", ["column"] = "new_score", ["type"] = "Integer",
        ["metadata"] = new JsonObject
        {
            ["@odata.type"] = "#Microsoft.Dynamics.CRM.IntegerAttributeMetadata", ["LogicalName"] = "new_score", ["SchemaName"] = "new_Score",
            ["AttributeType"] = "Integer", ["EntityLogicalName"] = "lead", ["DisplayName"] = Label("Score"), ["MinValue"] = 0, ["MaxValue"] = 100,
        },
    };

    /// <summary>A lookup of the delta with its relationship, in the shape the exporter writes.</summary>
    private static JsonObject LookupColumn(string table, string column, string referenced, string referencedKey, string relationship) => new()
    {
        ["table"] = table, ["column"] = column, ["type"] = "Lookup",
        ["metadata"] = new JsonObject
        {
            ["@odata.type"] = "#Microsoft.Dynamics.CRM.LookupAttributeMetadata", ["LogicalName"] = column, ["SchemaName"] = column,
            ["AttributeType"] = "Lookup", ["EntityLogicalName"] = table, ["DisplayName"] = Label(column), ["Targets"] = new JsonArray(referenced),
        },
        ["relationship"] = new JsonObject
        {
            ["SchemaName"] = relationship, ["ReferencedEntity"] = referenced, ["ReferencedAttribute"] = referencedKey,
            ["ReferencingEntity"] = table, ["ReferencingAttribute"] = column, ["RelationshipType"] = "OneToManyRelationship",
        },
    };

    private static JsonObject ManyToManyItem(string name, string first, string second) => new()
    {
        ["schemaName"] = name,
        ["metadata"] = new JsonObject
        {
            ["SchemaName"] = name, ["Entity1LogicalName"] = first, ["Entity2LogicalName"] = second, ["IntersectEntityName"] = name,
            ["Entity1IntersectAttribute"] = first + "id", ["Entity2IntersectAttribute"] = second + "id", ["RelationshipType"] = "ManyToManyRelationship",
        },
    };

    /// <summary>A package with the map target solution (unless <paramref name="withTarget"/> is false), <paramref name="delta"/> and <paramref name="map"/>.</summary>
    private string MappedDeltaPackage(JsonObject delta, string map, bool withTarget = true, JsonArray? unsupported = null, Action<PackageBuilder>? more = null) =>
        Build(_root, p =>
        {
            if (withTarget) p.Solution(MappingTargetSolution.Name, MappingTargetSolution.Build());
            p.File("schema/delta.json", Encoding.UTF8.GetBytes(delta.ToJsonString()))
                .File("schema/unsupported.json", Encoding.UTF8.GetBytes((unsupported ?? []).ToJsonString()));
            more?.Invoke(p);
        },
        new PackageMapsPart([("sales", map)], LeadCatalog));

    private static string Text(Microsoft.Xrm.Sdk.Label? label) => label?.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == 1033)?.Label ?? "";

    [Fact]
    public async Task Carried_column_is_created_on_the_target_with_its_options()
    {
        var delta = new JsonObject
        {
            ["columns"] = new JsonArray(RatingColumn()),
            // Local values of the mapped table's own columns and its status values are left to the value and state translation.
            ["optionValues"] = new JsonArray(new JsonObject
            {
                ["table"] = "lead", ["column"] = "leadsourcecode", ["value"] = 100000005,
                ["metadata"] = new JsonObject { ["Value"] = 100000005, ["Label"] = Label("Fair") },
            }),
            ["statusValues"] = new JsonArray(new JsonObject
            {
                ["table"] = "lead", ["column"] = "statuscode", ["value"] = 100000002, ["state"] = 2,
                ["metadata"] = new JsonObject { ["Value"] = 100000002, ["State"] = 2, ["Label"] = Label("Canceled") },
            }),
        };
        await using var h = MigrationPhaseHarness.Start(fx);
        var unsupported = new JsonArray(new JsonObject { ["kind"] = "column", ["name"] = "lead.new_total", ["reason"] = "rollup or formula column of a table mapped to another table" });
        await h.RunAsync(MappedDeltaPackage(delta, LeadMap, unsupported: unsupported), null, new SchemaPhase());

        var store = h.App.Services.GetRequiredService<IMetadataStore>();
        store.GetEntity("lead").Should().BeNull("a mapped table is not created");
        var rating = store.GetEntity(MappingTargetSolution.Lead)!.Attributes.Should().ContainSingle(a => a.LogicalName == "new_rating")
            .Which.Should().BeOfType<PicklistAttributeMetadata>().Subject;
        Text(rating.DisplayName).Should().Be("Rating");
        rating.OptionSet.Options.Select(o => (o.Value, Text(o.Label))).Should().Equal((100000000, "Cold"), (100000001, "Hot"));
        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Code == "schema-item-failed" || i.Severity == MigrationIssue.Error);
        // The unsupported item of the mapped table is reported under its target; the lead's status value is left to R4.
        issues.Where(i => i.Code == "unsupported").Should().ContainSingle()
            .Which.Should().Match<MigrationIssue>(i => i.Table == MappingTargetSolution.Lead && i.Column == "new_total" && i.Message.Contains("lead.new_total"));
        issues.Should().NotContain(i => i.Table == "lead", "no item is applied under the source name");
    }

    [Fact]
    public async Task Carried_lookup_to_a_mapped_table_targets_the_target_table()
    {
        var delta = new JsonObject
        {
            ["columns"] = new JsonArray(
                LookupColumn("lead", "new_rivalid", "xs_rival", "xs_rivalid", "new_xs_rival_lead_rivalid"),
                LookupColumn("account", "new_leadid", "lead", "leadid", "new_lead_account_leadid")),
            ["relationships"] = new JsonArray(
                ManyToManyItem("new_lead_contact", "lead", "contact"),
                // Named by the map: its pairs go to the target's own relationship.
                ManyToManyItem("leadcompetitors", "lead", "xs_rival")),
        };
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(MappedDeltaPackage(delta, LeadMap), null, new SchemaPhase());

        (await h.IssuesAsync()).Should().NotContain(i => i.Code == "schema-item-failed" || i.Severity == MigrationIssue.Error);
        var store = h.App.Services.GetRequiredService<IMetadataStore>();
        store.GetEntity(MappingTargetSolution.Lead)!.Attributes.OfType<LookupAttributeMetadata>().Should().ContainSingle(a => a.LogicalName == "new_rivalid")
            .Which.Targets.Should().Equal(MappingTargetSolution.Rival);
        var rival = store.GetRelationship("new_xs_rival_lead_rivalid").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (rival.ReferencingEntity, rival.ReferencedEntity, rival.ReferencedAttribute).Should().Be((MappingTargetSolution.Lead, MappingTargetSolution.Rival, "xv_rivalid"));
        store.GetEntity("account")!.Attributes.OfType<LookupAttributeMetadata>().Should().ContainSingle(a => a.LogicalName == "new_leadid")
            .Which.Targets.Should().Equal(MappingTargetSolution.Lead);
        var nn = store.GetRelationship("new_lead_contact").Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        (nn.Entity1LogicalName, nn.Entity2LogicalName, nn.Entity1IntersectAttribute).Should().Be((MappingTargetSolution.Lead, "contact", "xv_leadid"));
        store.GetRelationship("leadcompetitors").Should().BeNull("the map sends its pairs to xv_lead_xv_rival");
    }

    [Fact]
    public async Task Drop_skips_and_reports()
    {
        const string map = """
            version: 1
            tables:
              - source: lead
                target: xv_lead
                columns: { subject: xv_name }
                customColumns: drop
            """;
        await using var h = MigrationPhaseHarness.Start(fx);
        var package = MappedDeltaPackage(new JsonObject { ["columns"] = new JsonArray(RatingColumn(), ScoreColumn()) }, map);
        await h.RunAsync(package, null, new SchemaPhase());
        await h.RunAsync(package, null, new SchemaPhase()); // a resume reports nothing twice

        h.App.Services.GetRequiredService<IMetadataStore>().GetEntity(MappingTargetSolution.Lead)!.Attributes
            .Should().NotContain(a => a.LogicalName == "new_rating" || a.LogicalName == "new_score");
        (await h.IssuesAsync()).Where(i => i.Code == "column-dropped").Should().BeEquivalentTo(new[]
        {
            new MigrationIssue(SchemaPhase.PhaseOrder, MigrationIssue.Info, "column-dropped", MappingTargetSolution.Lead, null, "new_rating",
                "The custom column lead.new_rating is not created on xv_lead (customColumns: drop)."),
            new MigrationIssue(SchemaPhase.PhaseOrder, MigrationIssue.Info, "column-dropped", MappingTargetSolution.Lead, null, "new_score",
                "The custom column lead.new_score is not created on xv_lead (customColumns: drop)."),
        });
    }

    [Fact]
    public async Task Existing_same_type_column_is_reused()
    {
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(MappingTargetSolution.Name, MappingTargetSolution.Build())), null, new SchemaPhase());
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
        {
            EntityName = MappingTargetSolution.Lead,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_score", SchemaName = "new_score", MinValue = 0, MaxValue = 1000 },
        });

        await h.RunAsync(MappedDeltaPackage(new JsonObject { ["columns"] = new JsonArray(ScoreColumn()) }, LeadMap, withTarget: false), null, new SchemaPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Code == "schema-item-failed" || i.Severity == MigrationIssue.Error);
        issues.Should().ContainSingle(i => i.Code == "schema-exists" && i.Table == MappingTargetSolution.Lead && i.Column == "new_score");
        h.App.Services.GetRequiredService<IMetadataStore>().GetEntity(MappingTargetSolution.Lead)!.Attributes
            .Should().ContainSingle(a => a.LogicalName == "new_score").Which.Should().BeOfType<IntegerAttributeMetadata>()
            .Which.MaxValue.Should().Be(1000, "the target's column is kept as it is");
    }

    [Fact]
    public async Task Reused_carried_choice_gains_the_missing_options_and_its_rows_import()
    {
        const string map = """
            version: 1
            tables:
              - source: lead
                target: xv_lead
                columns: { subject: xv_name }
            """;
        await using var h = MigrationPhaseHarness.Start(fx);
        await h.RunAsync(Build(_root, p => p.Solution(MappingTargetSolution.Name, MappingTargetSolution.Build())), null, new SchemaPhase());
        var admin = TestServices.NewOrganizationService(h.App.Services, fx.AdminObjectId);
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
        {
            EntityName = MappingTargetSolution.Lead,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_rating", SchemaName = "new_rating", DisplayName = new Microsoft.Xrm.Sdk.Label("Rating", 1033),
                OptionSet = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist, Options = { new OptionMetadata(new Microsoft.Xrm.Sdk.Label("Cold", 1033), 100000000) } },
            },
        });
        var lead = Guid.NewGuid();
        var package = MappedDeltaPackage(new JsonObject { ["columns"] = new JsonArray(RatingColumn()) }, map, withTarget: false, more: p => p
            .Security("businessunit", "businessunitid")
            .Table("lead", "leadid", [Record(lead, ("subject", new Xrmd.Migration.Records.StringValue("Lead 1")), ("new_rating", new Xrmd.Migration.Records.OptionValue(100000001)))]));

        await h.RunAsync(package, null, new VerifyPackagePhase(), new SchemaPhase(), new SecurityPhase(), new RecordsPhase());

        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Code == "schema-item-failed" || i.Severity == MigrationIssue.Error);
        var rating = (PicklistAttributeMetadata)h.App.Services.GetRequiredService<IMetadataStore>().GetEntity(MappingTargetSolution.Lead)!.Attributes
            .Single(a => a.LogicalName == "new_rating");
        rating.OptionSet.Options.Select(o => (o.Value, Text(o.Label))).Should().Equal((100000000, "Cold"), (100000001, "Hot"));
        (await h.ScalarAsync("SELECT new_rating FROM data.xv_lead WHERE xv_leadid = @id", ("@id", lead))).Should().Be(100000001);
    }
}
