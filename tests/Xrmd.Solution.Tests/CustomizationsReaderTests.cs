using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Security;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

public class CustomizationsReaderTests
{
    private static readonly SolutionPackage Fixture = Read();

    private static SolutionPackage Read(Action<XDocument>? customizations = null, Action<XDocument>? solution = null)
    {
        var (solutionXml, customizationsXml, files) = SolutionZip.Read(SolutionFixtureZip.Build(customizations, solution));
        return CustomizationsReader.Read(solutionXml, customizationsXml, files);
    }

    private static EntityMetadata Entity(string logicalName) => Fixture.Entities.Single(e => e.LogicalName == logicalName);

    private static T Attribute<T>(string entity, string logicalName) where T : AttributeMetadata =>
        Entity(entity).Attributes.Single(a => a.LogicalName == logicalName).Should().BeOfType<T>().Subject;

    private static string? Text(Label? label, int languageCode) =>
        label?.LocalizedLabels.SingleOrDefault(l => l.LanguageCode == languageCode)?.Label;

    private static void ShouldHaveLabels(Label? label, string english, string italian)
    {
        Text(label, 1033).Should().Be(english);
        Text(label, 1040).Should().Be(italian);
        label!.UserLocalizedLabel.Should().NotBeNull();
        label.UserLocalizedLabel!.LanguageCode.Should().Be(1033);
    }

    [Fact]
    public void Reads_publisher_solution_and_version()
    {
        Fixture.Publisher.UniqueName.Should().Be("xrmverse");
        Fixture.Publisher.CustomizationPrefix.Should().Be("xv");
        Fixture.Publisher.CustomizationOptionValuePrefix.Should().Be(10000);
        Text(Fixture.Publisher.FriendlyName, 1033).Should().Be("xrmverse");

        Fixture.Solution.UniqueName.Should().Be("XrmverseFixture");
        Fixture.Solution.Version.Should().Be("1.0.0.0");
        Fixture.Solution.Managed.Should().BeFalse();
        Fixture.Solution.LanguageCode.Should().Be(1033);
        ShouldHaveLabels(Fixture.Solution.FriendlyName, "Xrmverse Fixture", "Fixture Xrmverse");

        Fixture.RootComponents.Should().Contain([
            (1, "xv_project", 0), (1, "xv_tag", 0), (1, "xv_task", 0), (9, "xv_priority", 0),
            (20, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01", 0), (61, "xv_/scripts/project.js", 0), (62, "xv_fixtureapp", 0),
            (80, "xv_fixtureapp", 0), (91, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10", 0), (92, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21", 0),
        ]);
        Fixture.RootComponents.Should().HaveCount(11);
    }

    [Fact]
    public void Root_component_behavior_is_kept()
    {
        var package = Read(solution: d => d.Descendants("RootComponent").First(c => (string?)c.Attribute("schemaName") == "xv_tag")
            .SetAttributeValue("behavior", "2"));

        package.RootComponents.Should().Contain((1, "xv_tag", 2));
    }

    [Fact]
    public void Managed_flag_is_read()
    {
        var managed = Read(solution: d => d.Descendants("Managed").Single().Value = "1");

        managed.Solution.Managed.Should().BeTrue();
    }

    [Fact]
    public void Reads_entities_attributes_and_their_types()
    {
        Fixture.Entities.Select(e => e.LogicalName).Should().BeEquivalentTo(["xv_project", "xv_tag", "xv_task"]);

        var project = Entity("xv_project");
        project.SchemaName.Should().Be("xv_Project");
        project.EntitySetName.Should().Be("xv_projects");
        project.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        project.PrimaryIdAttribute.Should().Be("xv_projectid");
        project.PrimaryNameAttribute.Should().Be("xv_name");
        project.IsActivity.Should().BeFalse();
        ShouldHaveLabels(project.DisplayName, "Project", "Progetto");
        ShouldHaveLabels(project.DisplayCollectionName, "Projects", "Progetti");
        ShouldHaveLabels(project.Description, "A project of the xrmverse fixture.", "Un progetto della fixture xrmverse.");
        Entity("xv_tag").OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);

        // Lookups are not entity attributes: they come in a second pass.
        project.Attributes.Should().NotContain(a => a is LookupAttributeMetadata);
        project.Attributes.Should().OnlyContain(a => a.EntityLogicalName == "xv_project");

        var id = Attribute<UniqueIdentifierAttributeMetadata>("xv_project", "xv_projectid");
        id.IsPrimaryId.Should().BeTrue();
        id.IsCustomAttribute.Should().BeFalse();
        id.SchemaName.Should().Be("xv_ProjectId");

        var name = Attribute<StringAttributeMetadata>("xv_project", "xv_name");
        name.SchemaName.Should().Be("xv_Name");
        name.MaxLength.Should().Be(100);
        name.Format.Should().Be(StringFormat.Text);
        name.IsPrimaryName.Should().BeTrue();
        name.IsCustomAttribute.Should().BeTrue();
        name.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        name.IsValidForCreate.Should().BeTrue();
        ShouldHaveLabels(name.DisplayName, "Name", "Nome");

        var description = Attribute<MemoAttributeMetadata>("xv_project", "xv_description");
        description.MaxLength.Should().Be(2000);
        description.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.None);
        ShouldHaveLabels(description.DisplayName, "Description", "Descrizione");
        ShouldHaveLabels(description.Description, "What the project is about.", "Di cosa tratta il progetto.");

        var count = Attribute<IntegerAttributeMetadata>("xv_project", "xv_count");
        count.MinValue.Should().Be(0);
        count.MaxValue.Should().Be(1000);
        count.Format.Should().Be(IntegerFormat.None);
        count.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.Recommended);
        ShouldHaveLabels(count.DisplayName, "Count", "Conteggio");

        var ratio = Attribute<DecimalAttributeMetadata>("xv_project", "xv_ratio");
        ratio.Precision.Should().Be(4);
        ratio.MinValue.Should().Be(-1000m);
        ratio.MaxValue.Should().Be(1000m);
        ShouldHaveLabels(ratio.DisplayName, "Ratio", "Rapporto");

        var budget = Attribute<MoneyAttributeMetadata>("xv_project", "xv_budget");
        budget.Precision.Should().Be(2);
        budget.PrecisionSource.Should().Be(2);
        budget.MinValue.Should().Be(0);
        budget.MaxValue.Should().Be(1_000_000_000);
        budget.IsBaseCurrency.Should().NotBe(true);
        ShouldHaveLabels(budget.DisplayName, "Budget", "Budget");
        Attribute<MoneyAttributeMetadata>("xv_project", "xv_budget_base").IsBaseCurrency.Should().BeTrue();

        var isActive = Attribute<BooleanAttributeMetadata>("xv_project", "xv_isactive");
        isActive.DefaultValue.Should().BeTrue();
        isActive.OptionSet.TrueOption.Value.Should().Be(1);
        ShouldHaveLabels(isActive.OptionSet.TrueOption.Label, "Yes", "Sì");
        isActive.OptionSet.FalseOption.Value.Should().Be(0);
        ShouldHaveLabels(isActive.OptionSet.FalseOption.Label, "No", "No");
        ShouldHaveLabels(isActive.DisplayName, "Is Active", "Attivo");

        var start = Attribute<DateTimeAttributeMetadata>("xv_project", "xv_startdate");
        start.Format.Should().Be(DateTimeFormat.DateOnly);
        start.DateTimeBehavior.Should().Be(DateTimeBehavior.DateOnly);
        ShouldHaveLabels(start.DisplayName, "Start Date", "Data inizio");
        var createdOn = Attribute<DateTimeAttributeMetadata>("xv_project", "createdon");
        createdOn.Format.Should().Be(DateTimeFormat.DateAndTime);
        createdOn.DateTimeBehavior.Should().Be(DateTimeBehavior.UserLocal);
        createdOn.IsCustomAttribute.Should().BeFalse();
        createdOn.IsValidForCreate.Should().BeFalse();

        var priority = Attribute<PicklistAttributeMetadata>("xv_project", "xv_priority");
        priority.OptionSet.IsGlobal.Should().BeTrue();
        priority.OptionSet.Name.Should().Be("xv_priority");
        priority.DefaultFormValue.Should().Be(100000001);
        ShouldHaveLabels(priority.DisplayName, "Priority", "Priorità");

        var stage = Attribute<PicklistAttributeMetadata>("xv_project", "xv_stage");
        stage.OptionSet.IsGlobal.Should().BeFalse();
        stage.DefaultFormValue.Should().BeNull();

        Attribute<BigIntAttributeMetadata>("xv_project", "versionnumber").IsCustomAttribute.Should().BeFalse();
        Attribute<IntegerAttributeMetadata>("xv_project", "utcconversiontimezonecode").MinValue.Should().Be(-1);
        Attribute<DecimalAttributeMetadata>("xv_project", "exchangerate").Precision.Should().Be(10);

        var state = Attribute<StateAttributeMetadata>("xv_project", "statecode");
        state.OptionSet.Options.Cast<StateOptionMetadata>().Select(o => (o.Value, o.DefaultStatus, o.InvariantName))
            .Should().Equal((0, 1, "Active"), (1, 2, "Inactive"));
        ShouldHaveLabels(state.OptionSet.Options[1].Label, "Inactive", "Inattivo");
        var status = Attribute<StatusAttributeMetadata>("xv_project", "statuscode");
        status.OptionSet.Options.Cast<StatusOptionMetadata>().Select(o => (o.Value, o.State)).Should().Equal((1, 0), (2, 1));
    }

    [Fact]
    public void Reads_lookups_with_targets_from_the_relationships()
    {
        Fixture.Lookups.Should().OnlyContain(l => l.EntityLogicalName != null);

        var project = Fixture.Lookups.Single(l => l.EntityLogicalName == "xv_task" && l.LogicalName == "xv_projectid");
        project.SchemaName.Should().Be("xv_ProjectId");
        project.Targets.Should().Equal("xv_project");
        project.IsCustomAttribute.Should().BeTrue();
        project.AttributeType.Should().Be(AttributeTypeCode.Lookup);
        project.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.Recommended);
        ShouldHaveLabels(project.DisplayName, "Project", "Progetto");

        var createdBy = Fixture.Lookups.Single(l => l.EntityLogicalName == "xv_project" && l.LogicalName == "createdby");
        createdBy.Targets.Should().Equal("systemuser");
        createdBy.IsCustomAttribute.Should().BeFalse();

        var owner = Fixture.Lookups.Single(l => l.EntityLogicalName == "xv_project" && l.LogicalName == "ownerid");
        owner.AttributeType.Should().Be(AttributeTypeCode.Owner);
        owner.Targets.Should().Equal("systemuser", "team");

        Fixture.Lookups.Single(l => l.EntityLogicalName == "xv_tag" && l.LogicalName == "organizationid").Targets.Should().Equal("organization");
        Fixture.Lookups.Should().NotContain(l => l.EntityLogicalName == "xv_tag" && l.LogicalName == "ownerid");
    }

    [Fact]
    public void Reads_global_and_local_option_sets_with_labels()
    {
        var priority = Fixture.OptionSets.Should().ContainSingle().Which.Should().BeOfType<OptionSetMetadata>().Subject;
        priority.Name.Should().Be("xv_priority");
        priority.IsGlobal.Should().BeTrue();
        priority.OptionSetType.Should().Be(OptionSetType.Picklist);
        ShouldHaveLabels(priority.DisplayName, "Priority", "Priorità");
        ShouldHaveLabels(priority.Description, "How urgent the work is.", "Quanto è urgente il lavoro.");
        priority.Options.Select(o => (o.Value, Text(o.Label, 1033), Text(o.Label, 1040)))
            .Should().Equal((100000000, "Low", "Bassa"), (100000001, "Medium", "Media"), (100000002, "High", "Alta"));

        var stage = Attribute<PicklistAttributeMetadata>("xv_project", "xv_stage").OptionSet;
        stage.Name.Should().Be("xv_project_xv_stage");
        stage.OptionSetType.Should().Be(OptionSetType.Picklist);
        ShouldHaveLabels(stage.DisplayName, "Stage", "Fase");
        stage.Options.Select(o => (o.Value, Text(o.Label, 1033), Text(o.Label, 1040)))
            .Should().Equal((100000000, "Draft", "Bozza"), (100000001, "Running", "In corso"), (100000002, "Closed", "Chiuso"));
    }

    [Fact]
    public void Reads_one_to_many_and_many_to_many_relationships()
    {
        Fixture.Relationships.OfType<OneToManyRelationshipMetadata>().Should().HaveCount(25);

        var tasks = Fixture.Relationships.OfType<OneToManyRelationshipMetadata>().Single(r => r.SchemaName == "xv_project_xv_task_ProjectId");
        tasks.ReferencingEntity.Should().Be("xv_task");
        tasks.ReferencingAttribute.Should().Be("xv_projectid");
        tasks.ReferencedEntity.Should().Be("xv_project");
        tasks.ReferencedAttribute.Should().Be("xv_projectid");
        tasks.IsCustomRelationship.Should().BeTrue();
        tasks.IsHierarchical.Should().BeFalse();
        tasks.CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        tasks.CascadeConfiguration.Assign.Should().Be(CascadeType.NoCascade);
        tasks.CascadeConfiguration.Share.Should().Be(CascadeType.NoCascade);
        tasks.CascadeConfiguration.Unshare.Should().Be(CascadeType.NoCascade);
        tasks.CascadeConfiguration.Reparent.Should().Be(CascadeType.NoCascade);
        tasks.CascadeConfiguration.Merge.Should().Be(CascadeType.NoCascade);
        tasks.ReferencingEntityNavigationPropertyName.Should().Be("xv_ProjectId");
        tasks.ReferencedEntityNavigationPropertyName.Should().Be("xv_project_xv_task_ProjectId");

        var createdBy = Fixture.Relationships.OfType<OneToManyRelationshipMetadata>().Single(r => r.SchemaName == "lk_xv_project_createdby");
        createdBy.ReferencedEntity.Should().Be("systemuser");
        createdBy.ReferencingAttribute.Should().Be("createdby");
        createdBy.IsCustomRelationship.Should().BeFalse();
        Fixture.Relationships.OfType<OneToManyRelationshipMetadata>().Single(r => r.SchemaName == "business_unit_xv_project")
            .CascadeConfiguration.Delete.Should().Be(CascadeType.Restrict);

        var tags = Fixture.Relationships.OfType<ManyToManyRelationshipMetadata>().Should().ContainSingle().Subject;
        tags.SchemaName.Should().Be("xv_project_xv_tag");
        tags.Entity1LogicalName.Should().Be("xv_project");
        tags.Entity2LogicalName.Should().Be("xv_tag");
        tags.IntersectEntityName.Should().Be("xv_project_xv_tag");
        tags.IsCustomRelationship.Should().BeTrue();
    }

    [Fact]
    public void Keeps_what_it_does_not_model_with_its_position()
    {
        var package = Read(d =>
        {
            var project = d.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
            project.Element("IsQuickCreateEnabled")!.Value = "1";
            var count = d.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count");
            count.Element("DisplayMask")!.Value = "ValidForAdvancedFind";
            count.Element("IsDataSourceSecret")!.Value = "1";
            count.Element("ImeMode")!.AddAfterSelf(new XElement("SomeFutureSetting", "7"));
        });

        var entity = package.Unmodelled![UnmodelledXml.EntityKey("xv_project")];
        entity.Element("IsQuickCreateEnabled")!.Value.Should().Be("1");
        UnmodelledXml.IsMarker(entity.Element("HasRelatedNotes")!).Should().BeTrue("modelled since plan 2.11 (HasNotes): kept as its position");
        UnmodelledXml.IsMarker(entity.Element("ActivityTypeMask")!).Should().BeTrue();
        UnmodelledXml.IsMarker(entity.Element("attributes")!).Should().BeTrue("a modelled element is kept as a marker of its position");
        entity.Elements().Select(e => e.Name.LocalName).ToList().IndexOf("EntitySetName")
            .Should().BeLessThan(entity.Elements().Select(e => e.Name.LocalName).ToList().IndexOf("IsDuplicateCheckSupported"));

        var count = package.Unmodelled![UnmodelledXml.AttributeKey("xv_project", "xv_count")];
        count.Element("DisplayMask")!.Value.Should().Be("ValidForAdvancedFind");
        count.Element("IsDataSourceSecret")!.Value.Should().Be("1");
        count.Element("ImeMode")!.ElementsAfterSelf().First().Name.LocalName.Should().Be("SomeFutureSetting");
        UnmodelledXml.IsMarker(count.Element("Type")!).Should().BeTrue();
        count.Attribute("PhysicalName").Should().BeNull("a modelled XML attribute is not kept");

        package.Unmodelled!.Keys.Should().Contain([
            UnmodelledXml.RelationshipKey("xv_project_xv_task_ProjectId"), UnmodelledXml.RelationshipKey("xv_project_xv_tag"),
            UnmodelledXml.OptionSetKey("xv_priority"), UnmodelledXml.StepKey(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21")),
            UnmodelledXml.WebResourceKey(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41")),
            UnmodelledXml.AssemblyKey(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10"))]);
        var assembly = package.Unmodelled![UnmodelledXml.AssemblyKey(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10"))];
        assembly.Attribute("CustomizationLevel")!.Value.Should().Be("1");
        assembly.Element("SomeFutureSetting")!.Value.Should().Be("3");
        assembly.Element("PluginTypes")!.Elements("PluginType").First().Attribute("SomeFutureAttribute")!.Value.Should().Be("t");
        package.Unmodelled![UnmodelledXml.OptionSetKey("xv_priority")].Element("options")!.Elements("option").First()
            .Attribute("IsHidden")!.Value.Should().Be("0");
    }

    [Fact]
    public void An_attribute_without_a_display_mask_keeps_none()
    {
        var package = Read(d => d.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count").Element("DisplayMask")!.Remove());

        package.Unmodelled![UnmodelledXml.AttributeKey("xv_project", "xv_count")].Element("DisplayMask").Should().BeNull();
    }

    [Fact]
    public void Reads_the_missing_dependencies()
    {
        var package = Read(solution: d => d.Descendants("MissingDependencies").Single().Add(new XElement("MissingDependency",
            new XElement("Required", new XAttribute("type", "9"), new XAttribute("schemaName", "xv_shared"), new XAttribute("displayName", "Shared"),
                new XAttribute("solution", "Other")),
            new XElement("Dependent", new XAttribute("type", "2"), new XAttribute("schemaName", "xv_level"), new XAttribute("displayName", "Level"),
                new XAttribute("parentSchemaName", "xv_project"), new XAttribute("parentDisplayName", "Project")))));

        package.MissingDependencies.Should().Equal(new PackageMissingDependency(
            new PackageDependencyNode(9, "xv_shared", "Shared", "Other"),
            new PackageDependencyNode(2, "xv_level", "Level", null, "xv_project", "Project")));
    }

    [Fact]
    public void The_fixture_has_no_unread_entries_and_no_declared_missing_dependencies()
    {
        Fixture.KeptFiles.Should().BeEmpty();
        Fixture.DeclaredMissingDependencies.Should().Be(0);
    }

    // ---- the shapes of a real export (tests/fixtures/solutions/RealExportShapes) -----------------------------------------

    private static SolutionPackage ReadRealShapes()
    {
        var (solutionXml, customizationsXml, files) = SolutionZip.Read(SolutionFixtureZip.BuildWithRealShapes());
        return CustomizationsReader.Read(solutionXml, customizationsXml, files);
    }

    [Fact]
    public void A_root_component_with_only_a_parent_id_is_keyed_by_it()
    {
        ReadRealShapes().RootComponents.Should().Contain((20, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01", 0));
    }

    [Fact]
    public void A_root_component_without_id_schema_name_or_parent_id_is_refused()
    {
        var act = () => Read(solution: d => d.Descendants("RootComponent").First(c => (string?)c.Attribute("type") == "20")
            .Attribute("id")!.Remove());

        act.Should().Throw<XrmdFaultException>().WithMessage("*RootComponent of type 20 has neither an id, a schemaName nor a parentId*");
    }

    [Fact]
    public void Real_missing_dependencies_are_counted_and_the_unrepresentable_ones_skipped()
    {
        var package = ReadRealShapes();

        package.DeclaredMissingDependencies.Should().Be(5);
        package.MissingDependencies.Should().Equal(new PackageMissingDependency(
            new PackageDependencyNode(1, "xv_ledger", "Ledger", "LedgerBase (1.0.0.0)"),
            new PackageDependencyNode(80, "xv_fixtureapp", "Fixture App")));
    }

    [Fact]
    public void Archive_entries_no_reader_consumes_are_kept_byte_for_byte()
    {
        var package = ReadRealShapes();

        // The customapis/ entry is read now (plan 15): a custom API, no longer a kept file; so are the environment variables (#167).
        package.KeptFiles.Should().BeEquivalentTo(SolutionFixtureZip.RealShapesEntries()
            .Where(e => !e.Key.StartsWith("customapis/", StringComparison.Ordinal)
                        && !e.Key.StartsWith("environmentvariabledefinitions/", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value));
        package.CustomApis.Should().ContainSingle().Which.UniqueName.Should().Be("xv_PickNextTask");
        package.EnvironmentVariables.Should().ContainSingle().Which.SchemaName.Should().Be("xv_ServiceUrl");
        package.KeptFiles!.Keys.Should().NotContain(k => k.StartsWith("WebResources/", StringComparison.OrdinalIgnoreCase)
                                                         || k.StartsWith("PluginAssemblies/", StringComparison.OrdinalIgnoreCase)
                                                         || k == SolutionZip.ContentTypesEntry);
    }

    [Fact]
    public void Reads_roles_with_privilege_names_and_depths()
    {
        var role = Fixture.Roles.Should().ContainSingle().Subject;
        role.Id.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01"));
        role.Name.Should().Be("Xrmverse Fixture User");
        role.Privileges.Should().Equal(
            ("prvCreatexv_Project", DepthMask.Basic), ("prvReadxv_Project", DepthMask.Basic), ("prvWritexv_Project", DepthMask.Basic),
            ("prvReadxv_Tag", DepthMask.Global), ("prvReadxv_Task", DepthMask.Local), ("prvAppendToxv_Project", DepthMask.Deep));
    }

    [Fact]
    public void Unknown_privilege_level_is_an_explicit_error()
    {
        var act = () => Read(d => d.Descendants("RolePrivilege").First().SetAttributeValue("level", "Everywhere"));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains("prvCreatexv_Project") && e.Message.Contains("Everywhere"));
    }

    [Fact]
    public void Reads_the_workflow_activity_group_name_of_a_plugin_type()
    {
        // Plan 2.14, W17: a type with a WorkflowActivityGroupName is a custom workflow activity; the others have none.
        var package = Read(d => d.Descendants("PluginType").First()
            .Add(new XElement("WorkflowActivityGroupName", "Xrmd.TestPlugins (1.0.0.0)")));

        var types = package.Assemblies.Single().Types;
        types[0].WorkflowActivityGroupName.Should().Be("Xrmd.TestPlugins (1.0.0.0)");
        types[1].WorkflowActivityGroupName.Should().BeNull();
        Fixture.Assemblies.Single().Types.Should().OnlyContain(t => t.WorkflowActivityGroupName == null);
    }

    [Fact]
    public void Reads_plugin_assembly_types_steps_and_images()
    {
        var assembly = Fixture.Assemblies.Should().ContainSingle().Subject;
        assembly.Id.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10"));
        assembly.Name.Should().Be("Xrmd.TestPlugins");
        assembly.FullName.Should().Be("Xrmd.TestPlugins, Version=1.0.0.0, Culture=neutral, PublicKeyToken=a15f04ad73430aaf");
        assembly.IsolationMode.Should().Be(2);
        assembly.SourceType.Should().Be(0);
        assembly.Content.Should().Equal(SolutionFixtureZip.TestPluginBytes);
        assembly.Types.Select(t => (t.Id, t.TypeName)).Should().Equal(
            (Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e11"), "Xrmd.TestPlugins.SetAttributePlugin"),
            (Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e12"), "Xrmd.TestPlugins.ContextRecorderPlugin"));
        assembly.Types[0].AssemblyQualifiedName.Should().Be("Xrmd.TestPlugins.SetAttributePlugin, " + assembly.FullName);
        assembly.Types[0].FriendlyName.Should().Be("1f0b5c7e-2d3a-4b6c-9e8f-7a6b5c4d3e11");

        Fixture.Steps.Should().HaveCount(2);
        var create = Fixture.Steps[0];
        create.Id.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21"));
        create.Name.Should().Be("Xrmd.TestPlugins.SetAttributePlugin: Create of xv_project");
        create.SdkMessageId.Should().Be(Guid.Parse("9ebdbb1b-ea3e-db11-86a7-000a3a5473e8"));
        create.SdkMessageFilterId.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e51"));
        create.PrimaryEntity.Should().Be("xv_project");
        create.PluginTypeId.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e11"));
        create.PluginTypeName.Should().Be("Xrmd.TestPlugins.SetAttributePlugin, " + assembly.FullName);
        (create.Stage, create.Mode, create.Rank, create.SupportedDeployment, create.InvocationSource).Should().Be((20, 0, 1, 0, 0));
        create.AsyncAutoDelete.Should().BeFalse();
        create.FilteringAttributes.Should().BeNull();
        create.Configuration.Should().Be("xv_description=set by the fixture pre-operation create step");
        create.Description.Should().Be("Pre-operation Create: sets xv_description on the Target.");
        create.ImpersonatingUserId.Should().BeNull();
        create.Disabled.Should().BeFalse();
        create.Images.Should().BeEmpty();

        var update = Fixture.Steps[1];
        update.SdkMessageId.Should().Be(Guid.Parse("20bebb1b-ea3e-db11-86a7-000a3a5473e8"));
        (update.Stage, update.Mode, update.Rank).Should().Be((40, 0, 2));
        update.FilteringAttributes.Should().Be("xv_name,xv_budget");
        update.Configuration.Should().Be("pb_pipelog");
        var image = update.Images.Should().ContainSingle().Subject;
        image.Should().Be(new PackageStepImage(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e23"), "PreImage", "PreImage", 0, "Target",
            "xv_name,xv_budget"));
    }

    [Fact]
    public void Reads_opaque_components_and_web_resources()
    {
        Fixture.Opaque.Select(o => (o.Type, o.Id, o.EntityLogicalName)).Should().BeEquivalentTo([
            (ComponentType.SystemForm, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e32", "xv_project"),
            (ComponentType.SavedQuery, "6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e31", "xv_project"),
            (ComponentType.SiteMap, "xv_fixtureapp", (string?)null),
            (ComponentType.AppModule, "xv_fixtureapp", (string?)null),
        ]);
        Fixture.Opaque.Single(o => o.Type == ComponentType.AppModule).Xml.Element("AppModuleComponents")!.Elements().Should().HaveCount(5);
        Fixture.Opaque.Single(o => o.Type == ComponentType.SavedQuery).Xml.Element("fetchxml")!.Element("fetch").Should().NotBeNull();

        var script = Fixture.WebResources.Should().ContainSingle().Subject;
        script.Id.Should().Be(Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41"));
        script.Name.Should().Be("xv_/scripts/project.js");
        script.DisplayName.Should().Be("project.js");
        script.WebResourceType.Should().Be(3);
        script.Content.Should().Equal(SolutionFixtureZip.ReadFixtureFile("WebResources/xv_/scripts/project.js6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E41"));
    }

    [Fact]
    public void Unknown_elements_are_ignored_and_unsupported_components_preserved()
    {
        var package = Read(d =>
        {
            var root = d.Root!;
            root.Element("Workflows")!.Add(new XElement("Workflow", new XAttribute("WorkflowId", "{0f8c6a3e-0000-4000-8000-000000000001}"),
                new XAttribute("Name", "Notify"), new XElement("XamlFileName", "/Workflows/Notify.xaml")));
            root.Add(new XElement("SomeFutureComponents"));
            root.Descendants("attribute").First(a => (string?)a.Attribute("PhysicalName") == "xv_Count")
                .Add(new XElement("SomeFutureAttributeSetting", "1"));
            root.Descendants("entity").First().Add(new XElement("SomeFutureEntitySetting", "1"));
            root.Descendants("SdkMessageProcessingStep").First().Add(new XElement("SomeFutureStepSetting", "1"));
        });

        var workflows = package.Unsupported.Should().ContainSingle().Subject;
        workflows.Name.LocalName.Should().Be("Workflows");
        workflows.Element("Workflow")!.Attribute("Name")!.Value.Should().Be("Notify");
        package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.Should().Contain(a => a.LogicalName == "xv_count");
        package.Steps.Should().HaveCount(2);
    }

    [Fact]
    public void A_webhook_step_is_an_explicit_error()
    {
        var act = () => Read(d => d.Root!.Descendants("SdkMessageProcessingStep").First().Element("EventHandlerTypeCode")!.Value = "4618");

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == "Webhooks and their steps cannot be added to a solution yet.");
    }

    [Fact]
    public void Service_endpoints_are_an_explicit_error()
    {
        var act = () => Read(d => d.Root!.Add(new XElement("ServiceEndpoints", new XElement("ServiceEndpoint",
            new XAttribute("ServiceEndpointId", "{0f8c6a3e-0000-4000-8000-000000000002}"), new XAttribute("Name", "orders hook")))));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == "Webhooks and their steps cannot be added to a solution yet.");
    }

    [Theory]
    [InlineData("partylist")]
    [InlineData("virtual")]
    [InlineData("managedproperty")]
    public void An_attribute_type_xrmverse_does_not_support_is_an_explicit_error(string type)
    {
        var act = () => Read(d => d.Descendants("attribute").First(a => (string?)a.Attribute("PhysicalName") == "xv_Count")
            .Element("Type")!.Value = type);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == $"Attribute 'xv_project.xv_count' has type '{type}', which xrmverse does not support.");
    }

    [Fact]
    public void A_custom_lookup_without_its_relationship_is_an_explicit_error()
    {
        var act = () => Read(d => d.Descendants("EntityRelationship")
            .Single(r => (string?)r.Attribute("Name") == "xv_project_xv_task_ProjectId").Remove());

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains("xv_task.xv_projectid"));
    }

    [Fact]
    public void A_plugin_assembly_whose_file_is_missing_is_an_explicit_error()
    {
        var zip = SolutionFixtureZip.Build(d => d.Descendants("PluginAssembly").Single().Element("FileName")!.Value = "/PluginAssemblies/Missing/Missing.dll",
            omit: "PluginAssemblies/Missing/Missing.dll");
        var (solution, customizations, files) = SolutionZip.Read(zip);
        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains("PluginAssemblies/Missing/Missing.dll") && e.Message.Contains("Xrmd.TestPlugins"));
    }

    [Theory]
    [InlineData("xv_project_SyncErrors", "syncerror")]
    [InlineData("xv_project_AsyncOperations", "asyncoperation")]
    public void Platform_relationships_whose_lookup_is_not_in_the_file_are_not_custom(string name, string referencing)
    {
        var relationship = Fixture.Relationships.OfType<OneToManyRelationshipMetadata>().Single(r => r.SchemaName == name);

        relationship.ReferencingEntity.Should().Be(referencing);
        relationship.ReferencingAttribute.Should().Be("regardingobjectid");
        relationship.ReferencedEntity.Should().Be("xv_project");
        relationship.IsCustomRelationship.Should().BeFalse();
        relationship.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
    }

    [Fact]
    public void IsCustomRelationship_in_the_file_wins()
    {
        var package = Read(d =>
        {
            Relationship(d, "xv_project_SyncErrors").Add(new XElement("IsCustomRelationship", "1"));
            Relationship(d, "xv_project_xv_task_ProjectId").Add(new XElement("IsCustomRelationship", "0"));
        });

        package.Relationships.Single(r => r.SchemaName == "xv_project_SyncErrors").IsCustomRelationship.Should().BeTrue();
        package.Relationships.Single(r => r.SchemaName == "xv_project_xv_task_ProjectId").IsCustomRelationship.Should().BeFalse();
    }

    [Fact]
    public void A_many_to_many_relationship_is_custom_when_its_name_has_the_publisher_prefix()
    {
        var package = Read(d =>
        {
            var copy = new XElement(Relationship(d, "xv_project_xv_tag"));
            copy.SetAttributeValue("Name", "acme_project_tag");
            Relationship(d, "xv_project_xv_tag").AddAfterSelf(copy);
            var explicitCopy = new XElement(copy);
            explicitCopy.SetAttributeValue("Name", "acme_project_tag_explicit");
            explicitCopy.Add(new XElement("IsCustomRelationship", "1"));
            copy.AddAfterSelf(explicitCopy);
        });

        var manyToMany = package.Relationships.OfType<ManyToManyRelationshipMetadata>().ToDictionary(r => r.SchemaName);
        manyToMany["xv_project_xv_tag"].IsCustomRelationship.Should().BeTrue();
        manyToMany["acme_project_tag"].IsCustomRelationship.Should().BeFalse();
        manyToMany["acme_project_tag_explicit"].IsCustomRelationship.Should().BeTrue();
    }

    [Theory]
    [InlineData("1", "Calculated column 'xv_project.xv_count' has no FormulaDefinitionFileName.")]
    [InlineData("2", "Rollup column 'xv_project.xv_count' has no FormulaDefinitionFileName.")]
    [InlineData("3", "Formula column 'xv_project.xv_count' has no FormulaDefinitionFileName.")]
    [InlineData("4", "Prompt columns are not supported: 'xv_project.xv_count'.")]
    public void Calculated_rollup_and_other_custom_columns_are_an_explicit_error(string sourceType, string message)
    {
        var act = () => Read(d => AttributeElement(d, "xv_Count").Element("SourceType")!.Value = sourceType);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == message);
    }

    [Fact]
    public void A_formula_definition_file_on_a_simple_custom_column_is_an_explicit_error()
    {
        var act = () => Read(d => AttributeElement(d, "xv_Count").Add(new XElement("FormulaDefinitionFileName", "/Formulas/xv_project-xv_count.xaml")));

        act.Should().Throw<XrmdFaultException>()
            .WithMessage("Column 'xv_project.xv_count' has a FormulaDefinitionFileName but SourceType 0: only a calculated (SourceType 1), rollup (SourceType 2) or formula (SourceType 3) column has a formula.");
    }

    [Fact]
    public void The_autonumber_format_of_a_string_column_is_read()
    {
        var package = Read(d => AttributeElement(d, "xv_Name").Element("AutoNumberFormat")!.Value = "PRJ-{SEQNUM:5}");

        var name = (StringAttributeMetadata)package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.Single(a => a.LogicalName == "xv_name");
        name.AutoNumberFormat.Should().Be("PRJ-{SEQNUM:5}");
    }

    [Fact]
    public void An_empty_autonumber_format_of_a_string_column_reads_as_no_format()
    {
        var package = Read(_ => { });

        var name = (StringAttributeMetadata)package.Entities.Single(e => e.LogicalName == "xv_project").Attributes.Single(a => a.LogicalName == "xv_name");
        name.AutoNumberFormat.Should().BeEmpty("an empty element removes the format of an existing column on import");
    }

    [Fact]
    public void An_autonumber_format_on_another_type_is_an_explicit_error()
    {
        var act = () => Read(d => AttributeElement(d, "xv_Count").Element("AutoNumberFormat")!.Value = "N-{SEQNUM:5}");

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message.Contains("'xv_project.xv_count'") && e.Message.Contains("only a string column can have one"));
    }

    [Fact]
    public void A_system_attribute_of_an_unsupported_type_is_skipped_and_recorded()
    {
        var package = Read(d =>
        {
            AttributeElement(d, "OverriddenCreatedOn").Element("Type")!.Value = "partylist";
            AttributeElement(d, "CreatedOn").Element("SourceType")!.Value = "1";
        });

        package.SkippedAttributes.Should().Equal(("xv_project", "overriddencreatedon", "partylist"));
        var project = package.Entities.Single(e => e.LogicalName == "xv_project");
        project.Attributes.Should().NotContain(a => a.LogicalName == "overriddencreatedon");
        project.Attributes.Should().Contain(a => a.LogicalName == "createdon");
        Fixture.SkippedAttributes.Should().BeEmpty();
    }

    [Fact]
    public void Non_empty_entity_ribbons_and_control_configs_are_preserved_with_their_entity()
    {
        Fixture.EntityUnsupported.Should().BeEmpty("the ribbons of the fixture are empty templates");

        var package = Read(d =>
        {
            var entity = EntityElement(d, "xv_Task");
            entity.Element("RibbonDiffXml")!.Element("CustomActions")!.Add(new XElement("CustomAction",
                new XAttribute("Id", "xv.task.Hello"), new XAttribute("Location", "Mscrm.Form.xv_task.MainTab.Actions.Controls._children"),
                new XAttribute("Sequence", "10")));
            entity.Add(new XElement("CustomControlDefaultConfigs"));
            entity.Add(new XElement("Unknown", new XElement("Part")));
        });

        package.EntityUnsupported.Select(p => (p.EntityLogicalName, p.Xml.Name.LocalName))
            .Should().Equal(("xv_task", "RibbonDiffXml"), ("xv_task", "Unknown"));
        package.EntityUnsupported[0].Xml.Descendants("CustomAction").Should().ContainSingle();
    }

    [Fact]
    public void Visualizations_are_read_as_chart_components_of_their_entity()
    {
        var package = Read(SolutionFixtureZip.AddCharts);

        package.Opaque.Where(o => o.Type == ComponentType.SavedQueryVisualization).Select(o => (o.Id, o.EntityLogicalName)).Should().Equal(
            (SolutionFixtureZip.AmountChartId.ToString("D"), "xv_project"), (SolutionFixtureZip.CountChartId.ToString("D"), "xv_project"));
        var amount = package.Opaque.First(o => o.Type == ComponentType.SavedQueryVisualization).Xml;
        amount.Name.LocalName.Should().Be("visualization");
        amount.Element("datadescription")!.Element("datadefinition").Should().NotBeNull();
        package.Sources!.Should().ContainKey((ComponentType.SavedQueryVisualization, SolutionFixtureZip.AmountChartId.ToString("D")))
            .WhoseValue.Entity.Should().Be("xv_project");
        package.EntityUnsupported.Should().NotContain(p => p.Xml.Name.LocalName == "Visualizations", "the charts are applied, not kept");
    }

    [Fact]
    public void Visualization_without_an_id_is_refused()
    {
        var act = () => Read(d =>
        {
            SolutionFixtureZip.AddCharts(d);
            d.Descendants("visualization").First().Element("savedqueryvisualizationid")!.Remove();
        });

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("a chart of xv_project");
    }

    [Fact]
    public void Visualizations_are_written_back_under_their_entity_after_the_views()
    {
        var original = XDocument.Load(Path.Combine(SolutionFixtureZip.FixtureDirectory, "customizations.xml"));
        SolutionFixtureZip.AddCharts(original);
        var package = Read(SolutionFixtureZip.AddCharts);

        var written = CustomizationsWriter.Write(package).Customizations;

        var project = EntityElement(written, "xv_Project");
        project.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("SavedQueries", "Visualizations", "RibbonDiffXml");
        XNode.DeepEquals(Normalized(project.Element("Visualizations")!), Normalized(EntityElement(original, "xv_Project").Element("Visualizations")!))
            .Should().BeTrue("the charts are written back as they were read");
    }

    [Fact]
    public void Kept_visualizations_of_an_import_before_ui_1f_merge_with_the_chart_rows_by_id()
    {
        // UI-1f final review M1: an import from before UI-1f kept the entity's Visualizations as unsupported XML. Once chart rows
        // exist for that entity, the export writes one Visualizations node: the rows, then the kept charts that are no row.
        var original = XDocument.Load(Path.Combine(SolutionFixtureZip.FixtureDirectory, "customizations.xml"));
        SolutionFixtureZip.AddCharts(original);
        var keptNode = new XElement(EntityElement(original, "xv_Project").Element("Visualizations")!);
        var stale = new XElement(keptNode.Elements("visualization").First());
        var oldId = Guid.Parse("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e6f");
        stale.Element("savedqueryvisualizationid")!.Value = oldId.ToString("B").ToUpperInvariant();
        keptNode.Add(stale);
        var package = Read(SolutionFixtureZip.AddCharts);
        package = package with { EntityUnsupported = [.. package.EntityUnsupported, ("xv_project", keptNode)] };

        var project = EntityElement(CustomizationsWriter.Write(package).Customizations, "xv_Project");

        project.Elements("Visualizations").Should().ContainSingle("the kept node and the chart rows are one node");
        project.Element("Visualizations")!.Elements("visualization")
            .Select(v => Guid.Parse(v.Element("savedqueryvisualizationid")!.Value))
            .Should().Equal(SolutionFixtureZip.AmountChartId, SolutionFixtureZip.CountChartId, oldId);
        project.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("SavedQueries", "Visualizations", "RibbonDiffXml");
    }

    [Fact]
    public void Dashboards_are_read_as_forms_of_no_table()
    {
        var package = Read(SolutionFixtureZip.AddDashboards);

        var dashboards = package.Opaque.Where(o => o.Type == ComponentType.SystemForm && o.EntityLogicalName is null).ToList();
        dashboards.Select(o => (o.Id, o.FormType)).Should().Equal(
            (SolutionFixtureZip.OverviewDashboardId.ToString("D"), "dashboard"), (SolutionFixtureZip.ChartsDashboardId.ToString("D"), "dashboard"));
        dashboards[0].Xml.Name.LocalName.Should().Be("Dashboard");
        package.Sources!.Should().ContainKey((ComponentType.SystemForm, SolutionFixtureZip.OverviewDashboardId.ToString("D")))
            .WhoseValue.Entity.Should().BeNull();
        package.Unsupported.Should().NotContain(u => u.Name.LocalName == "Dashboards", "the dashboards are applied, not kept");
    }

    [Fact]
    public void Dashboard_without_a_form_id_is_refused()
    {
        var act = () => Read(d =>
        {
            SolutionFixtureZip.AddDashboards(d);
            d.Descendants("Dashboard").First().Element("FormId")!.Remove();
        });

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("FormId for a dashboard");
    }

    [Fact]
    public void Dashboards_are_written_back_under_the_root_after_the_option_sets()
    {
        var original = XDocument.Load(Path.Combine(SolutionFixtureZip.FixtureDirectory, "customizations.xml"));
        SolutionFixtureZip.AddDashboards(original);
        var package = Read(SolutionFixtureZip.AddDashboards);

        var written = CustomizationsWriter.Write(package).Customizations;

        written.Root!.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("optionsets", "Dashboards", "WebResources");
        XNode.DeepEquals(Normalized(written.Root!.Element("Dashboards")!), Normalized(original.Root!.Element("Dashboards")!))
            .Should().BeTrue("the dashboards are written back as they were read");
        written.Root!.Element("Entities")!.Descendants("Dashboard").Should().BeEmpty();
        CustomizationsWriter.Write(Read()).Customizations.Root!.Element("Dashboards").Should().BeNull("a file without dashboards has no node");
    }

    /// <summary>The element with its insignificant whitespace dropped.</summary>
    private static XElement Normalized(XElement element) => XElement.Parse(element.ToString(SaveOptions.DisableFormatting), LoadOptions.None);

    [Fact]
    public void Forms_carry_the_type_of_their_forms_element()
    {
        Fixture.Opaque.Single(o => o.Type == ComponentType.SystemForm).FormType.Should().Be("main");

        var package = Read(d => EntityElement(d, "xv_Project").Element("FormXml")!.Add(new XElement("forms", new XAttribute("type", "quick"),
            new XElement("systemform", new XElement("formid", "{6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e37}"), new XElement("form")))));

        package.Opaque.Where(o => o.Type == ComponentType.SystemForm).Select(o => (o.Id, o.FormType)).Should().Equal(
            ("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e32", "main"), ("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e37", "quick"));
        package.Opaque.Where(o => o.Type != ComponentType.SystemForm).Should().OnlyContain(o => o.FormType == null);
    }

    private static XElement Relationship(XDocument d, string name) =>
        d.Descendants("EntityRelationship").Single(r => (string?)r.Attribute("Name") == name);

    private static XElement AttributeElement(XDocument d, string physicalName) =>
        d.Descendants("attribute").First(a => (string?)a.Attribute("PhysicalName") == physicalName);

    [Fact]
    public void Solution_reader_reads_AutoCreateAccessTeams()
    {
        Entity("xv_project").AutoCreateAccessTeams.Should().BeFalse("the fixture's tables have AutoCreateAccessTeams 0");

        var package = Read(c => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project")
            .Element("AutoCreateAccessTeams")!.Value = "1");
        package.Entities.Single(e => e.LogicalName == "xv_project").AutoCreateAccessTeams.Should().BeTrue();
        package.Entities.Single(e => e.LogicalName == "xv_task").AutoCreateAccessTeams.Should().BeFalse();
        // Modelled now: the kept XML has a marker in its place, not the value.
        var kept = package.Unmodelled![UnmodelledXml.EntityKey("xv_project")];
        UnmodelledXml.IsMarker(kept.Element("AutoCreateAccessTeams")!).Should().BeTrue();

        var absent = Read(c => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project")
            .Element("AutoCreateAccessTeams")!.Remove());
        absent.Entities.Single(e => e.LogicalName == "xv_project").AutoCreateAccessTeams.Should().BeNull("a file without the element says nothing");
    }

    [Fact]
    public void Solution_reader_reads_ChangeTrackingEnabled()
    {
        var package = Read();
        package.Entities.Single(e => e.LogicalName == "xv_project").ChangeTrackingEnabled.Should().BeTrue("the fixture's xv_Project has 1");
        package.Entities.Single(e => e.LogicalName == "xv_task").ChangeTrackingEnabled.Should().BeFalse();
        // Modelled now: the kept XML has markers in place of the flag and of the computed CanChangeTrackingBeEnabled.
        var kept = package.Unmodelled![UnmodelledXml.EntityKey("xv_project")];
        UnmodelledXml.IsMarker(kept.Element("ChangeTrackingEnabled")!).Should().BeTrue();
        UnmodelledXml.IsMarker(kept.Element("CanChangeTrackingBeEnabled")!).Should().BeTrue();
        // The computed flag is never read from a file.
        package.Entities.Single(e => e.LogicalName == "xv_project").CanChangeTrackingBeEnabled.Should().BeNull();

        var absent = Read(c => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project")
            .Element("ChangeTrackingEnabled")!.Remove());
        absent.Entities.Single(e => e.LogicalName == "xv_project").ChangeTrackingEnabled.Should().BeNull("a file without the element says nothing");
    }

    [Fact]
    public void Solution_reader_reads_IsDuplicateCheckSupported()
    {
        XElement Project(XDocument c) => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
        var package = Read(c => Project(c).Element("IsDuplicateCheckSupported")!.Value = "1");
        package.Entities.Single(e => e.LogicalName == "xv_project").IsDuplicateDetectionEnabled!.Value.Should().BeTrue();
        package.Entities.Single(e => e.LogicalName == "xv_task").IsDuplicateDetectionEnabled!.Value.Should().BeFalse("the fixture has 0");
        // Modelled now (plan 2.15, D18): the kept XML has a marker in place of the flag.
        UnmodelledXml.IsMarker(package.Unmodelled![UnmodelledXml.EntityKey("xv_project")].Element("IsDuplicateCheckSupported")!).Should().BeTrue();

        var absent = Read(c => Project(c).Element("IsDuplicateCheckSupported")!.Remove());
        absent.Entities.Single(e => e.LogicalName == "xv_project").IsDuplicateDetectionEnabled.Should().BeNull("a file without the element says nothing");
    }

    [Fact]
    public void Import_keeps_DuplicateRules_node_with_warning()
    {
        // Duplicate rules (D18): the node is kept for export, not applied (the import logs the unsupported nodes as warnings).
        var package = Read(c => c.Root!.Element("Entities")!.AddAfterSelf(
            new XElement("DuplicateRules", new XElement("DuplicateRule", new XAttribute("uniquename", "xv_rule"), new XElement("Name", "Rule")))));
        package.Unsupported.Should().ContainSingle(u => u.Name.LocalName == "DuplicateRules")
            .Which.Element("DuplicateRule")!.Attribute("uniquename")!.Value.Should().Be("xv_rule");
        CustomizationsWriter.Write(package).Customizations.Root!.Element("DuplicateRules")!.Element("DuplicateRule")!.Element("Name")!.Value
            .Should().Be("Rule", "the export writes it back");
    }

    /// <summary>An <c>EntityMaps</c> node (#143) with <paramref name="maps"/>, in place of the fixture's empty one.</summary>
    private static Action<XDocument> WithEntityMaps(params XElement[] maps) => d =>
        d.Root!.Element("EntityMaps")!.ReplaceWith(new XElement("EntityMaps", maps));

    private static XElement EntityMap(string? source, string? target, params (string? Source, string? Target)[] attributes) =>
        new("EntityMap",
            source is null ? null : new XElement("EntitySource", source),
            target is null ? null : new XElement("EntityTarget", target),
            new XElement("AttributeMaps", attributes.Select(a => new XElement("AttributeMap",
                a.Source is null ? null : new XElement("AttributeSource", a.Source),
                a.Target is null ? null : new XElement("AttributeTarget", a.Target)))));

    [Fact]
    public void Reads_entity_maps_lower_cased()
    {
        var package = Read(WithEntityMaps(
            EntityMap("Account", "xv_Project", ("Telephone1", "xv_Phone"), ("name", "xv_customername")),
            EntityMap("xv_project", "xv_task", ("xv_code", "xv_projectcode"))));

        package.EntityMaps.Should().BeEquivalentTo(new[]
        {
            new PackageEntityMap("account", "xv_project", [new("telephone1", "xv_phone"), new("name", "xv_customername")]),
            new PackageEntityMap("xv_project", "xv_task", [new("xv_code", "xv_projectcode")]),
        }, o => o.WithStrictOrdering());
        Fixture.EntityMaps.Should().BeEmpty("the fixture's EntityMaps node is empty");
    }

    [Fact]
    public void Keeps_entity_maps_node_for_export()
    {
        var package = Read(WithEntityMaps(EntityMap("account", "xv_project", ("telephone1", "xv_phone"))));

        package.Unsupported.Should().ContainSingle(u => u.Name.LocalName == "EntityMaps");
        CustomizationsWriter.Write(package).Customizations.Root!.Element("EntityMaps")!.Descendants("AttributeTarget").Single().Value
            .Should().Be("xv_phone", "the export writes the file's maps back");
    }

    [Fact]
    public void Entity_map_without_target_is_refused()
    {
        var act = () => Read(WithEntityMaps(EntityMap("account", null, ("telephone1", "xv_phone"))));

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("EntityTarget").And.Contain("an entity map");
    }

    [Fact]
    public void Attribute_map_without_source_is_refused()
    {
        var act = () => Read(WithEntityMaps(EntityMap("account", "xv_project", (null, "xv_phone"))));

        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("AttributeSource").And.Contain("'account' to 'xv_project'");
    }

    private static XElement EntityElement(XDocument d, string schemaName) =>
        d.Descendants("Entity").Single(e => e.Element("Name")!.Value == schemaName);
}
