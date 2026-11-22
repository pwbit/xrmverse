using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;
using Xrmd.Solution;
using Xrmd.Solution.Layers;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The managed install and update of plan 2.6 (task 4; rulings M6, M7, M8, M3 and M12): the routing of a managed file, the
/// layers it writes, the apply-or-keep decision of each component, the Active marker, <c>OverwriteUnmanagedCustomizations</c>,
/// and the refusals of patches. The solutions come from <see cref="ManagedTestSolution"/>.
/// </summary>
[Collection("solution-import")]
public sealed class ManagedImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Table = ManagedTestSolution.Table;
    private static readonly Guid FormId = new("6d0c1f7e-4b55-4c1e-9a43-2d8f0e7b1a01");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>The account table as the seed has it, put back after the System layer test.</summary>
    private (Label? Name, Label? Collection, Label? Description, string? Unmodelled)? _account;

    /// <summary>The label and unmodelled XML of <c>account.name</c> as the seed has them, put back after the system column test.</summary>
    private (Label? Name, string? Unmodelled)? _accountName;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public async Task DisposeAsync()
    {
        if (_account is { } account)
            await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
            {
                if (Store.GetEntity("account")!.Attributes.Any(a => a.LogicalName == "xm_rating"))
                    await Store.DeleteAttributeAsync(s, "account", "xm_rating", default);
                await Store.UpdateEntityAsync(s, new EntityMetadata
                {
                    LogicalName = "account", DisplayName = account.Name ?? new Label(), DisplayCollectionName = account.Collection ?? new Label(),
                    Description = account.Description ?? new Label(),
                }, replaceLabels: true, default);
                await Store.SetUnmodelledXmlAsync(s, ComponentType.Entity, "account", account.Unmodelled, default);
                await Store.SetIsManagedAsync(s, "account", null, null, default);
                if (_accountName is { } name)
                {
                    await Store.UpdateAttributeAsync(s, "account",
                        new StringAttributeMetadata { LogicalName = "name", DisplayName = name.Name ?? new Label() }, replaceLabels: true, default);
                    await Store.SetUnmodelledXmlAsync(s, ComponentType.Attribute, "account.name", name.Unmodelled, default);
                }
            });
        await CleanAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("""
            IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
            DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;
            DELETE FROM data.role WHERE name = N'xm_reader';
            DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
            DELETE FROM data.publisher WHERE uniquename IN (N'xrmversemanaged', N'microsoftmanagedtest');
            """);
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private Task<OrganizationResponse> ImportAsync(byte[] file, Guid? jobId = null, bool overwrite = false, bool holding = false) =>
        Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = file, ImportJobId = jobId ?? Guid.NewGuid(), OverwriteUnmanagedCustomizations = overwrite,
            HoldingSolution = holding,
        });

    private Task<OrganizationResponse> ImportAsync(ManagedSpec spec, Guid? jobId = null, bool overwrite = false) =>
        ImportAsync(ManagedTestSolution.Build(spec), jobId, overwrite);

    /// <summary>A built solution file with its <c>solution.xml</c> and <c>customizations.xml</c> edited.</summary>
    private static byte[] Edit(ManagedSpec spec, Action<XDocument>? solution = null, Action<XDocument>? customizations = null)
    {
        var (solutionXml, customizationsXml, files) = SolutionZip.Read(ManagedTestSolution.Build(spec));
        solution?.Invoke(solutionXml);
        customizations?.Invoke(customizationsXml);
        var kept = files.Where(f => !f.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        return SolutionZip.Write(solutionXml, customizationsXml, kept);
    }

    /// <summary><paramref name="spec"/> with a main form of <see cref="Table"/> named <paramref name="formName"/>.</summary>
    private static byte[] WithForm(ManagedSpec spec, string formName) => Edit(spec, customizations: c =>
        c.Root!.Element("Entities")!.Elements("Entity").Single().Add(new XElement("FormXml",
            new XElement("forms", new XAttribute("type", "main"),
                new XElement("systemform",
                    new XElement("formid", FormId.ToString("B")),
                    new XElement("IntroducedVersion", "1.0.0.0"),
                    new XElement("FormPresentation", 1),
                    new XElement("FormActivationState", 1),
                    new XElement("form", new XElement("tabs", new XElement("tab", new XAttribute("name", formName.Replace(' ', '_')),
                        new XElement("labels", new XElement("label", new XAttribute("description", formName), new XAttribute("languagecode", 1033)))))),
                    new XElement("IsCustomizable", 1),
                    new XElement("CanBeDeleted", 1),
                    new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", formName), new XAttribute("languagecode", 1033))))))));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    /// <summary>The Web API relabel of a column of <see cref="Table"/>: an unmanaged customization.</summary>
    private async Task RelabelAsync(string column, string label, string type = "StringAttributeMetadata")
    {
        var res = await SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='{column}')", $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.{{type}}", "LogicalName": "{{column}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "{{label}}", "LanguageCode": 1033 } ] } }
            """);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt32(await ScalarAsync(sql, parameters));

    private Task<object?> VersionAsync(string uniqueName) => ScalarAsync("SELECT version FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private Task<object?> IsManagedAsync(string uniqueName) => ScalarAsync("SELECT ismanaged FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private async Task<Guid> SolutionIdAsync(string uniqueName) =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", uniqueName)))!;

    private Task<int> LayerRowsAsync(string uniqueName) => CountAsync(
        "SELECT COUNT(*) FROM meta.component_layer WHERE solution_id IN (SELECT solutionid FROM data.solution WHERE uniquename = @n)", ("@n", uniqueName));

    private Guid ColumnId(string column, string table = Table) => Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == column).MetadataId!.Value;

    private Guid TableId(string table = Table) => Store.GetEntity(table)!.MetadataId!.Value;

    /// <summary>The layers of a component, top first, as (owner, payload).</summary>
    private async Task<List<(Guid Solution, string? Payload)>> StackAsync(int type, Guid id)
    {
        var stack = new List<(Guid, string?)>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT solution_id, payload FROM meta.component_layer WHERE component_type = @t AND object_id = @o ORDER BY layer_order DESC", conn);
        cmd.Parameters.AddWithValue("@t", type);
        cmd.Parameters.AddWithValue("@o", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) stack.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return stack;
    }

    private async Task<bool> HasActiveAsync(int type, Guid id) => (await StackAsync(type, id)).Any(l => l.Solution == SystemSeed.ActiveSolutionId);

    private async Task<string?> PayloadAsync(string uniqueName, int type, Guid id)
    {
        var solution = await SolutionIdAsync(uniqueName);
        return (await StackAsync(type, id)).Single(l => l.Solution == solution).Payload;
    }

    /// <summary><paramref name="spec"/> with the label of the primary key <c>xm_itemid</c> (a system column) set to <paramref name="label"/>.</summary>
    private static byte[] WithIdLabel(ManagedSpec spec, string label) => Edit(spec, customizations: c =>
        c.Root!.Descendants("attribute").Single(a => (string?)a.Element("LogicalName") == "xm_itemid")
            .Element("displaynames")!.Element("displayname")!.SetAttributeValue("description", label));

    private string? Label(string column) => Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == column).DisplayName?.UserLocalizedLabel?.Label;

    private async Task<string> LogAsync(Guid jobId) => (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;

    // ---- routing (M6) -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Final review I1: imports of the same managed solution at once. Both are prepared (classified Install) while the
    /// metadata lock is held elsewhere; then the lock is released. The first to take the lock installs; the others, which
    /// re-read the solution under the lock, fail with a 409 (or, prepared after the commit, update): one solution row, one rank.
    /// </summary>
    [Fact]
    public async Task Concurrent_installs_leave_one_solution_row()
    {
        var file = ManagedTestSolution.Build(ManagedTestSolution.V1);
        var locked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = Task.Run(() => TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            await Store.BeginWriteAsync(s, default);
            locked.SetResult();
            await release.Task;
        }));
        await locked.Task;

        var imports = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            try
            {
                await ImportAsync(file);
                return (XrmdFaultException?)null;
            }
            catch (XrmdFaultException fault)
            {
                return fault;
            }
        })).ToList();
        // Long enough for each import to prepare (read the file, classify) and wait for the lock.
        await Task.Delay(TimeSpan.FromSeconds(3));
        release.SetResult();
        await holder;
        var outcomes = await Task.WhenAll(imports);

        outcomes.Should().Contain(o => o == null, "one of the imports installs the solution");
        outcomes.Where(o => o is not null).Should().OnlyContain(o => o!.HttpStatus == 409 && o.Message.Contains("changed while it was being imported"),
            "an import prepared as an install before the other committed must not install a second row");
        outcomes.Should().Contain(o => o != null && o.HttpStatus == 409, "the imports were prepared while the lock was held");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM meta.solution_rank WHERE solution_id IN (SELECT solutionid FROM data.solution WHERE uniquename = N'xm_base')"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Lower_version_managed_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V2);

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.V1));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("xm_base").And.Contain("2.0.0.0").And.Contain("1.0.0.0");
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
    }

    [Fact]
    public async Task Managed_over_unmanaged_name_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1 with { Managed = false });

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.V2));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_base").And.Contain("unmanaged");
        (await IsManagedAsync("xm_base")).Should().Be(false);
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
        (await LayerRowsAsync("xm_base")).Should().Be(0);
    }

    [Fact]
    public async Task Unmanaged_over_managed_name_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.V2 with { Managed = false }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_base").And.Contain("managed");
        (await IsManagedAsync("xm_base")).Should().Be(true);
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
    }

    [Fact]
    public async Task Holding_solution_needs_the_base_installed()
    {
        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.Build(ManagedTestSolution.V2), holding: true));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("HoldingSolution").And.Contain("xm_base");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_base_Upgrade'")).Should().Be(0);
    }

    [Fact]
    public async Task ConvertToManaged_still_refused()
    {
        var fault = await FaultAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V1), ConvertToManaged = true,
        }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("ConvertToManaged");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be(0);
        Store.GetEntity(Table).Should().BeNull();
    }

    [Fact]
    public async Task Microsoft_publisher_warning_also_on_managed()
    {
        var jobId = Guid.NewGuid();
        var file = Edit(ManagedTestSolution.Other, solution: s =>
            s.Root!.Element("SolutionManifest")!.Element("Publisher")!.Element("UniqueName")!.Value = "microsoftmanagedtest");

        await ImportAsync(file, jobId);

        (await IsManagedAsync("xm_other")).Should().Be(true, "the warning blocks nothing");
        (await LogAsync(jobId)).Should().Contain("comes from a Microsoft publisher ('microsoftmanagedtest'");
    }

    // ---- install and update (M7, M8) --------------------------------------------------------------------------------

    [Fact]
    public async Task Update_keeps_components_missing_from_v2()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var legacy = ColumnId("xm_legacy");
        var role = ManagedTestSolution.IdOf("role", "xm_reader");
        var layers = await LayerRowsAsync("xm_base");

        await ImportAsync(ManagedTestSolution.V2);

        Store.GetEntity(Table)!.Attributes.Select(a => a.LogicalName).Should().Contain(["xm_legacy", "xm_extra"]);
        (await PayloadAsync("xm_base", ComponentType.Attribute, legacy)).Should().Contain("xm_legacy", "its v1 layer stays");
        (await PayloadAsync("xm_base", ComponentType.Role, role)).Should().Contain("xm_reader");
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", role))).Should().Be(1);
        (await PayloadAsync("xm_base", ComponentType.Attribute, ColumnId("xm_name"))).Should().Contain("Title", "v2 replaced the layer");
        (await PayloadAsync("xm_base", ComponentType.Attribute, ColumnId("xm_extra"))).Should().Contain("xm_extra");
        (await LayerRowsAsync("xm_base")).Should().Be(layers + 1, "one more component (xm_extra), none removed");
        Label("xm_name").Should().Be("Title");
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
        (await HasActiveAsync(ComponentType.Attribute, ColumnId("xm_name"))).Should().BeFalse("the import's own writes leave no Active marker");
        (await HasActiveAsync(ComponentType.SavedQuery, ManagedTestSolution.IdOf("view", "Active Items"))).Should().BeFalse();
        (await ScalarAsync("SELECT ismanaged FROM data.savedquery WHERE savedqueryid = @id", ("@id", ManagedTestSolution.IdOf("view", "Active Items"))))
            .Should().Be(true);
    }

    [Fact]
    public async Task Active_layer_hides_update()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await RelabelAsync("xm_name", "My name");
        Label("xm_name").Should().Be("My name");
        var column = ColumnId("xm_name");
        (await HasActiveAsync(ComponentType.Attribute, column)).Should().BeTrue();
        var jobId = Guid.NewGuid();

        await ImportAsync(ManagedTestSolution.V2, jobId);

        Label("xm_name").Should().Be("My name", "the unmanaged layer stays on top");
        (await HasActiveAsync(ComponentType.Attribute, column)).Should().BeTrue();
        (await PayloadAsync("xm_base", ComponentType.Attribute, column)).Should().Contain("Title", "v2's layer is recorded");
        var log = await LogAsync(jobId);
        log.Should().Contain("kept: layer above: attribute 'xm_item.xm_name'").And.Contain("unmanaged customizations");
        Label("xm_code").Should().Be("Code");
    }

    [Fact]
    public async Task Overwrite_removes_active_layer()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await RelabelAsync("xm_name", "My name");
        var column = ColumnId("xm_name");

        await ImportAsync(ManagedTestSolution.V2, overwrite: true);

        Label("xm_name").Should().Be("Title");
        (await HasActiveAsync(ComponentType.Attribute, column)).Should().BeFalse();
    }

    [Fact]
    public async Task Layer_under_higher_managed_is_kept()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.Other);
        Label("xm_code").Should().Be("Code (other)", "the later install sits above");
        var jobId = Guid.NewGuid();

        await ImportAsync(ManagedTestSolution.V2, jobId);

        Label("xm_code").Should().Be("Code (other)");
        Label("xm_name").Should().Be("Name", "xm_other layers xm_name too");
        Label("xm_extra").Should().Be("Extra", "a new column is applied");
        (await PayloadAsync("xm_base", ComponentType.Attribute, ColumnId("xm_name"))).Should().Contain("Title");
        (await LogAsync(jobId)).Should().Contain("kept: layer above: attribute 'xm_item.xm_name'")
            .And.Contain("the managed solution 'xm_other'");
        var stack = await StackAsync(ComponentType.Attribute, ColumnId("xm_code"));
        stack.Select(l => l.Solution).Should().Equal(await SolutionIdAsync("xm_other"), await SolutionIdAsync("xm_base"));
    }

    [Fact]
    public async Task Existing_custom_component_gets_active_marker()
    {
        var plain = new ManagedSpec("xm_plain", "1.0.0.0", false, ManagedTestSolution.Other.Tables);
        await ImportAsync(plain);
        var jobId = Guid.NewGuid();

        await ImportAsync(ManagedTestSolution.V1, jobId);

        Label("xm_code").Should().Be("Code (other)", "the unmanaged state stays on top");
        Label("xm_legacy").Should().Be("Legacy", "a column the managed solution adds is created");
        foreach (var (type, id) in new[] { (ComponentType.Entity, TableId()), (ComponentType.Attribute, ColumnId("xm_code")), (ComponentType.Attribute, ColumnId("xm_name")) })
        {
            var stack = await StackAsync(type, id);
            stack.Select(l => l.Solution).Should().Equal(SystemSeed.ActiveSolutionId, await SolutionIdAsync("xm_base"));
        }
        (await HasActiveAsync(ComponentType.Attribute, ColumnId("xm_legacy"))).Should().BeFalse();
        (await LogAsync(jobId)).Should().Contain("kept: layer above: attribute 'xm_item.xm_code'");
        Store.GetEntity(Table)!.IsManaged.Should().BeTrue();
    }

    [Fact]
    public async Task Unmanaged_import_of_layered_component_marks_active()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var plain = new ManagedSpec("xm_plain", "1.0.0.0", false, ManagedTestSolution.Other.Tables);

        await ImportAsync(plain);

        Label("xm_code").Should().Be("Code (other)", "an unmanaged import writes the effective state");
        (await HasActiveAsync(ComponentType.Attribute, ColumnId("xm_code"))).Should().BeTrue();
        (await HasActiveAsync(ComponentType.Entity, TableId())).Should().BeTrue();
        (await HasActiveAsync(ComponentType.Attribute, ColumnId("xm_legacy"))).Should().BeFalse("the unmanaged file does not touch it");
    }

    /// <summary>
    /// A managed file <c>xm_acct</c> layering the seeded <c>account</c> ("Client") with a custom column <c>xm_rating</c>, and with
    /// <paramref name="nameLabel"/> its system column <c>name</c> relabeled; remembers what <see cref="DisposeAsync"/> puts back.
    /// </summary>
    private async Task<byte[]> AccountFileAsync(string? nameLabel = null)
    {
        var account = Store.GetEntity("account")!;
        var name = (StringAttributeMetadata)account.Attributes.Single(a => a.LogicalName == "name");
        await TestEntities.InSessionAsync(fx.ConnectionString, s =>
        {
            _account = (account.DisplayName, account.DisplayCollectionName, account.Description,
                Store.GetUnmodelledXml(s, ComponentType.Entity, "account"));
            _accountName = (name.DisplayName, Store.GetUnmodelledXml(s, ComponentType.Attribute, "account.name"));
            return Task.CompletedTask;
        });
        var spec = new ManagedSpec("xm_acct", "1.0.0.0", true, [new TestTable("account", "Client", [new TestColumn("xm_rating", "Rating")])]);
        return Edit(spec, customizations: c =>
        {
            var entity = c.Root!.Descendants("entity").Single();
            entity.Element("OwnershipTypeMask")!.Value = "UserOwned";
            entity.Element("IsAuditEnabled")!.Value = account.IsAuditEnabled?.Value == true ? "1" : "0"; // the seed's, unchanged
            var attributes = entity.Element("attributes")!;
            attributes.Elements("attribute").Single(a => (string?)a.Element("LogicalName") == "accountid").Remove();
            if (nameLabel is null) return;
            var column = new XElement(attributes.Elements("attribute").Single(a => (string?)a.Element("LogicalName") == "xm_rating"));
            column.SetAttributeValue("PhysicalName", "Name");
            column.Element("Name")!.Value = column.Element("LogicalName")!.Value = "name";
            column.Element("IsCustomField")!.Value = "0";
            column.Element("MaxLength")!.Value = name.MaxLength!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            column.Element("Length")!.Value = (name.MaxLength!.Value * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            column.Element("displaynames")!.Element("displayname")!.SetAttributeValue("description", nameLabel);
            attributes.Add(column);
        });
    }

    [Fact]
    public async Task Seeded_account_gets_system_layer()
    {
        var account = Store.GetEntity("account")!;
        var file = await AccountFileAsync();

        await ImportAsync(file);

        var stack = await StackAsync(ComponentType.Entity, account.MetadataId!.Value);
        stack.Select(l => l.Solution).Should().Equal(await SolutionIdAsync("xm_acct"), ComponentLayerStore.SystemLayerSolutionId);
        stack[1].Payload.Should().Contain("account", "the System layer holds the seed state");
        Store.GetEntity("account")!.DisplayName.UserLocalizedLabel?.Label.Should().Be("Client");
        Store.GetEntity("account")!.IsManaged.Should().BeTrue();
        Store.GetEntity("account")!.IsAuditEnabled!.Value.Should().Be(account.IsAuditEnabled!.Value);
        (await StackAsync(ComponentType.Attribute, ColumnId("xm_rating", "account"))).Select(l => l.Solution)
            .Should().Equal(await SolutionIdAsync("xm_acct"));
    }

    [Fact]
    public async Task Managed_relabel_of_seeded_system_column_captures_system_layer()
    {
        var name = Store.GetEntity("account")!.Attributes.Single(a => a.LogicalName == "name");
        var file = await AccountFileAsync(nameLabel: "Client name");

        await ImportAsync(file);

        var stack = await StackAsync(ComponentType.Attribute, name.MetadataId!.Value);
        stack.Select(l => l.Solution).Should().Equal(await SolutionIdAsync("xm_acct"), ComponentLayerStore.SystemLayerSolutionId);
        stack[0].Payload.Should().Contain("Client name");
        stack[1].Payload.Should().Contain("PhysicalName=\\u0022Name\\u0022").And.NotContain("Client name", "the System layer holds the seed state");
        Store.GetEntity("account")!.Attributes.Single(a => a.LogicalName == "name").DisplayName.UserLocalizedLabel?.Label.Should().Be("Client name");
        (await HasActiveAsync(ComponentType.Attribute, name.MetadataId!.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task Unmanaged_relabel_of_system_column_survives_update()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var id = ColumnId("xm_itemid");
        (await StackAsync(ComponentType.Attribute, id)).Select(l => l.Solution).Should().Equal(await SolutionIdAsync("xm_base"));
        await RelabelAsync("xm_itemid", "My id", "UniqueIdentifierAttributeMetadata");
        (await HasActiveAsync(ComponentType.Attribute, id)).Should().BeTrue();
        var jobId = Guid.NewGuid();

        await ImportAsync(WithIdLabel(ManagedTestSolution.V2, "Item id v2"), jobId);

        Label("xm_itemid").Should().Be("My id", "the unmanaged layer stays on top");
        (await HasActiveAsync(ComponentType.Attribute, id)).Should().BeTrue();
        (await PayloadAsync("xm_base", ComponentType.Attribute, id)).Should().Contain("Item id v2", "v2's layer is recorded");
        (await LogAsync(jobId)).Should().Contain("kept: layer above: attribute 'xm_item.xm_itemid'")
            .And.Contain("because unmanaged customizations sit above");
    }

    [Fact]
    public async Task Higher_solution_keeps_system_column_on_lower_update()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(WithIdLabel(ManagedTestSolution.Other, "Id (other)"));
        Label("xm_itemid").Should().Be("Id (other)");
        var jobId = Guid.NewGuid();

        await ImportAsync(WithIdLabel(ManagedTestSolution.V2, "Item id v2"), jobId);

        Label("xm_itemid").Should().Be("Id (other)");
        var id = ColumnId("xm_itemid");
        (await StackAsync(ComponentType.Attribute, id)).Select(l => l.Solution)
            .Should().Equal(await SolutionIdAsync("xm_other"), await SolutionIdAsync("xm_base"));
        (await PayloadAsync("xm_base", ComponentType.Attribute, id)).Should().Contain("Item id v2");
        (await LogAsync(jobId)).Should().Contain("kept: layer above: attribute 'xm_item.xm_itemid'")
            .And.Contain("because the managed solution 'xm_other' sits above");
    }

    [Fact]
    public async Task Queued_import_with_overwrite_removes_active_layer()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await RelabelAsync("xm_name", "My name");
        var column = ColumnId("xm_name");
        (await HasActiveAsync(ComponentType.Attribute, column)).Should().BeTrue();
        using var scope = fx.App.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<SolutionImporter>();

        await importer.ImportQueuedAsync(Caller.Internal,
            SolutionImporter.OverwritePrefix + Convert.ToBase64String(ManagedTestSolution.Build(ManagedTestSolution.V2)), Guid.NewGuid(), default);

        Label("xm_name").Should().Be("Title");
        (await HasActiveAsync(ComponentType.Attribute, column)).Should().BeFalse();
    }

    [Theory]
    [InlineData("MaxLength")]
    [InlineData("type")]
    public async Task Active_layer_with_unappliable_change_fails(string change)
    {
        await ImportAsync(ManagedTestSolution.V1);
        await RelabelAsync("xm_code", "My code");
        (await HasActiveAsync(ComponentType.Attribute, ColumnId("xm_code"))).Should().BeTrue();
        var layers = await LayerRowsAsync("xm_base");
        TestColumn code = change == "MaxLength" ? new("xm_code", "Code", MaxLength: 50) : new("xm_code", "Code", Type: "ntext");
        var unappliable = ManagedTestSolution.V2 with { Tables = [new(Table, "Item", [new("xm_name", "Title", PrimaryName: true), code])] };

        var fault = await FaultAsync(() => ImportAsync(unappliable));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("'xm_item.xm_code'").And.Contain(change == "MaxLength" ? "MaxLength" : "MemoAttributeMetadata");
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
        (await LayerRowsAsync("xm_base")).Should().Be(layers);
        Label("xm_code").Should().Be("My code");
        Label("xm_name").Should().Be("Name");
    }

    [Fact]
    public async Task Form_hidden_change_logs_warning()
    {
        await ImportAsync(WithForm(ManagedTestSolution.V1, "Main"));
        await Admin.UpdateAsync(new Entity("systemform", FormId) { ["name"] = "My form" });
        (await HasActiveAsync(ComponentType.SystemForm, FormId)).Should().BeTrue();
        var jobId = Guid.NewGuid();

        await ImportAsync(WithForm(ManagedTestSolution.V2, "Main v2"), jobId);

        (await ScalarAsync("SELECT name FROM data.systemform WHERE systemformid = @id", ("@id", FormId))).Should().Be("My form");
        var log = await LogAsync(jobId);
        log.Should().Contain("is hidden by unmanaged customizations above it").And.Contain("systemform 'Main v2'");
        (await PayloadAsync("xm_base", ComponentType.SystemForm, FormId)).Should().Contain("Main v2");
    }

    [Fact]
    public async Task Update_with_unappliable_change_fails_and_keeps_v1()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var layers = await LayerRowsAsync("xm_base");
        var name = ColumnId("xm_name");
        var shorter = ManagedTestSolution.V2 with
        {
            Tables = [new(Table, "Item", [new("xm_name", "Title", PrimaryName: true), new("xm_code", "Code", MaxLength: 50)])],
        };

        var fault = await FaultAsync(() => ImportAsync(shorter));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_code").And.Contain("MaxLength");
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
        (await LayerRowsAsync("xm_base")).Should().Be(layers);
        (await PayloadAsync("xm_base", ComponentType.Attribute, name)).Should().NotContain("Title");
        Label("xm_name").Should().Be("Name");
        ((StringAttributeMetadata)Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == "xm_code")).MaxLength.Should().Be(100);
    }

    // ---- patches (M12) ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("parent")]
    [InlineData("name")]
    public async Task Patch_file_is_refused(string how)
    {
        var file = how == "parent"
            ? Edit(ManagedTestSolution.V1, solution: s => s.Root!.Element("SolutionManifest")!.Add(
                new XElement("ParentSolution", new XElement("UniqueName", "xm_base"))))
            : ManagedTestSolution.Build(ManagedTestSolution.V1 with { Name = "xm_base_Patch_0a1b2c3d" });

        var fault = await FaultAsync(() => ImportAsync(file));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("solution patches are not supported yet");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename LIKE N'xm[_]%'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmversemanaged'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer")).Should().Be(0);
        Store.GetEntity(Table).Should().BeNull();
    }

    [Theory]
    [InlineData("CloneAsPatch")]
    [InlineData("CloneAsSolution")]
    public async Task CloneAsPatch_and_CloneAsSolution_are_refused(string message)
    {
        await ImportAsync(ManagedTestSolution.V1 with { Managed = false });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest(message)
        {
            ["ParentSolutionUniqueName"] = "xm_base", ["DisplayName"] = "Patch", ["VersionNumber"] = "1.0.0.1",
        }));
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(message).And.Contain("solution patches are not supported yet");

        var res = await SendAsync(HttpMethod.Post, Api + message,
            """{ "ParentSolutionUniqueName": "xm_base", "DisplayName": "Patch", "VersionNumber": "1.0.0.1" }""");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var error = JsonNode.Parse(body)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should().Be(fault.Message);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename LIKE N'xm[_]%'")).Should().Be(1);
    }
}
