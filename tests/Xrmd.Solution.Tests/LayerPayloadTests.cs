using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Integration.Tests;
using Xrmd.Metadata;
using Xrmd.Solution.Layers;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Plan 2.6, Task 3 (ruling M5): the payload of a layer is the component as its file delivered it, and a package rebuilt from
/// payloads (<see cref="LayerPayload.ToPackage"/>) reads each component back as the original file had it.
/// </summary>
public class LayerPayloadTests
{
    private static SolutionPackage Read(byte[] zip)
    {
        var (solution, customizations, files) = SolutionZip.Read(zip);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static string Labels(Label? label) =>
        string.Join("|", (label?.LocalizedLabels ?? []).Select(l => $"{l.LanguageCode}:{l.Label}")) + "/" + label?.UserLocalizedLabel?.Label;

    private static string? Kept(SolutionPackage package, (int, string) key) =>
        package.Unmodelled?.GetValueOrDefault(key)?.ToString(SaveOptions.DisableFormatting);

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>What the package says of one component, in a form two packages can be compared by.</summary>
    private static string Describe(SolutionPackage package, int type, string key)
    {
        switch (type)
        {
            case ComponentType.Entity:
            {
                var e = package.Entities.Single(x => x.LogicalName == key);
                return string.Join("\n", e.LogicalName, e.SchemaName, Labels(e.DisplayName), Labels(e.DisplayCollectionName), Labels(e.Description),
                    e.OwnershipType, e.IsActivity, e.EntitySetName, Json(e.IsAuditEnabled), e.AutoCreateAccessTeams,
                    Kept(package, UnmodelledXml.EntityKey(key)),
                    string.Join("", package.EntityUnsupported.Where(u => u.EntityLogicalName == key).Select(u => u.Xml.ToString(SaveOptions.DisableFormatting))));
            }
            case ComponentType.Attribute:
            {
                var (entity, name) = (key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);
                var attribute = package.Entities.Single(e => e.LogicalName == entity).Attributes.Cast<AttributeMetadata>()
                    .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity))
                    .Single(a => a.LogicalName == name);
                return MetadataSerializer.SerializeAttribute(attribute) + Kept(package, UnmodelledXml.AttributeKey(entity, name));
            }
            case ComponentType.EntityRelationship:
            {
                var r = package.Relationships.Single(x => string.Equals(x.SchemaName, key, StringComparison.OrdinalIgnoreCase));
                return MetadataSerializer.SerializeRelationship(r) + Kept(package, UnmodelledXml.RelationshipKey(r.SchemaName))
                       + package.RelationshipMenus.GetValueOrDefault(r.SchemaName)?.ToString(SaveOptions.DisableFormatting);
            }
            case ComponentType.OptionSet:
            {
                var o = package.OptionSets.Single(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                return MetadataSerializer.SerializeOptionSet(o) + Kept(package, UnmodelledXml.OptionSetKey(o.Name));
            }
            case ComponentType.SavedQuery or ComponentType.SystemForm or ComponentType.SiteMap or ComponentType.AppModule
                or ComponentType.SavedQueryVisualization:
            {
                var o = package.Opaque.Single(x => x.Type == type && string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase));
                return $"{o.EntityLogicalName}/{o.FormType}/{o.Xml.ToString(SaveOptions.DisableFormatting)}";
            }
            case ComponentType.Role:
                return Json(package.Roles.Single(r => r.Id.ToString("D") == key));
            case ComponentType.FieldSecurityProfile:
                return Json(package.FieldSecurityProfiles!.Single(r => r.Id.ToString("D") == key));
            case ComponentType.WebResource:
                return Json(package.WebResources.Single(r => r.Id.ToString("D") == key)) + Kept(package, UnmodelledXml.WebResourceKey(Guid.Parse(key)));
            case ComponentType.PluginAssembly:
                return Json(package.Assemblies.Single(r => r.Id.ToString("D") == key)) + Kept(package, UnmodelledXml.AssemblyKey(Guid.Parse(key)));
            case ComponentType.SdkMessageProcessingStep:
                return Json(package.Steps.Single(r => r.Id.ToString("D") == key)) + Kept(package, UnmodelledXml.StepKey(Guid.Parse(key)));
            case ComponentType.CustomApi:
            {
                var api = package.CustomApis!.Single(a => string.Equals(a.UniqueName, key, StringComparison.OrdinalIgnoreCase));
                string Field(PackageCustomApiField f) =>
                    $"{f.UniqueName},{f.Name},{f.Type},{f.LogicalEntityName},{f.IsOptional},{Labels(f.DisplayName)},{Labels(f.Description)},{f.Kept}";
                return string.Join("\n", api.UniqueName, api.Name, api.BindingType, api.BoundEntity, api.AllowedStepType, api.IsFunction, api.IsPrivate,
                    api.WorkflowEnabled, api.ExecutePrivilegeName, api.PluginTypeKey, Labels(api.DisplayName), Labels(api.Description), api.Kept,
                    string.Join(";", api.RequestParameters.Select(Field)), string.Join(";", api.ResponseProperties.Select(Field)));
            }
            case ComponentType.EnvironmentVariableDefinition:
            {
                var v = package.EnvironmentVariables.Single(x => string.Equals(x.SchemaName, key, StringComparison.OrdinalIgnoreCase));
                return string.Join("\n", v.SchemaName, v.Type, v.DefaultValue, v.IsRequired, v.SecretStore, v.ValueSchema, v.IntroducedVersion,
                    v.Hint, v.Value, v.HasValueFile, v.ValueIsCustomizable, v.Source.ToString(SaveOptions.DisableFormatting));
            }
            default:
                throw new InvalidOperationException($"No description for type {type}.");
        }
    }

    [Fact]
    public void Payload_round_trips_each_component_type()
    {
        var package = Read(SolutionFixtureZip.BuildWithRealShapes());
        var sources = package.Sources!.Keys.ToList();

        // The fixture has every kind the payload must carry.
        sources.Select(k => k.Type).Distinct().Should().Contain([
            ComponentType.Entity, ComponentType.Attribute, ComponentType.EntityRelationship, ComponentType.OptionSet, ComponentType.SavedQuery,
            ComponentType.SystemForm, ComponentType.Role, ComponentType.WebResource, ComponentType.PluginAssembly,
            ComponentType.SdkMessageProcessingStep, ComponentType.CustomApi, ComponentType.EnvironmentVariableDefinition,
        ]);
        package.Steps.Should().Contain(s => s.Images.Count > 0, "a step with an image travels with its images");
        package.WebResources.Should().OnlyContain(w => w.Content.Length > 0);
        package.Assemblies.Should().OnlyContain(a => a.Content.Length > 0);
        package.Lookups.Should().Contain(l => l.IsCustomAttribute == true, "a custom lookup travels with its relationship");

        foreach (var (type, key) in sources)
        {
            var payload = LayerPayload.From(package, type, key);
            var rebuilt = LayerPayload.ToPackage([(type, Guid.Empty, payload)]);
            Describe(rebuilt, type, key).Should().Be(Describe(package, type, key), $"component {key} of type {type} round-trips");
        }
    }

    [Fact]
    public void Visualization_payload_round_trips_and_the_table_shell_leaves_it_out()
    {
        // Plan 3.5 UI-1f: a chart is a layer of its own inside an Entity that carries only the table's name.
        var package = Read(SolutionFixtureZip.BuildWithRealShapes(SolutionFixtureZip.AddCharts));
        var key = SolutionFixtureZip.AmountChartId.ToString("D");

        var payload = LayerPayload.From(package, ComponentType.SavedQueryVisualization, key);
        LayerPayload.Parse(payload).Fragment.Descendants("visualization").Should().ContainSingle();
        var rebuilt = LayerPayload.ToPackage([(ComponentType.SavedQueryVisualization, Guid.Empty, payload)]);
        Describe(rebuilt, ComponentType.SavedQueryVisualization, key).Should().Be(Describe(package, ComponentType.SavedQueryVisualization, key));
        LayerPayload.Parse(LayerPayload.From(package, ComponentType.Entity, "xv_project")).Fragment.Descendants("visualization")
            .Should().BeEmpty("the charts are layers of their own");
    }

    [Fact]
    public void Dashboard_payload_round_trips()
    {
        // Plan 3.5 UI-1f (ruling F1): a dashboard is a form of no table, its root Dashboard element under Dashboards.
        var package = Read(SolutionFixtureZip.BuildWithRealShapes(SolutionFixtureZip.AddDashboards));
        var key = SolutionFixtureZip.ChartsDashboardId.ToString("D");

        var payload = LayerPayload.From(package, ComponentType.SystemForm, SolutionFixtureZip.ChartsDashboardId);
        var fragment = LayerPayload.Parse(payload).Fragment;
        fragment.Element("Dashboards")!.Elements("Dashboard").Should().ContainSingle();
        fragment.Element("Entities").Should().BeNull();
        var rebuilt = LayerPayload.ToPackage([(ComponentType.SystemForm, SolutionFixtureZip.ChartsDashboardId, payload)]);
        Describe(rebuilt, ComponentType.SystemForm, key).Should().Be(Describe(package, ComponentType.SystemForm, key));
    }

    [Fact]
    public void Payload_carries_the_files_of_its_component()
    {
        var package = Read(SolutionFixtureZip.BuildWithRealShapes());
        var resource = package.WebResources[0];
        var (fragment, files, language) = LayerPayload.Parse(LayerPayload.From(package, ComponentType.WebResource, resource.Id));
        files.Values.Should().ContainSingle().Which.Should().Equal(resource.Content);
        fragment.Element("WebResources")!.Elements("WebResource").Should().ContainSingle();
        language.Should().Be(package.Solution.LanguageCode);

        var assembly = package.Assemblies[0];
        LayerPayload.Parse(LayerPayload.From(package, ComponentType.PluginAssembly, assembly.Id)).Files.Values
            .Should().ContainSingle().Which.Should().Equal(assembly.Content);

        var api = package.CustomApis!.Single();
        LayerPayload.Parse(LayerPayload.From(package, ComponentType.CustomApi, api.UniqueName)).Files.Keys
            .Should().NotBeEmpty().And.OnlyContain(p => p.StartsWith("customapis/", StringComparison.OrdinalIgnoreCase));

        // #167: an environment variable carries its folder, the value file with it.
        LayerPayload.Parse(LayerPayload.From(package, ComponentType.EnvironmentVariableDefinition, "xv_serviceurl")).Files.Keys.Should().BeEquivalentTo(
            "environmentvariabledefinitions/xv_ServiceUrl/environmentvariabledefinition.xml",
            "environmentvariabledefinitions/xv_ServiceUrl/environmentvariablevalues.json");
    }

    [Fact]
    public void Entity_payload_is_a_shell_and_columns_merge_back()
    {
        var package = Read(ManagedTestSolution.Build(ManagedTestSolution.V1));
        var shell = LayerPayload.From(package, ComponentType.Entity, ManagedTestSolution.Table);
        LayerPayload.Parse(shell).Fragment.Descendants("attribute").Should().BeEmpty("the columns are layers of their own");
        LayerPayload.Parse(shell).Fragment.Descendants("savedquery").Should().BeEmpty("the views are layers of their own");

        var code = LayerPayload.From(package, ComponentType.Attribute, $"{ManagedTestSolution.Table}.xm_code");
        var view = LayerPayload.From(package, ComponentType.SavedQuery, ManagedTestSolution.IdOf("view", "Active Items"));
        var rebuilt = LayerPayload.ToPackage([
            (ComponentType.Attribute, Guid.Empty, code), (ComponentType.SavedQuery, Guid.Empty, view), (ComponentType.Entity, Guid.Empty, shell),
        ]);
        var table = rebuilt.Entities.Should().ContainSingle().Subject;
        table.Attributes.Select(a => a.LogicalName).Should().Equal("xm_code");
        table.DisplayName.UserLocalizedLabel.Label.Should().Be("Item");
        rebuilt.Opaque.Should().ContainSingle(o => o.Type == ComponentType.SavedQuery && o.EntityLogicalName == ManagedTestSolution.Table);
        rebuilt.RootComponents.Should().ContainSingle(r => r.Type == ComponentType.Entity && r.SchemaNameOrId == ManagedTestSolution.Table);

        // Without the table's own payload, the table is carried only for its column: not a root component.
        LayerPayload.ToPackage([(ComponentType.Attribute, Guid.Empty, code)]).RootComponents.Should().BeEmpty();
    }

    [Fact]
    public void Ids_name_row_components_and_option_sets_only()
    {
        var package = Read(ManagedTestSolution.Build(ManagedTestSolution.V1));
        LayerPayload.From(package, ComponentType.OptionSet, MetadataStore.OptionSetId("xm_status")).Should().Contain("xm_status");
        LayerPayload.From(package, ComponentType.Role, ManagedTestSolution.IdOf("role", "xm_reader")).Should().Contain("xm_reader");
        var entity = () => LayerPayload.From(package, ComponentType.Entity, Guid.NewGuid());
        entity.Should().Throw<ArgumentException>();
        var missing = () => LayerPayload.From(package, ComponentType.Role, Guid.NewGuid());
        missing.Should().Throw<ArgumentException>();
        var invalid = () => LayerPayload.ToPackage([(ComponentType.Role, Guid.Empty, "not json")]);
        invalid.Should().Throw<ArgumentException>();
    }

    /// <summary>The payload with its fragment rewritten or its language set.</summary>
    private static string With(string payload, Func<string, string>? xml = null, int? language = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
        if (xml is not null) node["xml"] = xml((string)node["xml"]!);
        if (language is not null) node["language"] = language;
        return node.ToJsonString();
    }

    [Fact]
    public void Payloads_of_different_languages_are_refused_naming_both()
    {
        var package = Read(ManagedTestSolution.Build(ManagedTestSolution.V1));
        var code = LayerPayload.From(package, ComponentType.Attribute, $"{ManagedTestSolution.Table}.xm_code");
        var status = With(LayerPayload.From(package, ComponentType.OptionSet, "xm_status"), language: 1040);

        var mixed = () => LayerPayload.ToPackage([(ComponentType.Attribute, Guid.Empty, code), (ComponentType.OptionSet, Guid.Empty, status)]);

        var fault = mixed.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(Xrmd.Core.Errors.ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("1033").And.Contain("1040");
    }

    [Fact]
    public void A_component_given_twice_keeps_its_first_payload()
    {
        var package = Read(ManagedTestSolution.Build(ManagedTestSolution.V1));
        var status = LayerPayload.From(package, ComponentType.OptionSet, "xm_status");
        var relabelled = With(status, xml => xml.Replace("description=\"Open\"", "description=\"Opened\"", StringComparison.Ordinal));
        relabelled.Should().NotBe(status);

        var rebuilt = LayerPayload.ToPackage([(ComponentType.OptionSet, Guid.Empty, relabelled), (ComponentType.OptionSet, Guid.Empty, status)]);

        var optionSet = (OptionSetMetadata)rebuilt.OptionSets.Should().ContainSingle().Subject;
        optionSet.Options.Single(o => o.Value == 100000000).Label.UserLocalizedLabel.Label.Should().Be("Opened");
    }
}
