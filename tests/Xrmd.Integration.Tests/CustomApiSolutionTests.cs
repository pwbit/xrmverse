using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom API definitions in solution files (plan 15, task 3): the import writes the API, its parameters and properties, its
/// <c>sdkmessage</c> and <c>sdkmessagefilter</c>, tracks the API as a component and refreshes the registry; it refuses what
/// xrmverse cannot run (C1, C2, C5) and immutable changes (C11); the plug-in type and the bound table of an API cannot be
/// deleted; the cascade solution delete removes the APIs with their message; the steps on an API's message follow its
/// <c>allowedcustomprocessingsteptype</c>.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiSolutionTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XtCustomApiTests";
    private const string Echo = "Xrmd.TestPlugins.EchoActionPlugin";
    private const string BoundTable = "pb_apibound";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private CustomApiRegistry Registry => fx.App.Services.GetRequiredService<CustomApiRegistry>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await DropBoundTableAsync();
    }

    public async Task DisposeAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await DropBoundTableAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static readonly TestApi EchoApi = new("xt_Echo", AllowedStepType: 2, PluginTypeName: Echo,
        Requests: [("Text", 10, false, null), ("Count", 7, true, null), ("Record", 5, true, "systemuser")],
        Responses: [("Echoed", 10)]);

    private Task<OrganizationResponse> ImportAsync(byte[] zip, Guid? jobId = null) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId ?? Guid.NewGuid() });

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    private Task<int> ApiRowsAsync() => CountAsync("""
        SELECT (SELECT COUNT(*) FROM data.customapi) + (SELECT COUNT(*) FROM data.customapirequestparameter)
             + (SELECT COUNT(*) FROM data.customapiresponseproperty) + (SELECT COUNT(*) FROM data.sdkmessage WHERE categoryname = N'CustomAPI')
        """);

    private Task<Guid> ApiIdAsync(string uniqueName) =>
        ScalarAsync("SELECT customapiid FROM data.customapi WHERE uniquename = @n", ("@n", uniqueName)).ContinueWith(t => (Guid)t.Result!);

    private async Task CreateBoundTableAsync()
    {
        var entity = new EntityMetadata { LogicalName = BoundTable, SchemaName = "pb_ApiBound", OwnershipType = OwnershipTypes.UserOwned };
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
    }

    private async Task DropBoundTableAsync()
    {
        if (Store.GetEntity(BoundTable) is not null)
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, BoundTable, default));
    }

    private Task<HttpResponseMessage> DeleteSolutionAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/admin/solutions/{Solution}?cascade=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task Refused400Async(Func<Task> act, params string[] contains)
    {
        var fault = await FaultAsync(act);
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        foreach (var text in contains) fault.Message.Should().Contain(text);
    }

    /// <summary>The zip with the entry <paramref name="path"/> rewritten as text (the other entries unchanged).</summary>
    private static byte[] Rewrite(byte[] zip, string path, Func<string, string> rewrite)
    {
        using var stream = new MemoryStream();
        stream.Write(zip);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(path)!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(rewrite(text));
        }
        return stream.ToArray();
    }

    /// <summary>
    /// <see cref="EchoApi"/> imported with a synchronous stage 20 step on its message (and the API's filter) added to the
    /// solution, exported, then everything removed: the export, the organization back to empty, and the step's id.
    /// </summary>
    private async Task<(byte[] Zip, Guid Step)> ExportedWithStepAsync()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        var step = await PluginTestHelper.RegisterStepAsync(Admin, CustomApiTestSolution.PluginTypeId(Echo), "xt_Echo", "none", 20);
        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = ComponentType.SdkMessageProcessingStep, ComponentId = step, SolutionUniqueName = Solution,
        });
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Solution, Managed = false }))
            .ExportSolutionFile;
        await SolutionImportTests.ClearImportAsync(fx);
        (await ApiRowsAsync()).Should().Be(0);
        return (zip, step);
    }

    /// <summary>Nothing of a failed import is written: no API row, message, step, assembly or solution.</summary>
    private async Task NothingWrittenAsync()
    {
        (await ApiRowsAsync()).Should().Be(0, "nothing of the failed import is written");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Solution))).Should().Be(0);
    }

    /// <summary>A user with the role <paramref name="roleId"/> and its caller.</summary>
    private async Task<(Guid ObjectId, Caller Caller)> UserWithRoleAsync(Guid roleId)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, roleId);
        return (oid, await TestServices.CallerAsync(fx.App.Services, oid));
    }

    /// <summary>A copy of System Customizer without <paramref name="withoutPrivilege"/>.</summary>
    private async Task<Guid> CustomizerWithoutAsync(string withoutPrivilege)
    {
        var role = await CreateRoleAsync(fx, "apicascade-" + Guid.NewGuid().ToString("N")[..8]);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
            SELECT NEWID(), @role, rp.privilegeid, rp.privilegedepthmask, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
            FROM data.roleprivileges rp JOIN data.privilege p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @customizer AND p.name <> @without
            """, conn);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@customizer", Xrmd.Security.StandardRoles.SystemCustomizer);
        cmd.Parameters.AddWithValue("@without", withoutPrivilege);
        await cmd.ExecuteNonQueryAsync();
        Cache(fx).Invalidate();
        return role;
    }

    private Xrmd.Admin.SolutionCascadeDeleter NewDeleter(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<Xrmd.Admin.SolutionCascadeDeleter>();

    private static XElement ApiFile(IDictionary<string, XElement> files, string api, string? sub = null) =>
        files[sub is null ? $"customapis/{api}/customapi.xml" : $"customapis/{api}/{sub}"];

    // ---- import ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_writes_the_api_its_fields_message_filter_and_component()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi with { PrivilegeName = "prvXtNoSuchPrivilege" }), jobId);

        var apiId = await ApiIdAsync("xt_Echo");
        var message = SdkMessageCatalog.MessageId("xt_Echo");
        var admin = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;
        (await CountAsync("""
            SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND name = N'xt_Echo' AND bindingtype = 0
              AND boundentitylogicalname IS NULL AND allowedcustomprocessingsteptype = 2 AND isfunction = 0 AND isprivate = 0
              AND workflowsdkstepenabled = 0 AND executeprivilegename = N'prvXtNoSuchPrivilege' AND displayname = N'xt_Echo'
              AND plugintypeid = @type AND sdkmessageid = @message AND ownerid = @owner AND owninguser = @owner
              AND owningbusinessunit IS NOT NULL AND xrmverse_componentxml LIKE N'%iscustomizable%'
            """, ("@id", apiId), ("@type", CustomApiTestSolution.PluginTypeId(Echo)), ("@message", message), ("@owner", admin)))
            .Should().Be(1);
        (await CountAsync("""
            SELECT COUNT(*) FROM data.customapirequestparameter WHERE customapiid = @id AND ownerid IS NOT NULL
              AND ((uniquename = N'Text' AND type = 10 AND isoptional = 0 AND logicalentityname IS NULL)
                OR (uniquename = N'Count' AND type = 7 AND isoptional = 1)
                OR (uniquename = N'Record' AND type = 5 AND isoptional = 1 AND logicalentityname = N'systemuser'))
            """, ("@id", apiId))).Should().Be(3);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @id AND uniquename = N'Echoed' AND type = 10",
            ("@id", apiId))).Should().Be(1);

        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @m AND name = N'xt_Echo' AND categoryname = N'CustomAPI' AND isprivate = 0",
            ("@m", message))).Should().Be(1);
        (await CountAsync("""
            SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @m AND sdkmessagefilterid = @f
              AND primaryobjecttypecode = N'none' AND iscustomprocessingstepallowed = 1
            """, ("@m", message), ("@f", SdkMessageCatalog.FilterId("xt_Echo", "none")))).Should().Be(1);

        // The API is a component of the solution; its fields, message and filter are not.
        (await CountAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @n AND sc.componenttype = 9990 AND sc.objectid = @id
            """, ("@n", Solution), ("@id", apiId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid = @m", ("@m", message))).Should().Be(0);

        // The import job lists the API, with the warning for its unknown privilege.
        var data = XElement.Parse((string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!);
        var item = data.Element("customApis")!.Elements("customApi").Should().ContainSingle().Subject;
        ((string?)item.Attribute("id")).Should().Be(apiId.ToString("D"));
        item.Elements("result").Should().Contain(r => (string?)r.Attribute("result") == "warning"
            && ((string?)r.Attribute("errortext"))!.Contains("prvXtNoSuchPrivilege"));

        // The registry sees the API at once.
        var definition = await Registry.FindAsync("XT_ECHO", default);
        definition.Should().NotBeNull();
        definition!.Id.Should().Be(apiId);
        definition.AllowedStepType.Should().Be(2);
        definition.MainOperation!.TypeName.Should().Be(Echo);
        definition.MainOperation.AssemblyId.Should().Be(CustomApiTestSolution.AssemblyId);
        definition.RequestParameters.Select(p => p.UniqueName).Should().BeEquivalentTo("Text", "Count", "Record");
        definition.ResponseProperties.Should().ContainSingle().Which.Type.Should().Be(CustomApiType.String);
    }

    [Fact]
    public async Task Entity_bound_api_gets_a_filter_on_its_table()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, new TestApi("xt_OnPipe", BindingType: 1, BoundEntity: TestEntities.Pipe)));
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @m AND primaryobjecttypecode = @e AND iscustomprocessingstepallowed = 0",
            ("@m", SdkMessageCatalog.MessageId("xt_OnPipe")), ("@e", TestEntities.Pipe))).Should().Be(1);
    }

    public static TheoryData<string> InvalidCases() =>
    [
        "powerfx", "function-workflow", "response-isoptional", "binding-3", "steptype-3", "bound-missing", "global-bound",
        "bound-unknown", "type-13", "entity-on-string", "target-on-bound", "no-prefix", "bad-characters", "reserved-name",
        "unknown-plugin-type", "duplicate-parameter", "named-like-entity-set", "reserved-autonumber",
    ];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task Invalid_definitions_fail_the_import(string kind)
    {
        var (api, edit, expected) = kind switch
        {
            "powerfx" => (new TestApi("xt_Fx"), (Action<IDictionary<string, XElement>>)(f => ApiFile(f, "xt_Fx").Add(new XElement("powerfxruleid", Guid.NewGuid()))),
                "Power Fx"),
            "function-workflow" => (new TestApi("xt_Fn", IsFunction: true), f => ApiFile(f, "xt_Fn").Element("workflowsdkstepenabled")!.Value = "1",
                "enabled for workflows"),
            "response-isoptional" => (new TestApi("xt_Opt", Responses: [("Out", 10)]),
                f => ApiFile(f, "xt_Opt", "customapiresponseproperties/Out/customapiresponseproperty.xml").Add(new XElement("isoptional", 1)),
                "isoptional"),
            "binding-3" => (new TestApi("xt_B3", BindingType: 3, BoundEntity: TestEntities.Pipe), null, "bindingtype 3"),
            "steptype-3" => (new TestApi("xt_S3", AllowedStepType: 3), null, "allowedcustomprocessingsteptype 3"),
            "bound-missing" => (new TestApi("xt_NoTable", BindingType: 1), null, "needs a boundentitylogicalname"),
            "global-bound" => (new TestApi("xt_GlobalBound", BoundEntity: TestEntities.Pipe), null, "it is global"),
            "bound-unknown" => (new TestApi("xt_Unknown", BindingType: 2, BoundEntity: "pb_nosuchtable"), null, "pb_nosuchtable"),
            "type-13" => (new TestApi("xt_T13", Requests: [("Bad", 13, false, null)]), null, "type 13"),
            "entity-on-string" => (new TestApi("xt_EntityString", Requests: [("Text", 10, false, "systemuser")]), null, "names no table"),
            "target-on-bound" => (new TestApi("xt_Target", BindingType: 1, BoundEntity: TestEntities.Pipe, Requests: [("Target", 5, false, null)]),
                null, "adds Target"),
            "no-prefix" => (new TestApi("zzq_Thing"), null, "prefix"),
            "bad-characters" => (new TestApi("xt_Bad-Name"), null, "letters, digits"),
            "reserved-name" => (new TestApi("WhoAmI"), null, "handles itself"),
            "reserved-autonumber" => (new TestApi("GetNextAutoNumberValue"), null, "handles itself"),
            "named-like-entity-set" => (new TestApi(TestEntities.PipeSet), null, $"the entity set of table '{TestEntities.Pipe}'"),
            "unknown-plugin-type" => (new TestApi("xt_NoType", PluginTypeName: Echo),
                f => ApiFile(f, "xt_NoType").Element("plugintypeid")!.Element("plugintypeexportkey")!.Value = Guid.NewGuid().ToString("D"),
                "plugintypeexportkey"),
            "duplicate-parameter" => (new TestApi("xt_Twice", Requests: [("Text", 10, false, null)]), f =>
                {
                    var copy = new XElement(ApiFile(f, "xt_Twice", "customapirequestparameters/Text/customapirequestparameter.xml"));
                    copy.SetAttributeValue("uniquename", "TEXT");
                    f["customapis/xt_Twice/customapirequestparameters/TEXT/customapirequestparameter.xml"] = copy;
                },
                "more than once"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution, edit, api)), api.UniqueName, expected);

        (await ApiRowsAsync()).Should().Be(0, "nothing of the failed import is written");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Solution))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(0);
    }

    [Fact]
    public void Every_message_the_server_handles_itself_is_reserved()
    {
        using var scope = fx.App.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        foreach (var name in AutoNumberMessages.Messages.Concat(["WhoAmI", "Create", "SetState", "ExportSolution", "RetrieveEntity"]))
        {
            executor.IsReservedMessageName(name).Should().BeTrue(name);
            executor.IsReservedMessageName(name.ToUpperInvariant()).Should().BeTrue(name);
        }
        executor.IsReservedMessageName("xt_NotAMessage").Should().BeFalse();
    }

    [Fact]
    public async Task Api_named_like_an_existing_message_fails_the_import()
    {
        await Admin.CreateAsync(new Entity("sdkmessage", SdkMessageCatalog.MessageId("xt_Taken")) { ["name"] = "xt_Taken", ["categoryname"] = "CustomAction" });

        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution, new TestApi("XT_TAKEN"))), "XT_TAKEN", "already has the message 'xt_Taken'");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(0);
    }

    /// <summary>
    /// Task 3 fix round 1: the API of a re-import is found by its unique name as the organization compares text (case and
    /// trailing spaces ignored, as SQL Server's _CI_AS does), on both engines: the existing API and its fields are kept.
    /// <paramref name="stored"/>: the name the organization's row is given before the re-import (a row written with a trailing
    /// space), or null to keep it.
    /// </summary>
    [Theory]
    [InlineData("xt_ECHO", null)]
    [InlineData("xt_Echo", "xt_Echo ")]
    public async Task Reimport_naming_the_api_in_another_case_or_with_a_trailing_space_keeps_the_api(string name, string? stored)
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        var apiId = await ApiIdAsync("xt_Echo");
        var rows = await ApiRowsAsync();
        if (stored is not null)
            await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.customapi SET uniquename = @n WHERE customapiid = @id", ("@n", stored), ("@id", apiId));

        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi with { UniqueName = name }));

        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id", ("@id", apiId))).Should().Be(1);
        (await ApiRowsAsync()).Should().Be(rows, "the parameters and properties of the API are found again, not added");
    }

    [Fact]
    public async Task Reimport_with_an_immutable_change_fails_and_keeps_the_api()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        var apiId = await ApiIdAsync("xt_Echo");
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        (await ApiRowsAsync()).Should().Be(1 + 3 + 1 + 1, "an identical re-import changes nothing");

        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi with { AllowedStepType = 0 })),
            "xt_Echo", "allowedcustomprocessingsteptype is 2 in the organization and 0 in the file");
        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi with { IsFunction = true })), "isfunction");
        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution,
            EchoApi with { Requests = [("Text", 12, false, null), ("Count", 7, true, null), ("Record", 5, true, "systemuser")] })),
            "request parameter 'Text'", "type is 10 in the organization and 12 in the file");
        await Refused400Async(() => ImportAsync(CustomApiTestSolution.Build(Solution,
            EchoApi with { Requests = [("Text", 10, true, null), ("Count", 7, true, null), ("Record", 5, true, "systemuser")] })),
            "isoptional is 0 in the organization and 1 in the file");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND allowedcustomprocessingsteptype = 2 AND isfunction = 0",
            ("@id", apiId))).Should().Be(1);

        // What may change does: the display name, isprivate (on the API and its message), the privilege, a new property.
        await ImportAsync(CustomApiTestSolution.Build(Solution, f =>
            {
                var label = ApiFile(f, "xt_Echo").Element("displayname")!;
                label.SetAttributeValue("default", "Echo renamed");
                label.Element("label")!.SetAttributeValue("description", "Echo renamed");
            },
            EchoApi with { IsPrivate = true, PrivilegeName = "prvReadAccount", Responses = [("Echoed", 10), ("Length", 7)] }));
        (await CountAsync("""
            SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND isprivate = 1 AND executeprivilegename = N'prvReadAccount'
              AND displayname = N'Echo renamed'
            """,
            ("@id", apiId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE sdkmessageid = @m AND isprivate = 1", ("@m", SdkMessageCatalog.MessageId("xt_Echo"))))
            .Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.customapiresponseproperty WHERE customapiid = @id", ("@id", apiId))).Should().Be(2);
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(1);
        (await Registry.FindAsync("xt_Echo", default))!.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public async Task Imported_rows_are_authored_but_their_message_is_not()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        var apiId = await ApiIdAsync("xt_Echo");
        var parameter = (Guid)(await ScalarAsync("SELECT customapirequestparameterid FROM data.customapirequestparameter WHERE uniquename = N'Text'"))!;
        var property = (Guid)(await ScalarAsync("SELECT customapiresponsepropertyid FROM data.customapiresponseproperty"))!;
        var message = SdkMessageCatalog.MessageId("xt_Echo");
        var filter = SdkMessageCatalog.FilterId("xt_Echo", "none");
        const string refusal = "maintained by the server";

        // The API's message and filter stay the server's (plan 3.1, R1).
        await Refused400Async(() => Admin.UpdateAsync(new Entity("sdkmessage", message) { ["isprivate"] = true }), refusal);
        await Refused400Async(() => Admin.DeleteAsync("sdkmessage", message), refusal);
        await Refused400Async(() => Admin.UpdateAsync(new Entity("sdkmessagefilter", filter) { ["isvisible"] = false }), refusal);
        await Refused400Async(() => Admin.DeleteAsync("sdkmessagefilter", filter), refusal);
        (await ApiRowsAsync()).Should().Be(1 + 3 + 1 + 1);

        // An unmanaged imported API is authored like any other: its mutable columns change, and its Delete takes everything.
        foreach (var (table, id) in new[] { ("customapi", apiId), ("customapirequestparameter", parameter), ("customapiresponseproperty", property) })
            await Admin.UpdateAsync(new Entity(table, id) { ["name"] = "changed" });
        (await CountAsync("SELECT COUNT(*) FROM data.customapi WHERE customapiid = @id AND name = N'changed'", ("@id", apiId))).Should().Be(1);
        await Admin.DeleteAsync("customapi", apiId);
        (await ApiRowsAsync()).Should().Be(0);
    }

    // ---- what an API depends on --------------------------------------------------------------------------------------

    [Fact]
    public async Task Plug_in_type_assembly_and_bound_table_of_an_api_cannot_be_deleted()
    {
        await CreateBoundTableAsync();
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi with { BindingType = 1, BoundEntity = BoundTable }));

        await Refused400Async(() => Admin.DeleteAsync("plugintype", CustomApiTestSolution.PluginTypeId(Echo)), "xt_Echo");
        await Refused400Async(() => Admin.DeleteAsync("pluginassembly", CustomApiTestSolution.AssemblyId), "xt_Echo");
        await Refused400Async(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = BoundTable }), BoundTable, "xt_Echo");

        Store.GetEntity(BoundTable).Should().NotBeNull();
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype WHERE plugintypeid = @id", ("@id", CustomApiTestSolution.PluginTypeId(Echo))))
            .Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE primaryobjecttypecode = @e", ("@e", BoundTable))).Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Solution_delete_removes_the_apis_with_their_message_and_steps()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi, new TestApi("xt_Second", BindingType: 1, BoundEntity: TestEntities.Pipe)));
        var step = await PluginTestHelper.RegisterStepAsync(Admin, CustomApiTestSolution.PluginTypeId(Echo), "xt_Echo", null, 40);
        (await Registry.FindAsync("xt_Second", default)).Should().NotBeNull();

        var response = await DeleteSolutionAsync();
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        (await ApiRowsAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid IN (@a, @b)",
            ("@a", SdkMessageCatalog.MessageId("xt_Echo")), ("@b", SdkMessageCatalog.MessageId("xt_Second")))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Solution))).Should().Be(0);
        (await Registry.FindAsync("xt_Echo", default)).Should().BeNull();
        (await Registry.FindAsync("xt_Second", default)).Should().BeNull();
    }

    [Fact]
    public async Task Step_of_another_solution_on_an_api_message_blocks_the_solution_delete()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        var step = await PluginTestHelper.RegisterStepAsync(Admin, CustomApiTestSolution.PluginTypeId(Echo), "xt_Echo", null, 40);
        var publisher = (Guid)(await ScalarAsync("SELECT publisherid FROM data.publisher WHERE uniquename = @n", ("@n", CustomApiTestSolution.PublisherName)))!;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = "xt_other", ["friendlyname"] = "Other", ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", publisher),
        });
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = ComponentType.SdkMessageProcessingStep, ComponentId = step, SolutionUniqueName = "xt_other" });

        var response = await DeleteSolutionAsync();
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("xt_Echo").And.Contain("xt_other");

        (await ApiRowsAsync()).Should().Be(1 + 3 + 1 + 1, "the failed delete is rolled back");
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(1);
        (await Registry.FindAsync("xt_Echo", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Cascade_delete_needs_the_privileges_of_the_rows_it_deletes()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        const int rows = 1 + 3 + 1 + 1;

        // The Admin API is for System Administrators only.
        var (customizerOid, customizer) = await UserWithRoleAsync(Xrmd.Security.StandardRoles.SystemCustomizer);
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/solutions/{Solution}?cascade=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(customizerOid, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await ApiRowsAsync()).Should().Be(rows);

        // The deleter itself: without prvDeleteCustomAPI, 403 and every row stays.
        var (_, withoutApiDelete) = await UserWithRoleAsync(await CustomizerWithoutAsync("prvDeleteCustomAPI"));
        using (var scope = fx.App.Services.CreateScope())
        {
            var fault = await FaultAsync(() => NewDeleter(scope).DeleteAsync(withoutApiDelete, Solution, default));
            fault.HttpStatus.Should().Be(403, fault.ToString());
            fault.Message.Should().Contain("prvDeleteCustomAPI");
        }
        (await ApiRowsAsync()).Should().Be(rows);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Solution))).Should().Be(1);

        // System Customizer holds every privilege the delete needs.
        using (var scope = fx.App.Services.CreateScope())
            await NewDeleter(scope).DeleteAsync(customizer, Solution, default);
        (await ApiRowsAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(0);
    }

    [Fact]
    public async Task Step_registered_on_an_api_message_during_the_delete_is_an_explicit_conflict()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, EchoApi));
        using var scope = fx.App.Services.CreateScope();
        var deleter = NewDeleter(scope);
        // Planned without it; a step on the API's message (held by no solution) appears before the transaction.
        deleter.BeforeApply = async _ =>
            await PluginTestHelper.RegisterStepAsync(Admin, CustomApiTestSolution.PluginTypeId(Echo), "xt_Echo", null, 40);

        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var fault = await FaultAsync(() => deleter.DeleteAsync(admin, Solution, default));

        fault.HttpStatus.Should().Be(409, fault.ToString());
        fault.Message.Should().Contain(Solution).And.Contain("retry");
        (await ApiRowsAsync()).Should().Be(1 + 3 + 1 + 1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(1);
    }

    [Fact]
    public async Task Cascade_names_the_api_outside_the_solution_that_keeps_the_plug_in_type()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution, new TestApi("xt_Aaa", PluginTypeName: Echo), new TestApi("xt_Bbb", PluginTypeName: Echo)));
        // xt_Bbb leaves the solution: the cascade deletes xt_Aaa, and the type stays in use by xt_Bbb.
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("DELETE FROM data.solutioncomponent WHERE objectid = @id", conn);
            cmd.Parameters.AddWithValue("@id", await ApiIdAsync("xt_Bbb"));
            await cmd.ExecuteNonQueryAsync();
        }

        var response = await DeleteSolutionAsync();
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("xt_Bbb").And.NotContain("xt_Aaa");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(2);
    }

    // ---- the steps on an API's message ---------------------------------------------------------------------------------

    [Fact]
    public async Task Step_rules_follow_allowed_processing_step_type()
    {
        await ImportAsync(CustomApiTestSolution.Build(Solution,
            new TestApi("xt_None", AllowedStepType: 0, PluginTypeName: Echo),
            new TestApi("xt_AsyncOnly", AllowedStepType: 1, PluginTypeName: Echo),
            new TestApi("xt_Both", AllowedStepType: 2, PluginTypeName: Echo)));
        var type = CustomApiTestSolution.PluginTypeId(Echo);

        await Refused400Async(() => PluginTestHelper.RegisterStepAsync(Admin, type, "xt_None", null, 40), "xt_None", "accepts no processing step");
        await Refused400Async(() => PluginTestHelper.RegisterStepAsync(Admin, type, "xt_AsyncOnly", null, 40, mode: 0), "xt_AsyncOnly", "Async Only");
        await Refused400Async(() => PluginTestHelper.RegisterStepAsync(Admin, type, "xt_AsyncOnly", null, 20, mode: 0), "xt_AsyncOnly");
        var asyncStep = await PluginTestHelper.RegisterStepAsync(Admin, type, "xt_AsyncOnly", null, 40, mode: 1);
        await Refused400Async(() => Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", asyncStep) { ["mode"] = new OptionSetValue(0) }),
            "xt_AsyncOnly", "Async Only");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "xt_Both", null, 20, mode: 0);
        await PluginTestHelper.RegisterStepAsync(Admin, type, "xt_Both", null, 40, mode: 1);

        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(3);
    }

    [Fact]
    public async Task Step_on_an_api_message_round_trips_through_export()
    {
        var (zip, step) = await ExportedWithStepAsync();

        await ImportAsync(zip);

        (await CountAsync("""
            SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id AND sdkmessageid = @m
              AND sdkmessagefilterid = @f AND stage = 20 AND mode = 0
            """, ("@id", step), ("@m", SdkMessageCatalog.MessageId("xt_Echo")), ("@f", SdkMessageCatalog.FilterId("xt_Echo", "none"))))
            .Should().Be(1);
        (await ApiRowsAsync()).Should().Be(1 + 3 + 1 + 1);
    }

    [Fact]
    public async Task Step_in_the_file_on_an_api_that_accepts_none_fails_the_import()
    {
        var (zip, _) = await ExportedWithStepAsync();
        zip = Rewrite(zip, "customapis/xt_Echo/customapi.xml", x => Regex.Replace(x,
            "<allowedcustomprocessingsteptype>2</allowedcustomprocessingsteptype>", "<allowedcustomprocessingsteptype>0</allowedcustomprocessingsteptype>"));

        await Refused400Async(() => ImportAsync(zip), "xt_Echo", "accepts no processing step");
        await NothingWrittenAsync();
    }

    [Fact]
    public async Task Dataverse_exported_step_on_an_api_message_fails_the_import()
    {
        var (zip, _) = await ExportedWithStepAsync();
        // A Dataverse export gives the step the message's id in that organization, which xrmverse does not know.
        var dataverseId = Guid.NewGuid();
        zip = Rewrite(zip, "customizations.xml", x => Regex.Replace(x, "<SdkMessageId>[^<]*</SdkMessageId>", $"<SdkMessageId>{{{dataverseId}}}</SdkMessageId>"));

        await Refused400Async(() => ImportAsync(zip), "references the SDK message", dataverseId.ToString("D"));
        await NothingWrittenAsync();
    }
}
