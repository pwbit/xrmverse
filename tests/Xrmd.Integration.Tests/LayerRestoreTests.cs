using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Registration;
using Xrmd.Solution;
using Xrmd.Solution.Layers;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.6, Task 3 (ruling M5): restoring components from layer payloads through the importer's restore mode, as
/// <see cref="Caller.Internal"/>, with no publisher or solution row, no <c>solutioncomponent</c>, no <c>importjob</c> and no
/// Active marker; and the System payload of a seeded component, written by the exporter. The managed file is imported as the
/// importer does today; the layers are written directly (<see cref="ComponentLayerStore.UpsertAsync"/>).
/// </summary>
[Collection("solution-import")]
public sealed class LayerRestoreTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = ManagedTestSolution.Table;
    private static readonly Guid LayerSolution = new("3c2b7d0e-55f1-4c2a-9a63-6f0d7e0c2b11");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private ComponentLayerStore Layers => fx.App.Services.GetRequiredService<ComponentLayerStore>();
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
        await ExecAsync("""
            DELETE FROM meta.component_layer WHERE solution_id = @layer;
            IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
            DELETE FROM data.role WHERE name = N'xm_reader';
            DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
            DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
            DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
            """, ("@layer", LayerSolution));
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private async Task<SolutionPackage> ImportV1Async()
    {
        // Unmanaged: the tests lay their own layers (a managed import would layer the components itself, plan 2.6 task 4).
        var zip = ManagedTestSolution.Build(ManagedTestSolution.V1 with { Managed = false });
        await Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        var (solution, customizations, files) = SolutionZip.Read(zip);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private Guid AttributeId(string entity, string attribute) =>
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == attribute).MetadataId!.Value;

    private static ComponentLayer Layer(int type, Guid id, string payload) =>
        new(type, id, LayerSolution, ComponentLayerStore.Order(1, 0), LayerKind.Managed, payload);

    /// <summary>Restores <paramref name="layers"/> in a transaction of its own, committed when it succeeds.</summary>
    /// <returns>The restore's warnings.</returns>
    private async Task<IReadOnlyList<string>> RestoreAsync(params ComponentLayer[] layers)
    {
        using var scope = fx.App.Services.CreateScope();
        var restorer = scope.ServiceProvider.GetRequiredService<LayerRestorer>();
        IReadOnlyList<string> warnings = [];
        await TestEntities.InSessionAsync(fx.ConnectionString, async s => warnings = await restorer.RestoreAsync(s, layers, default));
        return warnings;
    }

    /// <summary>The payload with its fragment rewritten (the JSON escapes the markup, so it is rewritten parsed).</summary>
    private static string Rewrite(string payload, Action<XElement> change)
    {
        var node = JsonNode.Parse(payload)!;
        var fragment = XElement.Parse((string)node["xml"]!, LoadOptions.PreserveWhitespace);
        change(fragment);
        node["xml"] = fragment.ToString(SaveOptions.DisableFormatting);
        return node.ToJsonString();
    }

    private async Task<SolutionPackage> ImportFixtureAsync()
    {
        var zip = SolutionFixtureZip.Build();
        await Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        var (solution, customizations, files) = SolutionZip.Read(zip);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private string? Label(string entity, string attribute) =>
        Text(Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == attribute).DisplayName);

    private static string? Text(Label? label) => label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels.FirstOrDefault()?.Label;

    private Task RelabelAsync(string entity, string attribute, string label, int? maxLength = null) =>
        Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = entity, MergeLabels = false,
            Attribute = new StringAttributeMetadata { LogicalName = attribute, DisplayName = TestHelpers.Label(label), MaxLength = maxLength },
        });

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<string?> TextAsync(string sql, Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_writes_lower_label_and_options()
    {
        var package = await ImportV1Async();
        await RelabelAsync(Table, "xm_code", "Code (customized)");
        // An unmanaged relabel of an option (the store's write, as UpdateOptionValue would make it).
        var status = (OptionSetMetadata)MetadataSerializer.Clone(Store.GetOptionSet("xm_status")!);
        status.Options.Single(o => o.Value == 100000000).Label = TestHelpers.Label("Opened");
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateOptionSetAsync(s, status, default));
        Text(((OptionSetMetadata)Store.GetOptionSet("xm_status")!).Options.Single(o => o.Value == 100000000).Label).Should().Be("Opened");
        Label(Table, "xm_code").Should().Be("Code (customized)");

        await RestoreAsync(
            Layer(ComponentType.Attribute, AttributeId(Table, "xm_code"), LayerPayload.From(package, ComponentType.Attribute, $"{Table}.xm_code")),
            Layer(ComponentType.OptionSet, MetadataStore.OptionSetId("xm_status"), LayerPayload.From(package, ComponentType.OptionSet, "xm_status")));

        Label(Table, "xm_code").Should().Be("Code");
        var options = ((OptionSetMetadata)Store.GetOptionSet("xm_status")!).Options;
        Text(options.Single(o => o.Value == 100000000).Label).Should().Be("Open");
        Text(options.Single(o => o.Value == 100000001).Label).Should().Be("Closed");
    }

    [Fact]
    public async Task Restore_of_shorter_maxlength_fails_naming_property()
    {
        var package = await ImportV1Async();
        await RelabelAsync(Table, "xm_code", "Code (longer)", maxLength: 300);
        Label(Table, "xm_code").Should().Be("Code (longer)");

        var restore = () => RestoreAsync(
            Layer(ComponentType.Attribute, AttributeId(Table, "xm_code"), LayerPayload.From(package, ComponentType.Attribute, $"{Table}.xm_code")));

        var fault = (await restore.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("MaxLength").And.Contain("xm_code");
        // Nothing was written: the label and the length are the customized ones.
        Label(Table, "xm_code").Should().Be("Code (longer)");
        ((StringAttributeMetadata)Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == "xm_code")).MaxLength.Should().Be(300);
    }

    [Fact]
    public async Task Restore_writes_no_solutioncomponent_or_importjob()
    {
        var package = await ImportV1Async();
        await RelabelAsync(Table, "xm_code", "Code (customized)");
        var viewId = ManagedTestSolution.IdOf("view", "Active Items");
        var roleId = ManagedTestSolution.IdOf("role", "xm_reader");
        var resource = package.WebResources.Single();
        var step = package.Steps.Single();
        var assembly = package.Assemblies.Single();
        await Admin.UpdateAsync(new Entity("savedquery", viewId) { ["name"] = "Renamed view" });
        await Admin.UpdateAsync(new Entity("role", roleId) { ["name"] = "xm_reader_renamed" });
        await Admin.UpdateAsync(new Entity("webresource", resource.Id) { ["content"] = Convert.ToBase64String("// changed"u8.ToArray()) });
        await Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", step.Id) { ["rank"] = 7 });

        // The restored components are layered: a write as anyone but Caller.Internal would mark them Active.
        var layers = new[]
        {
            Layer(ComponentType.Entity, Store.GetEntity(Table)!.MetadataId!.Value, LayerPayload.From(package, ComponentType.Entity, Table)),
            Layer(ComponentType.Attribute, AttributeId(Table, "xm_code"), LayerPayload.From(package, ComponentType.Attribute, $"{Table}.xm_code")),
            Layer(ComponentType.SavedQuery, viewId, LayerPayload.From(package, ComponentType.SavedQuery, viewId)),
            Layer(ComponentType.Role, roleId, LayerPayload.From(package, ComponentType.Role, roleId)),
            Layer(ComponentType.WebResource, resource.Id, LayerPayload.From(package, ComponentType.WebResource, resource.Id)),
            Layer(ComponentType.PluginAssembly, assembly.Id, LayerPayload.From(package, ComponentType.PluginAssembly, assembly.Id)),
            Layer(ComponentType.SdkMessageProcessingStep, step.Id, LayerPayload.From(package, ComponentType.SdkMessageProcessingStep, step.Id)),
        };
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            await Layers.SetRankAsync(s, LayerSolution, 1, 0, default);
            foreach (var layer in layers) await Layers.UpsertAsync(s, layer, default);
        });

        const string counts = """
            SELECT (SELECT COUNT(*) FROM data.solutioncomponent) * 1000000 + (SELECT COUNT(*) FROM data.importjob) * 10000
                 + (SELECT COUNT(*) FROM data.solution) * 100 + (SELECT COUNT(*) FROM data.publisher)
            """;
        var before = await CountAsync(counts);
        var layerRows = await CountAsync("SELECT COUNT(*) FROM meta.component_layer");

        await RestoreAsync(layers);

        (await CountAsync(counts)).Should().Be(before, "a restore writes no solutioncomponent, importjob, solution or publisher row");
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer")).Should().Be(layerRows, "a restore marks nothing Active");
        foreach (var layer in layers)
        {
            IReadOnlyList<ComponentLayer> stack = [];
            await TestEntities.InSessionAsync(fx.ConnectionString, async s => stack = await Layers.StackAsync(s, layer.Type, layer.ObjectId, default));
            stack.Should().NotContain(l => l.Kind == LayerKind.Active);
        }

        Label(Table, "xm_code").Should().Be("Code");
        (await TextAsync("SELECT name FROM data.savedquery WHERE savedqueryid = @id", viewId)).Should().Be("Active Items");
        (await TextAsync("SELECT name FROM data.role WHERE roleid = @id", roleId)).Should().Be("xm_reader");
        Convert.FromBase64String((await TextAsync("SELECT content FROM data.webresource WHERE webresourceid = @id", resource.Id))!)
            .Should().Equal(resource.Content);
        (await CountAsync($"SELECT rank FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = '{step.Id}'")).Should().Be(step.Rank);
    }

    [Fact]
    public async Task System_fragment_of_account_restores_seed_labels()
    {
        var exporter = fx.App.Services.GetRequiredService<SolutionExporter>();
        var account = Store.GetEntity("account")!;
        var state = (StateAttributeMetadata)account.Attributes.Single(a => a.LogicalName == "statecode");
        Text(state.DisplayName).Should().Be("Status", "the seeded state column has labels");
        account.IsAuditEnabled!.Value.Should().BeTrue();
        string tablePayload = "", statePayload = "";
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            tablePayload = await exporter.WriteComponentFragment(s, ComponentType.Entity, account.MetadataId!.Value, default);
            statePayload = await exporter.WriteComponentFragment(s, ComponentType.Attribute, state.MetadataId!.Value, default);
        });
        LayerPayload.Parse(tablePayload).Fragment.Descendants("attribute").Should().BeEmpty("a table's payload is its shell");
        LayerPayload.Parse(statePayload).Fragment.Descendants("attribute").Should().ContainSingle();

        async Task CustomizeAsync(bool audit, string label, string option) => await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            await Store.UpdateEntityAsync(s, new EntityMetadata { LogicalName = "account", IsAuditEnabled = new BooleanManagedProperty(audit) }, default);
            var options = MetadataSerializer.Clone(state.OptionSet!.Options.Single(o => o.Value == 0));
            options.Label = TestHelpers.Label(option);
            await Store.UpdateAttributeAsync(s, "account", new StateAttributeMetadata
            {
                LogicalName = "statecode", DisplayName = TestHelpers.Label(label),
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection { options }) { IsGlobal = false },
            }, default);
        });

        Text(account.DisplayName).Should().BeNullOrEmpty("the account seed has no display name");
        // A managed layer that relabels the table.
        var managedTable = Rewrite(tablePayload, fragment =>
        {
            var info = fragment.Descendants("EntityInfo").Single().Element("entity")!;
            info.Element("LocalizedNames")?.Remove();
            info.AddFirst(new XElement("LocalizedNames",
                new XElement("LocalizedName", new XAttribute("description", "Client"), new XAttribute("languagecode", "1033"))));
        });
        var systemTable = new ComponentLayer(ComponentType.Entity, account.MetadataId!.Value, ComponentLayerStore.SystemLayerSolutionId, 0,
            LayerKind.System, tablePayload);
        var systemState = new ComponentLayer(ComponentType.Attribute, state.MetadataId!.Value, ComponentLayerStore.SystemLayerSolutionId, 0,
            LayerKind.System, statePayload);

        try
        {
            await RestoreAsync(Layer(ComponentType.Entity, account.MetadataId!.Value, managedTable));
            Text(Store.GetEntity("account")!.DisplayName).Should().Be("Client");
            await CustomizeAsync(false, "Status (changed)", "Open (changed)");
            Store.GetEntity("account")!.IsAuditEnabled!.Value.Should().BeFalse();
            Text(Store.GetEntity("account")!.Attributes.Single(a => a.LogicalName == "statecode").DisplayName).Should().Be("Status (changed)");

            await RestoreAsync(systemTable, systemState);

            var restored = Store.GetEntity("account")!;
            restored.IsAuditEnabled!.Value.Should().BeTrue();
            var restoredState = (StateAttributeMetadata)restored.Attributes.Single(a => a.LogicalName == "statecode");
            Text(restoredState.DisplayName).Should().Be("Status");
            Text(restoredState.OptionSet!.Options.Single(o => o.Value == 0).Label).Should().Be("Active");
            Text(restored.DisplayName).Should().BeNullOrEmpty("the System layer has no label: the managed one is cleared (R6)");
        }
        finally
        {
            await CustomizeAsync(true, "Status", "Active");
            await TestEntities.InSessionAsync(fx.ConnectionString, s =>
                Store.UpdateEntityAsync(s, new EntityMetadata { LogicalName = "account", DisplayName = new Label() }, replaceLabels: true, default));
        }
    }

    [Fact]
    public async Task Restore_refuses_an_active_marker()
    {
        var restore = () => RestoreAsync(new ComponentLayer(ComponentType.Attribute, Guid.NewGuid(), SystemSeed.ActiveSolutionId, long.MaxValue,
            LayerKind.Active, null));
        await restore.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Restore_keeps_an_option_value_the_layer_lacks_with_a_warning()
    {
        var package = await ImportV1Async();
        var status = (OptionSetMetadata)MetadataSerializer.Clone(Store.GetOptionSet("xm_status")!);
        status.Options.Add(new OptionMetadata(TestHelpers.Label("Reopened"), 100000002));
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateOptionSetAsync(s, status, default));

        var warnings = await RestoreAsync(
            Layer(ComponentType.OptionSet, MetadataStore.OptionSetId("xm_status"), LayerPayload.From(package, ComponentType.OptionSet, "xm_status")));

        warnings.Should().ContainSingle(w => w.Contains("Option 100000002 of xm_status is kept: a restore does not remove option values"));
        ((OptionSetMetadata)Store.GetOptionSet("xm_status")!).Options.Select(o => o.Value).Should().Contain(100000002);
    }

    [Fact]
    public async Task Restore_of_a_lookup_leaves_its_relationship_alone()
    {
        const string relationship = "xv_project_xv_task_ProjectId";
        var package = await ImportFixtureAsync();
        var menu = "<AssociatedMenuConfiguration><Behavior>DoNotDisplay</Behavior></AssociatedMenuConfiguration>";
        var kept = "<EntityRelationship Name=\"xv_project_xv_task_ProjectId\"><IntroducedVersion>9.9.9.9</IntroducedVersion></EntityRelationship>";
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            await Store.SetRelationshipMenuXmlAsync(s, relationship, menu, default);
            await Store.SetUnmodelledXmlAsync(s, ComponentType.EntityRelationship, relationship, kept, default);
            await Store.UpdateAttributeAsync(s, "xv_task", new LookupAttributeMetadata { LogicalName = "xv_projectid", DisplayName = TestHelpers.Label("Project (customized)") }, default);
        });
        var payload = LayerPayload.From(package, ComponentType.Attribute, "xv_task.xv_projectid");
        LayerPayload.Parse(payload).Fragment.Descendants("EntityRelationship").Should().NotBeEmpty("the lookup's payload carries its relationship");

        await RestoreAsync(Layer(ComponentType.Attribute, AttributeId("xv_task", "xv_projectid"), payload));

        Label("xv_task", "xv_projectid").Should().Be(Text(package.Lookups.Single(l => l.LogicalName == "xv_projectid").DisplayName));
        Store.GetRelationshipMenuXml(relationship).Should().Be(menu, "only the requested lookup is written (R9)");
        Store.GetUnmodelledXml(ComponentType.EntityRelationship, relationship).Should().Be(kept);
    }

    [Fact]
    public async Task Restore_keeps_a_cascade_configuration_with_a_warning()
    {
        const string relationship = "xv_project_xv_task_ProjectId";
        var package = await ImportFixtureAsync();
        var payload = Rewrite(LayerPayload.From(package, ComponentType.EntityRelationship, relationship), fragment =>
            fragment.Descendants("EntityRelationship").Single(r => (string?)r.Attribute("Name") == relationship).Element("CascadeDelete")!.Value = "Restrict");

        var warnings = await RestoreAsync(Layer(ComponentType.EntityRelationship, Store.GetRelationship(relationship)!.MetadataId!.Value, payload));

        warnings.Should().ContainSingle(w => w.Contains($"Relationship '{relationship}' keeps its cascade configuration") && w.Contains("CascadeConfiguration.Delete"));
        ((OneToManyRelationshipMetadata)Store.GetRelationship(relationship)!).CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lookup_and_relationship_from_different_layers_each_come_from_their_own(bool lookupFirst)
    {
        const string relationship = "xv_project_xv_task_ProjectId";
        var package = await ImportFixtureAsync();
        // Layer A: the lookup as the file has it (its payload carries the relationship as the file has it, a companion).
        var lookupLayer = Layer(ComponentType.Attribute, AttributeId("xv_task", "xv_projectid"),
            LayerPayload.From(package, ComponentType.Attribute, "xv_task.xv_projectid"));
        // Layer B: the relationship with another menu (its payload carries the lookup, relabelled, a companion).
        var relationshipLayer = Layer(ComponentType.EntityRelationship, Store.GetRelationship(relationship)!.MetadataId!.Value,
            Rewrite(LayerPayload.From(package, ComponentType.EntityRelationship, relationship), fragment =>
            {
                foreach (var order in fragment.Descendants("NavPaneOrder")) order.Value = "20000";
                foreach (var label in fragment.Descendants("displayname").Where(d => (string?)d.Attribute("languagecode") == "1033"))
                    label.SetAttributeValue("description", "Project (from B)");
            }));
        await TestEntities.InSessionAsync(fx.ConnectionString, s =>
            Store.UpdateAttributeAsync(s, "xv_task", new LookupAttributeMetadata { LogicalName = "xv_projectid", DisplayName = TestHelpers.Label("Project (customized)") }, default));

        await RestoreAsync(lookupFirst ? [lookupLayer, relationshipLayer] : [relationshipLayer, lookupLayer]);

        Label("xv_task", "xv_projectid").Should().Be("Project", "the lookup comes from its own layer, not the relationship's copy");
        Store.GetRelationshipMenuXml(relationship).Should().Contain("20000", "the relationship comes from its own layer, not the lookup's copy");
    }

    [Fact]
    public async Task Restore_refuses_a_row_component_that_does_not_exist()
    {
        var package = await ImportV1Async();
        var roleId = ManagedTestSolution.IdOf("role", "xm_reader");
        var payload = LayerPayload.From(package, ComponentType.Role, roleId);
        await Admin.DeleteAsync("role", roleId);

        var restore = () => RestoreAsync(Layer(ComponentType.Role, roleId, payload));

        var fault = (await restore.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(roleId.ToString());
        (await CountAsync($"SELECT COUNT(*) FROM data.role WHERE roleid = '{roleId}'")).Should().Be(0, "a restore never creates a component");
    }

    [Fact]
    public async Task Replacing_option_labels_clears_a_missing_one()
    {
        await ImportV1Async();
        var changes = new OptionSetMetadata(new OptionMetadataCollection { new OptionMetadata { Value = 100000001 } }) { Name = "xm_status" };

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateOptionSetAsync(s, changes, replaceLabels: true, default));

        Text(((OptionSetMetadata)Store.GetOptionSet("xm_status")!).Options.Single(o => o.Value == 100000001).Label).Should().BeNullOrEmpty(
            "with replaceLabels, an option given without a label has none (R6)");
    }
}
