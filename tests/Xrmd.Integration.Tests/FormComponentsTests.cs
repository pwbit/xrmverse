using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1b, task 1: the solution import fills <c>isdefault</c> and <c>formactivationstate</c> of a form, and
/// <c>RetrieveAppComponents</c> and <c>RetrieveUnpublished</c> answer over the SDK, SOAP and the Web API.
/// </summary>
[Collection("real-export")]
public sealed class FormComponentsTests(XrmdFixture fx)
{
    private const string Unique = "op365_OpenProject365";
    private const string Api = "/api/data/v9.2/";
    private const string FirstForm = "f83260a8-d6cf-f111-a05b-7ced8d2d53e4";
    private static readonly string FixturePath = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip");
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Services = "http://schemas.microsoft.com/xrm/2011/Contracts/Services";

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await rd.ReadAsync())
        {
            var values = new object?[rd.FieldCount];
            rd.GetValues(values!);
            rows.Add(values.Select(v => v is DBNull ? null : v).ToArray());
        }
        return rows;
    }

    private async Task ImportAsync(byte[]? file = null)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest
            { CustomizationFile = file ?? File.ReadAllBytes(FixturePath), ImportJobId = Guid.NewGuid() });
    }

    /// <summary>
    /// An app module made over the SDK with two table components (written internally, as the import writes them): the
    /// RetrieveAppComponents tests need an app with components, not the import that usually makes them.
    /// </summary>
    private async Task<Guid> AppAsync()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var app = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("appmodule") { ["name"] = "Components " + run, ["uniquename"] = "new_fc" + run });
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        foreach (var table in new[] { "account", "contact" })
            await TestEntities.InSessionAsync(fx.ConnectionString, s => core.ExecuteCoreAsync(new Microsoft.Xrm.Sdk.Messages.CreateRequest
            {
                Target = new Entity(AppModuleSchema.Component, Guid.NewGuid())
                {
                    ["appmoduleidunique"] = new EntityReference("appmodule", app),
                    ["componenttype"] = new OptionSetValue(AppModuleSchema.ComponentType.Entity),
                    ["objectid"] = store.GetEntity(table)!.MetadataId!.Value,
                    ["rootcomponentbehavior"] = new OptionSetValue(0),
                    ["ismetadata"] = true,
                    ["exempt"] = false,
                },
            }, Caller.Internal, s, default));
        return app;
    }

    /// <summary>A main form of <c>account</c> made over the SDK.</summary>
    private Task<Guid> NewFormAsync() => ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("systemform")
    {
        ["name"] = "Unpublished " + Guid.NewGuid().ToString("N")[..6], ["objecttypecode"] = "account", ["type"] = new OptionSetValue(2),
        ["formxml"] = "<form><tabs /></form>", ["formactivationstate"] = new OptionSetValue(1),
    });

    private static byte[] Edited(Func<string, string> edit)
    {
        using var input = new MemoryStream(File.ReadAllBytes(FixturePath));
        using var output = new MemoryStream();
        using (var zin = new ZipArchive(input, ZipArchiveMode.Read))
        using (var zout = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in zin.Entries)
            {
                var copy = zout.CreateEntry(entry.FullName);
                using var source = entry.Open();
                using var target = copy.Open();
                if (entry.FullName != "customizations.xml") { source.CopyTo(target); continue; }
                using var reader = new StreamReader(source, new UTF8Encoding(false));
                target.Write(new UTF8Encoding(false).GetBytes(edit(reader.ReadToEnd())));
            }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid objectId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    /// <summary>The fixture with the form <see cref="FirstForm"/>'s <c>IsDefault</c> and <c>FormActivationState</c> elements replaced (empty text removes them).</summary>
    private static byte[] WithFirstForm(string isDefault, string activationState) => Edited(xml =>
    {
        const string head = "<formid>{" + FirstForm + "}</formid>";
        var start = xml.IndexOf(head, StringComparison.Ordinal);
        var end = xml.IndexOf("</systemform>", start, StringComparison.Ordinal);
        var form = xml[start..end];
        var edited = form.Replace(head, head + isDefault);
        edited = System.Text.RegularExpressions.Regex.Replace(edited, "<FormActivationState>[^<]*</FormActivationState>", activationState, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
        return xml[..start] + edited + xml[end..];
    });

    private async Task<(bool? IsDefault, int? State)> FormAsync(string id)
    {
        var row = (await RowsAsync($"SELECT isdefault, formactivationstate FROM data.systemform WHERE systemformid = '{id}'")).Should().ContainSingle().Subject;
        return ((bool?)row[0], (int?)row[1]);
    }

    /// <summary>Forms of the export (by id), one of them <see cref="FirstForm"/>.</summary>
    private static readonly string[] AppForms =
        [FirstForm, "2c3360a8-d6cf-f111-a05b-7ced8d2d53e4", "07b6dea0-c4d0-4f22-bffa-4e5b97097bd4", "76681d1a-a4ec-4399-b93c-11267524c9ed"];

    [OpenProject365Fact]
    public async Task Import_reads_isdefault_and_activation_state()
    {
        await ImportAsync(WithFirstForm("<IsDefault>1</IsDefault>", "<FormActivationState>0</FormActivationState>"));

        (await FormAsync(FirstForm)).Should().Be((true, 0));
        foreach (var id in AppForms.Skip(1)) (await FormAsync(id)).Should().Be((false, 1), id);
    }

    [OpenProject365Fact]
    public async Task Reimport_without_the_elements_ends_at_the_defaults()
    {
        await ImportAsync(WithFirstForm("<IsDefault>1</IsDefault>", "<FormActivationState>0</FormActivationState>"));
        (await FormAsync(FirstForm)).Should().Be((true, 0));

        await ImportAsync(WithFirstForm("", ""));

        (await FormAsync(FirstForm)).Should().Be((false, 1));
    }

    [OpenProject365Fact]
    public async Task Open_project_forms_keep_the_defaults()
    {
        await ImportAsync();

        foreach (var id in AppForms) (await FormAsync(id)).Should().Be((false, 1), id);
    }

    private static string AppFilter(Guid app) => $"_appmoduleidunique_value eq {app}";

    private async Task<HashSet<Guid>> ComponentIdsAsync(Guid app)
    {
        var res = await GetAsync($"appmodulecomponents?$filter={AppFilter(app)}&$select=appmodulecomponentid", fx.AdminObjectId);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!["value"]!.AsArray().Select(r => r!["appmodulecomponentid"]!.GetValue<Guid>()).ToHashSet();
    }

    [Fact]
    public async Task RetrieveAppComponents_returns_app_rows()
    {
        var app = await AppAsync();
        var expected = await ComponentIdsAsync(app);
        expected.Should().NotBeEmpty();

        // SDK.
        var sdk = (RetrieveAppComponentsResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new RetrieveAppComponentsRequest { AppModuleId = app });
        sdk.AppComponents.Entities.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(expected);
        sdk.AppComponents.Entities.Should().OnlyContain(e => e.LogicalName == "appmodulecomponent");

        // Web API.
        var res = await GetAsync($"RetrieveAppComponents(AppModuleId={app})", fx.AdminObjectId);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        body["@odata.context"]!.GetValue<string>().Should().Contain("crmbaseentity");
        body["value"]!.AsArray().Select(r => r!["appmodulecomponentid"]!.GetValue<Guid>()).ToHashSet().Should().BeEquivalentTo(expected);

        // SOAP.
        using var req = new HttpRequestMessage(HttpMethod.Post, "XRMServices/2011/Organization.svc/web")
        {
            Content = new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(
                new RetrieveAppComponentsRequest { AppModuleId = app })), Encoding.UTF8, "text/xml"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var soap = await fx.Client.SendAsync(req);
        var soapText = await soap.Content.ReadAsStringAsync();
        soap.StatusCode.Should().Be(HttpStatusCode.OK, soapText);
        var result = XDocument.Parse(soapText).Root!.Element(Soap + "Body")!.Element(Services + "ExecuteResponse")!.Element(Services + "ExecuteResult")!;
        ((string)result.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!).Should().EndWith(":RetrieveAppComponentsResponse");
        foreach (var id in expected) soapText.Should().Contain(id.ToString());
    }

    [Fact]
    public async Task RetrieveAppComponents_needs_read_on_appmodule()
    {
        var app = await AppAsync();
        var oid = Guid.NewGuid();
        await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid); // no role

        var res = await GetAsync($"RetrieveAppComponents(AppModuleId={app})", oid);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        var act = async () => await ServiceFor(fx, oid).ExecuteAsync(new RetrieveAppComponentsRequest { AppModuleId = app });
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task RetrieveAppComponents_needs_only_read_on_appmodule()
    {
        var app = await AppAsync();
        var expected = await ComponentIdsAsync(app);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        var read = PrivilegeNames.For(AccessRights.ReadAccess, fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity("appmodule")!);
        await GrantAsync(fx, user, team: false, (read, DepthMask.Global)); // nothing on appmodulecomponent

        var denied = await GetAsync($"appmodulecomponents?$select=appmodulecomponentid", oid);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the role gives no read on appmodulecomponent");

        var res = await GetAsync($"RetrieveAppComponents(AppModuleId={app})", oid);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonNode.Parse(text)!["value"]!.AsArray().Select(r => r!["appmodulecomponentid"]!.GetValue<Guid>()).ToHashSet().Should().BeEquivalentTo(expected);
        var sdk = (RetrieveAppComponentsResponse)await ServiceFor(fx, oid).ExecuteAsync(new RetrieveAppComponentsRequest { AppModuleId = app });
        sdk.AppComponents.Entities.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task RetrieveUnpublished_returns_published_row()
    {
        var id = await NewFormAsync();

        var sdk = (RetrieveUnpublishedResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new RetrieveUnpublishedRequest { Target = new EntityReference("systemform", id), ColumnSet = new ColumnSet("name", "formactivationstate") });
        sdk.Entity.Id.Should().Be(id);
        sdk.Entity["formactivationstate"].Should().BeOfType<OptionSetValue>();
        sdk.Entity.Attributes.Keys.Should().BeSubsetOf(["name", "formactivationstate", "systemformid"], "only the ColumnSet comes back");
        sdk.Entity.Contains("formxml").Should().BeFalse();

        var res = await GetAsync($"systemforms({id})/Microsoft.Dynamics.CRM.RetrieveUnpublished()?$select=name", fx.AdminObjectId);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        body["name"]!.GetValue<string>().Should().Be(sdk.Entity.GetAttributeValue<string>("name"));
        body.AsObject().Select(p => p.Key).Should().NotContain("formxml", "only $select comes back");

        var missing = await GetAsync($"systemforms({Guid.NewGuid()})/Microsoft.Dynamics.CRM.RetrieveUnpublished()", fx.AdminObjectId);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound, await missing.Content.ReadAsStringAsync());
        var absent = async () => await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new RetrieveUnpublishedRequest { Target = new EntityReference("systemform", Guid.NewGuid()), ColumnSet = new ColumnSet("name") });
        (await absent.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task RetrieveUnpublished_refuses_other_tables()
    {
        var account = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("account") { ["name"] = "Unpublished " + Guid.NewGuid().ToString("N")[..6] });

        var act = async () => await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new RetrieveUnpublishedRequest { Target = new EntityReference("account", account), ColumnSet = new ColumnSet("name") });
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);

        var res = await GetAsync($"accounts({account})/Microsoft.Dynamics.CRM.RetrieveUnpublished()", fx.AdminObjectId);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("0x80040203");
    }
}

/// <summary>
/// Plan 3.5 UI-1b, task 1: an organization seeded before the two <c>systemform</c> columns (the plan 3.2 upgrade path of a seeded
/// table) gets them at startup, and its existing forms get the defaults (not default, active).
/// </summary>
[Collection("xrmd")]
public sealed class SystemFormSeedUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        _database = "xrmd_form_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, _database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await TestProvider.DropDatabaseAsync(fx.ConnectionString, _database);
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    [Fact]
    public async Task Existing_org_gets_systemform_columns_at_startup()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        await ScalarAsync("DELETE FROM meta.attribute WHERE entity_logical_name = 'systemform' AND logical_name IN ('isdefault', 'formactivationstate')");
        await ScalarAsync(TestSql.Dialect.DropColumns("data.systemform", ["isdefault", "formactivationstate"]));
        var stale = await NewStoreAsync();
        stale.GetEntity("systemform")!.Attributes.Should().NotContain(a => a.LogicalName == "isdefault");
        // A form made before the columns.
        await ScalarAsync("""
            INSERT INTO data.systemform (systemformid, name, objecttypecode, type, createdon, modifiedon)
            VALUES (NEWID(), N'old form', N'account', 2, SYSUTCDATETIME(), SYSUTCDATETIME())
            """);

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        foreach (var store in new[] { stale, await NewStoreAsync() })
            foreach (var column in new[] { "isdefault", "formactivationstate" })
                store.GetEntity("systemform")!.Attributes.Should().Contain(a => a.LogicalName == column, column);
        Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM data.systemform WHERE isdefault IS NULL OR formactivationstate IS NULL")).Should().Be(0);
        (await ScalarAsync("SELECT formactivationstate FROM data.systemform WHERE name = N'old form'")).Should().Be(1);
        (await ScalarAsync("SELECT CAST(isdefault AS int) FROM data.systemform WHERE name = N'old form'")).Should().Be(0);
    }
}
