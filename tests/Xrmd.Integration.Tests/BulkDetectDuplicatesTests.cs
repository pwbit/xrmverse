using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Duplicates;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 4: <c>BulkDetectDuplicates</c> (ruling D15) on a published rule contact → contact on <c>emailaddress1</c>: the
/// system job (operationtype 8) runs the query as its owner and writes one <c>duplicaterecord</c> per row and rule with a match.
/// </summary>
[Collection("xrmd")]
public sealed class BulkDetectDuplicatesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string Email = DuplicateDetectionTestKit.Email;
    private const string Record = DuplicateDetectionSchema.Record;

    private readonly DuplicateDetectionTestKit _kit = new(fx);

    private OrganizationService Admin => _kit.Admin;

    public Task InitializeAsync() => _kit.InitializeAsync(onlineCreateUpdate: false, Contact);

    public async Task DisposeAsync()
    {
        BulkDetectDuplicatesJob.RecordLimit = DuplicateDetectionSchema.MaxBulkRecords;
        BulkDetectDuplicatesJob.PageSize = DuplicateDetectionSchema.MaxPageSize;
        await _kit.ExecAsync("DELETE FROM data.duplicaterecord; DELETE FROM data.asyncoperation WHERE operationtype = 8");
        await _kit.DisposeAsync();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private string Mail(string label) => _kit.Mail(label);

    private Task<Guid> ContactAsync(string label, string? email, Guid? owner = null)
    {
        var contact = new Entity(Contact) { ["lastname"] = label + " " + _kit.Run, [Email] = email };
        if (owner is { } user) contact["ownerid"] = new EntityReference("systemuser", user);
        return Admin.CreateAsync(contact);
    }

    private async Task<Guid> PublishedRuleAsync()
    {
        var rule = await _kit.RuleAsync();
        await _kit.PublishAndRunAsync(rule);
        return rule;
    }

    /// <summary>The contacts of this run (their last name ends with the run id), with <paramref name="email"/> when given.</summary>
    private QueryExpression RunContacts(string? email = null)
    {
        var query = new QueryExpression(Contact) { ColumnSet = new ColumnSet("lastname") };
        query.Criteria.AddCondition("lastname", ConditionOperator.EndsWith, " " + _kit.Run);
        if (email is not null) query.Criteria.AddCondition(Email, ConditionOperator.Equal, email);
        return query;
    }

    private static BulkDetectDuplicatesRequest Request(QueryBase query) => new()
    {
        Query = query, JobName = "Bulk detection test", SendEmailNotification = false, ToRecipients = [], CCRecipients = [],
        TemplateId = Guid.Empty,
    };

    private static async Task<Guid> BulkAsync(OrganizationService service, BulkDetectDuplicatesRequest request) =>
        (Guid)(await service.ExecuteAsync(request)).Results["JobId"];

    /// <summary>Queues the job as <paramref name="service"/> (the administrator when null) and runs it to its end.</summary>
    private async Task<Guid> RunBulkAsync(QueryBase query, OrganizationService? service = null)
    {
        var job = await BulkAsync(service ?? Admin, Request(query));
        await _kit.RunJobAsync(job);
        return job;
    }

    private sealed record DuplicateRow(Guid Id, Guid BaseRecord, string? BaseType, Guid? Rule, Guid? Job, Guid? Owner, Guid? DuplicateRecord);

    private async Task<List<DuplicateRow>> RecordsAsync(Guid job)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn,
            "SELECT duplicateid, baserecordid, baserecordidtype, duplicateruleid, asyncoperationid, ownerid, duplicaterecordid " +
            "FROM data.duplicaterecord WHERE asyncoperationid = @job", ("@job", job));
        var rows = new List<DuplicateRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(new DuplicateRow(rd.GetGuid(0), rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetValue(2).ToString(),
                rd.IsDBNull(3) ? null : rd.GetGuid(3), rd.IsDBNull(4) ? null : rd.GetGuid(4), rd.IsDBNull(5) ? null : rd.GetGuid(5),
                rd.IsDBNull(6) ? null : rd.GetGuid(6)));
        return rows;
    }

    // ---- the job ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Job_records_each_record_with_duplicates()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        await ContactAsync("Other", Mail("other"));
        var rule = await PublishedRuleAsync();

        var job = await RunBulkAsync(RunContacts());

        (await _kit.JobAsync(job)).Should().Be((3, 30, (string?)null), "a complete run has no message");
        var records = await RecordsAsync(job);
        var admin = (Guid)(await _kit.ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!;
        records.Select(r => r.BaseRecord).Should().BeEquivalentTo([anna, twin]);
        records.Should().AllSatisfy(r =>
        {
            r.Rule.Should().Be(rule);
            r.Job.Should().Be(job);
            r.Owner.Should().Be(admin, "the records belong to the job's owner");
            r.DuplicateRecord.Should().BeNull("Learn's sample leaves duplicaterecordid empty");
        });
        (await _kit.ScalarAsync("SELECT operationtype FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be(8);
        (await _kit.ScalarAsync("SELECT name FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be("Bulk detection test");
        (await _kit.ScalarAsync("SELECT primaryentitytype FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be(Contact);

        // The Web API lists them by their job (AsyncOperation_DuplicateBaseRecord). This server serves no collection-valued
        // navigation (neither asyncoperations({id})/AsyncOperation_DuplicateBaseRecord nor its $expand): the lookup filter instead.
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/data/v9.2/duplicaterecords?$select=duplicateid&$filter=_asyncoperationid_value eq {job}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        using var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var listed = JsonNode.Parse(body)!["value"]!.AsArray().Select(v => Guid.Parse((string)v!["duplicateid"]!)).ToList();
        listed.Should().BeEquivalentTo(records.Select(r => r.Id));
    }

    [Fact]
    public async Task Query_filter_limits_the_records()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        await ContactAsync("Bob", Mail("bob"));
        await ContactAsync("Bobby", Mail("bob"));
        await PublishedRuleAsync();

        var job = await RunBulkAsync(RunContacts(Mail("anna")));

        (await RecordsAsync(job)).Select(r => r.BaseRecord).Should().BeEquivalentTo([anna, twin]);
    }

    [Fact]
    public async Task Job_runs_as_owner()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadContact", DepthMask.Basic));
        await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Twin", Mail("anna"));
        var own = await ContactAsync("Own", Mail("anna"), owner: user);
        await PublishedRuleAsync();

        var job = await RunBulkAsync(RunContacts(), ServiceFor(fx, objectId));

        var record = (await RecordsAsync(job)).Should().ContainSingle("the user reads only its own contact").Subject;
        record.BaseRecord.Should().Be(own, "its duplicates count even though the user cannot read them");
        record.Owner.Should().Be(user);
    }

    [Fact]
    public async Task Stops_at_5000_records()
    {
        for (var i = 0; i < 4; i++) await ContactAsync("Copy " + i, Mail("anna"));
        await PublishedRuleAsync();
        BulkDetectDuplicatesJob.RecordLimit = 2;

        var job = await RunBulkAsync(RunContacts());

        (await _kit.JobAsync(job)).Should().Be((3, 30, BulkDetectDuplicatesJob.LimitReachedMessage(2)), "the cap is said, not silent");
        (await RecordsAsync(job)).Should().HaveCount(2);
    }

    [Fact]
    public async Task One_to_many_link_examines_each_row_once()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        for (var i = 0; i < 2; i++)
            await Admin.CreateAsync(new Entity("account")
            {
                ["name"] = $"Account {i} {_kit.Run}", ["primarycontactid"] = new EntityReference(Contact, anna),
            });
        var rule = await PublishedRuleAsync();
        var query = RunContacts();
        query.LinkEntities.Add(new LinkEntity(Contact, "account", "contactid", "primarycontactid", JoinOperator.LeftOuter));

        var job = await RunBulkAsync(query);

        var records = await RecordsAsync(job);
        records.Select(r => r.BaseRecord).Should().BeEquivalentTo([anna, twin], "Anna comes back twice, once per account, and is recorded once");
        records.Should().AllSatisfy(r => r.Rule.Should().Be(rule));
    }

    [Fact]
    public async Task Job_reads_every_page()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await ContactAsync("Copy " + i, Mail("anna")));
        await PublishedRuleAsync();
        BulkDetectDuplicatesJob.PageSize = 2;

        var job = await RunBulkAsync(RunContacts());

        (await RecordsAsync(job)).Select(r => r.BaseRecord).Should().BeEquivalentTo(ids, "each row is examined once, on whatever page");
    }

    // ---- refusals -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Email_notification_and_recurrence_are_refused()
    {
        await PublishedRuleAsync();

        var email = Request(RunContacts());
        email.SendEmailNotification = true;
        (await BadRequestAsync(() => BulkAsync(Admin, email))).Should().Contain("SendEmailNotification");

        var recurring = Request(RunContacts());
        recurring.RecurrencePattern = "FREQ=DAILY;INTERVAL=1";
        (await BadRequestAsync(() => BulkAsync(Admin, recurring))).Should().Contain("RecurrencePattern");

        (await _kit.ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 8")).Should().Be(0);
    }

    [Fact]
    public async Task Table_without_published_rule_is_refused()
    {
        await _kit.RuleAsync();
        await BadRequestAsync(() => BulkAsync(Admin, Request(RunContacts())));

        // account does not have IsDuplicateDetectionEnabled.
        await BadRequestAsync(() => BulkAsync(Admin, Request(new QueryExpression("account") { ColumnSet = new ColumnSet("name") })));
    }

    [Fact]
    public async Task Base_table_read_is_required()
    {
        await PublishedRuleAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global));

        var fault = await FaultAsync(() => BulkAsync(ServiceFor(fx, objectId), Request(RunContacts())));
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvReadContact");
    }

    [Fact]
    public async Task Client_cannot_write_duplicaterecord()
    {
        await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Twin", Mail("anna"));
        var rule = await PublishedRuleAsync();
        var job = await RunBulkAsync(RunContacts());
        var existing = (await RecordsAsync(job))[0].Id;
        var admin = (Guid)(await _kit.ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!;

        await BadRequestAsync(() => Admin.CreateAsync(new Entity(Record)
        {
            [DuplicateDetectionSchema.DuplicateRuleId] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
        }));
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity(Record, existing)
        {
            [DuplicateDetectionSchema.DuplicateRuleId] = null,
        }));
        await BadRequestAsync(() => Admin.DeleteAsync(Record, existing));
        await BadRequestAsync(() => Admin.ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference(Record, existing), Assignee = new EntityReference("systemuser", admin),
        }));
    }

    [Fact]
    public async Task Deleting_the_job_deletes_its_records()
    {
        await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Twin", Mail("anna"));
        await PublishedRuleAsync();
        var job = await RunBulkAsync(RunContacts());
        (await RecordsAsync(job)).Should().HaveCount(2);

        await Admin.DeleteAsync("asyncoperation", job);

        (await RecordsAsync(job)).Should().BeEmpty();
    }
}
