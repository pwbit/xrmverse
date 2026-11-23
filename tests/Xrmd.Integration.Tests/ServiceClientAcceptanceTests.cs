using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.ServiceModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Extensions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 1 and Plan 3 (plug-ins) acceptance: the real Dataverse <see cref="ServiceClient"/> against xrmd.</summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class ServiceClientAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private Guid _adminUserId;

    public async Task InitializeAsync()
    {
        fx.DrainRequestLog(); // drop earlier tests' requests: DisposeAsync prints this test's ones
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid";
        cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
        _adminUserId = (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Prints the requests the client made (each test's handshake and operations) to the test output.</summary>
    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)), useUniqueInstance: true, new TestOutputLogger(output));

    [Fact]
    public void ServiceClient_connects_IsReady_true()
    {
        using var client = Connect();

        client.IsReady.Should().BeTrue(client.LastError);
        // Lazily loaded over the Web API login flow: RetrieveCurrentOrganization and WhoAmI.
        client.ConnectedOrgFriendlyName.Should().Be(ServiceClientFixture.OrgName);
        client.GetMyUserId().Should().Be(_adminUserId);
    }

    [Fact]
    public void ServiceClient_Create_Retrieve_Update_Delete_pb_project()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        var id = client.Create(new Entity(TestEntities.Project)
        {
            ["pb_name"] = "Apollo",
            ["pb_budget"] = new Money(1234.50m),
            ["pb_customerid"] = new EntityReference("systemuser", _adminUserId),
        });
        id.Should().NotBeEmpty();

        var retrieved = client.Retrieve(TestEntities.Project, id, new ColumnSet(true));
        retrieved.Id.Should().Be(id);
        retrieved.GetAttributeValue<string>("pb_name").Should().Be("Apollo");
        retrieved["pb_budget"].Should().BeOfType<Money>().Which.Value.Should().Be(1234.50m);
        var customer = retrieved["pb_customerid"].Should().BeOfType<EntityReference>().Subject;
        customer.LogicalName.Should().Be("systemuser");
        customer.Id.Should().Be(_adminUserId);

        client.Update(new Entity(TestEntities.Project, id) { ["pb_name"] = "Apollo 2", ["pb_budget"] = new Money(99m) });
        var updated = client.Retrieve(TestEntities.Project, id, new ColumnSet("pb_name", "pb_budget"));
        updated.GetAttributeValue<string>("pb_name").Should().Be("Apollo 2");
        updated.GetAttributeValue<Money>("pb_budget").Value.Should().Be(99m);

        client.Delete(TestEntities.Project, id);

        var act = () => client.Retrieve(TestEntities.Project, id, new ColumnSet(true));
        act.Should().Throw<FaultException<OrganizationServiceFault>>()
            .Which.Detail.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public void ServiceClient_creates_account_and_contact_with_parentcustomerid()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        var accountId = client.Create(new Entity("account") { ["name"] = "Fennwick Tiles" });
        var contactId = client.Create(new Entity("contact")
        {
            ["firstname"] = "Jane", ["lastname"] = "Doe", ["parentcustomerid"] = new EntityReference("account", accountId),
        });
        var contact2Id = client.Create(new Entity("contact")
        {
            ["lastname"] = "Roe", ["parentcustomerid"] = new EntityReference("contact", contactId),
        });
        try
        {
            var contact = client.Retrieve("contact", contactId, new ColumnSet("fullname", "parentcustomerid"));
            var parent = contact["parentcustomerid"].Should().BeOfType<EntityReference>().Subject;
            parent.LogicalName.Should().Be("account");
            parent.Id.Should().Be(accountId);
            contact.GetAttributeValue<string>("fullname").Should().Be("Doe, Jane");

            var second = client.Retrieve("contact", contact2Id, new ColumnSet("parentcustomerid"));
            second.GetAttributeValue<EntityReference>("parentcustomerid").LogicalName.Should().Be("contact");
        }
        finally
        {
            client.Delete("contact", contact2Id);
            client.Delete("contact", contactId);
            client.Delete("account", accountId);
        }
    }

    [Fact]
    public void ServiceClient_rejects_an_unprefixed_custom_table()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        var act = () => client.Execute(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = "nopub_thing", SchemaName = "nopub_thing", OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = "nopub_name", SchemaName = "nopub_name", MaxLength = 100 },
        });

        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.Message.Should().Contain("prefix");
    }

    [Fact]
    public void ServiceClient_with_UseWebApi_Create_Update_Delete_pb_project()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        // Create/Update/Delete go to the Web API. UseWebApi is stored in a process-wide ConfigurationOptions singleton,
        // not per client: it is reset in finally, and only this sequential "serviceclient" collection uses ServiceClient.
        client.UseWebApi = true;
        try
        {
            var id = client.Create(new Entity(TestEntities.Project)
            {
                ["pb_name"] = "Gemini",
                ["pb_budget"] = new Money(10m),
                ["pb_customerid"] = new EntityReference("systemuser", _adminUserId),
            });
            id.Should().NotBeEmpty();

            // Retrieve always goes over SOAP (ServiceClient 1.2.27 never translates it to the Web API).
            var created = client.Retrieve(TestEntities.Project, id, new ColumnSet("pb_budget", "pb_customerid"));
            created.GetAttributeValue<Money>("pb_budget").Value.Should().Be(10m);
            created.GetAttributeValue<EntityReference>("pb_customerid").Id.Should().Be(_adminUserId); // sent as pb_customerid@odata.bind

            client.Update(new Entity(TestEntities.Project, id) { ["pb_name"] = "Gemini 2" });
            client.Retrieve(TestEntities.Project, id, new ColumnSet("pb_name")).GetAttributeValue<string>("pb_name").Should().Be("Gemini 2");

            client.Delete(TestEntities.Project, id);
            var act = () => client.Retrieve(TestEntities.Project, id, new ColumnSet(true));
            act.Should().Throw<FaultException<OrganizationServiceFault>>()
                .Which.Detail.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        }
        finally
        {
            client.UseWebApi = false;
        }
    }

    [Fact]
    public void ServiceClient_Execute_WhoAmI_returns_admin()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        var response = (WhoAmIResponse)client.Execute(new WhoAmIRequest());

        response.UserId.Should().Be(_adminUserId);
        response.BusinessUnitId.Should().NotBeEmpty();
        response.OrganizationId.Should().NotBeEmpty();
    }

    private Guid[] SeedProjects(ServiceClient client, string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => client.Create(new Entity(TestEntities.Project)
        {
            ["pb_name"] = $"{prefix}-{i:D2}",
            ["pb_priority"] = new OptionSetValue(i % 3 + 1),
            ["pb_count"] = i,
        })).ToArray();

    [Fact]
    public void ServiceClient_RetrieveMultiple_QueryExpression_pages()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var prefix = "qp" + Guid.NewGuid().ToString("N")[..8];
        var created = SeedProjects(client, prefix, 12);

        var query = new QueryExpression(TestEntities.Project)
        {
            ColumnSet = new ColumnSet("pb_name"),
            PageInfo = new PagingInfo { Count = 5, PageNumber = 1 },
        };
        query.Criteria.AddCondition("pb_name", ConditionOperator.BeginsWith, prefix);

        var ids = new List<Guid>();
        EntityCollection page;
        do
        {
            page = client.RetrieveMultiple(query);
            ids.AddRange(page.Entities.Select(e => e.Id));
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        } while (page.MoreRecords);

        ids.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        ids.Should().BeEquivalentTo(created);
    }

    [Fact]
    public void ServiceClient_RetrieveMultiple_FetchExpression_aggregate()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var prefix = "qa" + Guid.NewGuid().ToString("N")[..8];
        SeedProjects(client, prefix, 12);

        var fetch = $"""
            <fetch aggregate="true">
              <entity name="pb_project">
                <attribute name="pb_projectid" alias="n" aggregate="count" />
                <filter><condition attribute="pb_name" operator="begins-with" value="{prefix}" /></filter>
              </entity>
            </fetch>
            """;
        var result = client.RetrieveMultiple(new FetchExpression(fetch));

        result.Entities.Should().ContainSingle();
        var n = result.Entities[0]["n"].Should().BeOfType<AliasedValue>().Subject;
        Convert.ToInt32(n.Value).Should().Be(12);
    }

    [Fact]
    public void ServiceClient_RetrieveMultiple_QueryByAttribute()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var prefix = "qb" + Guid.NewGuid().ToString("N")[..8];
        var ids = SeedProjects(client, prefix, 6); // priorities 1,2,3,1,2,3

        var query = new QueryByAttribute(TestEntities.Project)
        {
            ColumnSet = new ColumnSet("pb_name", "pb_priority"),
        };
        query.AddAttributeValue("pb_priority", 3);
        query.AddOrder("pb_count", OrderType.Ascending);

        var result = client.RetrieveMultiple(query);

        var mine = result.Entities.Where(e => e.GetAttributeValue<string>("pb_name").StartsWith(prefix)).ToList();
        mine.Select(e => e.Id).Should().Equal(ids[2], ids[5]);
        mine.Should().OnlyContain(e => e.GetAttributeValue<OptionSetValue>("pb_priority").Value == 3);
    }

    [Fact]
    public void ServiceClient_Retrieve_has_formatted_values()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var id = client.Create(new Entity(TestEntities.Project)
        {
            ["pb_name"] = "fv" + Guid.NewGuid().ToString("N")[..8],
            ["pb_priority"] = new OptionSetValue(3),
        });

        var e = client.Retrieve(TestEntities.Project, id, new ColumnSet("pb_priority"));

        e.FormattedValues["pb_priority"].Should().Be("High");
    }

    /// <summary>The first id returned by a RetrieveMultiple of <paramref name="entity"/> where every condition holds.</summary>
    private static Guid FindId(ServiceClient client, string entity, params (string Attribute, object Value)[] conditions)
    {
        var query = new QueryExpression(entity) { ColumnSet = new ColumnSet(false) };
        foreach (var (attribute, value) in conditions) query.Criteria.AddCondition(attribute, ConditionOperator.Equal, value);
        return client.RetrieveMultiple(query).Entities.Should().ContainSingle().Subject.Id;
    }

    // ---- auditing (plan 13) ------------------------------------------------------------------------------------------

    /// <summary>Switches auditing of the organization on or off, as an administrator would with an Update of the row.</summary>
    private static void SetOrganizationAudit(ServiceClient client, bool enabled)
    {
        var orgId = ((WhoAmIResponse)client.Execute(new WhoAmIRequest())).OrganizationId;
        client.Update(new Entity("organization", orgId) { ["isauditenabled"] = enabled });
    }

    /// <summary>Retrieves a column definition, sets its <c>IsAuditEnabled</c> and sends it back, as a metadata tool does.</summary>
    private static void SetColumnAudit(ServiceClient client, string entity, string column, bool enabled)
    {
        var attribute = ((Microsoft.Xrm.Sdk.Messages.RetrieveAttributeResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveAttributeRequest
        {
            EntityLogicalName = entity, LogicalName = column,
        })).AttributeMetadata;
        attribute.IsAuditEnabled = new Microsoft.Xrm.Sdk.BooleanManagedProperty(enabled);
        client.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateAttributeRequest { EntityName = entity, Attribute = attribute });
    }

    private static AuditDetailCollection RecordHistory(ServiceClient client, string entity, Guid id) =>
        ((RetrieveRecordChangeHistoryResponse)client.Execute(new RetrieveRecordChangeHistoryRequest
        {
            Target = new EntityReference(entity, id),
        })).AuditDetailCollection;

    [Fact]
    public void ServiceClient_update_on_audited_column_is_in_record_change_history()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var name = "au" + Guid.NewGuid().ToString("N")[..8];
        SetOrganizationAudit(client, true);
        SetColumnAudit(client, "account", "telephone1", true);
        var id = client.Create(new Entity("account") { ["name"] = name, ["telephone1"] = "555-0100" });
        try
        {
            client.Update(new Entity("account", id) { ["telephone1"] = "555-0199" });

            var history = RecordHistory(client, "account", id);

            history.AuditDetails.Should().HaveCount(2); // the Update, then the Create (newest first)
            var update = history.AuditDetails[0].Should().BeOfType<AttributeAuditDetail>().Subject;
            update.AuditRecord.GetAttributeValue<OptionSetValue>("action").Value.Should().Be(2);
            update.AuditRecord.GetAttributeValue<EntityReference>("userid").Id.Should().Be(_adminUserId);
            update.OldValue.GetAttributeValue<string>("telephone1").Should().Be("555-0100");
            update.NewValue.GetAttributeValue<string>("telephone1").Should().Be("555-0199");
            update.NewValue.Attributes.Keys.Should().Equal("telephone1"); // only what changed

            var create = history.AuditDetails[1].Should().BeOfType<AttributeAuditDetail>().Subject;
            create.AuditRecord.GetAttributeValue<OptionSetValue>("action").Value.Should().Be(1);
            create.NewValue.GetAttributeValue<string>("name").Should().Be(name);
        }
        finally
        {
            client.Delete("account", id);
            client.Execute(new DeleteRecordChangeHistoryRequest { Target = new EntityReference("account", id) });
            SetOrganizationAudit(client, false);
        }
    }

    [Fact]
    public void ServiceClient_disabling_column_audit_suppresses_history()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        SetOrganizationAudit(client, true);
        var id = client.Create(new Entity("account") { ["name"] = "ad" + Guid.NewGuid().ToString("N")[..8], ["telephone1"] = "555-0100" });
        try
        {
            SetColumnAudit(client, "account", "telephone1", false);

            client.Update(new Entity("account", id) { ["telephone1"] = "555-0101" });
            client.Update(new Entity("account", id) { ["telephone1"] = "555-0102", ["websiteurl"] = "https://example.org" });

            var history = RecordHistory(client, "account", id);
            history.AuditDetails.Should().HaveCount(2, "the Create and the second Update; the first Update changed only a column that is not audited");
            var update = history.AuditDetails[0].Should().BeOfType<AttributeAuditDetail>().Subject;
            update.NewValue.Attributes.Keys.Should().Equal("websiteurl");
            update.OldValue.Contains("telephone1").Should().BeFalse();

            var column = ((RetrieveAttributeChangeHistoryResponse)client.Execute(new RetrieveAttributeChangeHistoryRequest
            {
                Target = new EntityReference("account", id), AttributeLogicalName = "telephone1",
            })).AuditDetailCollection;
            column.AuditDetails.Should().ContainSingle("only the Create, written while the column was audited");
        }
        finally
        {
            SetColumnAudit(client, "account", "telephone1", true);
            client.Delete("account", id);
            client.Execute(new DeleteRecordChangeHistoryRequest { Target = new EntityReference("account", id) });
            SetOrganizationAudit(client, false);
        }
    }

    [Fact]
    public void ServiceClient_retrieve_audit_details_and_delete_record_change_history()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        SetOrganizationAudit(client, true);
        var id = client.Create(new Entity("account") { ["name"] = "ar" + Guid.NewGuid().ToString("N")[..8], ["telephone1"] = "555-0100" });
        try
        {
            client.Update(new Entity("account", id) { ["telephone1"] = "555-0101" });

            // The audit rows themselves are readable with prvReadAuditSummary (System Administrator holds it).
            var query = new QueryExpression("audit") { ColumnSet = new ColumnSet("action") };
            query.Criteria.AddCondition("objectid", ConditionOperator.Equal, id);
            var rows = client.RetrieveMultiple(query).Entities;
            rows.Select(r => r.GetAttributeValue<OptionSetValue>("action").Value).Should().BeEquivalentTo([1, 2]); // Create, Update
            var updateRow = rows.Single(r => r.GetAttributeValue<OptionSetValue>("action").Value == 2);

            var detail = ((RetrieveAuditDetailsResponse)client.Execute(new RetrieveAuditDetailsRequest { AuditId = updateRow.Id })).AuditDetail;
            var attribute = detail.Should().BeOfType<AttributeAuditDetail>().Subject;
            attribute.AuditRecord.Id.Should().Be(updateRow.Id);
            attribute.OldValue.GetAttributeValue<string>("telephone1").Should().Be("555-0100");
            attribute.NewValue.GetAttributeValue<string>("telephone1").Should().Be("555-0101");

            var deleted = (DeleteRecordChangeHistoryResponse)client.Execute(new DeleteRecordChangeHistoryRequest
            {
                Target = new EntityReference("account", id),
            });
            deleted.DeletedEntriesCount.Should().Be(2);
            RecordHistory(client, "account", id).AuditDetails.Should().BeEmpty();
        }
        finally
        {
            client.Delete("account", id);
            client.Execute(new DeleteRecordChangeHistoryRequest { Target = new EntityReference("account", id) });
            SetOrganizationAudit(client, false);
        }
    }

    [Fact]
    public async Task ServiceClient_registers_plugins_like_the_Plugin_Registration_Tool()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<Xrmd.Metadata.SdkMessageCatalog>().EnsureAsync(default);
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        try
        {
            var assemblyId = client.Create(new Entity("pluginassembly")
            {
                ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
                ["sourcetype"] = new OptionSetValue(0), // database
                ["isolationmode"] = new OptionSetValue(2), // sandbox
            });
            client.Retrieve("pluginassembly", assemblyId, new ColumnSet("name")).GetAttributeValue<string>("name")
                .Should().Be("Xrmd.TestPlugins");

            Guid Type(string name) => client.Create(new Entity("plugintype")
            {
                ["typename"] = "Xrmd.TestPlugins." + name,
                ["name"] = name,
                ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
            });

            Guid Step(Guid type, string message, int stage, int mode, string config, string? filteringAttributes = null)
            {
                var messageId = FindId(client, "sdkmessage", ("name", message));
                var filterId = FindId(client, "sdkmessagefilter", ("sdkmessageid", messageId), ("primaryobjecttypecode", TestEntities.Pipe));
                var step = new Entity("sdkmessageprocessingstep")
                {
                    ["name"] = $"{message} of {TestEntities.Pipe}",
                    ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
                    ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
                    ["eventhandler"] = new EntityReference("plugintype", type),
                    ["stage"] = new OptionSetValue(stage),
                    ["mode"] = new OptionSetValue(mode),
                    ["rank"] = 1,
                    ["configuration"] = config,
                };
                if (filteringAttributes is not null) step["filteringattributes"] = filteringAttributes;
                return client.Create(step);
            }

            Step(Type("SetAttributePlugin"), "Create", 20, 0, "pb_data=from-plugin");
            var recorder = Step(Type("ContextRecorderPlugin"), "Update", 40, 0, TestEntities.PipeLog);
            client.Create(new Entity("sdkmessageprocessingstepimage")
            {
                ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", recorder),
                ["imagetype"] = new OptionSetValue(0),
                ["entityalias"] = "pre",
                ["name"] = "pre",
                ["attributes"] = "pb_name",
                ["messagepropertyname"] = "Target",
            });
            Step(Type("ThrowPlugin"), "Update", 20, 0, "invalid:Count is locked.", filteringAttributes: "pb_count");

            var marker = "sc" + Guid.NewGuid().ToString("N")[..8];
            var id = client.Create(new Entity(TestEntities.Pipe) { ["pb_name"] = marker });
            client.Retrieve(TestEntities.Pipe, id, new ColumnSet("pb_data")).GetAttributeValue<string>("pb_data")
                .Should().Be("from-plugin");

            client.Update(new Entity(TestEntities.Pipe, id) { ["pb_name"] = marker + "-2" });
            var logs = new QueryExpression(TestEntities.PipeLog) { ColumnSet = new ColumnSet("pb_data") };
            logs.Criteria.AddCondition("pb_name", ConditionOperator.Equal, "Update|40|0|1");
            var log = client.RetrieveMultiple(logs).Entities.Should().ContainSingle().Subject.GetAttributeValue<string>("pb_data");
            log.Split('\n').Should().Contain("pre=pre:pb_name");
            client.Retrieve(TestEntities.Pipe, id, new ColumnSet("pb_name")).GetAttributeValue<string>("pb_name").Should().Be(marker + "-2");

            var act = () => client.Update(new Entity(TestEntities.Pipe, id) { ["pb_count"] = 5 });
            act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.Message.Should().Be("Count is locked.");
            client.Retrieve(TestEntities.Pipe, id, new ColumnSet("pb_count")).GetAttributeValue<int?>("pb_count").Should().BeNull();
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx.ConnectionString);
            fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
        }
    }

    // ---- field-level security (plan 14) ------------------------------------------------------------------------------

    private const string FsTable = "pb_fsdemo";
    private const string FsSalary = "pb_fssalary", FsCode = "pb_fscode", FsCity = "pb_fscity";
    private static readonly SemaphoreSlim FsLock = new(1, 1);

    /// <summary>
    /// <c>pb_fsdemo</c> (user-owned): <c>pb_name</c> primary name, <c>pb_fssalary</c> (Integer) and <c>pb_fscode</c> (String) secured,
    /// <c>pb_fscity</c> (String) not secured. Created once per database.
    /// </summary>
    private async Task EnsureFieldSecurityTableAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await FsLock.WaitAsync();
        try
        {
            if (store.GetEntity(FsTable) is not null) return;
            var entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = FsTable, SchemaName = "pb_FsDemo", OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
            };
            entity.SetAttributes(
            [
                new Microsoft.Xrm.Sdk.Metadata.IntegerAttributeMetadata { LogicalName = FsSalary, SchemaName = "pb_FsSalary", IsSecured = true },
                new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = FsCode, SchemaName = "pb_FsCode", MaxLength = 50, IsSecured = true },
                new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = FsCity, SchemaName = "pb_FsCity", MaxLength = 100 },
            ]);
            await store.CreateEntityAsync(entity,
                new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
        }
        finally
        {
            FsLock.Release();
        }
    }

    /// <summary>
    /// Through the administrator's client, as an admin script would: a new user in the root business unit holding a role with
    /// Global create, read and write on <c>pb_fsdemo</c>, and a new field security profile with the given column permissions.
    /// Returns the user's Entra object id. What it creates is recorded for <see cref="RemoveFieldSecurityUsers"/>.
    /// </summary>
    private Guid CreateFieldSecurityUser(ServiceClient admin, string label, params (string Column, bool Read, bool Create, bool Update)[] permissions)
    {
        var tag = label + Guid.NewGuid().ToString("N")[..8];
        var rootBusinessUnit = ((WhoAmIResponse)admin.Execute(new WhoAmIRequest())).BusinessUnitId;
        var oid = Guid.NewGuid();
        var user = admin.Create(new Entity("systemuser")
        {
            ["fullname"] = $"FS {tag}",
            ["domainname"] = $"fs-{tag}@example.test",
            ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", rootBusinessUnit),
        });
        _fieldSecurityUsers.Add(user);
        var role = admin.Create(new Entity("role") { ["name"] = $"FS role {tag}" });
        _fieldSecurityRoles.Add(role);
        admin.Execute(new AddPrivilegesRoleRequest
        {
            RoleId = role,
            Privileges = new[] { "prvCreatepb_FsDemo", "prvReadpb_FsDemo", "prvWritepb_FsDemo" }
                .Select(name => new RolePrivilege { PrivilegeName = name, Depth = PrivilegeDepth.Global }).ToArray(),
        });
        admin.Associate("systemuser", user, new Relationship("systemuserroles_association"),
            new EntityReferenceCollection { new EntityReference("role", role) });

        var profile = admin.Create(new Entity("fieldsecurityprofile") { ["name"] = $"FS profile {tag}" });
        _fieldSecurityProfiles.Add(profile);
        foreach (var (column, read, create, update) in permissions)
            admin.Create(new Entity("fieldpermission")
            {
                ["fieldsecurityprofileid"] = new EntityReference("fieldsecurityprofile", profile),
                ["entityname"] = FsTable,
                ["attributelogicalname"] = column,
                ["canread"] = new OptionSetValue(read ? 4 : 0),
                ["cancreate"] = new OptionSetValue(create ? 4 : 0),
                ["canupdate"] = new OptionSetValue(update ? 4 : 0),
            });
        admin.Associate("systemuser", user, new Relationship("systemuserprofiles_association"),
            new EntityReferenceCollection { new EntityReference("fieldsecurityprofile", profile) });
        return oid;
    }

    private readonly List<Guid> _fieldSecurityUsers = [], _fieldSecurityRoles = [], _fieldSecurityProfiles = [];

    /// <summary>
    /// Removes what <see cref="CreateFieldSecurityUser"/> created: the profiles (with their permissions and assignments) and
    /// the roles are deleted; the users, which cannot be deleted, are disabled. Best effort, for the finally of each test.
    /// </summary>
    private void RemoveFieldSecurityUsers(ServiceClient admin)
    {
        foreach (var (table, ids) in new[] { ("fieldsecurityprofile", _fieldSecurityProfiles), ("role", _fieldSecurityRoles) })
            foreach (var id in ids)
                try
                {
                    admin.Delete(table, id);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    output.WriteLine($"Cleanup: {table} {id} was not deleted: {ex.Message}");
                }
        foreach (var user in _fieldSecurityUsers)
            try
            {
                admin.Update(new Entity("systemuser", user) { ["isdisabled"] = true });
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                output.WriteLine($"Cleanup: systemuser {user} was not disabled: {ex.Message}");
            }
        _fieldSecurityProfiles.Clear();
        _fieldSecurityRoles.Clear();
        _fieldSecurityUsers.Clear();
    }

    private ServiceClient ConnectAs(Guid objectId)
    {
        var client = new ServiceClient(new Uri(ServiceClientFixture.BaseUrl),
            _ => Task.FromResult(DevTokenFactory.Create(objectId, ServiceClientFixture.BaseUrl)), useUniqueInstance: true, new TestOutputLogger(output));
        client.IsReady.Should().BeTrue(client.LastError);
        return client;
    }

    private static int FaultCode(Action act) =>
        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode;

    [Fact]
    public async Task ServiceClient_two_users_with_different_profiles_see_different_columns()
    {
        await EnsureFieldSecurityTableAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var name = "fs" + Guid.NewGuid().ToString("N")[..8];
        var id = Guid.Empty;
        try
        {
            var salaryReader = CreateFieldSecurityUser(admin, "sal", (FsSalary, true, false, false));
            var codeReader = CreateFieldSecurityUser(admin, "cod", (FsCode, true, false, false));
            id = admin.Create(new Entity(FsTable) { ["pb_name"] = name, [FsSalary] = 52000, [FsCode] = "C-17", [FsCity] = "Turin" });
            var columns = new ColumnSet("pb_name", FsSalary, FsCode, FsCity);

            // The administrator bypasses field security and reads everything.
            var all = admin.Retrieve(FsTable, id, columns);
            all.GetAttributeValue<int?>(FsSalary).Should().Be(52000);
            all.GetAttributeValue<string>(FsCode).Should().Be("C-17");

            // Each user reads the same record and gets the secured column of its own profile; the other one comes back as null.
            using (var salary = ConnectAs(salaryReader))
            {
                var seen = salary.Retrieve(FsTable, id, columns);
                seen.GetAttributeValue<int?>(FsSalary).Should().Be(52000);
                seen.GetAttributeValue<string>(FsCode).Should().BeNull();
                seen.GetAttributeValue<string>(FsCity).Should().Be("Turin");
                var fetched = salary.RetrieveMultiple(new FetchExpression($"""
                    <fetch><entity name="{FsTable}"><attribute name="{FsSalary}" /><attribute name="{FsCode}" />
                    <filter><condition attribute="pb_name" operator="eq" value="{name}" /></filter></entity></fetch>
                    """)).Entities.Should().ContainSingle().Subject;
                fetched.GetAttributeValue<int?>(FsSalary).Should().Be(52000);
                fetched.GetAttributeValue<string>(FsCode).Should().BeNull();
            }

            using (var code = ConnectAs(codeReader))
            {
                var seen = code.Retrieve(FsTable, id, columns);
                seen.GetAttributeValue<int?>(FsSalary).Should().BeNull();
                seen.GetAttributeValue<string>(FsCode).Should().Be("C-17");
                seen.GetAttributeValue<string>(FsCity).Should().Be("Turin");
            }

            // The Web API leaves the unreadable column out of the JSON.
            var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(FsTable)!.EntitySetName;
            using var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl) };
            var request = new HttpRequestMessage(HttpMethod.Get, $"api/data/v9.2/{set}({id})?$select=pb_name,{FsSalary},{FsCode}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                DevTokenFactory.Create(codeReader, ServiceClientFixture.BaseUrl));
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.IsSuccessStatusCode.Should().BeTrue(body);
            var json = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
            json[FsCode]!.GetValue<string>().Should().Be("C-17");
            json.ContainsKey(FsSalary).Should().BeFalse();
        }
        finally
        {
            if (id != Guid.Empty) admin.Delete(FsTable, id);
            RemoveFieldSecurityUsers(admin);
        }
    }

    [Fact]
    public async Task ServiceClient_fetchxml_filter_on_secured_column_fails()
    {
        await EnsureFieldSecurityTableAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var id = Guid.Empty;
        try
        {
            var salaryReader = CreateFieldSecurityUser(admin, "flt", (FsSalary, true, false, false));
            id = admin.Create(new Entity(FsTable) { ["pb_name"] = "fs" + Guid.NewGuid().ToString("N")[..8], [FsCode] = "C-42" });
            using var client = ConnectAs(salaryReader);

            // A filter on a secured column the user cannot read is an error, never an empty result.
            FaultCode(() => client.RetrieveMultiple(new FetchExpression($"""
                <fetch><entity name="{FsTable}"><attribute name="pb_name" />
                <filter><condition attribute="{FsCode}" operator="eq" value="C-42" /></filter></entity></fetch>
                """))).Should().Be(-2147158780);
            // The same holds for an order on it; a filter on the readable secured column works.
            FaultCode(() => client.RetrieveMultiple(new FetchExpression($"""
                <fetch><entity name="{FsTable}"><attribute name="pb_name" /><order attribute="{FsCode}" /></entity></fetch>
                """))).Should().Be(-2147158780);
            var query = new QueryExpression(FsTable) { ColumnSet = new ColumnSet("pb_name") };
            query.Criteria.AddCondition(FsSalary, ConditionOperator.Null);
            query.Criteria.AddCondition("pb_fsdemoid", ConditionOperator.Equal, id);
            client.RetrieveMultiple(query).Entities.Should().ContainSingle();
        }
        finally
        {
            if (id != Guid.Empty) admin.Delete(FsTable, id);
            RemoveFieldSecurityUsers(admin);
        }
    }

    [Fact]
    public async Task ServiceClient_update_secured_column_without_permission_fails()
    {
        await EnsureFieldSecurityTableAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var id = Guid.Empty;
        var created = Guid.Empty;
        try
        {
            var reader = CreateFieldSecurityUser(admin, "rdo", (FsSalary, true, false, false));
            var writer = CreateFieldSecurityUser(admin, "wrt", (FsSalary, true, true, true));
            id = admin.Create(new Entity(FsTable) { ["pb_name"] = "fs" + Guid.NewGuid().ToString("N")[..8], [FsSalary] = 100 });
            using (var client = ConnectAs(reader))
            {
                // Read-only on the column: an Update naming it fails, even with the value it already has.
                FaultCode(() => client.Update(new Entity(FsTable, id) { [FsSalary] = 200 })).Should().Be(-2147158777);
                FaultCode(() => client.Update(new Entity(FsTable, id) { [FsSalary] = 100 })).Should().Be(-2147158777);
                // Create with a value needs create permission; the plain columns stay writable.
                FaultCode(() => client.Create(new Entity(FsTable) { ["pb_name"] = "fs-denied", [FsSalary] = 1 })).Should().Be(-2147158782);
                client.Update(new Entity(FsTable, id) { [FsCity] = "Milan" });
            }
            admin.Retrieve(FsTable, id, new ColumnSet(FsSalary)).GetAttributeValue<int?>(FsSalary).Should().Be(100);

            using (var client = ConnectAs(writer))
            {
                client.Update(new Entity(FsTable, id) { [FsSalary] = 300 });
                created = client.Create(new Entity(FsTable) { ["pb_name"] = "fs-allowed", [FsSalary] = 1 });
            }
            admin.Retrieve(FsTable, id, new ColumnSet(FsSalary)).GetAttributeValue<int?>(FsSalary).Should().Be(300);
        }
        finally
        {
            if (id != Guid.Empty) admin.Delete(FsTable, id);
            if (created != Guid.Empty) admin.Delete(FsTable, created);
            RemoveFieldSecurityUsers(admin);
        }
    }

    // ---- custom APIs (plan 15) --------------------------------------------------------------------------------------

    /// <summary>One request parameter of each of the 13 custom API field types, by name and type.</summary>
    private static readonly (string Name, int Type)[] CustomApiTypes =
    [
        ("Bool", 0), ("Date", 1), ("Dec", 2), ("Ent", 3), ("Ents", 4), ("Ref", 5), ("Flt", 6), ("Int", 7), ("Cash", 8), ("Pick", 9),
        ("Text", 10), ("Texts", 11), ("Id", 12),
    ];

    /// <summary>The echo API of plan 15, task 4: every type in (only <c>Text</c> required), each one back as <c>{name}Out</c>.</summary>
    private static readonly TestApi EchoApi = new("xt_Echo", AllowedStepType: 2, PluginTypeName: "Xrmd.TestPlugins.CustomApiEchoPlugin",
        Requests: CustomApiTypes.Select(t => (t.Name, t.Type, t.Name != "Text", (string?)null)).ToArray(),
        Responses: CustomApiTypes.Select(t => (t.Name + "Out", t.Type)).Append(("Context", 10)).ToArray());

    /// <summary>Imports <paramref name="solution"/> through the client, then removes every imported plug-in, step and API at the end.</summary>
    private async Task WithImportedSolutionAsync(ServiceClient client, byte[] solution, Func<Task> body)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>()); // the cleanup empties pb_pipelog
        try
        {
            client.Execute(new ImportSolutionRequest { CustomizationFile = solution, ImportJobId = Guid.NewGuid() });
            await body();
        }
        finally
        {
            await SolutionImportTests.ClearImportAsync(fx.App.Services, fx.ConnectionString);
        }
    }

    [Fact]
    public async Task ServiceClient_calls_custom_api_with_all_types()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WithImportedSolutionAsync(client, CustomApiTestSolution.Build("XtAcceptanceApis", EchoApi), () =>
        {
            var values = new Dictionary<string, object>
            {
                ["Bool"] = true,
                ["Date"] = new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc),
                ["Dec"] = 12.345m,
                ["Ent"] = new Entity("contact", Guid.NewGuid()) { ["firstname"] = "Ada" },
                ["Ents"] = new EntityCollection([new Entity("account") { ["name"] = "One" }, new Entity("account") { ["name"] = "Two" }]),
                ["Ref"] = new EntityReference("systemuser", Guid.NewGuid()),
                ["Flt"] = 2.5d,
                ["Int"] = 42,
                ["Cash"] = new Money(9.99m),
                ["Pick"] = new OptionSetValue(3),
                ["Text"] = "abc",
                ["Texts"] = new[] { "a", "b" },
                ["Id"] = Guid.NewGuid(),
            };
            var request = new OrganizationRequest("xt_Echo");
            foreach (var (name, value) in values) request[name] = value;

            // SOAP Execute by name: the plug-in type runs as the main operation and echoes every value with its SDK type
            // (the data contract's ExtensionData is a serialization detail, not a value).
            var response = client.Execute(request);

            response.ResponseName.Should().Be("xt_Echo");
            response.Results.Keys.Should().BeEquivalentTo(CustomApiTypes.Select(t => t.Name + "Out").Append("Context"));
            foreach (var (name, value) in values)
                response.Results[name + "Out"].Should().BeEquivalentTo(value,
                    o => o.RespectingRuntimeTypes().Excluding((FluentAssertions.Equivalency.IMemberInfo m) => m.Name == "ExtensionData"), name);
            response.Results["Context"].Should().Be(
                $"Stage=30;PrimaryEntityName=none;PrimaryEntityId={Guid.Empty};UserId={_adminUserId};Outputs=");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ServiceClient_missing_required_parameter_fails()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WithImportedSolutionAsync(client, CustomApiTestSolution.Build("XtAcceptanceApis", EchoApi), () =>
        {
            // Text is the one required parameter: a fault with InvalidArgument (0x80040203) naming it, before anything runs.
            var fault = client.Invoking(c => c.Execute(new OrganizationRequest("xt_Echo") { ["Int"] = 1 }))
                .Should().Throw<FaultException<OrganizationServiceFault>>().Which;
            fault.Detail.ErrorCode.Should().Be(-2147220989);
            fault.Detail.Message.Should().Contain("Text");
            return Task.CompletedTask;
        });
    }

    [OpenProject365Fact]
    public async Task ServiceClient_invokes_a_custom_api_of_a_real_export()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var zip = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));

        // The unmodified export (autonumber columns included); op365_ExpireAgentProposals is global, takes no parameter and
        // returns an Integer Count: 0 on an empty database. A Count of 0 is also the default an unset property gets (C7), so the
        // trace row of the export's plug-in type, written with tracing set to All, is what shows the main operation ran.
        // The cleanup removes the solution, its APIs, plug-ins and publisher rows it knows; the export's tables (op365_*) stay
        // in this fixture's database, which no other test reads.
        await WithImportedSolutionAsync(client, zip, async () =>
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            async Task<object?> ScalarAsync(string sql)
            {
                await using var cmd = TestSql.Command(conn, sql);
                return await cmd.ExecuteScalarAsync();
            }
            var typeName = (string)(await ScalarAsync("""
                SELECT t.typename FROM data.customapi AS c JOIN data.plugintype AS t ON t.plugintypeid = c.plugintypeid
                WHERE c.uniquename = N'op365_ExpireAgentProposals'
                """))!;
            var setting = Convert.ToInt32(await ScalarAsync("SELECT plugintracelogsetting FROM data.organization"));
            var traces = $"SELECT COUNT(*) FROM data.plugintracelog WHERE typename = N'{typeName.Replace("'", "''")}' AND messagename = N'op365_ExpireAgentProposals'";
            var before = Convert.ToInt32(await ScalarAsync(traces));
            OrganizationResponse response;
            await ScalarAsync("UPDATE data.organization SET plugintracelogsetting = 2");
            try
            {
                response = client.Execute(new OrganizationRequest("op365_ExpireAgentProposals"));
            }
            finally
            {
                await ScalarAsync($"UPDATE data.organization SET plugintracelogsetting = {setting}");
            }
            response.Results.Should().ContainKey("Count");
            response.Results["Count"].Should().BeOfType<int>().Which.Should().Be(0);
            (Convert.ToInt32(await ScalarAsync(traces))).Should().Be(before + 1, "the export's plug-in type ran as the main operation");

            using var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl) };
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/data/v9.2/op365_ExpireAgentProposals")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl));
            using var web = await http.SendAsync(request);
            var body = await web.Content.ReadAsStringAsync();
            web.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
            var json = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
            json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.op365_ExpireAgentProposalsResponse");
            json["Count"]!.GetValue<int>().Should().Be(0);
        });
    }

    // ---- hierarchy security (plan 2.5) -------------------------------------------------------------------------------

    /// <summary>
    /// A new user in the root business unit holding a role with Basic read and write on <c>account</c> (own records only), and
    /// the given manager. Returns the user's id and Entra object id; the user and role are recorded for <see cref="RemoveFieldSecurityUsers"/>.
    /// </summary>
    private (Guid Id, Guid Oid) CreateHierarchyUser(ServiceClient admin, string label, Guid? manager)
    {
        var tag = label + Guid.NewGuid().ToString("N")[..8];
        var rootBusinessUnit = ((WhoAmIResponse)admin.Execute(new WhoAmIRequest())).BusinessUnitId;
        var oid = Guid.NewGuid();
        var user = new Entity("systemuser")
        {
            ["fullname"] = $"HS {tag}",
            ["domainname"] = $"hs-{tag}@example.test",
            ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", rootBusinessUnit),
        };
        if (manager is { } m) user["parentsystemuserid"] = new EntityReference("systemuser", m);
        var id = admin.Create(user);
        _fieldSecurityUsers.Add(id);
        var role = admin.Create(new Entity("role") { ["name"] = $"HS role {tag}" });
        _fieldSecurityRoles.Add(role);
        admin.Execute(new AddPrivilegesRoleRequest
        {
            RoleId = role,
            Privileges = new[] { "prvReadAccount", "prvWriteAccount", "prvAppendAccount", "prvAppendToAccount" }
                .Select(name => new RolePrivilege { PrivilegeName = name, Depth = PrivilegeDepth.Basic }).ToArray(),
        });
        admin.Associate("systemuser", id, new Relationship("systemuserroles_association"),
            new EntityReferenceCollection { new EntityReference("role", role) });
        return (id, oid);
    }

    private static void SetHierarchySettings(ServiceClient admin, bool enabled, int depth)
    {
        var orgId = ((WhoAmIResponse)admin.Execute(new WhoAmIRequest())).OrganizationId;
        admin.Update(new Entity("organization", orgId)
        {
            ["ishierarchicalsecuritymodelenabled"] = enabled,
            ["maxdepthforhierarchicalsecuritymodel"] = depth,
        });
    }

    private static Guid CreateOwnedAccount(ServiceClient admin, string name, Guid owner) =>
        admin.Create(new Entity("account") { ["name"] = name, ["ownerid"] = new EntityReference("systemuser", owner) });

    /// <summary>Clears the manager links before the users are disabled, and switches the model off again.</summary>
    private void ResetHierarchy(ServiceClient admin, IEnumerable<Guid> reports, IEnumerable<Guid> accounts)
    {
        foreach (var account in accounts)
            try { admin.Delete("account", account); } catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: account {account}: {ex.Message}"); }
        foreach (var report in reports)
            try { admin.Update(new Entity("systemuser", report) { ["parentsystemuserid"] = null }); } catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: manager of {report}: {ex.Message}"); }
        SetHierarchySettings(admin, false, 3);
        RemoveFieldSecurityUsers(admin);
    }

    [Fact]
    public void ServiceClient_manager_reads_direct_report_record()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var users = new List<Guid>();
        var accounts = new List<Guid>();
        try
        {
            var manager = CreateHierarchyUser(admin, "mgr", null);
            var report = CreateHierarchyUser(admin, "rep", manager.Id);
            users.AddRange([report.Id]);
            var reportAccount = CreateOwnedAccount(admin, "hs-report-" + Guid.NewGuid().ToString("N")[..8], report.Id);
            accounts.Add(reportAccount);

            using var client = ConnectAs(manager.Oid);
            // Model off: a Basic read right sees only the manager's own records.
            SetHierarchySettings(admin, false, 3);
            FaultCode(() => client.Retrieve("account", reportAccount, new ColumnSet("name"))).Should().Be(ErrorCodes.AccessDenied);
            // Model on: the direct report's record is readable and writable.
            SetHierarchySettings(admin, true, 3);
            client.Retrieve("account", reportAccount, new ColumnSet("name")).Id.Should().Be(reportAccount);
            client.Update(new Entity("account", reportAccount) { ["name"] = "hs-renamed" });
            admin.Retrieve("account", reportAccount, new ColumnSet("name")).GetAttributeValue<string>("name").Should().Be("hs-renamed");
            // The report does not see the manager's records.
            var managerAccount = CreateOwnedAccount(admin, "hs-manager-" + Guid.NewGuid().ToString("N")[..8], manager.Id);
            accounts.Add(managerAccount);
            using var reportClient = ConnectAs(report.Oid);
            FaultCode(() => reportClient.Retrieve("account", managerAccount, new ColumnSet("name"))).Should().Be(ErrorCodes.AccessDenied);
        }
        finally
        {
            ResetHierarchy(admin, users, accounts);
        }
    }

    [Fact]
    public void ServiceClient_grand_manager_cannot_update_level_two_record()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var users = new List<Guid>();
        var accounts = new List<Guid>();
        try
        {
            var grandManager = CreateHierarchyUser(admin, "gmg", null);
            var manager = CreateHierarchyUser(admin, "mgr", grandManager.Id);
            var report = CreateHierarchyUser(admin, "rep", manager.Id);
            users.AddRange([manager.Id, report.Id]);
            var levelTwo = CreateOwnedAccount(admin, "hs-level2-" + Guid.NewGuid().ToString("N")[..8], report.Id);
            accounts.Add(levelTwo);
            SetHierarchySettings(admin, true, 3);

            using var client = ConnectAs(grandManager.Oid);
            // Level 2 is read-only for the grand manager; the direct manager may write.
            client.Retrieve("account", levelTwo, new ColumnSet("name")).Id.Should().Be(levelTwo);
            FaultCode(() => client.Update(new Entity("account", levelTwo) { ["name"] = "hs-denied" })).Should().Be(ErrorCodes.AccessDenied);
            using var direct = ConnectAs(manager.Oid);
            direct.Update(new Entity("account", levelTwo) { ["name"] = "hs-allowed" });
        }
        finally
        {
            ResetHierarchy(admin, users, accounts);
        }
    }

    [Fact]
    public void ServiceClient_fetchxml_under_on_accounts()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var accounts = new List<Guid>();
        try
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var top = admin.Create(new Entity("account") { ["name"] = $"hs-top-{tag}" });
            accounts.Add(top);
            var child = admin.Create(new Entity("account") { ["name"] = $"hs-child-{tag}", ["parentaccountid"] = new EntityReference("account", top) });
            accounts.Add(child);
            var grandChild = admin.Create(new Entity("account") { ["name"] = $"hs-grandchild-{tag}", ["parentaccountid"] = new EntityReference("account", child) });
            accounts.Add(grandChild);
            var unrelated = admin.Create(new Entity("account") { ["name"] = $"hs-other-{tag}" });
            accounts.Add(unrelated);

            Guid[] Run(string op) => admin.RetrieveMultiple(new FetchExpression($"""
                <fetch><entity name="account"><attribute name="name" />
                <filter><condition attribute="accountid" operator="{op}" value="{top}" /></filter></entity></fetch>
                """)).Entities.Select(e => e.Id).ToArray();

            Run("under").Should().BeEquivalentTo(new[] { child, grandChild });
            Run("eq-or-under").Should().BeEquivalentTo(new[] { top, child, grandChild });
            var notUnder = Run("not-under");
            notUnder.Should().Contain(unrelated).And.NotContain([top, child, grandChild]);
        }
        finally
        {
            // Delete bottom-up: a parent with children cannot be removed (restrict).
            foreach (var id in Enumerable.Reverse(accounts))
                try { admin.Delete("account", id); } catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: account {id}: {ex.Message}"); }
        }
    }

    // ---- access team templates (plan 2.16) ---------------------------------------------------------------------------

    /// <summary>
    /// Switches <c>account</c> on for access teams and creates a Read template on it. Returns the template's id; the caller
    /// removes it with <see cref="ResetAccessTeams"/>.
    /// </summary>
    private static Guid CreateReadTemplate(ServiceClient admin)
    {
        admin.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "account", AutoCreateAccessTeams = true },
        });
        var objectTypeCode = ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)admin.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
        {
            LogicalName = "account", EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity,
        })).EntityMetadata.ObjectTypeCode!.Value;
        return admin.Create(new Entity("teamtemplate")
        {
            ["teamtemplatename"] = "at-read-" + Guid.NewGuid().ToString("N")[..8],
            ["objecttypecode"] = objectTypeCode,
            ["defaultaccessrightsmask"] = (int)AccessRights.ReadAccess,
        });
    }

    /// <summary>Deletes the template (and with it the access teams), then the accounts and users, and switches the table off.</summary>
    private void ResetAccessTeams(ServiceClient admin, Guid template, IEnumerable<Guid> accounts)
    {
        foreach (var account in accounts)
            try { admin.Delete("account", account); } catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: account {account}: {ex.Message}"); }
        try { admin.Delete("teamtemplate", template); } catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: template {template}: {ex.Message}"); }
        try
        {
            admin.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
            {
                Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "account", AutoCreateAccessTeams = false },
            });
        }
        catch (FaultException<OrganizationServiceFault> ex) { output.WriteLine($"Cleanup: AutoCreateAccessTeams: {ex.Message}"); }
        RemoveFieldSecurityUsers(admin);
    }

    [Fact]
    public void ServiceClient_add_user_to_record_team_grants_read()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var accounts = new List<Guid>();
        var template = Guid.Empty;
        try
        {
            template = CreateReadTemplate(admin);
            var owner = CreateHierarchyUser(admin, "own", null);
            var member = CreateHierarchyUser(admin, "mem", null);
            var account = CreateOwnedAccount(admin, "at-record-" + Guid.NewGuid().ToString("N")[..8], owner.Id);
            accounts.Add(account);

            using var client = ConnectAs(member.Oid);
            FaultCode(() => client.Retrieve("account", account, new ColumnSet("name"))).Should().Be(ErrorCodes.AccessDenied);

            var response = (AddUserToRecordTeamResponse)admin.Execute(new AddUserToRecordTeamRequest
            {
                Record = new EntityReference("account", account), SystemUserId = member.Id, TeamTemplateId = template,
            });
            response.AccessTeamId.Should().NotBe(Guid.Empty);
            client.Retrieve("account", account, new ColumnSet("name")).Id.Should().Be(account);
            // The template grants Read only.
            FaultCode(() => client.Update(new Entity("account", account) { ["name"] = "at-denied" })).Should().Be(ErrorCodes.AccessDenied);

            var team = admin.Retrieve("team", response.AccessTeamId, new ColumnSet("name", "teamtype", "systemmanaged"));
            team.GetAttributeValue<string>("name").Should().Be($"{account:D}+{template:D}");
            team.GetAttributeValue<OptionSetValue>("teamtype").Value.Should().Be(1);
            team.GetAttributeValue<bool>("systemmanaged").Should().BeTrue();
        }
        finally
        {
            ResetAccessTeams(admin, template, accounts);
        }
    }

    [Fact]
    public void ServiceClient_remove_user_from_record_team_revokes_read()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var accounts = new List<Guid>();
        var template = Guid.Empty;
        try
        {
            template = CreateReadTemplate(admin);
            var owner = CreateHierarchyUser(admin, "own", null);
            var member = CreateHierarchyUser(admin, "mem", null);
            var account = CreateOwnedAccount(admin, "at-record-" + Guid.NewGuid().ToString("N")[..8], owner.Id);
            accounts.Add(account);
            var record = new EntityReference("account", account);

            var added = ((AddUserToRecordTeamResponse)admin.Execute(new AddUserToRecordTeamRequest
            {
                Record = record, SystemUserId = member.Id, TeamTemplateId = template,
            })).AccessTeamId;
            using var client = ConnectAs(member.Oid);
            client.Retrieve("account", account, new ColumnSet("name")).Id.Should().Be(account);

            var removed = ((RemoveUserFromRecordTeamResponse)admin.Execute(new RemoveUserFromRecordTeamRequest
            {
                Record = record, SystemUserId = member.Id, TeamTemplateId = template,
            })).AccessTeamId;
            removed.Should().Be(added);
            // The next request is refused, with no restart; the team stays, empty.
            FaultCode(() => client.Retrieve("account", account, new ColumnSet("name"))).Should().Be(ErrorCodes.AccessDenied);
            admin.Retrieve("team", added, new ColumnSet("name")).Id.Should().Be(added);
        }
        finally
        {
            ResetAccessTeams(admin, template, accounts);
        }
    }

    /// <summary>A new user-owned table with change tracking on, created through the client (plan 2.13).</summary>
    private static string CreateTrackedTable(ServiceClient admin)
    {
        var name = "new_ct" + Guid.NewGuid().ToString("N")[..8];
        admin.Execute(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = new Label("Tracked " + name, 1033),
                DisplayCollectionName = new Label("Tracked " + name, 1033),
                OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = new Label("Name", 1033),
                RequiredLevel = new Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevelManagedProperty(
                    Microsoft.Xrm.Sdk.Metadata.AttributeRequiredLevel.None),
            },
        });
        admin.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = name, ChangeTrackingEnabled = true },
        });
        return name;
    }

    private static BusinessEntityChanges EntityChanges(ServiceClient client, string table, string? token) =>
        ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityChangesResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityChangesRequest
        {
            EntityName = table, Columns = new ColumnSet("new_name"), DataVersion = token,
            PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 },
        })).EntityChanges;

    [Fact]
    public void ServiceClient_retrieve_entity_changes_initial_and_delta()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var table = CreateTrackedTable(admin);
        var a = admin.Create(new Entity(table) { ["new_name"] = "a" });
        var b = admin.Create(new Entity(table) { ["new_name"] = "b" });

        var initial = EntityChanges(admin, table, null);

        initial.Changes.Select(c => c.Should().BeOfType<NewOrUpdatedItem>().Subject.NewOrUpdatedEntity.Id).Should().Equal(a, b);
        initial.MoreRecords.Should().BeFalse();
        initial.DataToken.Should().Contain("!");

        admin.Update(new Entity(table, b) { ["new_name"] = "b2" });
        var c = admin.Create(new Entity(table) { ["new_name"] = "c" });
        var delta = EntityChanges(admin, table, initial.DataToken);

        var updated = delta.Changes.Cast<NewOrUpdatedItem>().ToList();
        updated.Select(i => i.NewOrUpdatedEntity.Id).Should().Equal(b, c);
        updated[0].NewOrUpdatedEntity.GetAttributeValue<string>("new_name").Should().Be("b2");
        updated[0].NewOrUpdatedEntity.RowVersion.Should().NotBeNullOrEmpty();
        delta.DataToken.Should().NotBe(initial.DataToken);
    }

    [Fact]
    public void ServiceClient_retrieve_entity_changes_reports_deletion()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var table = CreateTrackedTable(admin);
        var kept = admin.Create(new Entity(table) { ["new_name"] = "kept" });
        var gone = admin.Create(new Entity(table) { ["new_name"] = "gone" });
        var token = EntityChanges(admin, table, null).DataToken;

        admin.Delete(table, gone);
        var delta = EntityChanges(admin, table, token);

        var removed = delta.Changes.Should().ContainSingle().Which.Should().BeOfType<RemovedOrDeletedItem>().Subject;
        removed.Type.Should().Be(ChangeType.RemoveOrDeleted);
        removed.RemovedItem.LogicalName.Should().Be(table);
        removed.RemovedItem.Id.Should().Be(gone);
        admin.Retrieve(table, kept, new ColumnSet("new_name")).Id.Should().Be(kept);
    }

    // ---- plan 2.15: duplicate detection -------------------------------------------------------------------------------

    /// <summary>
    /// Turns duplicate detection on (organization, Create/Update, the contact table), creates a rule contact → contact on
    /// <c>emailaddress1</c>, publishes it through <c>PublishDuplicateRuleRequest</c> and polls its job until it completes; returns the rule.
    /// </summary>
    private static Guid PublishEmailRule(ServiceClient admin)
    {
        var organization = admin.RetrieveMultiple(new QueryExpression("organization") { ColumnSet = new ColumnSet("organizationid") }).Entities[0].Id;
        admin.Update(new Entity("organization", organization)
        {
            [DuplicateDetectionSchema.OrgEnabled] = true, [DuplicateDetectionSchema.OrgEnabledForOnlineCreateUpdate] = true,
        });
        admin.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = "contact", IsDuplicateDetectionEnabled = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true),
            },
        });
        var rule = admin.Create(new Entity(DuplicateDetectionSchema.Rule)
        {
            ["name"] = "Email " + Guid.NewGuid().ToString("N")[..8], ["baseentityname"] = "contact", ["matchingentityname"] = "contact",
        });
        admin.Create(new Entity(DuplicateDetectionSchema.Condition)
        {
            ["regardingobjectid"] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
            ["baseattributename"] = "emailaddress1", ["matchingattributename"] = "emailaddress1", ["operatorcode"] = new OptionSetValue(0),
        });
        var job = ((PublishDuplicateRuleResponse)admin.Execute(new PublishDuplicateRuleRequest { DuplicateRuleId = rule })).JobId;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (admin.Retrieve("asyncoperation", job, new ColumnSet("statecode")).GetAttributeValue<OptionSetValue>("statecode").Value != 3)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the publish job should complete");
            Thread.Sleep(100);
        }
        admin.Retrieve(DuplicateDetectionSchema.Rule, rule, new ColumnSet("statuscode")).GetAttributeValue<OptionSetValue>("statuscode").Value
            .Should().Be(DuplicateDetectionSchema.RuleStatus.Published);
        return rule;
    }

    /// <summary>Deletes the rule and turns duplicate detection off again (organization and contact table).</summary>
    private static void RemoveEmailRule(ServiceClient admin, Guid rule)
    {
        admin.Delete(DuplicateDetectionSchema.Rule, rule);
        var organization = admin.RetrieveMultiple(new QueryExpression("organization") { ColumnSet = new ColumnSet("organizationid") }).Entities[0].Id;
        admin.Update(new Entity("organization", organization)
        {
            [DuplicateDetectionSchema.OrgEnabled] = false, [DuplicateDetectionSchema.OrgEnabledForOnlineCreateUpdate] = false,
        });
        admin.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = "contact", IsDuplicateDetectionEnabled = new Microsoft.Xrm.Sdk.BooleanManagedProperty(false),
            },
        });
    }

    private static Microsoft.Xrm.Sdk.Messages.CreateRequest CreateContactRequest(string email, bool suppress) =>
        new()
        {
            Target = new Entity("contact") { ["lastname"] = "Duplicate", ["emailaddress1"] = email },
            ["SuppressDuplicateDetection"] = suppress,
        };

    [Fact]
    public void ServiceClient_create_with_duplicate_email_fails_when_detection_requested()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var email = $"dup.{Guid.NewGuid():N}@example.com";
        admin.Create(new Entity("contact") { ["lastname"] = "Original", ["emailaddress1"] = email });
        var rule = PublishEmailRule(admin);
        try
        {
            FaultCode(() => admin.Execute(CreateContactRequest(email, suppress: false))).Should().Be(-2147220685);
        }
        finally
        {
            RemoveEmailRule(admin, rule);
        }
    }

    [Fact]
    public void ServiceClient_create_with_duplicate_email_succeeds_when_suppressed()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var email = $"dup.{Guid.NewGuid():N}@example.com";
        admin.Create(new Entity("contact") { ["lastname"] = "Original", ["emailaddress1"] = email });
        var rule = PublishEmailRule(admin);
        try
        {
            var response = (Microsoft.Xrm.Sdk.Messages.CreateResponse)admin.Execute(CreateContactRequest(email, suppress: true));
            response.id.Should().NotBeEmpty();
        }
        finally
        {
            RemoveEmailRule(admin, rule);
        }
    }

    [Fact]
    public void ServiceClient_retrieve_duplicates_finds_the_record()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var email = $"dup.{Guid.NewGuid():N}@example.com";
        var first = admin.Create(new Entity("contact") { ["lastname"] = "Original", ["emailaddress1"] = email });
        var rule = PublishEmailRule(admin);
        try
        {
            var response = (RetrieveDuplicatesResponse)admin.Execute(new RetrieveDuplicatesRequest
            {
                BusinessEntity = new Entity("contact") { ["emailaddress1"] = email },
                MatchingEntityName = "contact",
                PagingInfo = new PagingInfo { Count = 50, PageNumber = 1 },
            });

            response.DuplicateCollection.Entities.Select(e => e.Id).Should().Contain(first);
        }
        finally
        {
            RemoveEmailRule(admin, rule);
        }
    }

    [Fact]
    public void ServiceClient_bulk_detect_duplicates_records_pair()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        var run = Guid.NewGuid().ToString("N")[..8];
        var email = $"bulk.{run}@example.com";
        var first = admin.Create(new Entity("contact") { ["lastname"] = "First " + run, ["emailaddress1"] = email });
        var second = admin.Create(new Entity("contact") { ["lastname"] = "Second " + run, ["emailaddress1"] = email });
        admin.Create(new Entity("contact") { ["lastname"] = "Other " + run, ["emailaddress1"] = $"other.{run}@example.com" });
        var rule = PublishEmailRule(admin);
        try
        {
            var query = new QueryExpression("contact") { ColumnSet = new ColumnSet("lastname") };
            query.Criteria.AddCondition("lastname", ConditionOperator.EndsWith, " " + run);
            var job = ((BulkDetectDuplicatesResponse)admin.Execute(new BulkDetectDuplicatesRequest
            {
                Query = query, JobName = "Bulk " + run, SendEmailNotification = false, ToRecipients = [], CCRecipients = [],
            })).JobId;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (admin.Retrieve("asyncoperation", job, new ColumnSet("statecode")).GetAttributeValue<OptionSetValue>("statecode").Value != 3)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the bulk detection job should complete");
                Thread.Sleep(100);
            }
            admin.Retrieve("asyncoperation", job, new ColumnSet("statuscode")).GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(30);

            var records = new QueryExpression(DuplicateDetectionSchema.Record) { ColumnSet = new ColumnSet("baserecordid", "duplicateruleid") };
            records.Criteria.AddCondition("asyncoperationid", ConditionOperator.Equal, job);
            var found = admin.RetrieveMultiple(records).Entities;
            found.Select(e => e.GetAttributeValue<EntityReference>("baserecordid").Id).Should().BeEquivalentTo([first, second]);
            found.Should().AllSatisfy(e => e.GetAttributeValue<EntityReference>("duplicateruleid").Id.Should().Be(rule));
            admin.Delete("asyncoperation", job);
        }
        finally
        {
            RemoveEmailRule(admin, rule);
        }
    }

    // ---- managed solutions (plan 2.6) -------------------------------------------------------------------------------

    /// <summary>The unique name of the managed test solution and its holding solution.</summary>
    private const string ManagedBase = "xm_base";

    /// <summary>Removes what the managed test solutions leave behind (layers, rows, the table and the choice) and makes the plug-in step's table available.</summary>
    private async Task CleanManagedAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store); // the step of the test solution writes into pb_pipelog; the cleanup empties it
        await SolutionImportTests.ClearImportAsync(fx.App.Services, fx.ConnectionString);
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (store.GetEntity(ManagedTestSolution.Table) is not null) await store.DeleteEntityAsync(s, ManagedTestSolution.Table, default);
            if (store.GetOptionSet("xm_status") is not null) await store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM data.role WHERE name = N'xm_reader'; DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WithManagedCleanupAsync(Func<Task> body)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanManagedAsync();
        try { await body(); }
        finally { await CleanManagedAsync(); }
    }

    private static ImportSolutionRequest ManagedImport(ManagedSpec spec, bool holding = false) =>
        new() { CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(), HoldingSolution = holding };

    private static Guid SolutionId(ServiceClient client, string uniqueName)
    {
        var query = new QueryByAttribute("solution") { ColumnSet = new ColumnSet("solutionid") };
        query.AddAttributeValue("uniquename", uniqueName);
        var rows = client.RetrieveMultiple(query).Entities;
        return rows.Count == 1 ? rows[0].Id : Guid.Empty;
    }

    private static string? ColumnLabel(ServiceClient client, string column)
    {
        var response = (Microsoft.Xrm.Sdk.Messages.RetrieveAttributeResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveAttributeRequest
        {
            EntityLogicalName = ManagedTestSolution.Table, LogicalName = column,
        });
        return response.AttributeMetadata.DisplayName.UserLocalizedLabel?.Label;
    }

    [Fact]
    public async Task ServiceClient_managed_lifecycle()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WithManagedCleanupAsync(() =>
        {
            client.Execute(ManagedImport(ManagedTestSolution.V1));
            ColumnLabel(client, "xm_code").Should().Be("Code");

            // An unmanaged customization: relabel xm_code with a retrieved definition sent back with the new label.
            var attribute = ((Microsoft.Xrm.Sdk.Messages.RetrieveAttributeResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveAttributeRequest
            {
                EntityLogicalName = ManagedTestSolution.Table, LogicalName = "xm_code",
            })).AttributeMetadata;
            attribute.DisplayName = new Label("Customized code", 1033);
            client.Execute(new Microsoft.Xrm.Sdk.Messages.UpdateAttributeRequest { EntityName = ManagedTestSolution.Table, Attribute = attribute });
            ColumnLabel(client, "xm_code").Should().Be("Customized code");

            client.Execute(new StageAndUpgradeRequest
            {
                CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2), ImportJobId = Guid.NewGuid(),
            });

            ColumnLabel(client, "xm_code").Should().Be("Customized code", "the customization survives the upgrade");
            var table = ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
            {
                LogicalName = ManagedTestSolution.Table, EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
            })).EntityMetadata;
            table.Attributes.Select(a => a.LogicalName).Should().Contain("xm_extra").And.NotContain("xm_legacy");

            var id = SolutionId(client, ManagedBase);
            id.Should().NotBeEmpty();
            client.Delete("solution", id); // uninstall

            var gone = () => client.Execute(new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest { LogicalName = ManagedTestSolution.Table });
            gone.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
            SolutionId(client, ManagedBase).Should().BeEmpty();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ServiceClient_holding_then_DeleteAndPromote()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WithManagedCleanupAsync(() =>
        {
            client.Execute(ManagedImport(ManagedTestSolution.V1));
            client.Execute(ManagedImport(ManagedTestSolution.V2, holding: true));
            var holdingId = SolutionId(client, ManagedBase + "_Upgrade");
            holdingId.Should().NotBeEmpty("the holding solution is a row of its own");

            var response = (DeleteAndPromoteResponse)client.Execute(new DeleteAndPromoteRequest { UniqueName = ManagedBase });

            response.SolutionId.Should().Be(holdingId, "the former holding row's id lives on");
            SolutionId(client, ManagedBase).Should().Be(holdingId);
            SolutionId(client, ManagedBase + "_Upgrade").Should().BeEmpty();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void ServiceClient_CloneAsPatch_is_refused()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        var act = () => client.Execute(new CloneAsPatchRequest
        {
            ParentSolutionUniqueName = "Default", DisplayName = "Patch", VersionNumber = "1.0.0.1",
        });

        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(-2147220989);
    }

    [Fact]
    public async Task ServiceClient_uninstall_with_data_fails()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WithManagedCleanupAsync(() =>
        {
            client.Execute(ManagedImport(ManagedTestSolution.V1));
            client.Create(new Entity(ManagedTestSolution.Table) { ["xm_name"] = "one" });
            var id = SolutionId(client, ManagedBase);

            var act = () => client.Delete("solution", id);

            var fault = act.Should().Throw<FaultException<OrganizationServiceFault>>().Which;
            fault.Detail.ErrorCode.Should().Be(-2147220989);
            fault.Detail.Message.Should().Contain(ManagedTestSolution.Table);
            SolutionId(client, ManagedBase).Should().Be(id, "nothing changed");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The USD currency at <paramref name="rate"/>: created as a System currency, or, when it exists, made active and given the
    /// rate. USD must not be the base currency (its rate is fixed at 1): the fixture's base currency is the euro.
    /// </summary>
    private static Guid UsdAt(ServiceClient client, decimal rate)
    {
        var organization = client.RetrieveMultiple(new QueryExpression("organization") { ColumnSet = new ColumnSet(CurrencySchema.BaseCurrencyId) })
            .Entities.Single();
        var query = new QueryExpression(CurrencySchema.Table) { ColumnSet = new ColumnSet(CurrencySchema.CurrencyId, "statecode") };
        query.Criteria.AddCondition(CurrencySchema.IsoCode, ConditionOperator.Equal, "USD");
        if (client.RetrieveMultiple(query).Entities.FirstOrDefault() is { } existing)
        {
            organization.GetAttributeValue<EntityReference>(CurrencySchema.BaseCurrencyId).Id.Should().NotBe(existing.Id,
                "these tests need USD as a second currency, not as the base currency (whose rate is always 1)");
            if (existing.GetAttributeValue<OptionSetValue>("statecode")?.Value != 0)
                client.Execute(new SetStateRequest
                {
                    EntityMoniker = existing.ToEntityReference(), State = new OptionSetValue(0), Status = new OptionSetValue(1),
                });
            client.Update(new Entity(CurrencySchema.Table, existing.Id)
            {
                [CurrencySchema.ExchangeRate] = rate, [CurrencySchema.Symbol] = "$", [CurrencySchema.Precision] = 2,
            });
            return existing.Id;
        }
        return client.Create(new Entity(CurrencySchema.Table)
        {
            ["currencyname"] = "US Dollar", [CurrencySchema.IsoCode] = "USD", [CurrencySchema.Symbol] = "$",
            [CurrencySchema.Precision] = 2, [CurrencySchema.CurrencyType] = new OptionSetValue(0), [CurrencySchema.ExchangeRate] = rate,
        });
    }

    /// <summary>Plan 2.10 acceptance (rulings X5, X9): a USD record at rate 1.10 stores the rate and its base value.</summary>
    [Fact]
    public void ServiceClient_creates_usd_currency_and_record_at_1_10()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var usd = UsdAt(client, 1.10m);

        var id = client.Create(new Entity(TestEntities.Project)
        {
            ["pb_name"] = "Dollar project", ["pb_budget"] = new Money(100m),
            [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, usd),
        });
        try
        {
            var project = client.Retrieve(TestEntities.Project, id,
                new ColumnSet("pb_budget", "pb_budget_base", CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate));
            project.GetAttributeValue<EntityReference>(CurrencySchema.CurrencyId).Id.Should().Be(usd);
            project.GetAttributeValue<decimal>(CurrencySchema.ExchangeRate).Should().Be(1.10m);
            project.GetAttributeValue<Money>("pb_budget").Value.Should().Be(100m);
            project.GetAttributeValue<Money>("pb_budget_base").Value.Should().Be(90.9091m);
            // Ruling X11: the record currency's symbol and precision, the base currency's for the base value.
            project.FormattedValues["pb_budget"].Should().Be("$100.00");
            project.FormattedValues["pb_budget_base"].Should().Be("€90.91");
        }
        finally
        {
            client.Delete(TestEntities.Project, id);
        }
    }

    /// <summary>Plan 2.10 acceptance (ruling X12): RetrieveExchangeRate over SOAP.</summary>
    [Fact]
    public void ServiceClient_retrieve_exchange_rate()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var usd = UsdAt(client, 1.10m);

        var response = (RetrieveExchangeRateResponse)client.Execute(new RetrieveExchangeRateRequest { TransactionCurrencyId = usd });

        response.ExchangeRate.Should().Be(1.10m);
        var act = () => client.Execute(new RetrieveExchangeRateRequest { TransactionCurrencyId = Guid.NewGuid() });
        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
    }

    /// <summary>
    /// Plan 2.11 acceptance (spec): an email regarding an account with two contacts in <c>to</c>, found through
    /// <c>activitypointer</c> filtered by <c>regardingobjectid</c>, with its party list read back.
    /// </summary>
    [Fact]
    public void ServiceClient_creates_email_with_two_recipients_and_finds_it_via_activitypointer()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var run = Guid.NewGuid().ToString("N")[..8];
        var account = client.Create(new Entity("account") { ["name"] = "Acceptance Example " + run });
        var ada = client.Create(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Example", ["emailaddress1"] = $"ada.{run}@example.com" });
        var bob = client.Create(new Entity("contact") { ["firstname"] = "Bob", ["lastname"] = "Example", ["emailaddress1"] = $"bob.{run}@example.com" });

        var email = client.Create(new Entity("email")
        {
            ["subject"] = "Welcome " + run,
            ["regardingobjectid"] = new EntityReference("account", account),
            ["to"] = new EntityCollection(
            [
                new Entity("activityparty") { ["partyid"] = new EntityReference("contact", ada) },
                new Entity("activityparty") { ["partyid"] = new EntityReference("contact", bob) },
            ]),
        });

        var query = new QueryExpression("activitypointer") { ColumnSet = new ColumnSet("subject", "activitytypecode", "regardingobjectid") };
        query.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, account);
        var found = client.RetrieveMultiple(query).Entities.Should().ContainSingle().Subject;
        found.Id.Should().Be(email);
        found.GetAttributeValue<string>("activitytypecode").Should().Be("email");
        found.GetAttributeValue<string>("subject").Should().Be("Welcome " + run);
        found.GetAttributeValue<EntityReference>("regardingobjectid").LogicalName.Should().Be("account");

        var retrieved = client.Retrieve("email", email, new ColumnSet("to"));
        var to = retrieved.GetAttributeValue<EntityCollection>("to");
        to.Entities.Select(p => p.GetAttributeValue<EntityReference>("partyid").Id).Should().Equal(ada, bob);
        to.Entities[0].GetAttributeValue<string>("addressused").Should().Be($"ada.{run}@example.com");
    }

    /// <summary>Plan 2.11, A17: SendEmail over SOAP without a provider (IssueSend false marks the email Sent); A18 refusals.</summary>
    [Fact]
    public void ServiceClient_send_email_issue_send_false()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var run = Guid.NewGuid().ToString("N")[..8];
        var ada = client.Create(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "Example", ["emailaddress1"] = $"ada.{run}@example.com" });
        var email = client.Create(new Entity("email")
        {
            ["subject"] = "Send " + run,
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", ada) }]),
        });

        var response = (SendEmailResponse)client.Execute(new SendEmailRequest { EmailId = email, IssueSend = false, TrackingToken = "" });

        response.Subject.Should().Be("Send " + run);
        var sent = client.Retrieve("email", email, new ColumnSet("statecode", "statuscode", "senton", "from"));
        sent.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        sent.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3);
        sent.GetAttributeValue<DateTime?>("senton").Should().NotBeNull();
        sent.GetAttributeValue<EntityCollection>("from").Entities.Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("partyid").LogicalName.Should().Be("systemuser");

        var again = () => client.Execute(new SendEmailRequest { EmailId = email, IssueSend = false, TrackingToken = "" });
        again.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        var book = () => client.Execute(new OrganizationRequest("Book") { ["Target"] = new Entity("appointment") });
        book.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.Message.Should().Contain("not supported");
    }

    [Fact]
    public void ServiceClient_creates_note_with_attachment()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var run = Guid.NewGuid().ToString("N")[..8];
        var account = client.Create(new Entity("account") { ["name"] = "Acceptance Example " + run });
        var content = System.Text.Encoding.UTF8.GetBytes("Meeting notes " + run);

        var note = client.Create(new Entity("annotation")
        {
            ["subject"] = "Notes " + run,
            ["objectid"] = new EntityReference("account", account),
            ["filename"] = "notes.txt",
            ["documentbody"] = Convert.ToBase64String(content),
        });

        var query = new QueryExpression("annotation") { ColumnSet = new ColumnSet("subject", "filename", "mimetype", "filesize", "isdocument", "documentbody") };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, account);
        var found = client.RetrieveMultiple(query).Entities.Should().ContainSingle().Subject;
        found.Id.Should().Be(note);
        found.GetAttributeValue<bool>("isdocument").Should().BeTrue();
        found.GetAttributeValue<int>("filesize").Should().Be(content.Length);
        found.GetAttributeValue<string>("mimetype").Should().Be("text/plain");
        Convert.FromBase64String(found.GetAttributeValue<string>("documentbody")).Should().Equal(content);
    }

    /// <summary>
    /// Plan 2.14, Task 5 (W15 as amended by Task 3b): the parity export imported and its business rule (Entity scope through its
    /// <c>ProcessTriggerScope</c>) activated through the client; a Create through the client runs the rule on the server.
    /// </summary>
    [Fact]
    public void ServiceClient_business_rule_sets_value_on_create()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var rule = Guid.Parse("216fa344-e5de-f111-84e8-002248872b66");
        var zip = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));
        // Imported as the file has it (every process Draft); only the rule is activated (OnCreate starts a child workflow).
        client.Execute(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        SetState(client, rule, WorkflowSchema.ActivatedState, WorkflowSchema.ActivatedStatus);
        try
        {
            var run = Guid.NewGuid().ToString("N")[..8];
            var order = client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Acceptance order " + run });
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_status")).GetAttributeValue<OptionSetValue>("xrmv_status").Value
                .Should().Be(128830001, "the rule sets the status when the amount is empty");

            var blocked = () => client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Blocked " + run, ["xrmv_amount"] = 5m });
            var fault = blocked.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;
            (fault.ErrorCode, fault.Message).Should().Be((ErrorCodes.ISVAborted, "fdsfds"));
        }
        finally
        {
            SetState(client, rule, WorkflowSchema.DraftState, WorkflowSchema.DraftStatus);
        }

        static void SetState(ServiceClient client, Guid id, int state, int status) => client.Execute(new SetStateRequest
        {
            EntityMoniker = new EntityReference(WorkflowSchema.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });
    }

    /// <summary>
    /// Plan 2.14, Task 7 (W14): the parity export imported (its action Recalculate gets the message xrmv_Recalculate, with the file's
    /// SdkMessageId), the action activated through the client, then called by its message name through the client: the XAML updates the
    /// order and the response carries its Total output.
    /// </summary>
    [Fact]
    public void ServiceClient_calls_xaml_action_and_reads_output()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var recalculate = Guid.Parse("9df696a1-e6de-f111-84e8-002248872b66");
        var zip = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));
        client.Execute(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        SetState(client, recalculate, WorkflowSchema.ActivatedState, WorkflowSchema.ActivatedStatus);
        try
        {
            var order = client.Create(new Entity(TestEntities.Order) { ["xrmv_name"] = "Acceptance action " + Guid.NewGuid().ToString("N")[..8], ["xrmv_amount"] = 10m });

            var response = client.Execute(new OrganizationRequest("xrmv_Recalculate")
            {
                ["Target"] = new EntityReference(TestEntities.Order, order), ["Increment"] = 2.5m,
            });

            response.Results["Total"].Should().Be(12.5m);
            client.Retrieve(TestEntities.Order, order, new ColumnSet("xrmv_amount")).GetAttributeValue<decimal>("xrmv_amount").Should().Be(12.5m);
            var wrong = () => client.Execute(new OrganizationRequest("xrmv_Recalculate")
            {
                ["Target"] = new EntityReference(TestEntities.Order, order), ["Increment"] = "2.5",
            });
            wrong.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        }
        finally
        {
            SetState(client, recalculate, WorkflowSchema.DraftState, WorkflowSchema.DraftStatus);
        }

        static void SetState(ServiceClient client, Guid id, int state, int status) => client.Execute(new SetStateRequest
        {
            EntityMoniker = new EntityReference(WorkflowSchema.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });
    }

    /// <summary>
    /// Plan 2.14, Task 7 (W21): ExecuteWorkflowRequest through the client (SOAP). A real-time on-demand workflow on account (the
    /// provisional create step: a task) runs in the request and answers Guid.Empty; a background one is queued and answers its job.
    /// </summary>
    [Fact]
    public async Task ServiceClient_executes_on_demand_workflow()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var run = Guid.NewGuid().ToString("N")[..8];
        var subject = "Acceptance on demand " + run;
        var xaml = WorkflowTestHelper.Remove(WorkflowTestHelper.Replace(
                WorkflowTestHelper.Without(Xrmd.TestSupport.WorkflowFixtures.Xaml("realtime-create-update"), "UpdateStep2"),
                "\"Call the new customer\"", $"\"{subject}\""),
            "<mxswa:SetEntityProperty Attribute=\"regardingobjectid\"[^>]*/>");
        Xrmd.Workflows.WorkflowRow Definition(int mode) => new()
        {
            Id = Guid.NewGuid(), Name = $"Acceptance on demand {mode} {run}", Category = 0, Mode = mode, Type = 1, Scope = 4, RunAs = 1,
            PrimaryEntity = "account", Xaml = xaml, OnDemand = true, OwnerId = new EntityReference("systemuser", _adminUserId),
        };
        var (realtime, background) = (Definition(1), Definition(0));
        await using (var scope = fx.App.Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            foreach (var row in new[] { realtime, background })
                await scope.ServiceProvider.GetRequiredService<Xrmd.Workflows.WorkflowStore>().SaveAsync(new DataSession(conn, null), row, [], default);
        }
        SetState(client, realtime.Id, WorkflowSchema.ActivatedState, WorkflowSchema.ActivatedStatus);
        SetState(client, background.Id, WorkflowSchema.ActivatedState, WorkflowSchema.ActivatedStatus);
        try
        {
            var account = client.Create(new Entity("account") { ["name"] = "Acceptance " + run });

            var response = (ExecuteWorkflowResponse)client.Execute(new ExecuteWorkflowRequest { WorkflowId = realtime.Id, EntityId = account });
            response.Id.Should().Be(Guid.Empty);
            client.RetrieveMultiple(new QueryExpression("task")
            {
                Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, subject) } },
            }).Entities.Should().ContainSingle("the real-time workflow ran in the request");

            var queued = (ExecuteWorkflowResponse)client.Execute(new ExecuteWorkflowRequest { WorkflowId = background.Id, EntityId = account });
            queued.Id.Should().NotBe(Guid.Empty);
            client.Retrieve("asyncoperation", queued.Id, new ColumnSet("operationtype")).GetAttributeValue<OptionSetValue>("operationtype").Value
                .Should().Be(10);

            var notOnDemand = () => client.Execute(new ExecuteWorkflowRequest { WorkflowId = Guid.NewGuid(), EntityId = account });
            notOnDemand.Should().Throw<FaultException<OrganizationServiceFault>>();
        }
        finally
        {
            SetState(client, realtime.Id, WorkflowSchema.DraftState, WorkflowSchema.DraftStatus);
            SetState(client, background.Id, WorkflowSchema.DraftState, WorkflowSchema.DraftStatus);
        }

        static void SetState(ServiceClient client, Guid id, int state, int status) => client.Execute(new SetStateRequest
        {
            EntityMoniker = new EntityReference(WorkflowSchema.Table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });
    }
    // ---- Plan 2.14, Task 9: the parity solution (W25, rulings R1–R3) ------------------------------------------------------------
    //
    // The parity solution is the user's own export xrmdWfTest_1_0_0_2.zip (publisher xrmv, invented data), imported unmodified through
    // the client. Each test activates only the processes it shows: the export's business rule refuses every order with an amount, so
    // with it active no amount could be set (the same holds on Dataverse). The assertions are WorkflowParityAssertions (plan 2.21,
    // Task 8): WorkflowAuthoringParityTests runs them on the same processes authored as YAML.

    [Fact]
    public async Task ServiceClient_parity_realtime_creates_task()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WorkflowParityAssertions.RealtimeCreatesTaskAsync(client,
            WorkflowParityAssertions.Imported(client, WorkflowParityAssertions.Child, WorkflowParityAssertions.OnCreate));
    }

    [Fact]
    public async Task ServiceClient_parity_background_waits_then_sends_email()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WorkflowParityAssertions.BackgroundWaitsThenSendsEmailAsync(client,
            WorkflowParityAssertions.Imported(client, WorkflowParityAssertions.OnUpdate));
    }

    [Fact]
    public async Task ServiceClient_parity_business_rule_sets_and_refuses()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WorkflowParityAssertions.BusinessRuleSetsAndRefusesAsync(client,
            WorkflowParityAssertions.Imported(client, WorkflowParityAssertions.Rule));
    }

    [Fact]
    public async Task ServiceClient_parity_action_returns_total()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await WorkflowParityAssertions.ActionReturnsTotalAsync(client,
            WorkflowParityAssertions.Imported(client, WorkflowParityAssertions.Recalculate));
    }

    // ---- calculated columns (plan 2.12a, Task 6) -------------------------------------------------------------------------

    /// <summary>
    /// The 2.12a parity export (<c>xrmCalcTest_1_0_0_4.zip</c>, without its rollup and formula columns) imported and read through the
    /// real <see cref="ServiceClient"/>: Retrieve of the calculated columns, FetchXML filtering (<c>gt</c>) and ordering on
    /// <c>xrmv_total</c>, and the Web API's <c>$filter</c> on it. The values are derived from the definitions (total = price × qty,
    /// label = CONCAT(code, "-", account name), net = amount − amount / 10). The SQL endpoint part is
    /// <see cref="CalculatedTdsAcceptanceTests"/> (this fixture's host has no SQL endpoint).
    /// </summary>
    [Fact]
    public async Task ServiceClient_calculated_acceptance()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        client.Execute(new ImportSolutionRequest { CustomizationFile = CalculatedParityExport.WithoutRollupAndFormulaColumns(), ImportJobId = Guid.NewGuid() });

        var tag = "acc" + Guid.NewGuid().ToString("N")[..8];
        var account = client.Create(new Entity("account") { ["name"] = tag + " Fabrikam Example", ["xrmv_budget"] = new Money(800m) });
        var ids = new Dictionary<string, Guid>();
        foreach (var (code, price, qty) in new[] { ("A1", 10m, 3), ("B2", 25m, 4), ("C3", 7.5m, 2) })
            ids[code] = client.Create(new Entity(CalculatedParityExport.Order)
            {
                ["xrmv_name"] = $"{tag} {code}", ["xrmv_code"] = code, ["xrmv_price"] = new Money(price), ["xrmv_qty"] = qty,
                ["xrmv_amount"] = price * qty, ["xrmv_status"] = new OptionSetValue(1),
                ["xrmv_accountid"] = new EntityReference("account", account),
            });

        // Retrieve: the calculated values, and the columns are read-only in the metadata.
        var b2 = client.Retrieve(CalculatedParityExport.Order, ids["B2"], new ColumnSet("xrmv_total", "xrmv_label", "xrmv_net", "xrmv_accountbudget"));
        b2.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(100m);
        b2.GetAttributeValue<string>("xrmv_label").Should().Be($"B2-{tag} Fabrikam Example");
        b2.GetAttributeValue<decimal>("xrmv_net").Should().Be(90m);
        b2.GetAttributeValue<Money>("xrmv_accountbudget").Value.Should().Be(800m);
        var total = (RetrieveAttributeResponse)client.Execute(new RetrieveAttributeRequest
        {
            EntityLogicalName = CalculatedParityExport.Order, LogicalName = "xrmv_total",
        });
        total.AttributeMetadata.SourceType.Should().Be(1);
        total.AttributeMetadata.IsValidForCreate.Should().BeFalse();

        // FetchXML: total > 20, sorted by total descending.
        var fetch = $"""
            <fetch><entity name="{CalculatedParityExport.Order}"><attribute name="xrmv_code" /><attribute name="xrmv_total" />
              <filter><condition attribute="xrmv_name" operator="like" value="{tag} %" /><condition attribute="xrmv_total" operator="gt" value="20" /></filter>
              <order attribute="xrmv_total" descending="true" /></entity></fetch>
            """;
        client.RetrieveMultiple(new FetchExpression(fetch)).Entities
            .Select(e => (e.GetAttributeValue<string>("xrmv_code"), e.GetAttributeValue<Money>("xrmv_total").Value))
            .Should().Equal(("B2", 100m), ("A1", 30m));

        // The Web API: $filter on the calculated column.
        using var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/data/v9.2/xrmv_calcorders?$select=xrmv_code,xrmv_total&$filter=startswith(xrmv_name,'{tag}') and xrmv_total gt 20&$orderby=xrmv_total asc");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl));
        using var web = await http.SendAsync(request);
        var body = await web.Content.ReadAsStringAsync();
        web.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        System.Text.Json.Nodes.JsonNode.Parse(body)!["value"]!.AsArray()
            .Select(r => (r!["xrmv_code"]!.GetValue<string>(), r["xrmv_total"]!.GetValue<decimal>()))
            .Should().Equal(("A1", 30m), ("B2", 100m));
    }

    /// <summary>
    /// Plan 4.1a, task 8: the queue round trip through the real <see cref="ServiceClient"/>. An administrator puts an email about an
    /// account in a private team queue; a second user, holding only Basic User and a membership of that queue (ruling R23), picks it,
    /// releases it and routes it to a public queue, and is cut off from it when it is routed to a private queue that user does not
    /// belong to; <c>RetrieveUserQueues</c> with and without public queues.
    /// </summary>
    [Fact]
    public async Task ServiceClient_QueueRoundTrip()
    {
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var tag = "q" + Guid.NewGuid().ToString("N")[..8];
        var rootBusinessUnit = ((WhoAmIResponse)admin.Execute(new WhoAmIRequest())).BusinessUnitId;
        var oid = Guid.NewGuid();
        var user = admin.Create(new Entity("systemuser")
        {
            ["fullname"] = $"Queue Worker {tag}", ["domainname"] = $"qw-{tag}@example.test", ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", rootBusinessUnit),
        });
        var created = new List<(string Table, Guid Id)>();
        try
        {
            // Basic User only: Read Queue, the queue item privileges, and the activities the user owns.
            admin.Associate("systemuser", user, new Relationship("systemuserroles_association"),
                new EntityReferenceCollection { new EntityReference("role", Xrmd.Security.StandardRoles.BasicUser) });

            Guid Make(string table, Entity e) { var id = admin.Create(e); created.Add((table, id)); return id; }
            var account = Make("account", new Entity("account") { ["name"] = $"{tag} Fabrikam Example" });
            var email = Make("email", new Entity("email")
            {
                ["subject"] = $"{tag} Where is my order", ["regardingobjectid"] = new EntityReference("account", account),
                ["ownerid"] = new EntityReference("systemuser", user),
            });
            var teamQueue = Make("queue", new Entity("queue") { ["name"] = $"{tag} Support team", ["queueviewtype"] = new OptionSetValue(1), ["queuetypecode"] = new OptionSetValue(1) });
            var publicQueue = Make("queue", new Entity("queue") { ["name"] = $"{tag} Support", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1) });
            var privateQueue = Make("queue", new Entity("queue") { ["name"] = $"{tag} Escalations", ["queueviewtype"] = new OptionSetValue(1), ["queuetypecode"] = new OptionSetValue(1) });
            admin.Execute(new AddPrincipalToQueueRequest { QueueId = teamQueue, Principal = new Entity("systemuser", user) });

            // Add: one active item, no worker.
            var add = (AddToQueueResponse)admin.Execute(new AddToQueueRequest
            {
                Target = new EntityReference("email", email), DestinationQueueId = teamQueue,
            });
            var item = add.QueueItemId;
            Entity Item() => admin.Retrieve("queueitem", item, new ColumnSet("queueid", "workerid", "statecode"));
            Item().GetAttributeValue<EntityReference>("queueid").Id.Should().Be(teamQueue);
            Item().GetAttributeValue<EntityReference>("workerid").Should().BeNull();
            Item().GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);

            // Pick: the member takes the item; release hands it back; route moves it on to the public queue.
            using var worker = ConnectAs(oid);
            worker.RetrieveMultiple(new QueryExpression("queueitem") { ColumnSet = new ColumnSet("title") })
                .Entities.Select(e => e.Id).Should().Contain(item);
            worker.Execute(new PickFromQueueRequest { QueueItemId = item, WorkerId = user });
            Item().GetAttributeValue<EntityReference>("workerid").Id.Should().Be(user);
            worker.Execute(new ReleaseToQueueRequest { QueueItemId = item });
            Item().GetAttributeValue<EntityReference>("workerid").Should().BeNull();
            worker.Execute(new RouteToRequest { Target = new EntityReference("queue", publicQueue), QueueItemId = item });
            Item().GetAttributeValue<EntityReference>("queueid").Id.Should().Be(publicQueue);

            // RetrieveUserQueues: the member queue always, the public queue only when asked.
            Guid[] Queues(bool includePublic) => ((RetrieveUserQueuesResponse)admin.Execute(new RetrieveUserQueuesRequest { UserId = user, IncludePublic = includePublic }))
                .EntityCollection.Entities.Select(e => e.Id).ToArray();
            Queues(true).Should().Contain([publicQueue, teamQueue]).And.NotContain(privateQueue);
            Queues(false).Should().Contain(teamQueue).And.NotContain(publicQueue).And.NotContain(privateQueue);

            // Route to a private queue the user does not belong to: the item leaves that user's view, and the user cannot work it.
            admin.Execute(new RouteToRequest { Target = new EntityReference("queue", privateQueue), QueueItemId = item });
            Item().GetAttributeValue<EntityReference>("queueid").Id.Should().Be(privateQueue);
            worker.RetrieveMultiple(new QueryExpression("queueitem") { ColumnSet = new ColumnSet("title") })
                .Entities.Select(e => e.Id).Should().NotContain(item);
            worker.RetrieveMultiple(new QueryExpression("queue") { ColumnSet = new ColumnSet("name") })
                .Entities.Select(e => e.Id).Should().NotContain(privateQueue);
            FluentActions.Invoking(() => worker.Execute(new PickFromQueueRequest { QueueItemId = item, WorkerId = user }))
                .Should().Throw<FaultException<OrganizationServiceFault>>();

            admin.Execute(new RemoveFromQueueRequest { QueueItemId = item });
        }
        finally
        {
            foreach (var (table, id) in Enumerable.Reverse(created))
                try
                {
                    admin.Delete(table, id);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    output.WriteLine($"Cleanup: {table} {id} was not deleted: {ex.Message}");
                }
            try
            {
                admin.Update(new Entity("systemuser", user) { ["isdisabled"] = true });
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                output.WriteLine($"Cleanup: systemuser {user} was not disabled: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Plan 4.1b, task 8: outgoing email through the real <see cref="ServiceClient"/>. An SMTP profile on GreenMail, a queue's mailbox
    /// approved and tested (the mailbox test job), an email about an account with one attachment sent with <c>SendEmail</c>
    /// (<c>IssueSend</c> true): it arrives with the attachment and its <c>Message-ID</c> is <c>email.messageid</c>. A second queue's
    /// mailbox on a server that refuses its password fails its test (<c>outgoingemailstatus</c> Failure, the reason in
    /// <c>lastsyncerror</c>), and an email sent from it ends Failed.
    /// </summary>
    [Fact]
    public async Task ServiceClient_SendEmail_ArrivesWithAttachment()
    {
        await using var greenMail = await GreenMail.StartAsync();
        await using var refusing = FakeSmtpServer.Start(mailReply: "250 ok"); // answers 535 to AUTH
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var tag = "m" + Guid.NewGuid().ToString("N")[..8];
        var created = new List<(string Table, Guid Id)>();
        Guid Make(Entity e) { var id = admin.Create(e); created.Add((e.LogicalName, id)); return id; }

        Guid Profile(string host, int port, string password) => Make(new Entity("emailserverprofile")
        {
            ["name"] = $"{tag} SMTP {port}", ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = host,
            ["outgoingportnumber"] = port, ["outgoingusessl"] = false, ["outgoingauthenticationprotocol"] = new OptionSetValue(3),
            ["outgoingcredentialretrieval"] = new OptionSetValue(1), ["outgoingusername"] = GreenMail.SenderUser, ["outgoingpassword"] = password,
        });

        // A queue with an address has a mailbox; approved, on the profile, Server-Side Synchronization, then tested.
        Guid ReadyQueue(string name, Guid profile)
        {
            var queue = Make(new Entity("queue")
            {
                ["name"] = $"{tag} {name}", ["emailaddress"] = $"{name.ToLowerInvariant()}.{tag}@example.com",
                ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
            });
            admin.Update(new Entity("queue", queue) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
            var mailbox = admin.Retrieve("queue", queue, new ColumnSet("defaultmailbox")).GetAttributeValue<EntityReference>("defaultmailbox").Id;
            admin.Update(new Entity("mailbox", mailbox)
            {
                ["emailserverprofile"] = new EntityReference("emailserverprofile", profile), ["outgoingemaildeliverymethod"] = new OptionSetValue(2),
                ["enabledforoutgoingemail"] = true,
            });
            admin.Update(new Entity("mailbox", mailbox) { ["testemailconfigurationscheduled"] = true });
            return mailbox;
        }

        async Task<Entity> TestedMailbox(Guid mailbox)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var row = admin.Retrieve("mailbox", mailbox, new ColumnSet("testemailconfigurationscheduled", "outgoingemailstatus", "lastsyncerror"));
                if (row.GetAttributeValue<bool?>("testemailconfigurationscheduled") == false
                    && row.GetAttributeValue<OptionSetValue>("outgoingemailstatus")?.Value is 1 or 2)
                    return row;
                DateTime.UtcNow.Should().BeBefore(deadline, "the mailbox test job should complete");
                await Task.Delay(100);
            }
        }

        async Task<Entity> Sent(Guid email)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var row = admin.Retrieve("email", email, new ColumnSet("statecode", "statuscode", "messageid", "senton"));
                if (row.GetAttributeValue<OptionSetValue>("statuscode").Value is 3 or 8) return row;
                DateTime.UtcNow.Should().BeBefore(deadline, "the send job should complete");
                await Task.Delay(100);
            }
        }

        Guid Email(Guid fromQueue, Guid to, Guid account, string subject) => Make(new Entity("email")
        {
            ["subject"] = subject, ["description"] = "<p>The figures are attached.</p>",
            ["regardingobjectid"] = new EntityReference("account", account),
            ["from"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("queue", fromQueue) }]),
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", to) }]),
        });

        try
        {
            var account = Make(new Entity("account") { ["name"] = $"{tag} Fabrikam Example" });
            var contact = Make(new Entity("contact") { ["firstname"] = "Grace", ["lastname"] = tag, ["emailaddress1"] = GreenMail.RecipientAddress });

            // The mailbox that works.
            var good = ReadyQueue("Sales", Profile(greenMail.Hostname, greenMail.SmtpPort, GreenMail.SenderPassword));
            var goodQueue = admin.Retrieve("mailbox", good, new ColumnSet("regardingobjectid")).GetAttributeValue<EntityReference>("regardingobjectid").Id;
            var tested = await TestedMailbox(good);
            tested.GetAttributeValue<OptionSetValue>("outgoingemailstatus").Value.Should().Be(1, tested.GetAttributeValue<string>("lastsyncerror"));

            var subject = $"{tag} Quarterly numbers";
            var email = Email(goodQueue, contact, account, subject);
            admin.Create(new Entity("activitymimeattachment")
            {
                ["objectid"] = new EntityReference("email", email), ["objecttypecode"] = "email", ["filename"] = "numbers.txt",
                ["mimetype"] = "text/plain", ["body"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Q3: 42")),
            });

            admin.Execute(new SendEmailRequest { EmailId = email, IssueSend = true, TrackingToken = "" });

            var sent = await Sent(email);
            sent.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3, "the email is Sent");
            sent.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
            sent.GetAttributeValue<DateTime?>("senton").Should().NotBeNull();
            var messageId = sent.GetAttributeValue<string>("messageid");
            messageId.Should().NotBeNullOrEmpty();

            var inbox = await greenMail.RecipientInboxAsync();
            var received = inbox.Should().ContainSingle(m => m.Subject == subject).Subject;
            ("<" + received.MessageId + ">").Should().Be(messageId);
            received.HtmlBody.Should().Contain("The figures are attached.");
            var attachment = received.Attachments.OfType<MimeKit.MimePart>().Should().ContainSingle().Subject;
            attachment.FileName.Should().Be("numbers.txt");
            using (var content = new MemoryStream())
            {
                await attachment.Content!.DecodeToAsync(content);
                System.Text.Encoding.UTF8.GetString(content.ToArray()).Should().Be("Q3: 42");
            }

            // The mailbox whose password the server refuses: its test fails with the reason, and an email from it ends Failed.
            var bad = ReadyQueue("Billing", Profile("127.0.0.1", refusing.Port, "wrong-password"));
            var badQueue = admin.Retrieve("mailbox", bad, new ColumnSet("regardingobjectid")).GetAttributeValue<EntityReference>("regardingobjectid").Id;
            var failed = await TestedMailbox(bad);
            failed.GetAttributeValue<OptionSetValue>("outgoingemailstatus").Value.Should().Be(2, "the test fails");
            failed.GetAttributeValue<string>("lastsyncerror").Should().Contain("authentication failed").And.NotContain("wrong-password");

            var refused = Email(badQueue, contact, account, $"{tag} Not delivered");
            admin.Execute(new SendEmailRequest { EmailId = refused, IssueSend = true, TrackingToken = "" });
            var outcome = await Sent(refused);
            outcome.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(8, "the email is Failed");
            outcome.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
            (await greenMail.RecipientInboxAsync(atLeast: 2, TimeSpan.FromSeconds(1))).Should().ContainSingle("nothing else arrived");
        }
        finally
        {
            foreach (var (table, id) in Enumerable.Reverse(created))
                try
                {
                    admin.Delete(table, id);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    output.WriteLine($"Cleanup: {table} {id} was not deleted: {ex.Message}");
                }
        }
    }

    // ---- Plan 4.1c, task 11: incoming email through the real ServiceClient (GreenMail SMTP and IMAP, the host's sync job) ----

    /// <summary>
    /// A queue whose mailbox sends through GreenMail's SMTP (user <c>sender</c>) and reads the inbox of <paramref name="inbox"/> over IMAP,
    /// the queue's address being the inbox's. The mailbox is approved, both delivery methods are Server-Side Synchronization, both sides
    /// enabled, and the test is scheduled (a successful incoming test schedules the sync job). Created records go to <paramref name="created"/>.
    /// </summary>
    private static (Guid Queue, Guid Mailbox) ReceivingQueue(ServiceClient admin, List<(string Table, Guid Id)> created, string tag, GreenMail greenMail,
        MailUser inbox, string name)
    {
        Guid Make(Entity e) { var id = admin.Create(e); created.Add((e.LogicalName, id)); return id; }
        var profile = Make(new Entity("emailserverprofile")
        {
            ["name"] = $"{tag} {name} SMTP and IMAP", ["servertype"] = new OptionSetValue(4),
            ["outgoingserverlocation"] = greenMail.Hostname, ["outgoingportnumber"] = greenMail.SmtpPort, ["outgoingusessl"] = false,
            ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(1),
            ["outgoingusername"] = GreenMail.SenderUser, ["outgoingpassword"] = GreenMail.SenderPassword,
            ["incomingserverlocation"] = greenMail.Hostname, ["incomingportnumber"] = greenMail.ImapPort, ["incomingusessl"] = false,
            ["incomingcredentialretrieval"] = new OptionSetValue(1), ["incomingusername"] = inbox.Login, ["incomingpassword"] = inbox.Password,
            // The outgoing side keeps its own credentials (sender), the incoming side reads the queue's inbox.
            ["usesamesettingsforoutgoingconnections"] = false,
        });
        var queue = Make(new Entity("queue")
        {
            ["name"] = $"{tag} {name}", ["emailaddress"] = inbox.Address,
            ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
        });
        admin.Update(new Entity("queue", queue) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        var mailbox = admin.Retrieve("queue", queue, new ColumnSet("defaultmailbox")).GetAttributeValue<EntityReference>("defaultmailbox").Id;
        admin.Update(new Entity("mailbox", mailbox)
        {
            ["emailserverprofile"] = new EntityReference("emailserverprofile", profile),
            ["outgoingemaildeliverymethod"] = new OptionSetValue(2), ["enabledforoutgoingemail"] = true,
            ["incomingemaildeliverymethod"] = new OptionSetValue(2), ["enabledforincomingemail"] = true,
        });
        admin.Update(new Entity("mailbox", mailbox) { ["testemailconfigurationscheduled"] = true });
        return (queue, mailbox);
    }

    /// <summary>Waits for the mailbox test to succeed on both sides and for the first sync pass (which sets <c>processemailreceivedafter</c>).</summary>
    private static async Task<DateTime> FirstPassAsync(ServiceClient admin, Guid mailbox)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            var row = admin.Retrieve("mailbox", mailbox, new ColumnSet("testemailconfigurationscheduled", "incomingemailstatus", "outgoingemailstatus",
                "lastsyncerror", "lastsuccessfulsynccompletedon", "processemailreceivedafter"));
            var error = row.GetAttributeValue<string>("lastsyncerror");
            if (row.GetAttributeValue<bool?>("testemailconfigurationscheduled") == false)
            {
                row.GetAttributeValue<OptionSetValue>("outgoingemailstatus")?.Value.Should().NotBe(2, error);
                row.GetAttributeValue<OptionSetValue>("incomingemailstatus")?.Value.Should().NotBe(2, error);
            }
            if (row.GetAttributeValue<DateTime?>("lastsuccessfulsynccompletedon") is { } done)
            {
                row.GetAttributeValue<OptionSetValue>("incomingemailstatus").Value.Should().Be(1, error);
                row.GetAttributeValue<DateTime?>("processemailreceivedafter").Should().NotBeNull("the first pass sets it");
                return done;
            }
            DateTime.UtcNow.Should().BeBefore(deadline, "the mailbox test and the first sync pass should complete ({0})", error);
            await Task.Delay(250);
        }
    }

    /// <summary>Waits for a sync pass of <paramref name="mailbox"/> that completes after <paramref name="after"/>.</summary>
    private static async Task<DateTime> PassAfterAsync(ServiceClient admin, Guid mailbox, DateTime after)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var row = admin.Retrieve("mailbox", mailbox, new ColumnSet("lastsuccessfulsynccompletedon", "lastsyncerror"));
            if (row.GetAttributeValue<DateTime?>("lastsuccessfulsynccompletedon") is { } done && done > after) return done;
            DateTime.UtcNow.Should().BeBefore(deadline, "a sync pass should complete ({0})", row.GetAttributeValue<string>("lastsyncerror"));
            await Task.Delay(250);
        }
    }

    /// <summary>The received emails whose <c>messageid</c> is <paramref name="messageId"/> (with its angle brackets).</summary>
    private static List<Entity> ReceivedEmails(ServiceClient admin, string messageId)
    {
        var query = new QueryExpression("email")
        {
            ColumnSet = new ColumnSet("subject", "statecode", "statuscode", "directioncode", "regardingobjectid", "parentactivityid",
                "correlationmethod", "messageid", "inreplyto"),
        };
        query.Criteria.AddCondition("messageid", ConditionOperator.Equal, messageId);
        query.Criteria.AddCondition("directioncode", ConditionOperator.Equal, false);
        return [.. admin.RetrieveMultiple(query).Entities];
    }

    /// <summary>Waits up to a minute for the received email whose <c>messageid</c> is <paramref name="messageId"/>.</summary>
    private static async Task<Entity> ReceivedAsync(ServiceClient admin, string messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (ReceivedEmails(admin, messageId) is [var email, ..]) return email;
            DateTime.UtcNow.Should().BeBefore(deadline, "the sync job should import {0}", messageId);
            await Task.Delay(250);
        }
    }

    /// <summary>The active queue items of <paramref name="email"/>, with their queue.</summary>
    private static List<Entity> QueueItemsOf(ServiceClient admin, Guid email)
    {
        var query = new QueryExpression("queueitem") { ColumnSet = new ColumnSet("queueid", "objectid", "title", "statecode") };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, email);
        return [.. admin.RetrieveMultiple(query).Entities];
    }

    /// <summary>A message from <paramref name="from"/> to <paramref name="to"/> with a new Message-ID and an HTML body.</summary>
    private static MimeKit.MimeMessage IncomingMessage(string tag, string from, string to, string subject)
    {
        var m = new MimeKit.MimeMessage { Subject = subject, Date = DateTimeOffset.UtcNow };
        m.From.Add(MimeKit.MailboxAddress.Parse(from));
        m.To.Add(MimeKit.MailboxAddress.Parse(to));
        m.MessageId = $"{Guid.NewGuid():N}.{tag}@example.com";
        m.Body = new MimeKit.TextPart("html") { Text = "<p>" + subject + "</p>" };
        return m;
    }

    /// <summary>
    /// Puts <paramref name="message"/> in <paramref name="inbox"/> (IMAP APPEND) with an internal date a few seconds ahead, so it is never
    /// before the mailbox's <c>processemailreceivedafter</c> (IMAP dates have whole seconds).
    /// </summary>
    private static Task DeliverAsync(GreenMail greenMail, MailUser inbox, MimeKit.MimeMessage message) =>
        greenMail.AppendAsync(inbox, message, DateTimeOffset.UtcNow.AddSeconds(5));

    private void Cleanup(ServiceClient admin, List<(string Table, Guid Id)> created)
    {
        foreach (var (table, id) in Enumerable.Reverse(created))
            try
            {
                admin.Delete(table, id);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                output.WriteLine($"Cleanup: {table} {id} was not deleted: {ex.Message}");
            }
    }

    /// <summary>
    /// Plan 4.1c, task 11: an email about an account is sent from a queue's mailbox (SendEmail over GreenMail's SMTP, as in 4.1b); the
    /// contact's reply, with <c>In-Reply-To</c> the sent email's <c>Message-ID</c>, reaches the queue's inbox and the host's sync job imports
    /// it: a Received email regarding the same account, <c>parentactivityid</c> the sent email, <c>correlationmethod</c> 3 (In-Reply-To).
    /// </summary>
    [Fact]
    public async Task ServiceClient_Reply_LinksToSameAccount()
    {
        await using var greenMail = await GreenMail.StartAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var tag = "r" + Guid.NewGuid().ToString("N")[..8];
        var created = new List<(string Table, Guid Id)>();
        Guid Make(Entity e) { var id = admin.Create(e); created.Add((e.LogicalName, id)); return id; }
        try
        {
            var inbox = await greenMail.NewUserAsync();
            var (queue, mailbox) = ReceivingQueue(admin, created, tag, greenMail, inbox, "Support");
            await FirstPassAsync(admin, mailbox);

            var account = Make(new Entity("account") { ["name"] = $"{tag} Fabrikam Example" });
            var contact = Make(new Entity("contact") { ["firstname"] = "Grace", ["lastname"] = tag, ["emailaddress1"] = GreenMail.RecipientAddress });
            var subject = $"{tag} Your order";
            var sentId = Make(new Entity("email")
            {
                ["subject"] = subject, ["description"] = "<p>Your order has shipped.</p>",
                ["regardingobjectid"] = new EntityReference("account", account),
                ["from"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("queue", queue) }]),
                ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", contact) }]),
            });
            admin.Execute(new SendEmailRequest { EmailId = sentId, IssueSend = true, TrackingToken = "" });

            var deadline = DateTime.UtcNow.AddSeconds(60);
            Entity sent;
            while (true)
            {
                sent = admin.Retrieve("email", sentId, new ColumnSet("statuscode", "messageid"));
                if (sent.GetAttributeValue<OptionSetValue>("statuscode").Value is 3 or 8) break;
                DateTime.UtcNow.Should().BeBefore(deadline, "the send job should complete");
                await Task.Delay(100);
            }
            sent.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3, "the email is Sent");
            var sentMessageId = sent.GetAttributeValue<string>("messageid");
            (await greenMail.RecipientInboxAsync()).Should().Contain(m => m.Subject == subject);

            // The contact answers: the reply names the sent email in In-Reply-To and reaches the queue's inbox.
            var reply = IncomingMessage(tag, GreenMail.RecipientAddress, inbox.Address, "RE: " + subject);
            reply.InReplyTo = sentMessageId.Trim('<', '>');
            await DeliverAsync(greenMail, inbox, reply);

            var received = await ReceivedAsync(admin, $"<{reply.MessageId}>");
            created.Add(("email", received.Id));
            received.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(4, "the email is Received");
            received.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
            received.GetAttributeValue<EntityReference>("regardingobjectid").Should().NotBeNull("the reply joins the thread's record");
            received.GetAttributeValue<EntityReference>("regardingobjectid").LogicalName.Should().Be("account");
            received.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(account);
            received.GetAttributeValue<EntityReference>("parentactivityid").Should().NotBeNull();
            received.GetAttributeValue<EntityReference>("parentactivityid").Id.Should().Be(sentId);
            received.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(3, "correlated by In-Reply-To");
            received.GetAttributeValue<string>("inreplyto").Should().Be(sentMessageId);

            var full = admin.Retrieve("email", received.Id, new ColumnSet("from"));
            full.GetAttributeValue<EntityCollection>("from").Entities.Should().ContainSingle()
                .Which.GetAttributeValue<string>("addressused").Should().Be(GreenMail.RecipientAddress);
        }
        finally
        {
            Cleanup(admin, created);
        }
    }

    /// <summary>
    /// Plan 4.1c, task 11: a new message (no thread headers, no token) to a queue's address is imported by the host's sync job as a
    /// Received email without a regarding record, and a delivery adds it to the queue: one active queue item titled with the subject.
    /// </summary>
    [Fact]
    public async Task ServiceClient_NewMessageToQueueMailbox_InQueue_WithoutRegarding()
    {
        await using var greenMail = await GreenMail.StartAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var tag = "q" + Guid.NewGuid().ToString("N")[..8];
        var created = new List<(string Table, Guid Id)>();
        try
        {
            var inbox = await greenMail.NewUserAsync();
            var (queue, mailbox) = ReceivingQueue(admin, created, tag, greenMail, inbox, "Help desk");
            await FirstPassAsync(admin, mailbox);

            var subject = $"{tag} The printer is down";
            var message = IncomingMessage(tag, $"someone.{tag}@example.com", inbox.Address, subject);
            await DeliverAsync(greenMail, inbox, message);

            var received = await ReceivedAsync(admin, $"<{message.MessageId}>");
            created.Add(("email", received.Id));
            received.GetAttributeValue<string>("subject").Should().Be(subject);
            received.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(4, "the email is Received");
            received.GetAttributeValue<EntityReference>("regardingobjectid").Should().BeNull("a new message is about nothing yet");
            received.GetAttributeValue<EntityReference>("parentactivityid").Should().BeNull();
            received.GetAttributeValue<OptionSetValue>("correlationmethod").Value.Should().Be(0, "nothing correlated");

            var items = QueueItemsOf(admin, received.Id);
            var item = items.Should().ContainSingle("a delivery adds the email to its queue").Subject;
            item.GetAttributeValue<EntityReference>("queueid").Id.Should().Be(queue);
            item.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0, "the item is active");
            item.GetAttributeValue<string>("title").Should().Be(subject);
        }
        finally
        {
            Cleanup(admin, created);
        }
    }

    /// <summary>
    /// Plan 4.1c, task 11: a message imported by one pass is not imported again by the next ones, even when the inbox holds a second copy
    /// of it (same <c>Message-ID</c>, a new IMAP UID): one email and one queue item.
    /// </summary>
    [Fact]
    public async Task ServiceClient_TwoPasses_NoDuplicate()
    {
        await using var greenMail = await GreenMail.StartAsync();
        using var admin = Connect();
        admin.IsReady.Should().BeTrue(admin.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var tag = "d" + Guid.NewGuid().ToString("N")[..8];
        var created = new List<(string Table, Guid Id)>();
        try
        {
            var inbox = await greenMail.NewUserAsync();
            var (_, mailbox) = ReceivingQueue(admin, created, tag, greenMail, inbox, "Orders");
            await FirstPassAsync(admin, mailbox);

            var message = IncomingMessage(tag, $"buyer.{tag}@example.com", inbox.Address, $"{tag} Order 1001");
            var messageId = $"<{message.MessageId}>";
            await DeliverAsync(greenMail, inbox, message);
            var received = await ReceivedAsync(admin, messageId);
            created.Add(("email", received.Id));

            // The same message again (a second copy in the inbox), then two more passes: the first that ends after the copy arrived
            // may have listed the inbox before it, the second has listed it for sure.
            await DeliverAsync(greenMail, inbox, message);
            var second = await PassAfterAsync(admin, mailbox, DateTime.UtcNow);
            await PassAfterAsync(admin, mailbox, second);

            ReceivedEmails(admin, messageId).Should().ContainSingle("a message is imported once").Which.Id.Should().Be(received.Id);
            QueueItemsOf(admin, received.Id).Should().ContainSingle("and added to its queue once");
            var row = admin.Retrieve("mailbox", mailbox, new ColumnSet("incomingemailstatus", "lastsyncerrorcount"));
            row.GetAttributeValue<OptionSetValue>("incomingemailstatus").Value.Should().Be(1);
            row.GetAttributeValue<int?>("lastsyncerrorcount").GetValueOrDefault().Should().Be(0);
        }
        finally
        {
            Cleanup(admin, created);
        }
    }
}
