using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 3.5 UI-1a, task 1: the embedded seed definitions of the app module tables, <c>userquery</c> and the two new
/// <c>usersettings</c> columns, with the types, lengths and options of Learn's table references (no database).
/// </summary>
public class AppModuleSeedTests
{
    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static T Column<T>(EntityMetadata e, string name) where T : AttributeMetadata =>
        e.Attributes.Should().ContainSingle(a => a.LogicalName == name, name).Subject.Should().BeAssignableTo<T>(name).Subject;

    private static IEnumerable<(int, string)> Options(EntityMetadata e, string name) =>
        Column<EnumAttributeMetadata>(e, name).OptionSet!.Options.Select(o => (o.Value!.Value, o.Label.UserLocalizedLabel.Label));

    [Fact]
    public void Seed_has_app_module_tables_with_learn_columns()
    {
        var app = Seed("appmodule");
        Column<UniqueIdentifierAttributeMetadata>(app, "appmoduleidunique");
        foreach (var guid in new[] { "webresourceid", "welcomepageid" }) Column<UniqueIdentifierAttributeMetadata>(app, guid);
        (Column<IntegerAttributeMetadata>(app, "clienttype").MinValue, Column<IntegerAttributeMetadata>(app, "clienttype").MaxValue).Should().Be((1, 31));
        (Column<IntegerAttributeMetadata>(app, "formfactor").MinValue, Column<IntegerAttributeMetadata>(app, "formfactor").MaxValue).Should().Be((1, 8));
        Column<StringAttributeMetadata>(app, "url").MaxLength.Should().Be(200);
        Column<StringAttributeMetadata>(app, "appmoduleversion").MaxLength.Should().Be(48);
        Options(app, "navigationtype").Should().Equal((0, "Single session"), (1, "Multi session"));
        Column<EnumAttributeMetadata>(app, "navigationtype").OptionSet.Name.Should().Be("appmodule_navigationtype");
        foreach (var flag in new[] { "isdefault", "isfeatured" }) Column<BooleanAttributeMetadata>(app, flag).DefaultValue.Should().BeFalse(flag);
        Column<LookupAttributeMetadata>(app, "publisherid").Targets.Should().Equal("publisher");
        // statecode and statuscode are generated for every table (Active/Inactive), so the seed does not repeat them.
        app.ManyToOneRelationships.Should().ContainSingle(r => r.SchemaName == "publisher_appmodule" && r.ReferencingAttribute == "publisherid");

        var component = Seed(AppModuleSchema.Component);
        (component.EntitySetName, component.OwnershipType).Should().Be(("appmodulecomponents", OwnershipTypes.OrganizationOwned));
        Column<LookupAttributeMetadata>(component, "appmoduleidunique").Targets.Should().Equal("appmodule");
        Options(component, "componenttype").Should().Equal(
            (1, "Entities"), (26, "Views"), (29, "Business Process Flows"), (48, "Command (Ribbon) for Forms, Grids, sub grids"),
            (59, "Charts"), (60, "Forms"), (62, "Sitemap"));
        Options(component, "rootcomponentbehavior").Should().Equal((0, "Include Subcomponents"), (1, "Do not include subcomponents"), (2, "Include As Shell Only"));
        foreach (var guid in new[] { "objectid", "rootappmodulecomponentid" }) Column<UniqueIdentifierAttributeMetadata>(component, guid);
        foreach (var flag in new[] { "ismetadata", "exempt" }) Column<BooleanAttributeMetadata>(component, flag).DefaultValue.Should().BeFalse(flag);
        var link = component.ManyToOneRelationships.Should().ContainSingle().Subject;
        (link.SchemaName, link.ReferencedEntity, link.ReferencingAttribute, link.CascadeConfiguration.Delete)
            .Should().Be((AppModuleSchema.ComponentToApp, "appmodule", "appmoduleidunique", CascadeType.Cascade));

        var roles = Seed(AppModuleSchema.RolesIntersect);
        (roles.IsIntersect, roles.EntitySetName, roles.ObjectTypeCode, roles.PrimaryIdAttribute).Should().Be((true, "appmodulerolescollection", 9009, "appmoduleroleid"));
        roles.Attributes.Select(a => a.LogicalName).Should().Equal("appmoduleid", "roleid");

        var view = Seed(AppModuleSchema.UserQuery);
        (view.EntitySetName, view.ObjectTypeCode, view.OwnershipType, view.PrimaryNameAttribute)
            .Should().Be(("userqueries", AppModuleSchema.UserQueryTypeCode, OwnershipTypes.UserOwned, "name"));
        AppModuleSchema.UserQueryTypeCode.Should().Be(4230);
        Column<StringAttributeMetadata>(view, "name").MaxLength.Should().Be(200);
        Column<StringAttributeMetadata>(view, "name").RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        Column<MemoAttributeMetadata>(view, "description").MaxLength.Should().Be(2000);
        Column<MemoAttributeMetadata>(view, "fetchxml").RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        Column<MemoAttributeMetadata>(view, "layoutxml");
        Column<IntegerAttributeMetadata>(view, "querytype");
        Column<UniqueIdentifierAttributeMetadata>(view, "parentqueryid");
        Column<StringAttributeMetadata>(view, "returnedtypecode");
        Column<StateAttributeMetadata>(view, "statecode").OptionSet.Options.Select(o => (o.Value!.Value, o.Label.UserLocalizedLabel.Label))
            .Should().Equal((0, "Active"), (1, "Inactive"));
        Options(view, "statuscode").Should().Equal((1, "Active"), (2, "Inactive"), (3, "All"));
        foreach (var unmodelled in new[] { "columnsetxml", "conditionalformatting", "advancedgroupby", "layoutjson", "offlinesqlquery", "enablecrosspartition" })
            view.Attributes.Should().NotContain(a => a.LogicalName == unmodelled, unmodelled);

        var settings = Seed("usersettings");
        Column<IntegerAttributeMetadata>(settings, "uilanguageid");
        Column<IntegerAttributeMetadata>(settings, AppModuleSchema.PagingLimit);
        AppModuleSchema.DefaultPagingLimit.Should().Be(50);
    }

    /// <summary>#119: the last form used per user and table, as Learn's UserEntityUISettings table reference names it.</summary>
    [Fact]
    public void Userentityuisettings_is_seeded()
    {
        var settings = Seed(AppModuleSchema.UserEntityUiSettings);
        (settings.SchemaName, settings.EntitySetName, settings.ObjectTypeCode, settings.OwnershipType, settings.PrimaryIdAttribute)
            .Should().Be(("UserEntityUISettings", "userentityuisettingsset", 2500, OwnershipTypes.UserOwned, "userentityuisettingsid"));
        Column<StringAttributeMetadata>(settings, "objecttypecode").RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        Column<MemoAttributeMetadata>(settings, "lastviewedformxml").SchemaName.Should().Be("LastViewedFormXml");
    }

    /// <summary>#117: the organization's auto-save switch, on by default as in Dataverse.</summary>
    [Fact]
    public void Organization_has_autosave_on_by_default()
    {
        var column = Column<BooleanAttributeMetadata>(Seed("organization"), AppModuleSchema.AutoSaveEnabled);
        (column.SchemaName, column.DefaultValue, column.RequiredLevel.Value).Should().Be(("IsAutoSaveEnabled", true, AttributeRequiredLevel.SystemRequired));
    }

    /// <summary>#129: the app settings tables, with xrmverse-assigned type codes and the observed names and lookups.</summary>
    [Fact]
    public void Settings_tables_are_seeded()
    {
        var definition = Seed(AppModuleSchema.SettingDefinition);
        (definition.SchemaName, definition.EntitySetName, definition.ObjectTypeCode, definition.OwnershipType, definition.PrimaryNameAttribute)
            .Should().Be(("SettingDefinition", "settingdefinitions", 9966, OwnershipTypes.OrganizationOwned, "displayname"));
        (definition.PrimaryIdAttribute ?? AppModuleSchema.SettingDefinition + "id").Should().Be("settingdefinitionid");
        Column<StringAttributeMetadata>(definition, "uniquename").MaxLength.Should().Be(100);
        Column<StringAttributeMetadata>(definition, "displayname").MaxLength.Should().Be(100);
        Column<StringAttributeMetadata>(definition, "defaultvalue").MaxLength.Should().Be(2000);
        foreach (var unmodelled in new[] { "datatype", "overridablelevel", "isoverridable" })
            definition.Attributes.Should().NotContain(a => a.LogicalName == unmodelled, unmodelled);

        var setting = Seed(AppModuleSchema.AppSetting);
        (setting.SchemaName, setting.EntitySetName, setting.ObjectTypeCode, setting.OwnershipType, setting.PrimaryNameAttribute)
            .Should().Be(("AppSetting", "appsettings", 9965, OwnershipTypes.OrganizationOwned, "displayname"));
        (setting.PrimaryIdAttribute ?? AppModuleSchema.AppSetting + "id").Should().Be("appsettingid");
        foreach (var name in new[] { "uniquename", "displayname" }) Column<StringAttributeMetadata>(setting, name).MaxLength.Should().Be(100);
        Column<StringAttributeMetadata>(setting, "value").MaxLength.Should().Be(2000);
        Column<LookupAttributeMetadata>(setting, "parentappmoduleid").Targets.Should().Equal("appmodule");
        Column<LookupAttributeMetadata>(setting, "settingdefinitionid").Targets.Should().Equal(AppModuleSchema.SettingDefinition);
        setting.ManyToOneRelationships.Select(r => (r.SchemaName, r.ReferencedEntity, r.ReferencingAttribute, r.CascadeConfiguration.Delete))
            .Should().BeEquivalentTo(new[]
            {
                (AppModuleSchema.AppSettingToApp, "appmodule", "parentappmoduleid", CascadeType.Cascade),
                (AppModuleSchema.AppSettingToDefinition, AppModuleSchema.SettingDefinition, "settingdefinitionid", CascadeType.Restrict),
            });

        (AppModuleSchema.AsyncOnLoad, AppModuleSchema.AsyncOnSave).Should().Be(("AsyncOnLoad", "AsyncOnSave"));
        (AppModuleSchema.AsyncOnLoadId, AppModuleSchema.AsyncOnSaveId).Should().Be(
            (new Guid("fe39737f-4953-4a92-9943-56a697b82c7b"), new Guid("aaf2a566-153a-4384-9acb-7d762a411c6d")));
    }

    [Fact]
    public void Seed_inserts_the_two_timeline_definitions_with_default_false()
    {
        // The rows are inserted by SystemSeed and checked against a database in AppModuleSeedUpgradeTests; here the names and ids.
        (AppModuleSchema.TimelineGroupByDate, AppModuleSchema.TimelineRememberFilters)
            .Should().Be(("xrmverse_TimelineGroupByDate", "xrmverse_TimelineRememberFilters"));
        (AppModuleSchema.TimelineGroupByDateId, AppModuleSchema.TimelineRememberFiltersId).Should().Be(
            (new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e01"), new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e02")));
    }

    /// <summary>The setting that says how forms open related records: a side pane by default.</summary>
    [Fact]
    public void Seeds_lookup_open_mode_setting_definition()
    {
        (AppModuleSchema.LookupOpenMode, AppModuleSchema.LookupOpenModeId, AppModuleSchema.LookupOpenModeDefault).Should().Be(
            ("xrmverse_LookupOpenMode", new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e30"), "sidepane"));
    }

    /// <summary>X.10: the app OnLoad setting definition (the seeded row is checked in Xrmd.Integration.Tests, PresentationSettingsTests).</summary>
    [Fact]
    public void SeedsAppOnLoadDefinition()
    {
        AppModuleSchema.AppOnLoad.Should().Be("xrmverse_AppOnLoad");
        AppModuleSchema.AppOnLoadId.Should().Be(new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e20"));
        AppModuleSchema.AppOnLoadDefault.Should().BeEmpty();
    }

    /// <summary>X.11: the subgrid filters setting definition (the seeded row is checked in Xrmd.Integration.Tests, PresentationSettingsTests).</summary>
    [Fact]
    public void SeedsSubgridFiltersDefinition()
    {
        AppModuleSchema.SubgridFilters.Should().Be("xrmverse_SubgridFilters");
        AppModuleSchema.SubgridFiltersId.Should().Be(new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e31"));
        AppModuleSchema.SubgridFiltersDefault.Should().BeEmpty();
    }

    /// <summary>UI-2a: the two presentation setting definitions, the <c>organizationsetting</c> table and the <c>usersettings</c> columns.</summary>
    [Fact]
    public void Seeds_presentation_setting_definitions()
    {
        (AppModuleSchema.Renderer, AppModuleSchema.Presentation).Should().Be(("xrmverse_Renderer", "xrmverse_Presentation"));
        (AppModuleSchema.RendererId, AppModuleSchema.PresentationId).Should().Be(
            (new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10"), new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11")));
        (AppModuleSchema.RendererDefault, AppModuleSchema.PresentationDefault).Should().Be(("classic", ""));

        var setting = Seed(AppModuleSchema.OrganizationSetting);
        (setting.SchemaName, setting.EntitySetName, setting.ObjectTypeCode, setting.OwnershipType, setting.PrimaryNameAttribute)
            .Should().Be(("OrganizationSetting", "organizationsettings", 9967, OwnershipTypes.OrganizationOwned, "displayname"));
        foreach (var name in new[] { "uniquename", "displayname" }) Column<StringAttributeMetadata>(setting, name).MaxLength.Should().Be(100);
        Column<StringAttributeMetadata>(setting, "value").MaxLength.Should().Be(2000);
        Column<LookupAttributeMetadata>(setting, "settingdefinitionid").Targets.Should().Equal(AppModuleSchema.SettingDefinition);
        var link = setting.ManyToOneRelationships.Should().ContainSingle().Subject;
        (link.SchemaName, link.ReferencedEntity, link.ReferencingAttribute, link.CascadeConfiguration.Delete)
            .Should().Be(("settingdefinition_organizationsetting", AppModuleSchema.SettingDefinition, "settingdefinitionid", CascadeType.Restrict));

        var user = Seed("usersettings");
        Column<StringAttributeMetadata>(user, "xrmverse_renderer").MaxLength.Should().Be(100);
        var scheme = Column<PicklistAttributeMetadata>(user, "xrmverse_colorscheme");
        scheme.OptionSet!.Name.Should().Be("xrmverse_colorscheme");
        Options(user, "xrmverse_colorscheme").Should().Equal((0, "System"), (1, "Light"), (2, "Dark"));
        scheme.DefaultFormValue.Should().Be(0);
    }

    [Fact]
    public void Component_types_are_learns()
    {
        (AppModuleSchema.ComponentType.Entity, AppModuleSchema.ComponentType.View, AppModuleSchema.ComponentType.BusinessProcessFlow,
            AppModuleSchema.ComponentType.Chart, AppModuleSchema.ComponentType.Form, AppModuleSchema.ComponentType.SiteMap)
            .Should().Be((1, 26, 29, 59, 60, 62));
        (AppModuleSchema.RolesRelationship, AppModuleSchema.ComponentToApp).Should().Be(("appmoduleroles_association", "appmodule_appmodulecomponent"));
    }
}
