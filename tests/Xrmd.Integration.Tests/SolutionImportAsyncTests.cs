using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// ImportSolutionAsync, ExecuteAsync(ImportSolution) and RetrieveFormattedImportJobResults: the import is queued as an
/// <c>asyncoperation</c> (operationtype 203, the file in <c>data</c> as base64) and run by the <see cref="AsyncOperationWorker"/>
/// as the user who queued it. The fixture host's worker is disabled, so the tests run jobs with
/// <see cref="AsyncOperationWorker.RunOnceAsync"/>.
/// </summary>
[Collection("solution-import")]
public sealed class SolutionImportAsyncTests(XrmdFixture fx) : IAsyncLifetime
{
    private const int ImportSolutionOperation = 203;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
    }

    public async Task DisposeAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(), Options.Create(new PipelineOptions()));

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private sealed record Job(int StateCode, int StatusCode, int? OperationType, string? MessageName, string? Data, string? Message,
        int? ErrorCode, Guid? OwnerId, Guid? CreatedBy, Guid? ImportJobId, DateTime? CompletedOn);

    private async Task<Job> JobAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT statecode, statuscode, operationtype, messagename, data, message, errorcode, ownerid, createdby, " +
            "xrmverse_importjobid, completedon FROM data.asyncoperation WHERE asyncoperationid = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("the asyncoperation {0} must exist", id);
        T? N<T>(int i) => rd.IsDBNull(i) ? default : rd.GetFieldValue<T>(i);
        return new Job(rd.GetInt32(0), rd.GetInt32(1), N<int?>(2), N<string>(3), N<string>(4), N<string>(5), N<int?>(6),
            N<Guid?>(7), N<Guid?>(8), N<Guid?>(9), N<DateTime?>(10));
    }

    private async Task<(double Progress, string Data)?> ImportJobAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT progress, data FROM data.importjob WHERE importjobid = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return null;
        return (rd.GetDouble(0), rd.GetString(1));
    }

    private async Task<Guid> AdminUserIdAsync() => (await Admin.ExecuteAsync(new WhoAmIRequest())).Results["UserId"] is Guid id ? id : Guid.Empty;

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportSolutionAsync_returns_ids_and_the_worker_completes_the_job()
    {
        var zip = SolutionFixtureZip.Build();
        var importJobId = Guid.NewGuid();

        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = zip, ImportJobId = importJobId });

        response.ResponseName.Should().Be("ImportSolutionAsync");
        response.AsyncOperationId.Should().NotBeEmpty();
        response.ImportJobKey.Should().Be(importJobId.ToString());

        // Queued, not run: nothing imported yet.
        var queued = await JobAsync(response.AsyncOperationId);
        queued.StateCode.Should().Be(0);
        queued.StatusCode.Should().Be(0);
        queued.OperationType.Should().Be(ImportSolutionOperation);
        queued.MessageName.Should().Be("ImportSolution");
        queued.Data.Should().Be(Convert.ToBase64String(zip));
        queued.ImportJobId.Should().Be(importJobId);
        var adminId = await AdminUserIdAsync();
        queued.OwnerId.Should().Be(adminId);
        queued.CreatedBy.Should().Be(adminId);
        Store.GetEntity("xv_project").Should().BeNull();
        (await ImportJobAsync(importJobId)).Should().BeNull();

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var done = await JobAsync(response.AsyncOperationId);
        done.StateCode.Should().Be(3);
        done.StatusCode.Should().Be(30);
        done.CompletedOn.Should().NotBeNull();
        done.Data.Should().BeNull("the solution file is dropped once the job has completed");
        Store.GetEntity("xv_project").Should().NotBeNull();
        var importJob = await ImportJobAsync(importJobId);
        importJob!.Value.Progress.Should().Be(100);
        (await NewWorker().RunOnceAsync(default)).Should().BeFalse("the job ran once");
    }

    [Fact]
    public async Task ImportSolutionAsync_without_an_import_job_id_returns_a_new_one()
    {
        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build() });

        var importJobId = Guid.Parse(response.ImportJobKey);
        importJobId.Should().NotBeEmpty();
        (await JobAsync(response.AsyncOperationId)).ImportJobId.Should().Be(importJobId);
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();
        (await ImportJobAsync(importJobId))!.Value.Progress.Should().Be(100);
    }

    [Fact]
    public async Task Failing_async_import_ends_as_failed_with_message()
    {
        var importJobId = Guid.NewGuid();
        var zip = SolutionFixtureZip.Build(c =>
            c.Root!.Element("SdkMessageProcessingSteps")!.Elements("SdkMessageProcessingStep").First()
                .Element("PluginTypeId")!.Value = Guid.NewGuid().ToString("D"));

        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = zip, ImportJobId = importJobId });
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        job.StateCode.Should().Be(3);
        job.StatusCode.Should().Be(31);
        job.Message.Should().NotBeNullOrEmpty();
        job.ErrorCode.Should().NotBeNull();
        job.CompletedOn.Should().NotBeNull();
        job.Data.Should().BeNull("the solution file is dropped once the job has completed");
        Store.GetEntity("xv_project").Should().BeNull("the failed import is rolled back");
        (await CountAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = N'data' AND TABLE_TYPE = N'BASE TABLE' AND TABLE_NAME LIKE N'xv[_]%'")).Should().Be(0);

        var importJob = await ImportJobAsync(importJobId);
        importJob.Should().NotBeNull("a failed import still leaves its log");
        importJob!.Value.Progress.Should().Be(0);
        importJob.Value.Data.Should().Contain(job.Message!);
        XElement.Parse(importJob.Value.Data).Descendants("result").Should().Contain(r => (string?)r.Attribute("result") == "failure");
    }

    [Fact]
    public async Task Async_import_with_an_unreadable_file_fails_with_a_log()
    {
        var importJobId = Guid.NewGuid();
        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = [1, 2, 3, 4], ImportJobId = importJobId });
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        (await ImportJobAsync(importJobId))!.Value.Progress.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_wraps_ImportSolution()
    {
        var importJobId = Guid.NewGuid();
        var response = (ExecuteAsyncResponse)await Admin.ExecuteAsync(new ExecuteAsyncRequest
        {
            Request = new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId },
        });

        response.ResponseName.Should().Be("ExecuteAsync");
        response.AsyncJobId.Should().NotBeEmpty();
        var queued = await JobAsync(response.AsyncJobId);
        queued.OperationType.Should().Be(ImportSolutionOperation);
        queued.ImportJobId.Should().Be(importJobId);

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncJobId);
        (job.StateCode, job.StatusCode).Should().Be((3, 30));
        Store.GetEntity("xv_project").Should().NotBeNull();
        (await ImportJobAsync(importJobId))!.Value.Progress.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_of_another_request_is_400()
    {
        var whoAmI = await FaultAsync(() => Admin.ExecuteAsync(new ExecuteAsyncRequest { Request = new WhoAmIRequest() }));
        whoAmI.HttpStatus.Should().Be(400);
        whoAmI.Message.Should().Contain("WhoAmI");

        // Merge can run asynchronously in Dataverse, not in xrmverse: refused explicitly. DeleteAndPromote is queued (plan 2.6,
        // Task 7) after its plan: an unknown solution is refused before anything is queued.
        var merge = await FaultAsync(() => Admin.ExecuteAsync(new ExecuteAsyncRequest
        {
            Request = new MergeRequest { Target = new EntityReference("account", Guid.NewGuid()), SubordinateId = Guid.NewGuid() },
        }));
        merge.HttpStatus.Should().Be(400);
        merge.Message.Should().Contain("Merge");
        var promote = await FaultAsync(() => Admin.ExecuteAsync(new ExecuteAsyncRequest
        {
            Request = new DeleteAndPromoteRequest { UniqueName = "anything" },
        }));
        promote.HttpStatus.Should().Be(404);
        promote.Message.Should().Contain("anything");

        var none = await FaultAsync(() => Admin.ExecuteAsync(new ExecuteAsyncRequest()));
        none.HttpStatus.Should().Be(400);

        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);
    }

    [Fact]
    public async Task RetrieveFormattedImportJobResults_returns_an_excel_workbook_of_the_log()
    {
        var importJobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId });

        var response = (RetrieveFormattedImportJobResultsResponse)await Admin.ExecuteAsync(
            new RetrieveFormattedImportJobResultsRequest { ImportJobId = importJobId });

        response.ResponseName.Should().Be("RetrieveFormattedImportJobResults");
        var log = XElement.Parse((await ImportJobAsync(importJobId))!.Value.Data);
        log.Name.LocalName.Should().Be("importexportxml", "importjob.data keeps the raw log");
        AssertWorkbookOf(response.FormattedResults, log);

        var missing = await FaultAsync(() => Admin.ExecuteAsync(
            new RetrieveFormattedImportJobResultsRequest { ImportJobId = Guid.NewGuid() }));
        missing.HttpStatus.Should().Be(404);
    }

    /// <summary>A SpreadsheetML 2003 workbook: one worksheet, a header row and one row per <c>result</c> of the log.</summary>
    private static void AssertWorkbookOf(string formatted, XElement log)
    {
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var doc = XDocument.Parse(formatted);
        doc.Nodes().OfType<XProcessingInstruction>().Should().ContainSingle(pi => pi.Target == "mso-application" && pi.Data.Contains("Excel.Sheet"));
        doc.Root!.Name.Should().Be(ss + "Workbook");
        var sheet = doc.Root.Elements(ss + "Worksheet").Should().ContainSingle().Which;
        var rows = sheet.Element(ss + "Table")!.Elements(ss + "Row").ToList();
        rows.Should().HaveCount(1 + log.Descendants("result").Count());
        rows[0].Elements(ss + "Cell").Select(c => c.Element(ss + "Data")!.Value).Should().Contain(["Result", "Error Code", "Description"]);
        rows.Skip(1).Select(r => r.Elements(ss + "Cell").ElementAt(3).Value).Should().Contain("success");
    }

    [Fact]
    public async Task ImportSolutionAsync_requires_prvImportCustomization()
    {
        var root = await RootBusinessUnitAsync(fx);
        var basicOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basicOid), StandardRoles.BasicUser);

        var fault = await FaultAsync(() => ServiceFor(fx, basicOid).ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build() }));

        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvImportCustomization");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);
    }

    [Fact]
    public async Task ImportSolutionAsync_with_a_used_import_job_id_is_400()
    {
        var importJobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId });

        // Queued but not run yet: the id is taken.
        var queued = await FaultAsync(() => Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId }));
        queued.HttpStatus.Should().Be(400);
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(1);
    }

    [Fact]
    public async Task ImportSolutionAsync_carries_HoldingSolution_to_the_job()
    {
        // Queued with the flag (plan 2.6, Task 7); the run applies it: an unmanaged file cannot be staged, so the job fails.
        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), HoldingSolution = true });
        (await CountAsync($"SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = '{response.AsyncOperationId}' AND data LIKE N'{Xrmd.Solution.SolutionImporter.HoldingPrefix}%'"))
            .Should().Be(1);

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Contain("HoldingSolution");
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task System_job_of_an_operation_type_without_handler_fails_explicitly()
    {
        var response = (ImportSolutionAsyncResponse)await Admin.ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build() });
        await ExecAsync($"UPDATE data.asyncoperation SET operationtype = 202 WHERE asyncoperationid = '{response.AsyncOperationId}'");

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Contain("operation type 202");
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task ComponentParameters_and_SolutionParameters_are_400_naming_the_parameter()
    {
        var components = new EntityCollection { Entities = { new Entity("connectionreference") } };
        var parameters = new SolutionParameters { StageSolutionUploadId = Guid.NewGuid() };
        OrganizationRequest[] requests =
        [
            new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ["ComponentParameters"] = components },
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), ["ComponentParameters"] = components },
            new ExecuteAsyncRequest
            {
                Request = new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ["ComponentParameters"] = components },
            },
        ];
        foreach (var request in requests)
        {
            var fault = await FaultAsync(() => Admin.ExecuteAsync(request));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain("ComponentParameters");
        }

        foreach (var request in new OrganizationRequest[]
                 {
                     new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ["SolutionParameters"] = parameters },
                     new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), ["SolutionParameters"] = parameters },
                     new ExecuteAsyncRequest
                     {
                         Request = new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ["SolutionParameters"] = parameters },
                     },
                 })
        {
            var fault = await FaultAsync(() => Admin.ExecuteAsync(request));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain("SolutionParameters");
        }

        // Empty ones carry nothing to apply: accepted.
        await Admin.ExecuteAsync(new ImportSolutionAsyncRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(), ["ComponentParameters"] = new EntityCollection(),
            ["SolutionParameters"] = new SolutionParameters(),
        });
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.importjob")).Should().Be(0);
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Owner_losing_prvImportCustomization_before_the_run_fails_the_job_with_a_log()
    {
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, root, oid);
        await AssignRoleAsync(fx, userId, StandardRoles.SystemCustomizer);
        var importJobId = Guid.NewGuid();
        var response = (ImportSolutionAsyncResponse)await ServiceFor(fx, oid).ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId });

        await ExecAsync($"DELETE FROM data.systemuserroles WHERE systemuserid = '{userId}'");
        Cache(fx).Invalidate();
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Contain("prvImportCustomization");
        Store.GetEntity("xv_project").Should().BeNull();
        var importJob = await ImportJobAsync(importJobId);
        importJob!.Value.Progress.Should().Be(0);
        importJob.Value.Data.Should().Contain("prvImportCustomization");
    }

    [Fact]
    public async Task Disabled_owner_fails_the_job()
    {
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, root, oid);
        await AssignRoleAsync(fx, userId, StandardRoles.SystemCustomizer);
        var response = (ImportSolutionAsyncResponse)await ServiceFor(fx, oid).ExecuteAsync(
            new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build() });

        await ExecAsync($"UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = '{userId}'");
        Cache(fx).Invalidate();
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.StateCode, job.StatusCode).Should().Be((3, 31));
        job.Message.Should().Be(AsyncOperationWorker.OwnerUnavailableMessage);
        job.Data.Should().BeNull();
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Plugin_service_answers_RetrieveFormattedImportJobResults_and_refuses_queueing()
    {
        var importJobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = importJobId });
        var me = (WhoAmIResponse)await Admin.ExecuteAsync(new WhoAmIRequest());
        var caller = new Caller(me.UserId, me.BusinessUnitId);

        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, null);

        core.IsNonPipelineMessage("RetrieveFormattedImportJobResults").Should().BeTrue();
        var response = (RetrieveFormattedImportJobResultsResponse)await core.ExecuteNonPipelineAsync(
            new RetrieveFormattedImportJobResultsRequest { ImportJobId = importJobId }, caller, session, default);
        AssertWorkbookOf(response.FormattedResults, XElement.Parse((await ImportJobAsync(importJobId))!.Value.Data));

        foreach (var request in new OrganizationRequest[]
                 {
                     new ImportSolutionAsyncRequest { CustomizationFile = SolutionFixtureZip.Build() },
                     new ExecuteAsyncRequest { Request = new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build() } },
                 })
        {
            var fault = await FaultAsync(() => core.ExecuteNonPipelineAsync(request, caller, session, default));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain("plug-in");
        }
    }
}
