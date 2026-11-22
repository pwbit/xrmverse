using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Host;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// A sweep of the features PostgreSQL organizations got in plan 2.7b (the refusals of plan 2.7a, ruling P10, all gone): writes on
/// the tables those features seed, and the host's startup with the local identity provider and the SQL endpoint. Runs on a
/// PostgreSQL host of its own, on both providers' runs.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresFeatureSweepTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private Task<long> CountAsync(string sql) => TestSql.CountAsync(fx.ConnectionString, sql);

    // ---- P11 writes on the tables of a 2.7b feature (final review, Important 1) -------------------------------------------------

    private Task<Guid> DraftWorkflowAsync(string name) => Admin.CreateAsync(new Entity("workflow")
    {
        ["name"] = name, ["type"] = new OptionSetValue(1), ["category"] = new OptionSetValue(0), ["mode"] = new OptionSetValue(1),
        ["scope"] = new OptionSetValue(4), ["primaryentity"] = "account", ["xaml"] = WorkflowFixtures.Xaml("realtime-create-update"),
    });

    /// <summary>A Draft definition is inert, as in Dataverse: it can be renamed and deleted on PostgreSQL.</summary>
    [Fact]
    public async Task A_draft_workflow_can_be_updated_and_deleted()
    {
        var id = await DraftWorkflowAsync("pg draft to edit");

        await Admin.UpdateAsync(new Entity("workflow", id) { ["name"] = "pg draft renamed" });
        (await Admin.RetrieveAsync("workflow", id, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("pg draft renamed");

        await Admin.DeleteAsync("workflow", id);
        (await CountAsync($"SELECT COUNT(*) FROM data.workflow WHERE workflowid = '{id}'")).Should().Be(0);
    }

    /// <summary>A rule is created and deleted on PostgreSQL (an unpublished rule has no match code table to drop).</summary>
    [Fact]
    public async Task A_duplicate_rule_can_be_deleted()
    {
        await EnableDuplicateDetectionAsync("contact");
        var rule = await Admin.CreateAsync(new Entity("duplicaterule")
        {
            ["name"] = "pg rule to delete", ["baseentityname"] = "contact", ["matchingentityname"] = "contact",
        });
        await Admin.CreateAsync(new Entity("duplicaterulecondition")
        {
            ["regardingobjectid"] = new EntityReference("duplicaterule", rule), ["baseattributename"] = "lastname",
            ["matchingattributename"] = "lastname", ["operatorcode"] = new OptionSetValue(0),
        });

        await Admin.DeleteAsync("duplicaterule", rule);

        (await CountAsync($"SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = '{rule}'")).Should().Be(0);
        (await CountAsync($"SELECT COUNT(*) FROM data.duplicaterulecondition WHERE regardingobjectid = '{rule}'")).Should().Be(0);
    }

    /// <summary>Turns the table switch on (a rule needs it on its tables); other tests of this class use <c>contact</c> only so.</summary>
    private Task EnableDuplicateDetectionAsync(string table) => Admin.ExecuteAsync(new UpdateEntityRequest
    {
        Entity = new EntityMetadata { LogicalName = table, IsDuplicateDetectionEnabled = new BooleanManagedProperty(true) },
    });

    /// <summary>The table switch is metadata (P11): turning it off works (it unpublishes the table's rules, none here).</summary>
    [Fact]
    public async Task Turning_off_duplicate_detection_of_a_table_works()
    {
        foreach (var on in new[] { true, false })
            await Admin.ExecuteAsync(new UpdateEntityRequest
            {
                Entity = new EntityMetadata { LogicalName = "account", IsDuplicateDetectionEnabled = new BooleanManagedProperty(on) },
            });

        var account = (RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest
        {
            LogicalName = "account", EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true,
        });
        account.EntityMetadata.IsDuplicateDetectionEnabled!.Value.Should().BeFalse();
    }

    /// <summary>The organization switch is a P11 setting: turning it off works (it unpublishes every rule, none here).</summary>
    [Fact]
    public async Task Turning_off_duplicate_detection_of_the_organization_works()
    {
        var organization = (await Admin.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("organizationid") }))
            .Entities[0].Id;
        foreach (var on in new[] { true, false })
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isduplicatedetectionenabled"] = on });

        (await Admin.RetrieveAsync("organization", organization, new ColumnSet("isduplicatedetectionenabled")))
            .GetAttributeValue<bool>("isduplicatedetectionenabled").Should().BeFalse();
    }

    /// <summary>
    /// The class of Important 1: a create (where a public create is allowed), an update and a delete of a row of each table a
    /// PostgreSQL organization seeds for a 2.7b feature succeeds (plan 2.7b, Task 10: no feature answers the P10 501 any more). Not here:
    /// <c>processtrigger</c>, <c>solutioncomponent</c> and the other tables no public write reaches on either engine.
    /// </summary>
    [Fact]
    public async Task Writes_on_the_tables_of_the_2_7b_features_succeed()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        async Task<Guid?> CreateAsync(Entity row)
        {
            Guid? id = null;
            await SucceedsAsync($"Create {row.LogicalName}", async () => id = await Admin.CreateAsync(row));
            return id;
        }
        Task UpdateAsync(Entity row) => SucceedsAsync($"Update {row.LogicalName}", () => Admin.UpdateAsync(row));
        Task DeleteAsync(string table, Guid id) => SucceedsAsync($"Delete {table}", () => Admin.DeleteAsync(table, id));

        // Solutions.
        var publisher = await CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = "pgpub" + tag, ["friendlyname"] = "pg publisher", ["customizationprefix"] = "pg" + tag[..3],
            ["customizationoptionvalueprefix"] = 20000 + Random.Shared.Next(0, 9999),
        });
        publisher.Should().NotBeNull("a publisher is P11 metadata");
        var solution = await CreateAsync(new Entity("solution")
        {
            ["uniquename"] = "pgsol" + tag, ["friendlyname"] = "pg solution", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisher!.Value),
        });
        if (solution is { } sol)
        {
            await UpdateAsync(new Entity("solution", sol) { ["friendlyname"] = "pg solution renamed" });
            await DeleteAsync("solution", sol);
        }
        await UpdateAsync(new Entity("publisher", publisher.Value) { ["friendlyname"] = "pg publisher renamed" });
        await DeleteAsync("publisher", publisher.Value);

        // Classic workflows: a Draft definition.
        if (await CreateAsync(new Entity("workflow")
            {
                ["name"] = "pg sweep " + tag, ["type"] = new OptionSetValue(1), ["category"] = new OptionSetValue(0), ["mode"] = new OptionSetValue(1),
                ["scope"] = new OptionSetValue(4), ["primaryentity"] = "account", ["xaml"] = WorkflowFixtures.Xaml("realtime-create-update"),
            }) is { } workflow)
        {
            await UpdateAsync(new Entity("workflow", workflow) { ["name"] = "pg sweep renamed " + tag, ["description"] = "edited" });
            await DeleteAsync("workflow", workflow);
        }

        // Duplicate detection: a rule and its condition.
        await EnableDuplicateDetectionAsync("contact");
        if (await CreateAsync(new Entity("duplicaterule")
            {
                ["name"] = "pg sweep " + tag, ["baseentityname"] = "contact", ["matchingentityname"] = "contact",
            }) is { } rule)
        {
            if (await CreateAsync(new Entity("duplicaterulecondition")
                {
                    ["regardingobjectid"] = new EntityReference("duplicaterule", rule), ["baseattributename"] = "lastname",
                    ["matchingattributename"] = "lastname", ["operatorcode"] = new OptionSetValue(0),
                }) is { } condition)
            {
                await UpdateAsync(new Entity("duplicaterulecondition", condition) { ["ignoreblankvalues"] = true });
                await DeleteAsync("duplicaterulecondition", condition);
            }
            await UpdateAsync(new Entity("duplicaterule", rule) { ["name"] = "pg sweep renamed " + tag, ["description"] = "edited" });
            await DeleteAsync("duplicaterule", rule);
        }

        // Webhooks (PostgreSQL runs them since 2.7b Task 7): an endpoint with its secret.
        if (await CreateAsync(new Entity("serviceendpoint")
            {
                ["name"] = "pg sweep " + tag, ["contract"] = new OptionSetValue(8), ["url"] = "https://example.com/hook",
                ["authtype"] = new OptionSetValue(5), ["authvalue"] = "<settings><setting name=\"x-key\" value=\"secret\" /></settings>",
            }) is { } endpoint)
        {
            await UpdateAsync(new Entity("serviceendpoint", endpoint) { ["name"] = "pg sweep renamed " + tag });
            await DeleteAsync("serviceendpoint", endpoint);
        }

        // A note (its body is file storage, which PostgreSQL runs since 2.7b Task 6).
        if (await CreateAsync(new Entity("annotation") { ["subject"] = "pg sweep " + tag }) is { } note)
        {
            await UpdateAsync(new Entity("annotation", note) { ["subject"] = "pg sweep renamed " + tag });
            await DeleteAsync("annotation", note);
        }
    }

    /// <summary>Runs <paramref name="act"/>, which must succeed; a failure fails the test, named <paramref name="what"/>.</summary>
    private static async Task SucceedsAsync(string what, Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{what} failed: {ex}");
        }
    }

    // ---- the host's startup ---------------------------------------------------------------------------------------------------

    /// <summary>Plan 2.7b, Task 8: the local identity provider and the SQL endpoint run on PostgreSQL, so the host builds with them.</summary>
    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public void The_host_starts_with_the_local_identity_provider_and_the_sql_endpoint_on(string provider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[StorageSettings.ProviderKey] = provider;
        builder.Configuration["Auth:Mode"] = "Local";
        builder.Configuration["Tds:Port"] = "5558";

        var act = () => builder.AddXrmd();

        act.Should().NotThrow();
    }
}
