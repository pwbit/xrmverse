using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 3: the write path of the activity model. Every Create, Update, Assign, SetState and Delete of an activity keeps
/// its <c>activitypointer</c> row (A1) and its <c>activityparty</c> rows (A9, A10) in the same transaction; closed activities are
/// read-only (A11); the pointer and party tables refuse direct writes; deleting a regarding or party record (RF3).
/// </summary>
[Collection("xrmd")]
public sealed class ActivityWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----

    private async Task<EntityReference> ContactAsync(string first, string last = "Example")
    {
        var id = await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = first, ["lastname"] = last, ["emailaddress1"] = $"{first.ToLowerInvariant()}.{_run}@example.com",
        });
        return new EntityReference("contact", id);
    }

    private async Task<EntityReference> AccountAsync(string name = "Contoso Example") =>
        new("account", await Admin.CreateAsync(new Entity("account") { ["name"] = name + " " + _run, ["emailaddress1"] = $"info.{_run}@example.com" }));

    private static Entity Party(EntityReference party) => new("activityparty") { ["partyid"] = party };

    private static EntityCollection Parties(params EntityReference[] parties) => new([.. parties.Select(Party)]);

    private async Task<Guid> CallerIdAsync() =>
        (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private sealed record PartyRow(int Mask, Guid? PartyId, int? PartyType, string? Address, bool Deleted, Guid? Owner);

    private async Task<List<PartyRow>> PartyRowsAsync(Guid activityId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, """
            SELECT participationtypemask, partyid, partyidtype, addressused, ispartydeleted, ownerid
            FROM data.activityparty WHERE activityid = @id
            """);
        cmd.Parameters.AddWithValue("@id", activityId);
        var rows = new List<PartyRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(new PartyRow(rd.GetInt32(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetInt32(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), !rd.IsDBNull(4) && rd.GetBoolean(4), rd.IsDBNull(5) ? null : rd.GetGuid(5)));
        return rows;
    }

    private async Task<Dictionary<string, object?>?> PointerRowAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT * FROM data.activitypointer WHERE activityid = @id");
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < rd.FieldCount; i++) row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
        return row;
    }

    private async Task<int> CountAsync(string table, string column, Guid id) =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.[{table}] WHERE [{column}] = @id", ("@id", id)));

    private Task SetStateAsync(string table, Guid id, int state, int status) =>
        Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(table, id), State = new OptionSetValue(state), Status = new OptionSetValue(status),
        });

    // ---- pointer ----

    [Fact]
    public async Task Create_email_writes_pointer_row()
    {
        var account = await AccountAsync();
        var me = await CallerIdAsync();

        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Welcome " + _run, ["regardingobjectid"] = account });

        var pointer = await PointerRowAsync(id);
        pointer.Should().NotBeNull();
        pointer!["activitytypecode"].Should().Be(ActivitySchema.EmailObjectTypeCode);
        pointer["subject"].Should().Be("Welcome " + _run);
        pointer["regardingobjectid"].Should().Be(account.Id);
        pointer["regardingobjectidtype"].Should().Be(1);
        pointer["ownerid"].Should().Be(me);
        pointer["statecode"].Should().Be(0);
        pointer["statuscode"].Should().Be(1);
        // The email's own row names its type too, and the pointer reads as any table.
        (await Admin.RetrieveAsync("email", id, new ColumnSet("activitytypecode"))).GetAttributeValue<string>("activitytypecode").Should().Be("email");
        var read = await Admin.RetrieveAsync("activitypointer", id, new ColumnSet("activitytypecode", "subject", "regardingobjectid"));
        read.GetAttributeValue<string>("activitytypecode").Should().Be("email");
        read.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(account.Id);
    }

    [Fact]
    public async Task Pointer_subject_is_truncated_to_its_length()
    {
        // Ruling Y1: email.subject (800) is longer than activitypointer.subject (400); the pointer keeps the first 400 characters.
        var subject = string.Concat(Enumerable.Repeat("0123456789", 60));
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = subject });

        (await Admin.RetrieveAsync("email", id, new ColumnSet("subject"))).GetAttributeValue<string>("subject").Should().Be(subject);
        (await PointerRowAsync(id))!["subject"].Should().Be(subject[..400]);

        await Admin.UpdateAsync(new Entity("email", id) { ["subject"] = "short" });
        (await PointerRowAsync(id))!["subject"].Should().Be("short");
    }

    [Fact]
    public async Task Pointer_description_is_truncated_to_its_declared_length()
    {
        // Final review M8: activitypointer.description is a Memo of 2000; an email's description is far longer. The pointer keeps
        // the first 2000 characters on Create and on Update, as for the subject (ruling Y1); the email keeps the full text.
        var description = string.Concat(Enumerable.Repeat("0123456789", 250));
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Long body " + _run, ["description"] = description });

        (await Admin.RetrieveAsync("email", id, new ColumnSet("description"))).GetAttributeValue<string>("description").Should().Be(description);
        (await PointerRowAsync(id))!["description"].Should().Be(description[..2000]);
        (await Admin.RetrieveAsync("activitypointer", id, new ColumnSet("description"))).GetAttributeValue<string>("description")
            .Should().HaveLength(2000);

        await Admin.UpdateAsync(new Entity("email", id) { ["description"] = description + "!" });
        (await PointerRowAsync(id))!["description"].Should().Be(description[..2000]);
        await Admin.UpdateAsync(new Entity("email", id) { ["description"] = "short" });
        (await PointerRowAsync(id))!["description"].Should().Be("short");
    }

    [Fact]
    public async Task Custom_activity_writes_pointer_and_parties()
    {
        var name = $"new_sms{_run}";
        var entity = TestHelpers.Entity(name, name, "Text message " + _run);
        entity.IsActivity = true;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "subject", SchemaName = "Subject", MaxLength = 200, DisplayName = Label("Subject"),
            },
        });
        var code = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(name)!.ObjectTypeCode;
        var ada = await ContactAsync("Ada");
        var account = await AccountAsync();

        var id = await Admin.CreateAsync(new Entity(name) { ["subject"] = "Custom " + _run, ["to"] = Parties(ada), ["regardingobjectid"] = account });

        var pointer = await PointerRowAsync(id);
        pointer!["activitytypecode"].Should().Be(code);
        pointer["subject"].Should().Be("Custom " + _run);
        (await PartyRowsAsync(id)).Select(r => r.Mask).Should().BeEquivalentTo([2, 8, 9]);
        (await Admin.RetrieveAsync("activitypointer", id, new ColumnSet("activitytypecode"))).GetAttributeValue<string>("activitytypecode").Should().Be(name);

        // Final review C1: a standard activity cannot take the custom activity's id (412), and its rows stay.
        (await FaultAsync(() => Admin.CreateAsync(new Entity("task", id) { ["subject"] = "Takeover " + _run }))).HttpStatus.Should().Be(412);
        (await PointerRowAsync(id))!["activitytypecode"].Should().Be(code);
        (await PartyRowsAsync(id)).Select(r => r.Mask).Should().BeEquivalentTo([2, 8, 9]);

        await SetStateAsync(name, id, 1, 2);
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity(name, id) { ["subject"] = "changed" }));
        await SetStateAsync(name, id, 0, 1);
        await Admin.DeleteAsync(name, id);
        (await CountAsync("activitypointer", "activityid", id)).Should().Be(0);
        (await CountAsync("activityparty", "activityid", id)).Should().Be(0);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    [Fact]
    public async Task Create_task_defaults_to_not_started()
    {
        var id = await Admin.CreateAsync(new Entity("task") { ["subject"] = "Call back " + _run });

        var task = await Admin.RetrieveAsync("task", id, new ColumnSet("statecode", "statuscode"));
        task.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        task.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
        (await PointerRowAsync(id))!["statuscode"].Should().Be(2);
    }

    // ---- parties ----

    [Fact]
    public async Task Create_email_with_parties()
    {
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var me = await CallerIdAsync();
        await ScalarAsync("UPDATE data.systemuser SET internalemailaddress = N'admin@example.com' WHERE systemuserid = @id AND internalemailaddress IS NULL",
            ("@id", me));
        var myAddress = (string?)await ScalarAsync("SELECT internalemailaddress FROM data.systemuser WHERE systemuserid = @id", ("@id", me));

        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "Parties " + _run,
            ["regardingobjectid"] = account,
            ["to"] = Parties(ada, ben),
            ["from"] = Parties(new EntityReference("systemuser", me)),
        });

        var rows = await PartyRowsAsync(id);
        rows.Should().HaveCount(5);
        rows.Should().ContainSingle(r => r.Mask == 1).Which.Should().Be(new PartyRow(1, me, 8, myAddress, false, me));
        rows.Where(r => r.Mask == 2).Select(r => (r.PartyId, r.PartyType, r.Address)).Should().BeEquivalentTo(
            [(ada.Id, (int?)2, $"ada.{_run}@example.com"), (ben.Id, (int?)2, $"ben.{_run}@example.com")]);
        rows.Should().ContainSingle(r => r.Mask == 9).Which.PartyId.Should().Be(me);
        rows.Should().ContainSingle(r => r.Mask == 8).Which.Should().Match<PartyRow>(r => r.PartyId == account.Id && r.PartyType == 1);
        rows.Should().OnlyContain(r => r.Owner == me && !r.Deleted);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND activityidtype = 4202", ("@id", id))).Should().Be(5);
    }

    [Fact]
    public async Task Unresolved_address_is_accepted_on_email_only()
    {
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "Unresolved " + _run,
            ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "someone@example.com" }]),
        });
        (await PartyRowsAsync(id)).Should().ContainSingle(r => r.Mask == 2).Which.Should()
            .Match<PartyRow>(r => r.PartyId == null && r.Address == "someone@example.com");

        var message = await BadRequestAsync(() => Admin.CreateAsync(new Entity("phonecall")
        {
            ["subject"] = "Unresolved " + _run,
            ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "someone@example.com" }]),
        }));
        message.Should().Contain("addressused");
    }

    [Fact]
    public async Task Party_rules()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "party " + _run });
        var ada = await ContactAsync("Ada");
        var missing = Guid.NewGuid();

        var notFound = await FaultAsync(() => Admin.CreateAsync(new Entity("email") { ["subject"] = "x", ["to"] = Parties(new EntityReference("contact", missing)) }));
        notFound.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        notFound.HttpStatus.Should().Be(404);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE partyid = @id", ("@id", missing))).Should().Be(0);

        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("email")
            {
                ["subject"] = "x", ["to"] = Parties(new EntityReference(TestEntities.Project, project)),
            })))
            .Should().Contain(TestEntities.Project);
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("email") { ["subject"] = "x", ["from"] = Parties(ada, ada) })))
            .Should().Contain("from");
        foreach (var mask in new[] { 8, 9 })
            await BadRequestAsync(() => Admin.CreateAsync(new Entity("email")
            {
                ["subject"] = "x",
                ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = ada, ["participationtypemask"] = new OptionSetValue(mask) }]),
            }));
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("email") { ["subject"] = "x", ["requiredattendees"] = Parties(ada) })))
            .Should().Contain("requiredattendees");
        // An appointment's organizer takes one party too, and its attendees are written with their types.
        await BadRequestAsync(() => Admin.CreateAsync(new Entity("appointment") { ["subject"] = "x", ["organizer"] = Parties(ada, ada) }));
        var appointment = await Admin.CreateAsync(new Entity("appointment")
        {
            ["subject"] = "Meeting " + _run, ["requiredattendees"] = Parties(ada), ["optionalattendees"] = Parties(ada),
        });
        (await PartyRowsAsync(appointment)).Select(r => r.Mask).Should().BeEquivalentTo([5, 6, 9]);
    }

    [Fact]
    public async Task Update_of_to_keeps_other_party_types()
    {
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "RF4 " + _run, ["regardingobjectid"] = account, ["to"] = Parties(ada), ["cc"] = Parties(ben),
        });

        await Admin.UpdateAsync(new Entity("email", id) { ["to"] = Parties(ben) });

        var rows = await PartyRowsAsync(id);
        rows.Where(r => r.Mask == 2).Select(r => r.PartyId).Should().Equal(ben.Id);
        rows.Where(r => r.Mask == 3).Select(r => r.PartyId).Should().Equal(ben.Id);
        rows.Should().ContainSingle(r => r.Mask == 8);
        rows.Should().ContainSingle(r => r.Mask == 9);
    }

    [Fact]
    public async Task Empty_to_clears_only_to()
    {
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "RF4 " + _run, ["to"] = Parties(ada, ben), ["cc"] = Parties(ben) });

        await Admin.UpdateAsync(new Entity("email", id) { ["to"] = new EntityCollection() });

        var rows = await PartyRowsAsync(id);
        rows.Should().NotContain(r => r.Mask == 2);
        rows.Where(r => r.Mask == 3).Select(r => r.PartyId).Should().Equal(ben.Id);
        rows.Should().ContainSingle(r => r.Mask == 9);

        // Null clears the type as well.
        await Admin.UpdateAsync(new Entity("email", id) { ["cc"] = null });
        (await PartyRowsAsync(id)).Select(r => r.Mask).Should().Equal(9);
    }

    [Fact]
    public async Task Assign_updates_pointer_owner_and_owner_party()
    {
        var ada = await ContactAsync("Ada");
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, user, team: false, ("prvReadActivity", DepthMask.Basic));
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Assign " + _run, ["to"] = Parties(ada) });

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("email", id), Assignee = new EntityReference("systemuser", user) });

        (await PointerRowAsync(id))!["ownerid"].Should().Be(user);
        (await PointerRowAsync(id))!["owninguser"].Should().Be(user);
        var rows = await PartyRowsAsync(id);
        rows.Should().ContainSingle(r => r.Mask == 9).Which.PartyId.Should().Be(user);
        rows.Should().OnlyContain(r => r.Owner == user);
    }

    [Fact]
    public async Task Changing_regarding_updates_pointer_and_regarding_party()
    {
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("task") { ["subject"] = "Regarding " + _run, ["regardingobjectid"] = account });

        await Admin.UpdateAsync(new Entity("task", id) { ["regardingobjectid"] = ada });

        var pointer = await PointerRowAsync(id);
        pointer!["regardingobjectid"].Should().Be(ada.Id);
        pointer["regardingobjectidtype"].Should().Be(2);
        (await PartyRowsAsync(id)).Should().ContainSingle(r => r.Mask == 8).Which.Should().Match<PartyRow>(r => r.PartyId == ada.Id && r.PartyType == 2);

        await Admin.UpdateAsync(new Entity("task", id) { ["regardingobjectid"] = null });
        (await PointerRowAsync(id))!["regardingobjectid"].Should().BeNull();
        (await PartyRowsAsync(id)).Select(r => r.Mask).Should().Equal(9);
    }

    // ---- EntityName columns (A7) ----

    [Fact]
    public async Task Entityname_values_must_match_their_target()
    {
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "A7 " + _run, ["regardingobjectid"] = account, ["regardingobjecttypecode"] = "account", ["activitytypecode"] = "email",
        });
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("email") { ["regardingobjectid"] = account, ["regardingobjecttypecode"] = "contact" })))
            .Should().Contain("regardingobjecttypecode");
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("email") { ["activitytypecode"] = "task" }))).Should().Contain("activitytypecode");

        // On Update without the lookup, the stored target decides.
        await Admin.UpdateAsync(new Entity("email", id) { ["regardingobjecttypecode"] = "account" });
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("email", id) { ["regardingobjecttypecode"] = "contact" })))
            .Should().Contain("regardingobjecttypecode");
    }

    // ---- state ----

    [Fact]
    public async Task Closed_activity_is_read_only()
    {
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Closed " + _run });

        await SetStateAsync("email", id, 1, 2);

        var closed = await Admin.RetrieveAsync("email", id, new ColumnSet("actualend", "statecode"));
        closed.GetAttributeValue<DateTime?>("actualend").Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        (await PointerRowAsync(id))!["statecode"].Should().Be(1);
        (await PointerRowAsync(id))!["actualend"].Should().NotBeNull();
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("email", id) { ["subject"] = "changed" }))).Should().Contain("closed");
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity("email", id) { ["to"] = new EntityCollection() }));

        await SetStateAsync("email", id, 0, 1);
        await Admin.UpdateAsync(new Entity("email", id) { ["subject"] = "reopened" });
        (await PointerRowAsync(id))!["subject"].Should().Be("reopened");

        // Canceled is closed as well; an actualend already set is kept.
        var given = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = "Canceled " + _run, ["actualend"] = given });
        await SetStateAsync("task", task, 2, 6);
        (await Admin.RetrieveAsync("task", task, new ColumnSet("actualend"))).GetAttributeValue<DateTime>("actualend").Should().Be(given);
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity("task", task) { ["percentcomplete"] = 50 }));
    }

    [Fact]
    public async Task Assign_of_closed_activity_is_allowed()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, user, team: false, ("prvReadActivity", DepthMask.Basic));
        var id = await Admin.CreateAsync(new Entity("phonecall") { ["subject"] = "Assign closed " + _run });
        await SetStateAsync("phonecall", id, 1, 2);

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("phonecall", id), Assignee = new EntityReference("systemuser", user) });

        (await PointerRowAsync(id))!["ownerid"].Should().Be(user);
        // An Update of ownerid is still an Update of a closed activity.
        var me = await CallerIdAsync();
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity("phonecall", id) { ["ownerid"] = new EntityReference("systemuser", me) }));
    }

    [Fact]
    public async Task Status_must_belong_to_state()
    {
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Status " + _run });

        await BadRequestAsync(() => SetStateAsync("email", id, 1, 1));
        await Admin.UpdateAsync(new Entity("email", id) { ["statecode"] = new OptionSetValue(1) });

        (await Admin.RetrieveAsync("email", id, new ColumnSet("statuscode"))).GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
        (await PointerRowAsync(id))!["statuscode"].Should().Be(2);
    }

    // ---- system tables ----

    [Fact]
    public async Task Pointer_and_party_tables_refuse_writes()
    {
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Refuse " + _run, ["to"] = Parties(ada) });
        var party = (Guid)(await ScalarAsync("SELECT TOP 1 activitypartyid FROM data.activityparty WHERE activityid = @id", ("@id", id)))!;
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));

        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("activitypointer") { ["subject"] = "x" }))).Should().Contain("activitypointer");
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("activityparty") { ["addressused"] = "x@example.com" }))).Should().Contain("activityparty");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("activitypointer", id) { ["subject"] = "x" }))).Should().Contain("activitypointer");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("activityparty", party) { ["addressused"] = "x@example.com" }))).Should().Contain("activityparty");
        (await BadRequestAsync(() => Admin.DeleteAsync("activitypointer", id))).Should().Contain("activitypointer");
        (await BadRequestAsync(() => Admin.DeleteAsync("activityparty", party))).Should().Contain("activityparty");
        await BadRequestAsync(() => SetStateAsync("activitypointer", id, 1, 2));
        await BadRequestAsync(() => Admin.ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference("activitypointer", id), Assignee = new EntityReference("systemuser", user),
        }));
        foreach (var grant in new OrganizationRequest[]
                 {
                     new GrantAccessRequest
                     {
                         Target = new EntityReference("activitypointer", id),
                         PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", user), AccessMask = AccessRights.ReadAccess },
                     },
                     new ModifyAccessRequest
                     {
                         Target = new EntityReference("activitypointer", id),
                         PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", user), AccessMask = AccessRights.ReadAccess },
                     },
                 })
            await BadRequestAsync(() => Admin.ExecuteAsync(grant));

        (await PointerRowAsync(id))!["subject"].Should().Be("Refuse " + _run);
        (await PartyRowsAsync(id)).Should().HaveCount(2);
    }

    // ---- deletes (RF3) ----

    [Fact]
    public async Task Deleting_account_cascades_to_activities_pointer_parties_and_notes()
    {
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = "RF3 " + _run, ["regardingobjectid"] = account, ["to"] = Parties(ada) });
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = "RF3 " + _run, ["regardingobjectid"] = account });
        var emailNote = await Admin.CreateAsync(new Entity("annotation") { ["subject"] = "On the email", ["objectid"] = new EntityReference("email", email) });
        var accountNote = await Admin.CreateAsync(new Entity("annotation") { ["subject"] = "On the account", ["objectid"] = account });

        await Admin.DeleteAsync("account", account.Id);

        (await CountAsync("email", "activityid", email)).Should().Be(0);
        (await CountAsync("task", "activityid", task)).Should().Be(0);
        (await CountAsync("activitypointer", "activityid", email)).Should().Be(0);
        (await CountAsync("activitypointer", "activityid", task)).Should().Be(0);
        (await CountAsync("activityparty", "activityid", email)).Should().Be(0);
        (await CountAsync("activityparty", "activityid", task)).Should().Be(0);
        (await CountAsync("annotation", "annotationid", emailNote)).Should().Be(0);
        (await CountAsync("annotation", "annotationid", accountNote)).Should().Be(0);
        // The contact named as a recipient stays.
        (await CountAsync("contact", "contactid", ada.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_account_removes_the_bodies_of_its_cascaded_notes()
    {
        var account = await AccountAsync();
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = "RF3 " + _run, ["regardingobjectid"] = account });
        // Task 5: the notes carry bodies, whose fileattachment rows and blobs go with them.
        var emailNote = await Admin.CreateAsync(new Entity("annotation")
        {
            ["subject"] = "On the email", ["objectid"] = new EntityReference("email", email), ["filename"] = "a.txt", ["documentbody"] = Convert.ToBase64String([1, 2, 3]),
        });
        var accountNote = await Admin.CreateAsync(new Entity("annotation")
        {
            ["subject"] = "On the account", ["objectid"] = account, ["filename"] = "b.txt", ["documentbody"] = Convert.ToBase64String([4, 5]),
        });
        var bodies = new List<Guid>();
        foreach (var note in new[] { emailNote, accountNote })
            bodies.Add((Guid)(await ScalarAsync("SELECT fileattachmentid FROM data.fileattachment WHERE objectid = @id", ("@id", note)))!);

        await Admin.DeleteAsync("account", account.Id);

        (await CountAsync("annotation", "annotationid", emailNote)).Should().Be(0);
        (await CountAsync("annotation", "annotationid", accountNote)).Should().Be(0);
        await fx.DrainFileDeletionsAsync();
        var blobs = fx.App.Services.GetRequiredService<Xrmd.Files.IFileStore>();
        foreach (var body in bodies)
        {
            (await CountAsync("fileattachment", "fileattachmentid", body)).Should().Be(0);
            (await blobs.GetLengthAsync($"xrmverse_test/{body}", default)).Should().BeNull("a deleted note's body blob goes after the commit");
        }
    }

    [Fact]
    public async Task Deleting_contact_leaves_records_of_the_account_with_the_same_id()
    {
        // Final re-review N4: a polymorphic lookup matches the deleted record by its id and its table. Deleting contact X neither
        // cascades to the activities and notes regarding account X nor clears a lookup that names account X (RemoveLink).
        var account = await AccountAsync();
        await Admin.CreateAsync(new Entity("contact", account.Id) { ["firstname"] = "Same", ["lastname"] = "Id " + _run });
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = "N4 " + _run, ["regardingobjectid"] = account });
        var note = await Admin.CreateAsync(new Entity("annotation") { ["subject"] = "N4 " + _run, ["objectid"] = account });
        var child = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Child " + _run, ["parentcustomerid"] = account });

        await Admin.DeleteAsync("contact", account.Id);

        (await CountAsync("contact", "contactid", account.Id)).Should().Be(0);
        (await CountAsync("task", "activityid", task)).Should().Be(1);
        (await CountAsync("activitypointer", "activityid", task)).Should().Be(1);
        (await CountAsync("annotation", "annotationid", note)).Should().Be(1);
        (await ScalarAsync("SELECT parentcustomerid FROM data.contact WHERE contactid = @id", ("@id", child))).Should().Be(account.Id);
    }

    [Fact]
    public async Task Deleting_activity_deletes_its_pointer_and_parties()
    {
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Delete " + _run, ["to"] = Parties(ada) });

        await Admin.DeleteAsync("email", id);

        (await CountAsync("activitypointer", "activityid", id)).Should().Be(0);
        (await CountAsync("activityparty", "activityid", id)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_party_record_marks_party_deleted()
    {
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Party deleted " + _run, ["to"] = Parties(ada, ben) });

        await Admin.DeleteAsync("contact", ada.Id);

        var rows = await PartyRowsAsync(id);
        rows.Should().ContainSingle(r => r.PartyId == ada.Id).Which.Should().Match<PartyRow>(r => r.Deleted && r.Mask == 2 && r.PartyType == 2);
        rows.Should().ContainSingle(r => r.PartyId == ben.Id).Which.Deleted.Should().BeFalse();
        (await CountAsync("email", "activityid", id)).Should().Be(1);
    }

    // ---- transaction and pipeline ----

    private async Task<Guid> StepAsync(string plugin, string message, string entity, int stage, string? config = null)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins." + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, type, message, entity, stage, config: config);
    }

    [Fact]
    public async Task Rolled_back_create_leaves_no_pointer_or_parties()
    {
        var ada = await ContactAsync("Ada");
        await StepAsync("ThrowPlugin", "Create", "email", 40, "invalid:refused after the create");
        var id = Guid.NewGuid();

        var act = () => Admin.CreateAsync(new Entity("email", id) { ["subject"] = "Rolled back " + _run, ["to"] = Parties(ada) });

        await act.Should().ThrowAsync<Exception>();
        (await CountAsync("email", "activityid", id)).Should().Be(0);
        (await CountAsync("activitypointer", "activityid", id)).Should().Be(0);
        (await CountAsync("activityparty", "activityid", id)).Should().Be(0);
    }

    private async Task<string> RecordedAsync(int stage)
    {
        var data = (string?)await ScalarAsync($"SELECT TOP 1 pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE @name ORDER BY createdon DESC",
            ("@name", $"Create|{stage}|%"));
        data.Should().NotBeNull();
        return data!;
    }

    [Fact]
    public async Task Post_image_of_email_create_has_parties()
    {
        var ada = await ContactAsync("Ada");
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var step = await StepAsync("ContextRecorderPlugin", "Create", "email", 40, TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "subject,to,cc");

        var ben = await ContactAsync("Ben");
        await Admin.CreateAsync(new Entity("email") { ["subject"] = "Image " + _run, ["to"] = Parties(ada, ben) });

        var lines = (await RecordedAsync(40)).Split('\n');
        var post = lines.Single(line => line.StartsWith("post=post:", StringComparison.Ordinal));
        post["post=post:".Length..].Split(',').Should().Contain(["subject", "to"]);
        // The image's party list holds the parties as written (count and first party).
        lines.Single(line => line.StartsWith("postcollections=", StringComparison.Ordinal))["postcollections=post:".Length..].Split(',')
            .Should().Contain($"to:2:{ada.Id}");
    }

    [Fact]
    public async Task Pre_operation_target_carries_party_lists_as_sent()
    {
        var ada = await ContactAsync("Ada");
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        await StepAsync("ContextRecorderPlugin", "Create", "email", 20, TestEntities.PipeLog);

        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Target " + _run, ["to"] = Parties(ada), ["cc"] = Parties(ada) });

        var lines = (await RecordedAsync(20)).Split('\n');
        var target = lines.Single(line => line.StartsWith("target=", StringComparison.Ordinal));
        target["target=".Length..].Split(',').Should().Contain(["cc", "subject", "to"]);
        lines.Single(line => line.StartsWith("targetcollections=", StringComparison.Ordinal))
            .Should().Be($"targetcollections=cc:1:{ada.Id},to:1:{ada.Id}");
        (await PartyRowsAsync(id)).Select(r => r.Mask).Should().BeEquivalentTo([2, 3, 9]);
    }

    // ---- fix round 1 ----

    [Fact]
    public async Task Party_without_read_is_refused()
    {
        // Ruling Y2: naming a party the caller cannot read is a Read denial, before its address is read; AppendTo is not needed.
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, team: false, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic));
        var service = ServiceFor(fx, oid);
        var ada = await ContactAsync("Ada");
        var subject = "No read " + _run;

        var denied = await FaultAsync(() => service.CreateAsync(new Entity("email") { ["subject"] = subject, ["to"] = Parties(ada) }));
        denied.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        denied.HttpStatus.Should().Be(403);
        (await ScalarAsync("SELECT COUNT(*) FROM data.email WHERE subject = @s", ("@s", subject))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE partyid = @id", ("@id", ada.Id))).Should().Be(0);

        // Read on the table but not on this record (owned by another user): the record's Read denial.
        await GrantAsync(fx, user, team: false, ("prvReadContact", DepthMask.Basic));
        Cache(fx).Invalidate();
        var notOwn = await FaultAsync(() => service.CreateAsync(new Entity("email") { ["subject"] = subject, ["to"] = Parties(ada) }));
        notOwn.ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        var own = await service.CreateAsync(new Entity("email") { ["subject"] = subject });
        (await FaultAsync(() => service.UpdateAsync(new Entity("email", own) { ["to"] = Parties(ada) }))).HttpStatus.Should().Be(403);
        (await PartyRowsAsync(own)).Select(r => r.Mask).Should().Equal(9);

        // With Read on the record it works (no AppendTo on the contact).
        await GrantAsync(fx, user, team: false, ("prvReadContact", DepthMask.Global));
        Cache(fx).Invalidate();
        var id = await service.CreateAsync(new Entity("email") { ["subject"] = subject, ["to"] = Parties(ada) });
        (await PartyRowsAsync(id)).Should().ContainSingle(r => r.Mask == 2).Which.PartyId.Should().Be(ada.Id);
    }

    [Fact]
    public async Task Closed_custom_activity_refuses_a_file_upload()
    {
        var name = $"new_fax{_run}";
        var entity = TestHelpers.Entity(name, name, "Fax " + _run);
        entity.IsActivity = true;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "subject", SchemaName = "Subject", MaxLength = 200, DisplayName = Label("Subject"),
            },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new Microsoft.Xrm.Sdk.Metadata.FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc" },
        });
        var id = await Admin.CreateAsync(new Entity(name) { ["subject"] = "File " + _run });
        await SetStateAsync(name, id, 1, 2);

        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(name, id), FileAttributeName = "new_doc", FileName = "a.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String("block-000000"u8.ToArray());
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = [1, 2, 3] });

        (await BadRequestAsync(() => Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
            {
                FileContinuationToken = token, BlockList = [block], FileName = "a.txt", MimeType = "text/plain",
            })))
            .Should().Contain("new_doc");
        (await ScalarAsync($"SELECT new_doc FROM data.[{name}] WHERE activityid = @id", ("@id", id))).Should().BeNull();
        await SetStateAsync(name, id, 0, 1);
        await Admin.DeleteAsync(name, id);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    [Fact]
    public async Task Deleted_owner_team_marks_its_owner_party_row()
    {
        // A team that owns an activity cannot be deleted (it owns records), so its Owner row never dangles through the API;
        // the handler the Delete of a team calls marks the rows that name it.
        var root = await RootBusinessUnitAsync(fx);
        var team = await CreateTeamAsync(fx, root);
        await GrantAsync(fx, team, team: true, ("prvReadActivity", DepthMask.Basic));
        var id = await Admin.CreateAsync(new Entity("task") { ["subject"] = "Team " + _run, ["ownerid"] = new EntityReference("team", team) });
        (await PartyRowsAsync(id)).Should().ContainSingle(r => r.Mask == 9).Which.Should().Match<PartyRow>(r => r.PartyId == team && r.PartyType == 9);
        await BadRequestAsync(() => Admin.DeleteAsync("team", team));

        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            var session = new DataSession(conn, null);
            await new ActivityParties(fx.App.Services.GetRequiredService<IMetadataStore>()).MarkPartyDeletedAsync(session, "team", team, default);
        }

        (await PartyRowsAsync(id)).Should().ContainSingle(r => r.Mask == 9).Which.Should().Match<PartyRow>(r => r.Deleted && r.PartyId == team);
    }

    [Fact]
    public async Task Rolled_back_update_keeps_old_parties_and_pointer()
    {
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Before " + _run, ["to"] = Parties(ada) });
        await StepAsync("ThrowPlugin", "Update", "email", 40, "invalid:refused after the update");

        await FaultAsync(() => Admin.UpdateAsync(new Entity("email", id) { ["subject"] = "After", ["to"] = Parties(ben) }));

        (await PartyRowsAsync(id)).Where(r => r.Mask == 2).Select(r => r.PartyId).Should().Equal(ada.Id);
        (await PointerRowAsync(id))!["subject"].Should().Be("Before " + _run);
    }

    [Fact]
    public async Task Audited_update_of_a_party_list_writes_no_party_column()
    {
        var organization = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        try
        {
            var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Audited " + _run, ["to"] = Parties(ada) });

            await Admin.UpdateAsync(new Entity("email", id) { ["to"] = Parties(ben) });

            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "SELECT changedata FROM data.audit WHERE objectid = @id");
            cmd.Parameters.AddWithValue("@id", id);
            var changes = new List<Xrmd.Audit.AttributeChange>();
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync())
                    if (Xrmd.Audit.ChangeData.Deserialize(rd.GetString(0)) is Xrmd.Audit.AttributeChange change) changes.Add(change);
            changes.Should().NotBeEmpty("the Create of an audited email is audited");
            changes.SelectMany(c => c.Old.Keys.Concat(c.New.Keys)).Should().NotContain(["to", "from", "cc", "bcc", "related"]);
            (await PartyRowsAsync(id)).Where(r => r.Mask == 2).Select(r => r.PartyId).Should().Equal(ben.Id);
        }
        finally
        {
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
        }
    }

    // ---- final review ----

    [Fact]
    public async Task Create_with_the_id_of_another_activity_is_refused_and_keeps_its_rows()
    {
        // Final review C1: every activity table has its own primary key, so the pointer row is the only place two activities
        // with one id meet. A Basic user creating a task with an email's id gets Dataverse's duplicate-record 412, and the
        // email's pointer and party rows stay as they were.
        var ada = await ContactAsync("Ada");
        var me = await CallerIdAsync();
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Victim " + _run, ["to"] = Parties(ada) });
        var pointerBefore = await PointerRowAsync(email);
        var partiesBefore = await PartyRowsAsync(email);

        var oid = Guid.NewGuid();
        var attacker = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, attacker, team: false, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic), ("prvShareActivity", DepthMask.Basic));
        var service = ServiceFor(fx, oid);

        var refused = await FaultAsync(() => service.CreateAsync(new Entity("task", email) { ["subject"] = "Takeover " + _run }));
        refused.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        refused.HttpStatus.Should().Be(412);
        refused.Message.Should().Contain($"activityid = {email}");
        var byAttribute = await FaultAsync(() => service.CreateAsync(new Entity("phonecall") { ["activityid"] = email, ["subject"] = "Takeover " + _run }));
        byAttribute.HttpStatus.Should().Be(412);

        (await CountAsync("task", "activityid", email)).Should().Be(0);
        (await CountAsync("phonecall", "activityid", email)).Should().Be(0);
        var pointerAfter = await PointerRowAsync(email);
        pointerAfter.Should().BeEquivalentTo(pointerBefore);
        pointerAfter!["activitytypecode"].Should().Be(4202);
        pointerAfter["ownerid"].Should().Be(me);
        (await PartyRowsAsync(email)).Should().BeEquivalentTo(partiesBefore);
        (await Admin.RetrieveAsync("email", email, new ColumnSet("subject"))).GetAttributeValue<string>("subject").Should().Be("Victim " + _run);
    }

    [Fact]
    public async Task Assign_to_a_user_of_a_child_business_unit_moves_pointer_and_parties()
    {
        // Final review M9: the pointer and every party row take the new owner's business unit.
        var ada = await ContactAsync("Ada");
        var child = await CreateBusinessUnitAsync(fx, await RootBusinessUnitAsync(fx), "Assign child " + _run);
        var user = await CreateUserAsync(fx, child);
        await GrantAsync(fx, user, team: false, ("prvReadActivity", DepthMask.Basic));
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Assign BU " + _run, ["to"] = Parties(ada) });
        (await PointerRowAsync(id))!["owningbusinessunit"].Should().NotBe(child);

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference("email", id), Assignee = new EntityReference("systemuser", user) });

        (await ScalarAsync("SELECT owningbusinessunit FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(child);
        var pointer = await PointerRowAsync(id);
        pointer!["ownerid"].Should().Be(user);
        pointer["owningbusinessunit"].Should().Be(child);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND (owningbusinessunit IS NULL OR owningbusinessunit <> @bu)",
            ("@id", id), ("@bu", child))).Should().Be(0);
        (await PartyRowsAsync(id)).Should().HaveCount(2).And.OnlyContain(r => r.Owner == user);
    }
}
