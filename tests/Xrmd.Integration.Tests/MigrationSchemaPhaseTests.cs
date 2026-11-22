using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
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
}
