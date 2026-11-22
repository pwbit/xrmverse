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
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 4: the read path of the activity model. <c>activitypointer</c> is queried like any table and follows the
/// activity's security, shares matched by the activity id and the table code in <c>activitytypecode</c> (A13, RF1, review C1); party lists come back on Retrieve and on
/// every RetrieveMultiple page (A8, D5) and cannot be filtered or sorted on; <c>activityparty</c> rows are readable when their
/// activity is (A10, pre-flight C13).
/// </summary>
[Collection("xrmd")]
public sealed class ActivityReadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Enabled = "ishierarchicalsecuritymodelenabled";
    private const string Depth = "maxdepthforhierarchicalsecuritymodel";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await ExecAsync($"UPDATE data.organization SET {Enabled} = 0, {Depth} = 3; DELETE FROM meta.hierarchy_security_exclusion;");
        Cache(fx).Invalidate();
    }

    // ---- helpers ----

    private string Tag(string label) => $"{label} {_run}";

    private async Task<EntityReference> ContactAsync(string first)
    {
        var id = await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = first, ["lastname"] = "Example", ["emailaddress1"] = $"{first.ToLowerInvariant()}.{_run}@example.com",
        });
        return new EntityReference("contact", id);
    }

    private async Task<EntityReference> AccountAsync(string name = "Contoso Example") =>
        new("account", await Admin.CreateAsync(new Entity("account") { ["name"] = Tag(name), ["emailaddress1"] = $"info.{_run}@example.com" }));

    private static EntityCollection Parties(params EntityReference[] parties) =>
        new([.. parties.Select(p => new Entity("activityparty") { ["partyid"] = p })]);

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

    private Task ExecAsync(string sql, params (string Name, object Value)[] parameters) => ScalarAsync(sql, parameters);

    /// <summary>A user in <paramref name="businessUnit"/> holding <paramref name="privileges"/>.</summary>
    private async Task<(Guid Id, OrganizationService Service)> UserAsync(Guid businessUnit, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, businessUnit, oid);
        if (privileges.Length > 0) await GrantAsync(fx, id, team: false, privileges);
        return (id, ServiceFor(fx, oid));
    }

    private Task<Guid> EmailAsync(string subject, Guid? owner = null, EntityReference? regarding = null)
    {
        var email = new Entity("email") { ["subject"] = Tag(subject) };
        if (owner is { } o) email["ownerid"] = new EntityReference("systemuser", o);
        if (regarding is not null) email["regardingobjectid"] = regarding;
        return Admin.CreateAsync(email);
    }

    private QueryExpression Pointers(params string[] columns)
    {
        var q = new QueryExpression("activitypointer") { ColumnSet = new ColumnSet(columns) };
        q.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        return q;
    }

    private static async Task<List<string>> SubjectsAsync(OrganizationService service, QueryBase query) =>
        [.. (await service.RetrieveMultipleAsync(query)).Entities.Select(e => e.GetAttributeValue<string>("subject")).Order()];

    // ---- activitypointer ----

    [Fact]
    public async Task Pointer_query_by_regarding_returns_every_activity_type()
    {
        var sms = $"new_sms{_run}";
        var entity = TestHelpers.Entity(sms, sms, "SMS " + _run);
        entity.IsActivity = true;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "subject", SchemaName = "Subject", MaxLength = 200, DisplayName = Label("Subject"),
            },
        });
        var account = await AccountAsync();
        var other = await AccountAsync("Other Example");
        var email = await EmailAsync("Mail", regarding: account);
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Call back"), ["regardingobjectid"] = account });
        var text = await Admin.CreateAsync(new Entity(sms) { ["subject"] = Tag("Text"), ["regardingobjectid"] = account });
        await EmailAsync("Elsewhere", regarding: other);
        try
        {
            var q = new QueryExpression("activitypointer") { ColumnSet = new ColumnSet("subject", "activitytypecode", "regardingobjectid") };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, account.Id);
            var rows = (await Admin.RetrieveMultipleAsync(q)).Entities;
            rows.Select(r => r.Id).Should().BeEquivalentTo([email, task, text]);
            rows.Select(r => r.GetAttributeValue<string>("activitytypecode")).Should().BeEquivalentTo(["email", "task", sms]);
            rows.Should().OnlyContain(r => r.GetAttributeValue<EntityReference>("regardingobjectid").Id == account.Id
                                           && r.GetAttributeValue<EntityReference>("regardingobjectid").LogicalName == "account");

            var fetch = new FetchExpression($"""
                <fetch><entity name="activitypointer"><attribute name="activitytypecode"/><attribute name="subject"/>
                  <filter><condition attribute="regardingobjectid" operator="eq" value="{account.Id}"/>
                    <condition attribute="activitytypecode" operator="eq" value="task"/></filter></entity></fetch>
                """);
            var tasks = (await Admin.RetrieveMultipleAsync(fetch)).Entities;
            tasks.Should().ContainSingle().Which.Id.Should().Be(task);
            tasks[0].FormattedValues["activitytypecode"].Should().Be("Task");
        }
        finally
        {
            await Admin.DeleteAsync(sms, text);
            await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = sms });
        }
    }

    [Fact]
    public async Task Pointer_formatted_status_uses_the_activity_option_set()
    {
        // A11 (checklist 3f): the pointer keeps the activity's statuscode; its label comes from the activity's own option set.
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Status"), ["statuscode"] = new OptionSetValue(3) });
        var email = await EmailAsync("Draft");

        var byStatus = await Admin.RetrieveAsync("activitypointer", task, new ColumnSet("statuscode"));
        byStatus.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3);
        byStatus.FormattedValues["statuscode"].Should().Be("In Progress");
        byStatus.Contains("activitytypecode").Should().BeFalse("the type read for the label is not returned when not asked for");

        var all = await Admin.RetrieveAsync("activitypointer", email, new ColumnSet(true));
        all.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1);
        all.FormattedValues["statuscode"].Should().Be("Draft");
        all.FormattedValues["statecode"].Should().Be("Open");

        var many = await Admin.RetrieveMultipleAsync(Pointers("statuscode"));
        many.Entities.Single(e => e.Id == task).FormattedValues["statuscode"].Should().Be("In Progress");
        many.Entities.Single(e => e.Id == email).FormattedValues["statuscode"].Should().Be("Draft");
    }

    // ---- party lists ----

    [Fact]
    public async Task Retrieve_returns_party_lists()
    {
        var me = await CallerIdAsync();
        var ada = await ContactAsync("Ada");
        var bob = await ContactAsync("Bob");
        var account = await AccountAsync();
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Tag("Parties"),
            ["from"] = Parties(new EntityReference("systemuser", me)),
            ["to"] = Parties(ada, bob),
            ["cc"] = Parties(account),
        });

        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("subject", "to", "cc", "bcc"));
        var to = email["to"].Should().BeOfType<EntityCollection>().Subject;
        to.EntityName.Should().Be("activityparty");
        to.Entities.Select(p => p.GetAttributeValue<EntityReference>("partyid").Id).Should().Equal(ada.Id, bob.Id);
        var first = to.Entities[0];
        first.LogicalName.Should().Be("activityparty");
        first.GetAttributeValue<EntityReference>("partyid").Name.Should().Be("Example, Ada"); // the contact's fullname
        first.GetAttributeValue<OptionSetValue>("participationtypemask").Value.Should().Be(2);
        first.GetAttributeValue<string>("addressused").Should().Be($"ada.{_run}@example.com");
        first.GetAttributeValue<EntityReference>("activityid").Id.Should().Be(id);
        first.GetAttributeValue<bool>("ispartydeleted").Should().BeFalse();
        first.Id.Should().NotBeEmpty();
        email.GetAttributeValue<EntityCollection>("cc").Entities.Should().ContainSingle()
            .Which.GetAttributeValue<EntityReference>("partyid").LogicalName.Should().Be("account");
        // An empty party list is omitted, as a null value is (from memory: checklist 3j).
        email.Contains("bcc").Should().BeFalse();
        email.Contains("from").Should().BeFalse("not requested");

        var all = await Admin.RetrieveAsync("email", id, new ColumnSet(true));
        all.Attributes.Keys.Should().Contain(["from", "to", "cc"]).And.NotContain(["bcc", "related"]);
        all.GetAttributeValue<EntityCollection>("from").Entities.Single().GetAttributeValue<EntityReference>("partyid").Id.Should().Be(me);
    }

    [Fact]
    public async Task RetrieveMultiple_returns_party_lists_per_row()
    {
        var contacts = new[] { await ContactAsync("Ada"), await ContactAsync("Bob"), await ContactAsync("Cy") };
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag($"Row {i}"), ["to"] = Parties(contacts[i]) }));
        ids.Add(await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Row 3") }));

        var q = new QueryExpression("email") { ColumnSet = new ColumnSet("subject", "to"), PageInfo = new PagingInfo { Count = 2, PageNumber = 1 } };
        q.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        q.AddOrder("subject", OrderType.Ascending);
        var page1 = await Admin.RetrieveMultipleAsync(q);
        page1.MoreRecords.Should().BeTrue();
        q.PageInfo = new PagingInfo { Count = 2, PageNumber = 2, PagingCookie = page1.PagingCookie };
        var page2 = await Admin.RetrieveMultipleAsync(q);
        var rows = page1.Entities.Concat(page2.Entities).ToList();
        rows.Select(r => r.Id).Should().Equal(ids);
        for (var i = 0; i < 3; i++)
            rows[i].GetAttributeValue<EntityCollection>("to").Entities.Single().GetAttributeValue<EntityReference>("partyid").Id
                .Should().Be(contacts[i].Id);
        rows[3].Contains("to").Should().BeFalse();

        var fetch = new FetchExpression($"""
            <fetch><entity name="email"><all-attributes/>
              <filter><condition attribute="subject" operator="like" value="%{_run}"/></filter></entity></fetch>
            """);
        var all = (await Admin.RetrieveMultipleAsync(fetch)).Entities;
        all.Single(e => e.Id == ids[1]).GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle();
    }

    [Fact]
    public async Task Filter_on_party_list_is_refused()
    {
        var ada = await ContactAsync("Ada");
        var filtered = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        filtered.Criteria.AddCondition("to", ConditionOperator.Equal, ada.Id);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(filtered))).Should().Contain("'to'").And.Contain("activityparty");

        var ordered = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        ordered.AddOrder("to", OrderType.Ascending);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(ordered))).Should().Contain("'to'").And.Contain("activityparty");

        var fetch = new FetchExpression("""
            <fetch><entity name="email"><attribute name="subject"/>
              <filter><condition attribute="cc" operator="not-null"/></filter></entity></fetch>
            """);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(fetch))).Should().Contain("'cc'");

        var linked = new FetchExpression("""
            <fetch><entity name="account"><attribute name="name"/>
              <link-entity name="email" from="regardingobjectid" to="accountid"><attribute name="to"/></link-entity></entity></fetch>
            """);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(linked))).Should().Contain("'to'");

        var aggregate = new FetchExpression("""
            <fetch aggregate="true"><entity name="email"><attribute name="to" alias="n" aggregate="count"/></entity></fetch>
            """);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(aggregate))).Should().Contain("'to'");
    }

    [Fact]
    public async Task Link_to_activityparty_filters_by_party()
    {
        var ada = await ContactAsync("Ada");
        var bob = await ContactAsync("Bob");
        var toAda = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("To Ada"), ["to"] = Parties(ada) });
        await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("To Bob"), ["to"] = Parties(bob), ["cc"] = Parties(ada) });

        var q = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        q.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        var link = q.AddLink("activityparty", "activityid", "activityid");
        link.LinkCriteria.AddCondition("partyid", ConditionOperator.Equal, ada.Id);
        link.LinkCriteria.AddCondition("participationtypemask", ConditionOperator.Equal, 2);
        (await Admin.RetrieveMultipleAsync(q)).Entities.Should().ContainSingle().Which.Id.Should().Be(toAda);

        var fetch = new FetchExpression($"""
            <fetch><entity name="activityparty"><attribute name="participationtypemask"/><attribute name="partyid"/>
              <filter><condition attribute="partyid" operator="eq" value="{ada.Id}"/></filter>
              <link-entity name="activitypointer" from="activityid" to="activityid" alias="a"><attribute name="subject"/>
                <filter><condition attribute="subject" operator="like" value="%{_run}"/></filter></link-entity></entity></fetch>
            """);
        var rows = (await Admin.RetrieveMultipleAsync(fetch)).Entities;
        rows.Select(r => r.GetAttributeValue<OptionSetValue>("participationtypemask").Value).Should().BeEquivalentTo([2, 3]);
        rows.Should().OnlyContain(r => r.GetAttributeValue<EntityReference>("partyid").Name == "Example, Ada");
    }

    // ---- security ----

    [Fact]
    public async Task Shared_email_is_visible_through_activitypointer_only_it()
    {
        // RF1: a share recorded on the email (its own object type code) opens its pointer row, and no other.
        var root = await RootBusinessUnitAsync(fx);
        var (userId, user) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic));
        var shared = await EmailAsync("Shared");
        var hidden = await EmailAsync("Hidden");
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Task") });
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("email", shared),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        Cache(fx).Invalidate();

        (await user.RetrieveMultipleAsync(Pointers("subject"))).Entities.Should().ContainSingle().Which.Id.Should().Be(shared);
        var email = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        email.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        (await user.RetrieveMultipleAsync(email)).Entities.Should().ContainSingle().Which.Id.Should().Be(shared);
        (await user.RetrieveAsync("activitypointer", shared, new ColumnSet("subject"))).Id.Should().Be(shared);
        (await FaultAsync(() => user.RetrieveAsync("activitypointer", hidden, new ColumnSet("subject")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await FaultAsync(() => user.RetrieveAsync("activitypointer", task, new ColumnSet("subject")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await Admin.RetrieveMultipleAsync(Pointers("subject"))).Entities.Should().HaveCount(3);

        // Without the activity privilege nothing is read through the pointer.
        var (_, none) = await UserAsync(root, ("prvReadAccount", DepthMask.Basic));
        (await FaultAsync(() => none.RetrieveMultipleAsync(Pointers("subject")))).ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
    }

    [Fact]
    public async Task Party_rows_follow_activity_read_access()
    {
        var root = await RootBusinessUnitAsync(fx);
        var (userId, user) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic), ("prvCreateActivity", DepthMask.Basic));
        var ada = await ContactAsync("Ada");
        var other = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Not mine"), ["to"] = Parties(ada) });
        var mine = await user.CreateAsync(new Entity("email") { ["subject"] = Tag("Mine") });

        QueryExpression PartiesOf(params Guid[] activities)
        {
            var q = new QueryExpression("activityparty") { ColumnSet = new ColumnSet("participationtypemask", "partyid", "activityid") };
            q.Criteria.AddCondition("activityid", ConditionOperator.In, activities.Cast<object>().ToArray());
            return q;
        }

        // No prvReadActivityParty exists: the rows of a readable activity are readable, the others are not, and nothing is refused.
        var visible = (await user.RetrieveMultipleAsync(PartiesOf(other, mine))).Entities;
        visible.Should().ContainSingle().Which.GetAttributeValue<OptionSetValue>("participationtypemask").Value.Should().Be(9);
        visible[0].GetAttributeValue<EntityReference>("activityid").Id.Should().Be(mine);
        (await FaultAsync(() => user.RetrieveAsync("email", other, new ColumnSet("to")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await Admin.RetrieveMultipleAsync(PartiesOf(other, mine))).Entities.Should().HaveCount(3);

        // A link from a readable activity does not open the party rows of another one.
        var linked = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        linked.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        linked.AddLink("activityparty", "activityid", "activityid").LinkCriteria.AddCondition("partyid", ConditionOperator.Equal, ada.Id);
        (await user.RetrieveMultipleAsync(linked)).Entities.Should().BeEmpty();

        // Shared, the email's party rows become readable with it.
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("email", other),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        Cache(fx).Invalidate();
        (await user.RetrieveMultipleAsync(PartiesOf(other))).Entities.Select(e => e.GetAttributeValue<OptionSetValue>("participationtypemask").Value)
            .Should().BeEquivalentTo([2, 9]);
        (await user.RetrieveAsync("email", other, new ColumnSet("to"))).GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle();

        // Without any activity privilege the table reads empty, not a request for a privilege that does not exist.
        var (_, none) = await UserAsync(root, ("prvReadAccount", DepthMask.Basic));
        (await none.RetrieveMultipleAsync(PartiesOf(other, mine))).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Hierarchy_and_owner_depth_apply_to_pointer()
    {
        var root = await RootBusinessUnitAsync(fx);
        var unit = await CreateBusinessUnitAsync(fx, root, "Activities " + _run);
        var (localId, local) = await UserAsync(unit, ("prvReadActivity", DepthMask.Local));
        var (peerId, _) = await UserAsync(unit, ("prvReadActivity", DepthMask.Basic));
        var (outsiderId, _) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic));
        await EmailAsync("Local own", localId);
        await EmailAsync("Peer", peerId);
        await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Outsider"), ["ownerid"] = new EntityReference("systemuser", outsiderId) });
        await EmailAsync("Outsider mail", outsiderId);
        var sharedToReport = await EmailAsync("Shared to report");
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("email", sharedToReport),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", outsiderId), AccessMask = AccessRights.ReadAccess },
        });

        (await SubjectsAsync(local, Pointers("subject"))).Should().Equal(Tag("Local own"), Tag("Peer"));

        // Manager hierarchy: a manager with Basic depth reads its report's activities through the pointer too.
        var (managerId, manager) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic));
        (await SubjectsAsync(manager, Pointers("subject"))).Should().BeEmpty();
        await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = @m WHERE systemuserid = @u", ("@m", managerId), ("@u", outsiderId));
        await ExecAsync($"UPDATE data.organization SET {Enabled} = 1, {Depth} = 3");
        Cache(fx).Invalidate();
        (await SubjectsAsync(manager, Pointers("subject"))).Should().Equal(Tag("Outsider"), Tag("Outsider mail"), Tag("Shared to report"));
        var task = new QueryExpression("task") { ColumnSet = new ColumnSet("subject") };
        task.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        (await SubjectsAsync(manager, task)).Should().Equal(Tag("Outsider"));

        // Review I1: a table excluded from the hierarchy model gives no reach through the pointer either, by ownership or by share.
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Cache(fx).SetHierarchyExclusionsAsync(s, ["email"], default));
        Cache(fx).Invalidate();
        (await SubjectsAsync(manager, Pointers("subject"))).Should().Equal(Tag("Outsider"));
        var email = new QueryExpression("email") { ColumnSet = new ColumnSet("subject") };
        email.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        (await SubjectsAsync(manager, email)).Should().BeEmpty();
    }
    [Fact]
    public async Task Share_of_another_table_with_the_activity_id_does_not_open_the_activity()
    {
        // Review C1: the pointer row has the activity's id; a share of a record of another table that reuses that id must not open
        // the pointer row nor the party rows (the share is matched on the activity's table code too, never the id alone).
        var root = await RootBusinessUnitAsync(fx);
        var (userId, user) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic));
        var ada = await ContactAsync("Ada");
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Private"), ["to"] = Parties(ada) });
        await Admin.CreateAsync(new Entity("account", email) { ["name"] = Tag("Same id") });
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("account", email),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        Cache(fx).Invalidate();

        (await user.RetrieveAsync("account", email, new ColumnSet("name"))).Id.Should().Be(email, "the account share works");
        (await user.RetrieveMultipleAsync(Pointers("subject"))).Entities.Should().BeEmpty();
        (await FaultAsync(() => user.RetrieveAsync("activitypointer", email, new ColumnSet("subject")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        var parties = new QueryExpression("activityparty") { ColumnSet = new ColumnSet("partyid") };
        parties.Criteria.AddCondition("activityid", ConditionOperator.Equal, email);
        (await user.RetrieveMultipleAsync(parties)).Entities.Should().BeEmpty();
        (await Admin.RetrieveMultipleAsync(parties)).Entities.Should().HaveCount(2);
    }

    [Fact]
    public async Task Access_team_grant_is_seen_through_the_pointer()
    {
        // A record shared with an access team the user belongs to (team principal, the email's own table code) opens its pointer row.
        var root = await RootBusinessUnitAsync(fx);
        var (userId, user) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic));
        var team = await CreateTeamAsync(fx, root, teamType: 1, userId);
        var shared = await EmailAsync("Team shared");
        await EmailAsync("Not shared");
        // The grant an access team receives (AddUserToRecordTeam): a share row with the team as principal and the email's code.
        await ExecAsync("""
            INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask, inheritedaccessrightsmask)
            VALUES (NEWID(), @team, 9, @email, 4202, 1, 0)
            """, ("@team", team), ("@email", shared));
        Cache(fx).Invalidate();

        (await user.RetrieveMultipleAsync(Pointers("subject"))).Entities.Should().ContainSingle().Which.Id.Should().Be(shared);
        (await user.RetrieveAsync("activitypointer", shared, new ColumnSet("subject"))).Id.Should().Be(shared);
    }

    [Fact]
    public async Task Distinct_query_keeps_party_lists_with_the_id_and_refuses_them_without()
    {
        // Review m2: a distinct query that selects the id (all columns do) reads its party lists; without the id it is refused.
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Distinct"), ["to"] = Parties(ada) });

        var all = new QueryExpression("email") { ColumnSet = new ColumnSet(true), Distinct = true };
        all.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        var row = (await Admin.RetrieveMultipleAsync(all)).Entities.Should().ContainSingle().Subject;
        row.Id.Should().Be(id);
        row.GetAttributeValue<EntityCollection>("to").Entities.Single().GetAttributeValue<EntityReference>("partyid").Id.Should().Be(ada.Id);

        var withId = new QueryExpression("email") { ColumnSet = new ColumnSet("activityid", "subject", "to"), Distinct = true };
        withId.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        (await Admin.RetrieveMultipleAsync(withId)).Entities.Single().GetAttributeValue<EntityCollection>("to").Entities.Should().ContainSingle();

        var noId = new QueryExpression("email") { ColumnSet = new ColumnSet("subject", "to"), Distinct = true };
        noId.Criteria.AddCondition("subject", ConditionOperator.EndsWith, _run);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(noId))).Should().Contain("'to'").And.Contain("activityid");
    }

    [Fact]
    public async Task Common_activity_columns_cannot_be_secured()
    {
        // Review I2, ruling Y3: activitypointer copies the common columns, so they cannot be secured on an activity table nor on the
        // pointer (known limit, checklist). A column of an activity table's own stays securable.
        async Task NotSecurableAsync(string entity, string attribute)
        {
            var fault = await FaultAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = entity, Attribute = new Microsoft.Xrm.Sdk.Metadata.MemoAttributeMetadata { LogicalName = attribute, IsSecured = true },
            }));
            (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.FieldNotSecurable, 400), fault.Message);
            fault.Message.Should().Contain($"{entity}.{attribute}");
        }
        await NotSecurableAsync("email", "description");
        await NotSecurableAsync("task", "description");
        await NotSecurableAsync("activitypointer", "description");

        var column = $"new_note{_run}";
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "email",
            Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = column, SchemaName = column, MaxLength = 100, DisplayName = Label("Note"),
            },
        });
        try
        {
            await Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = "email", Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = column, IsSecured = true },
            });
            var secured = (RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = "email", LogicalName = column });
            secured.AttributeMetadata.IsSecured.Should().BeTrue();
            await Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = "email", Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = column, IsSecured = false },
            });
        }
        finally
        {
            await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "email", LogicalName = column });
        }
    }

    // ---- final review ----

    private async Task<int?> ShareMaskAsync(Guid objectId, int typeCode, Guid principal) =>
        (int?)await ScalarAsync("SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @o AND objecttypecode = @t AND principalid = @p",
            ("@o", objectId), ("@t", typeCode), ("@p", principal));

    [Fact]
    public async Task Sharing_an_own_activity_never_changes_the_share_of_another_tables_record()
    {
        // Final review C2, the escalation probe: a user with only a Read share on an account creates a task with the account's
        // id and shares it with itself for Write. The account's share must not change.
        var root = await RootBusinessUnitAsync(fx);
        var (userId, user) = await UserAsync(root, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic), ("prvDeleteActivity", DepthMask.Basic), ("prvShareActivity", DepthMask.Basic),
            ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic));
        var account = await AccountAsync("Escalation");
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = account,
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", userId), AccessMask = AccessRights.ReadAccess },
        });
        Cache(fx).Invalidate();
        (await FaultAsync(() => user.UpdateAsync(new Entity("account", account.Id) { ["name"] = Tag("Hijacked") }))).ErrorCode
            .Should().Be(ErrorCodes.AccessDenied);

        await user.CreateAsync(new Entity("task", account.Id) { ["subject"] = Tag("Same id") });
        await user.ExecuteAsync(new ModifyAccessRequest
        {
            Target = new EntityReference("task", account.Id),
            PrincipalAccess = new PrincipalAccess
            {
                Principal = new EntityReference("systemuser", userId),
                AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.DeleteAccess | AccessRights.ShareAccess,
            },
        });
        Cache(fx).Invalidate();

        (await FaultAsync(() => user.UpdateAsync(new Entity("account", account.Id) { ["name"] = Tag("Hijacked") }))).ErrorCode
            .Should().Be(ErrorCodes.AccessDenied);
        (await ShareMaskAsync(account.Id, 1, userId)).Should().Be((int)AccessRights.ReadAccess);
        (await ShareMaskAsync(account.Id, 4212, userId)).Should().Be(
            (int)(AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.DeleteAccess | AccessRights.ShareAccess));
        (await Admin.RetrieveAsync("account", account.Id, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be(Tag("Escalation"));
    }

    [Fact]
    public async Task Revoke_and_delete_of_an_own_record_keep_the_share_of_another_tables_record()
    {
        // Final review C2: RevokeAccess on, and the Delete of, a user's own task that reuses an account's id remove only the
        // task's shares; another user's share of the account stays.
        var root = await RootBusinessUnitAsync(fx);
        var (ownerId, owner) = await UserAsync(root, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic), ("prvDeleteActivity", DepthMask.Basic), ("prvShareActivity", DepthMask.Basic));
        var (readerId, reader) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic));
        var account = await AccountAsync("Shared account");
        var readShare = new PrincipalAccess { Principal = new EntityReference("systemuser", readerId), AccessMask = AccessRights.ReadAccess };
        await Admin.ExecuteAsync(new GrantAccessRequest { Target = account, PrincipalAccess = readShare });
        var task = new EntityReference("task", await owner.CreateAsync(new Entity("task", account.Id) { ["subject"] = Tag("Same id") }));
        await owner.ExecuteAsync(new GrantAccessRequest { Target = task, PrincipalAccess = readShare });
        Cache(fx).Invalidate();
        (await ShareMaskAsync(account.Id, 1, readerId)).Should().Be((int)AccessRights.ReadAccess);
        (await ShareMaskAsync(account.Id, 4212, readerId)).Should().Be((int)AccessRights.ReadAccess);

        await owner.ExecuteAsync(new RevokeAccessRequest { Target = task, Revokee = new EntityReference("systemuser", readerId) });
        (await ShareMaskAsync(account.Id, 4212, readerId)).Should().BeNull();
        (await ShareMaskAsync(account.Id, 1, readerId)).Should().Be((int)AccessRights.ReadAccess);
        Cache(fx).Invalidate();
        (await reader.RetrieveAsync("account", account.Id, new ColumnSet("name"))).Id.Should().Be(account.Id);

        await owner.ExecuteAsync(new GrantAccessRequest { Target = task, PrincipalAccess = readShare });
        await owner.DeleteAsync("task", account.Id);
        (await ShareMaskAsync(account.Id, 4212, readerId)).Should().BeNull();
        (await ShareMaskAsync(account.Id, 1, readerId)).Should().Be((int)AccessRights.ReadAccess);
        Cache(fx).Invalidate();
        (await reader.RetrieveAsync("account", account.Id, new ColumnSet("name"))).Id.Should().Be(account.Id);
    }

    [Fact]
    public async Task Hierarchy_share_of_another_table_with_the_activity_id_does_not_open_the_activity()
    {
        // Final review M2: the hierarchy branch of the pointer's share filter. A manager reads its report's shares through the
        // hierarchy; a share of an account that reuses an email's id opens the account, never the email's pointer or parties.
        var root = await RootBusinessUnitAsync(fx);
        var (reportId, _) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic));
        var (managerId, manager) = await UserAsync(root, ("prvReadActivity", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic));
        var ada = await ContactAsync("Ada");
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Private"), ["to"] = Parties(ada) });
        await Admin.CreateAsync(new Entity("account", email) { ["name"] = Tag("Same id") });
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("account", email),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", reportId), AccessMask = AccessRights.ReadAccess },
        });
        await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = @m WHERE systemuserid = @u", ("@m", managerId), ("@u", reportId));
        await ExecAsync($"UPDATE data.organization SET {Enabled} = 1, {Depth} = 3");
        Cache(fx).Invalidate();

        (await manager.RetrieveAsync("account", email, new ColumnSet("name"))).Id.Should().Be(email, "the report's account share reaches the manager");
        (await manager.RetrieveMultipleAsync(Pointers("subject"))).Entities.Should().BeEmpty();
        (await FaultAsync(() => manager.RetrieveAsync("activitypointer", email, new ColumnSet("subject")))).ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        var parties = new QueryExpression("activityparty") { ColumnSet = new ColumnSet("partyid") };
        parties.Criteria.AddCondition("activityid", ConditionOperator.Equal, email);
        (await manager.RetrieveMultipleAsync(parties)).Entities.Should().BeEmpty();
        (await Admin.RetrieveMultipleAsync(parties)).Entities.Should().HaveCount(2);
    }
}
