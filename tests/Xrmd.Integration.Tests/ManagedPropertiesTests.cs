using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
using Xrmd.Pipeline.Registration;
using Xrmd.Solution;
using Xrmd.Solution.Layers;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.6, task 5: the managed properties honoured (ruling M13) and the rules of managed components and solutions (ruling
/// M14). Each flag is refused through the SDK and the Web API on a component of a managed solution and allowed on the same
/// file imported unmanaged; the layer operations (restore, the managed import's own writes) bypass the rules (ruling R2).
/// </summary>
[Collection("solution-import")]
public sealed class ManagedPropertiesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Table = ManagedTestSolution.Table;
    private const string Holder = "xm_holder";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public Task DisposeAsync() => CleanAsync();

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("""
                IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
                DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;
                DELETE FROM data.systemform WHERE objecttypecode = N'xm_item' AND name LIKE N'xm[_]%';
                DELETE FROM data.savedquery WHERE returnedtypecode = N'xm_item' AND name LIKE N'xm[_]%';
                DELETE FROM data.roleprivileges WHERE roleid IN (SELECT roleid FROM data.role WHERE name LIKE N'xm[_]%');
                DELETE FROM data.role WHERE name LIKE N'xm[_]%';
                DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
                DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
                DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetRelationship("xm_account_xm_item") is not null) await Store.DeleteRelationshipAsync(s, "xm_account_xm_item", default);
            if (Store.GetEntity("account")!.Attributes.Any(a => a.LogicalName == "xm_itemid")) await Store.DeleteAttributeAsync(s, "account", "xm_itemid", default);
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private Task<OrganizationResponse> ImportAsync(ManagedSpec spec) => ImportAsync(ManagedTestSolution.Build(spec));

    private Task<OrganizationResponse> ImportAsync(byte[] file) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

    /// <summary>V1 with the managed properties of <c>xm_code</c>, of the table and of the view and script set.</summary>
    private static ManagedSpec Spec(bool managed, TestManagedProps? code = null, TestManagedProps? table = null, TestManagedProps? view = null,
        TestManagedProps? script = null, TestManagedProps? step = null, TestManagedProps? optionSet = null) =>
        ManagedTestSolution.V1 with
        {
            Managed = managed,
            Tables = [new(Table, "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Code", Props: code),
                new("xm_legacy", "Legacy"),
            ], table)],
            Views = [new("Active Items", Table, ["xm_name", "xm_code"], view)],
            WebResources = [new("xm_/scripts/item.js", "// xrmverse managed test script\n", script)],
            OptionSets = [new("xm_status", "Status", [(100000000, "Open"), (100000001, "Closed")], optionSet)],
            Steps = [new(Table, "Update", ManagedTestSolution.StepPlugin, 40, "pb_pipelog", step)],
        };

    /// <summary>The step of the specs.</summary>
    private static Guid StepId => ManagedTestSolution.IdOf("step", $"{ManagedTestSolution.StepPlugin}:Update:{Table}");

    /// <summary>Lays a Managed layer whose element <paramref name="element"/> says <c>IsCustomizable</c> 0 on a component
    /// (for the kinds the test files cannot lock).</summary>
    private async Task LockAsync(int type, Guid id, string element)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            xml = $"<ImportExportXml><{element}><IsCustomizable>0</IsCustomizable></{element}></ImportExportXml>",
            files = new Dictionary<string, string>(),
        });
        await TestEntities.InSessionAsync(fx.ConnectionString, s => fx.App.Services.GetRequiredService<ComponentLayerStore>().UpsertAsync(s,
            new ComponentLayer(type, id, LockingSolution, ComponentLayerStore.Order(9, 0), LayerKind.Managed, payload), default));
    }

    /// <summary>The owner of the layers <see cref="LockAsync"/> lays (no solution row: the refusal names its id).</summary>
    private static readonly Guid LockingSolution = new("7b3f6c2e-1d4a-4f0e-9c5b-2a8e6d1f0c33");

    /// <summary>Runs a core message as <see cref="Caller.Internal"/> (a layer operation), committed.</summary>
    private async Task InternalAsync(OrganizationRequest request)
    {
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => core.ExecuteCoreAsync(request, Caller.Internal, s, default));
    }

    /// <summary>A built solution file with its <c>customizations.xml</c> edited.</summary>
    private static byte[] Edit(ManagedSpec spec, Action<XDocument> customizations)
    {
        var (solutionXml, customizationsXml, files) = SolutionZip.Read(ManagedTestSolution.Build(spec));
        customizations(customizationsXml);
        var kept = files.Where(f => !f.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        return SolutionZip.Write(solutionXml, customizationsXml, kept);
    }

    /// <summary>The expected outcome of a call: refused (400 naming <paramref name="named"/>) when <paramref name="managed"/>.</summary>
    private static async Task RefusedIfAsync(bool managed, Func<Task> act, params string[] named)
    {
        if (!managed)
        {
            await act();
            return;
        }
        var fault = await FaultAsync(act);
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        foreach (var name in named) fault.Message.Should().Contain(name);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdFaultException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The call succeeded; a fault was expected.");
    }

    /// <summary>Runs <paramref name="sdk"/> (or <paramref name="web"/>) and checks the outcome: refused naming the component
    /// and the solution when managed, applied otherwise.</summary>
    private static async Task ExpectAsync(bool managed, bool viaWebApi, Func<Task> sdk, Func<Task<HttpResponseMessage>> web,
        params string[] named)
    {
        if (viaWebApi)
        {
            var res = await web();
            var body = await res.Content.ReadAsStringAsync();
            if (!managed)
            {
                res.IsSuccessStatusCode.Should().BeTrue(body);
                return;
            }
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("0x80040203");
            foreach (var name in named) body.Should().Contain(name);
            return;
        }
        if (!managed)
        {
            await sdk();
            return;
        }
        var fault = await FaultAsync(sdk);
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        foreach (var name in named) fault.Message.Should().Contain(name);
    }

    private AttributeMetadata Column(string column) => Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == column);

    private static string? Text(Label? label) => label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels.FirstOrDefault()?.Label;

    private static Label Label(string text) => new(text, 1033);

    private Task<OrganizationResponse> RelabelAsync(string column, string label) =>
        Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = Table, Attribute = new StringAttributeMetadata { LogicalName = column, DisplayName = Label(label) },
        });

    private Task<HttpResponseMessage> WebRelabelAsync(string column, string label) =>
        SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='{column}')", $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "{{column}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "{{label}}", "LanguageCode": 1033 } ] } }
            """);

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<Guid> SolutionIdAsync(string uniqueName) =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", uniqueName)))!;

    // ---- M13: the flags -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NonCustomizable_column_refuses_change(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, code: new(IsCustomizable: false)));

        await ExpectAsync(managed, viaWebApi, () => RelabelAsync("xm_code", "Changed"), () => WebRelabelAsync("xm_code", "Changed"),
            "xm_code", "xm_base", "IsCustomizable");

        Text(Column("xm_code").DisplayName).Should().Be(managed ? "Code" : "Changed");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NonCustomizable_table_refuses_new_column(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, table: new(IsCustomizable: false)));

        await ExpectAsync(managed, viaWebApi,
            () => Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = Table,
                Attribute = new StringAttributeMetadata { LogicalName = "xm_new", SchemaName = "xm_New", MaxLength = 50, DisplayName = Label("New") },
            }),
            () => SendAsync(HttpMethod.Post, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes", """
                { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "xm_new", "SchemaName": "xm_New",
                  "MaxLength": 50, "DisplayName": { "LocalizedLabels": [ { "Label": "New", "LanguageCode": 1033 } ] } }
                """),
            "xm_new", Table, "xm_base");

        Store.GetEntity(Table)!.Attributes.Any(a => a.LogicalName == "xm_new").Should().Be(!managed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NonRenameable_column_refuses_new_name(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, code: new(IsRenameable: false)));

        await ExpectAsync(managed, viaWebApi, () => RelabelAsync("xm_code", "Renamed"), () => WebRelabelAsync("xm_code", "Renamed"),
            "xm_code", "xm_base", "IsRenameable");

        Text(Column("xm_code").DisplayName).Should().Be(managed ? "Code" : "Renamed");
        // Another change than the name is allowed (the column stays customizable).
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = Table, Attribute = new StringAttributeMetadata { LogicalName = "xm_code", Description = Label("Described") },
        });
        Text(Column("xm_code").Description).Should().Be("Described");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonRenameable_table_refuses_new_name(bool managed)
    {
        await ImportAsync(Spec(managed, table: new(IsRenameable: false)));

        await ExpectAsync(managed, viaWebApi: false,
            () => Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = Table, DisplayName = Label("Thing") } }),
            () => throw new NotSupportedException(), Table, "xm_base", "IsRenameable");

        Text(Store.GetEntity(Table)!.DisplayName).Should().Be(managed ? "Item" : "Thing");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Fixed_requirement_level_refuses_change(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, code: new(CanModifyRequirementLevelSettings: false)));

        await ExpectAsync(managed, viaWebApi,
            () => Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = Table,
                Attribute = new StringAttributeMetadata
                {
                    LogicalName = "xm_code", RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
                },
            }),
            () => SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='xm_code')", """
                { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "xm_code",
                  "RequiredLevel": { "Value": "ApplicationRequired" } }
                """),
            "xm_code", "xm_base", "CanModifyRequirementLevelSettings");

        Column("xm_code").RequiredLevel.Value.Should().Be(managed ? AttributeRequiredLevel.None : AttributeRequiredLevel.ApplicationRequired);
        // A relabel stays allowed.
        await RelabelAsync("xm_code", "Relabelled");
        Text(Column("xm_code").DisplayName).Should().Be("Relabelled");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Undeletable_view_refuses_delete(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, view: new(CanBeDeleted: false)));
        var view = ManagedTestSolution.IdOf("view", "Active Items");

        await ExpectAsync(managed, viaWebApi, () => Admin.DeleteAsync("savedquery", view),
            () => SendAsync(HttpMethod.Delete, $"{Api}savedqueries({view})"), "savedquery", "xm_base", "CanBeDeleted");

        (await ScalarAsync("SELECT COUNT(*) FROM data.savedquery WHERE savedqueryid = @id", ("@id", view))).Should().Be(managed ? 1 : 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NonCustomizable_row_component_refuses_update(bool managed, bool viaWebApi)
    {
        await ImportAsync(Spec(managed, script: new(IsCustomizable: false)));
        var script = ManagedTestSolution.IdOf("webresource", "xm_/scripts/item.js");

        await ExpectAsync(managed, viaWebApi,
            () => Admin.UpdateAsync(new Entity("webresource", script) { ["description"] = "Changed" }),
            () => SendAsync(HttpMethod.Patch, $"{Api}webresourceset({script})", """{ "description": "Changed" }"""),
            "webresource", "xm_base", "IsCustomizable");

        (await ScalarAsync("SELECT description FROM data.webresource WHERE webresourceid = @id", ("@id", script)))
            .Should().Be(managed ? "The test script xm_/scripts/item.js." : "Changed");
    }

    [Fact]
    public async Task Unmanaged_import_over_noncustomizable_component_is_refused()
    {
        await ImportAsync(Spec(true, code: new(IsCustomizable: false)));

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.Other with { Managed = false }));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("xm_base").And.Contain("IsCustomizable");
        Text(Column("xm_code").DisplayName).Should().Be("Code", "the whole import is rolled back");
        (await ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_other'")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmanaged_import_renaming_nonrenameable_column_is_refused(bool managed)
    {
        await ImportAsync(Spec(managed, code: new(IsRenameable: false)));

        // xm_other relabels xm_code "Code (other)".
        await RefusedIfAsync(managed, () => ImportAsync(ManagedTestSolution.Other with { Managed = false }), "xm_code", "xm_base", "IsRenameable");

        Text(Column("xm_code").DisplayName).Should().Be(managed ? "Code" : "Code (other)");
    }

    /// <summary>
    /// <paramref name="spec"/> built with a money column <c>xm_amount</c> (its <c>IsRenameable</c> as given) and its <c>xm_amount_base</c>
    /// labelled <paramref name="baseLabel"/> on the table.
    /// </summary>
    private static byte[] WithMoney(ManagedSpec spec, string baseLabel, bool renameable) => Edit(spec, c =>
    {
        var code = c.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xm_code");
        XElement Money(string name, string physical, string label)
        {
            var money = new XElement(code);
            money.SetAttributeValue("PhysicalName", physical);
            money.Element("Type")!.Value = "money";
            money.Element("Name")!.Value = name;
            money.Element("LogicalName")!.Value = name;
            money.Element("IsRenameable")!.Value = renameable ? "1" : "0";
            foreach (var element in new[] { "Format", "MaxLength", "Length" }) money.Element(element)?.Remove();
            money.Element("displaynames")!.Element("displayname")!.SetAttributeValue("description", label);
            return money;
        }
        code.AddAfterSelf(Money("xm_amount", "xm_Amount", "Amount"), Money("xm_amount_base", "xm_amount_Base", baseLabel));
    });

    /// <summary>
    /// Plan 2.10, task 2 (review I1): the labels of a money <c>_base</c> follow its money column's managed properties. An unmanaged
    /// import that relabels the <c>_base</c> of a managed, not renameable money column is refused; over an unmanaged one it applies.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmanaged_import_relabelling_base_of_nonrenameable_money_is_refused(bool managed)
    {
        await ImportAsync(WithMoney(Spec(managed), "Amount (Base)", renameable: false));
        Text(Column("xm_amount_base").DisplayName).Should().Be("Amount (Base)");

        var other = ManagedTestSolution.Other with
        {
            Managed = false,
            Tables = [new(Table, "Item", [new("xm_name", "Name", PrimaryName: true), new("xm_code", "Code")])],
        };
        await RefusedIfAsync(managed, () => ImportAsync(WithMoney(other, "Amount (other base)", renameable: true)),
            "xm_amount_base", "xm_base", "IsRenameable");

        Text(Column("xm_amount_base").DisplayName).Should().Be(managed ? "Amount (Base)" : "Amount (other base)");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmanaged_import_changing_fixed_requirement_level_is_refused(bool managed)
    {
        await ImportAsync(Spec(managed, code: new(CanModifyRequirementLevelSettings: false)));
        var file = Edit(ManagedTestSolution.Other with { Managed = false }, c =>
            c.Descendants("attribute").Single(a => (string?)a.Element("LogicalName") == "xm_code").Element("RequiredLevel")!.Value = "required");

        await RefusedIfAsync(managed, () => ImportAsync(file), "xm_code", "xm_base", "CanModifyRequirementLevelSettings");

        Column("xm_code").RequiredLevel.Value.Should().Be(managed ? AttributeRequiredLevel.None : AttributeRequiredLevel.ApplicationRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonCustomizable_role_refuses_privilege_changes(bool managed)
    {
        await ImportAsync(Spec(managed));
        var role = await CreateRoleAsync(fx, "xm_locked_role");
        if (managed) await LockAsync(ComponentType.Role, role, "Role");
        var privilege = (Guid)(await ScalarAsync("SELECT privilegeid FROM data.privilege WHERE name = 'prvReadAccount'"))!;
        RolePrivilege[] privileges = [new() { PrivilegeId = privilege, Depth = PrivilegeDepth.Basic }];
        if (managed) await InternalAsync(new AddPrivilegesRoleRequest { RoleId = role, Privileges = privileges });

        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new AddPrivilegesRoleRequest { RoleId = role, Privileges = privileges }), "role", "IsCustomizable");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new ReplacePrivilegesRoleRequest { RoleId = role, Privileges = privileges }), "role", "IsCustomizable");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new RemovePrivilegeRoleRequest { RoleId = role, PrivilegeId = privilege }), "role", "IsCustomizable");

        (await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @r", ("@r", role))).Should().Be(managed ? 1 : 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Child_rows_of_noncustomizable_step_are_refused(bool managed)
    {
        await ImportAsync(Spec(managed, step: new(IsCustomizable: false)));
        Entity Image(string alias) => new("sdkmessageprocessingstepimage")
        {
            ["name"] = alias, ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", StepId),
            ["imagetype"] = new OptionSetValue(0), ["entityalias"] = alias, ["messagepropertyname"] = "Target",
        };
        var existing = Guid.NewGuid();
        await InternalAsync(new CreateRequest { Target = new Entity("sdkmessageprocessingstepimage", existing) { Attributes = Image("pre").Attributes } });

        await RefusedIfAsync(managed, () => Admin.CreateAsync(Image("pre2")), "sdkmessageprocessingstep", "IsCustomizable");
        await RefusedIfAsync(managed, () => Admin.UpdateAsync(new Entity("sdkmessageprocessingstepimage", existing) { ["entityalias"] = "changed" }),
            "sdkmessageprocessingstep", "IsCustomizable");
        await RefusedIfAsync(managed, () => Admin.DeleteAsync("sdkmessageprocessingstepimage", existing), "sdkmessageprocessingstep", "IsCustomizable");

        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepid = @s", ("@s", StepId)))
            .Should().Be(1, "managed: only the image made internally; unmanaged: pre2 created, the other deleted");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Child_row_moved_under_noncustomizable_step_is_refused(bool managed)
    {
        await ImportAsync(Spec(managed, step: new(IsCustomizable: false)));
        // A second, unmanaged step (no layer) with an image, which is then pointed at the step of the solution.
        var other = await PluginTestHelper.RegisterStepAsync(Admin, ManagedTestSolution.IdOf("plugintype", ManagedTestSolution.StepPlugin),
            "Update", Table, 20);
        var image = await PluginTestHelper.RegisterImageAsync(Admin, other, 0, "pre");

        await RefusedIfAsync(managed, () => Admin.UpdateAsync(new Entity("sdkmessageprocessingstepimage", image)
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", StepId),
        }), "sdkmessageprocessingstep", "IsCustomizable");

        (await ScalarAsync("SELECT sdkmessageprocessingstepid FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepimageid = @i", ("@i", image)))
            .Should().Be(managed ? other : StepId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonCustomizable_global_option_set_refuses_changes(bool managed)
    {
        await ImportAsync(Spec(managed, optionSet: new(IsCustomizable: false)));

        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new UpdateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata { Name = "xm_status", DisplayName = Label("Changed") },
        }), "xm_status", "xm_base", "IsCustomizable");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new InsertOptionValueRequest { OptionSetName = "xm_status", Label = Label("Pending") }),
            "xm_status", "xm_base", "IsCustomizable");

        ((OptionSetMetadata)Store.GetOptionSet("xm_status")!).Options.Should().HaveCount(managed ? 2 : 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonCustomizable_local_column_refuses_new_option(bool managed)
    {
        await ImportAsync(Spec(managed));
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = Table,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "xm_kind", SchemaName = "xm_Kind", DisplayName = Label("Kind"),
                OptionSet = new OptionSetMetadata { IsGlobal = false, Options = { new OptionMetadata(Label("A"), 100000000) } },
            },
        });
        if (managed) await LockAsync(ComponentType.Attribute, Column("xm_kind").MetadataId!.Value, "attribute");

        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            EntityLogicalName = Table, AttributeLogicalName = "xm_kind", Label = Label("B"),
        }), "xm_kind", "IsCustomizable");

        ((PicklistAttributeMetadata)Column("xm_kind")).OptionSet.Options.Should().HaveCount(managed ? 1 : 2);
    }

    /// <summary>Final review I4: a table delivered with CanCreateAttributes 0 (but customizable) takes no new column or relationship.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanCreateAttributes_false_refuses_new_columns_and_relationships(bool managed)
    {
        await ImportAsync(Spec(managed, table: new(CanCreateAttributes: false)));

        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = Table,
            Attribute = new StringAttributeMetadata { LogicalName = "xm_new", SchemaName = "xm_New", MaxLength = 50, DisplayName = Label("New") },
        }), "xm_new", Table, "xm_base", "CanCreateAttributes");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = "xm_account_item", ReferencedEntity = Table, ReferencingEntity = "account" },
            Lookup = new LookupAttributeMetadata { LogicalName = "xm_itemid", SchemaName = "xm_ItemId", DisplayName = Label("Item") },
        }), "xm_account_item", Table, "xm_base", "CanCreateAttributes");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            IntersectEntitySchemaName = "xm_account_xm_item",
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = "xm_account_xm_item", Entity1LogicalName = "account", Entity2LogicalName = Table,
            },
        }), "xm_account_xm_item", Table, "xm_base", "CanCreateAttributes");

        Store.GetEntity(Table)!.Attributes.Any(a => a.LogicalName == "xm_new").Should().Be(!managed);
        (Store.GetRelationship("xm_account_item") is not null).Should().Be(!managed);
        (Store.GetRelationship("xm_account_xm_item") is not null).Should().Be(!managed);
        // The table itself stays customizable.
        await RelabelAsync("xm_code", "Changed");
    }

    /// <summary>Final review I4: an unmanaged import that adds a column to a table delivered with CanCreateAttributes 0 is refused whole.</summary>
    [Fact]
    public async Task Unmanaged_import_adding_column_to_CanCreateAttributes_false_table_is_refused()
    {
        await ImportAsync(Spec(true, table: new(CanCreateAttributes: false)));
        var other = ManagedTestSolution.Other with
        {
            Managed = false,
            Tables = [new(Table, "Item", [new("xm_name", "Name", PrimaryName: true), new("xm_code", "Code"), new("xm_added", "Added")])],
        };

        var fault = await FaultAsync(() => ImportAsync(other));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_added").And.Contain("CanCreateAttributes").And.Contain("xm_base");
        Store.GetEntity(Table)!.Attributes.Should().NotContain(a => a.LogicalName == "xm_added");
        (await ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_other'")).Should().Be(0);
    }

    /// <summary>Final review I4: forms and views of a table delivered with CanCreateForms / CanCreateViews 0 are not created.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanCreateForms_and_CanCreateViews_false_refuse_new_forms_and_views(bool managed)
    {
        await ImportAsync(Spec(managed, table: new(CanCreateForms: false, CanCreateViews: false)));

        await RefusedIfAsync(managed, () => Admin.CreateAsync(new Entity("systemform")
        {
            ["name"] = "xm_form", ["objecttypecode"] = Table, ["type"] = new OptionSetValue(2), ["formxml"] = "<form><tabs /></form>",
        }), "xm_form", Table, "xm_base", "CanCreateForms");
        await RefusedIfAsync(managed, () => Admin.CreateAsync(new Entity("savedquery")
        {
            ["name"] = "xm_view", ["returnedtypecode"] = Table, ["querytype"] = 0, ["isdefault"] = false,
            ["fetchxml"] = $"<fetch><entity name=\"{Table}\"><attribute name=\"xm_name\" /></entity></fetch>",
            ["layoutxml"] = $"<grid name=\"resultset\" object=\"10000\" jump=\"xm_name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"xm_itemid\"><cell name=\"xm_name\" width=\"300\" /></row></grid>",
        }), "xm_view", Table, "xm_base", "CanCreateViews");

        (await ScalarAsync("SELECT COUNT(*) FROM data.systemform WHERE name = N'xm_form'")).Should().Be(managed ? 0 : 1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.savedquery WHERE name = N'xm_view'")).Should().Be(managed ? 0 : 1);
        // A form of another table is not concerned.
        var form = await Admin.CreateAsync(new Entity("systemform")
        {
            ["name"] = "xm_account_form", ["objecttypecode"] = "account", ["type"] = new OptionSetValue(2), ["formxml"] = "<form><tabs /></form>",
        });
        await Admin.DeleteAsync("systemform", form);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonCustomizable_table_refuses_new_relationships(bool managed)
    {
        await ImportAsync(Spec(managed, table: new(IsCustomizable: false)));

        // The locked table on the referencing side, then on the referenced side, then in a many-to-many.
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = "xm_item_account", ReferencedEntity = "account", ReferencingEntity = Table },
            Lookup = new LookupAttributeMetadata { LogicalName = "xm_accountid", SchemaName = "xm_AccountId", DisplayName = Label("Account") },
        }), "xm_item_account", Table, "xm_base");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = "xm_account_item", ReferencedEntity = Table, ReferencingEntity = "account" },
            Lookup = new LookupAttributeMetadata { LogicalName = "xm_itemid", SchemaName = "xm_ItemId", DisplayName = Label("Item") },
        }), "xm_account_item", Table, "xm_base");
        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            IntersectEntitySchemaName = "xm_account_xm_item",
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = "xm_account_xm_item", Entity1LogicalName = "account", Entity2LogicalName = Table,
            },
        }), "xm_account_xm_item", Table, "xm_base");

        (Store.GetRelationship("xm_item_account") is not null).Should().Be(!managed);
        (Store.GetRelationship("xm_account_item") is not null).Should().Be(!managed);
        (Store.GetRelationship("xm_account_xm_item") is not null).Should().Be(!managed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_of_managed_relationship_is_refused(bool managed)
    {
        const string relationship = "xv_project_xv_task_ProjectId";
        await ImportAsync(SolutionFixtureZip.Build(solution: d => d.Descendants("Managed").Single().Value = managed ? "1" : "0"));

        await RefusedIfAsync(managed, () => Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = relationship }), relationship, "uninstall");

        (Store.GetRelationship(relationship) is not null).Should().Be(managed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonRenameable_table_refuses_new_name_through_Web_API(bool managed)
    {
        await ImportAsync(Spec(managed, table: new(IsRenameable: false)));

        await ExpectAsync(managed, viaWebApi: true, () => throw new NotSupportedException(),
            () => SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')", $$"""
                { "LogicalName": "{{Table}}", "DisplayName": { "LocalizedLabels": [ { "Label": "Thing", "LanguageCode": 1033 } ] } }
                """), Table, "xm_base", "IsRenameable");

        Text(Store.GetEntity(Table)!.DisplayName).Should().Be(managed ? "Item" : "Thing");
    }

    [Fact]
    public async Task Internal_restore_bypasses_flags()
    {
        await ImportAsync(Spec(true, code: new(IsCustomizable: false, IsRenameable: false)));
        var file = ManagedTestSolution.Build(Spec(true, code: new(IsCustomizable: false, IsRenameable: false)) with
        {
            Tables = [new(Table, "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Restored code", Props: new(IsCustomizable: false, IsRenameable: false)),
                new("xm_legacy", "Legacy"),
            ])],
        });
        var (solution, customizations, files) = SolutionZip.Read(file);
        var package = CustomizationsReader.Read(solution, customizations, files);
        var layer = new ComponentLayer(ComponentType.Attribute, Column("xm_code").MetadataId!.Value, await SolutionIdAsync("xm_base"),
            ComponentLayerStore.Order(1, 0), LayerKind.Managed, LayerPayload.From(package, ComponentType.Attribute, $"{Table}.xm_code"));

        using var scope = fx.App.Services.CreateScope();
        var restorer = scope.ServiceProvider.GetRequiredService<LayerRestorer>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => restorer.RestoreAsync(s, [layer], default));

        Text(Column("xm_code").DisplayName).Should().Be("Restored code");

        // An internal metadata write is not refused either.
        await fx.App.Services.GetRequiredService<MetadataOperations>().ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = Table, Attribute = new StringAttributeMetadata { LogicalName = "xm_code", DisplayName = Label("Internal code") },
        }, Caller.Internal, null, default);
        Text(Column("xm_code").DisplayName).Should().Be("Internal code");
    }

    [Fact]
    public async Task Managed_update_applies_to_locked_components()
    {
        var locked = new TestManagedProps(IsCustomizable: false, IsRenameable: false);
        await ImportAsync(Spec(true, code: locked, view: locked, script: locked));

        // The managed import's own writes (metadata, rows and the solution row) bypass the flags (ruling R2).
        await ImportAsync(Spec(true, code: locked, view: locked, script: locked) with
        {
            Version = "1.1.0.0",
            Tables = [new(Table, "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Code v1.1", Props: locked),
                new("xm_legacy", "Legacy"),
            ])],
            WebResources = [new("xm_/scripts/item.js", "// version 1.1\n", locked)],
        });

        Text(Column("xm_code").DisplayName).Should().Be("Code v1.1");
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be("1.1.0.0");
    }

    // ---- M14: managed components and solutions ------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_of_managed_column_is_refused(bool viaWebApi)
    {
        await ImportAsync(ManagedTestSolution.V1);

        await ExpectAsync(true, viaWebApi,
            () => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = Table, LogicalName = "xm_legacy" }),
            () => SendAsync(HttpMethod.Delete, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='xm_legacy')"),
            "xm_legacy", "xm_base", "uninstall");

        Store.GetEntity(Table)!.Attributes.Should().Contain(a => a.LogicalName == "xm_legacy");
    }

    [Fact]
    public async Task Delete_of_other_managed_components_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);

        foreach (var (name, act) in new (string, Func<Task>)[]
                 {
                     (Table, () => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = Table })),
                     ("xm_status", () => Admin.ExecuteAsync(new DeleteOptionSetRequest { Name = "xm_status" })),
                     ("role", () => Admin.DeleteAsync("role", ManagedTestSolution.IdOf("role", "xm_reader"))),
                     ("savedquery", () => Admin.DeleteAsync("savedquery", ManagedTestSolution.IdOf("view", "Active Items"))),
                 })
        {
            var fault = await FaultAsync(act);
            fault.HttpStatus.Should().Be(400, name);
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, name);
            fault.Message.Should().Contain(name).And.Contain("xm_base").And.Contain("uninstall");
        }

        Store.GetEntity(Table).Should().NotBeNull();
        Store.GetOptionSet("xm_status").Should().NotBeNull();
        (await ScalarAsync("SELECT COUNT(*) FROM data.role WHERE name = N'xm_reader'")).Should().Be(1);
    }

    [Fact]
    public async Task Delete_of_unmanaged_column_is_allowed()
    {
        await ImportAsync(ManagedTestSolution.V1 with { Managed = false });

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = Table, LogicalName = "xm_legacy" });

        Store.GetEntity(Table)!.Attributes.Should().NotContain(a => a.LogicalName == "xm_legacy");
    }

    [Fact]
    public async Task Add_component_to_managed_solution_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var table = Store.GetEntity(Table)!.MetadataId!.Value;

        var add = await FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = Store.GetEntity("account")!.MetadataId!.Value, ComponentType = ComponentType.Entity, SolutionUniqueName = "xm_base",
        }));
        var remove = await FaultAsync(() => Admin.ExecuteAsync(new RemoveSolutionComponentRequest
        {
            ComponentId = table, ComponentType = ComponentType.Entity, SolutionUniqueName = "xm_base",
        }));

        foreach (var fault in new[] { add, remove })
        {
            fault.HttpStatus.Should().Be(400);
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            fault.Message.Should().Contain("xm_base").And.Contain("managed");
        }
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = N'xm_base' AND sc.objectid = @id
            """, ("@id", table))).Should().Be(1);
    }

    [Fact]
    public async Task Managed_component_into_unmanaged_solution_is_allowed()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var publisher = (Guid)(await ScalarAsync("SELECT publisherid FROM data.publisher WHERE uniquename = N'xrmversemanaged'"))!;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Holder, ["friendlyname"] = Holder, ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", publisher),
        });

        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = Store.GetEntity(Table)!.MetadataId!.Value, ComponentType = ComponentType.Entity, SolutionUniqueName = Holder,
        });

        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid WHERE s.uniquename = @n
            """, ("@n", Holder))).Should().Be(1);
        // Its row and its export are those of any unmanaged solution.
        await Admin.UpdateAsync(new Entity("solution", await SolutionIdAsync(Holder)) { ["description"] = "Holds a managed table" });
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Holder })).ExportSolutionFile;
        SolutionZip.Read(zip).Customizations.Descendants("Entity").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Export_of_managed_solution_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "xm_base" }));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("xm_base").And.Contain("managed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_of_managed_solution_row_is_refused(bool viaWebApi)
    {
        await ImportAsync(ManagedTestSolution.V1);
        var id = await SolutionIdAsync("xm_base");

        await ExpectAsync(true, viaWebApi,
            () => Admin.UpdateAsync(new Entity("solution", id) { ["description"] = "Changed" }),
            () => SendAsync(HttpMethod.Patch, $"{Api}solutions({id})", """{ "description": "Changed" }"""),
            "xm_base", "managed");

        (await ScalarAsync("SELECT description FROM data.solution WHERE solutionid = @id", ("@id", id))).Should().NotBe("Changed");
        // The import still updates its own row.
        await ImportAsync(ManagedTestSolution.V1 with { Version = "1.0.1.0" });
        (await ScalarAsync("SELECT version FROM data.solution WHERE solutionid = @id", ("@id", id))).Should().Be("1.0.1.0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetrieveAttribute_reports_IsManaged(bool managed)
    {
        await ImportAsync(ManagedTestSolution.V1 with { Managed = managed });

        var attribute = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = Table, LogicalName = "xm_code" })).AttributeMetadata;
        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = Table, EntityFilters = EntityFilters.Entity })).EntityMetadata;
        var optionSet = ((RetrieveOptionSetResponse)await Admin.ExecuteAsync(new RetrieveOptionSetRequest { Name = "xm_status" })).OptionSetMetadata;

        (attribute.IsManaged ?? false).Should().Be(managed);
        (entity.IsManaged ?? false).Should().Be(managed);
        (optionSet.IsManaged ?? false).Should().Be(managed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetrieveRelationship_reports_IsManaged(bool managed)
    {
        const string relationship = "xv_project_xv_task_ProjectId";
        await ImportAsync(SolutionFixtureZip.Build(solution: d => d.Descendants("Managed").Single().Value = managed ? "1" : "0"));

        var metadata = ((RetrieveRelationshipResponse)await Admin.ExecuteAsync(new RetrieveRelationshipRequest { Name = relationship }))
            .RelationshipMetadata;

        (metadata.IsManaged ?? false).Should().Be(managed);
        (Store.GetRelationship(relationship)!.IsManaged ?? false).Should().Be(managed);
    }
}
