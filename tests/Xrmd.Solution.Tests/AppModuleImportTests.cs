using System.Xml.Linq;
using FluentAssertions;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.Tests;

/// <summary>The parse of the <c>AppModule</c> of a real export (plan 3.5 UI-1a, task 2). No database access.</summary>
public class AppModuleImportTests
{
    private static XElement OpenProject365App()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip"));
        var (_, customizations, _) = SolutionZip.Read(bytes);
        return customizations.Descendants("AppModule").Single();
    }

    [OpenProject365Fact]
    public void Parse_reads_the_columns_of_the_real_export()
    {
        var definition = AppModuleXml.Parse(OpenProject365App());

        definition.Columns.Should().Contain("clienttype", 4).And.Contain("formfactor", 1).And.Contain("navigationtype", 0)
            .And.Contain("statecode", 0).And.Contain("statuscode", 1)
            .And.Contain("webresourceid", Guid.Parse("953b9fac-1e5e-e611-80d6-00155ded156f"));
        definition.Columns.Keys.Should().NotContain(["url", "welcomepageid", "isdefault", "isfeatured", "appmoduleversion"], "the file does not give them");
    }

    [OpenProject365Fact]
    public void Parse_reads_the_components_and_roles_in_file_order()
    {
        var definition = AppModuleXml.Parse(OpenProject365App());

        definition.Components.Should().HaveCount(11);
        definition.Components.Take(4).Should().Equal(
            new AppModuleComponentRef(1, null, "sharepointdocument", 0), new AppModuleComponentRef(1, null, "account", 0),
            new AppModuleComponentRef(1, null, "entity", 0), new AppModuleComponentRef(1, null, "sharepointdocumentlocation", 0));
        definition.Components.Skip(4).Take(6).Should().OnlyContain(c => c.Type == 60 && c.Id != null && c.SchemaName == null);
        definition.Components[4].Id.Should().Be(Guid.Parse("f83260a8-d6cf-f111-a05b-7ced8d2d53e4"));
        definition.Components[10].Should().Be(new AppModuleComponentRef(62, null, "op365_OpenProject365", 0));
        definition.RoleIds.Should().Equal(Guid.Parse("627090ff-40a3-4053-8790-584edc5be201"), Guid.Parse("119f245c-3cc8-4b62-b31c-d1a046ced15d"));
    }

    [Fact]
    public void Parse_reads_behavior_and_ignores_empty_elements()
    {
        var app = XElement.Parse("""
            <AppModule><UniqueName>x_app</UniqueName><Url></Url><IsDefault>1</IsDefault><IsFeatured>0</IsFeatured><AppModuleVersion>1.2</AppModuleVersion>
              <AppModuleComponents><AppModuleComponent type="1" schemaName="account" behavior="2" /></AppModuleComponents></AppModule>
            """);

        var definition = AppModuleXml.Parse(app);

        definition.Columns.Should().Contain("isdefault", true).And.Contain("isfeatured", false).And.Contain("appmoduleversion", "1.2");
        definition.Columns.Keys.Should().NotContain("url");
        definition.Components.Single().Behavior.Should().Be(2);
    }

    [Fact]
    public void Parse_refuses_an_unreadable_value()
    {
        var act = () => AppModuleXml.Parse(XElement.Parse("<AppModule><ClientType>web</ClientType></AppModule>"));
        act.Should().Throw<XrmdFaultException>().WithMessage("*ClientType*");
    }

    [Fact]
    public void Parse_refuses_an_unreadable_role_id()
    {
        var act = () => AppModuleXml.Parse(XElement.Parse("<AppModule><AppModuleRoleMaps><Role id=\"not-a-guid\" /></AppModuleRoleMaps></AppModule>"));
        act.Should().Throw<XrmdFaultException>().WithMessage("*not-a-guid*");
    }

    /// <summary>#129: the <c>appsettings</c> block of an app, as an export of a real environment writes it.</summary>
    [Fact]
    public void Parse_reads_the_app_settings_in_file_order()
    {
        var app = XElement.Parse("""
            <AppModule><UniqueName>x_app</UniqueName><LocalizedNames><LocalizedName description="App" languagecode="1033" /></LocalizedNames>
              <appsettings>
                <appsetting settingdefinitionid.uniquename="AppChannel"><iscustomizable>1</iscustomizable><value>1</value></appsetting>
                <appsetting settingdefinitionid.uniquename="AsyncOnLoad"><iscustomizable>1</iscustomizable><value>true</value></appsetting>
                <appsetting settingdefinitionid.uniquename="AsyncOnSave"><iscustomizable>1</iscustomizable><value>true</value></appsetting>
              </appsettings>
            </AppModule>
            """);

        AppModuleXml.Parse(app).Settings.Should().Equal(
            new AppSettingRef("AppChannel", "1"), new AppSettingRef("AsyncOnLoad", "true"), new AppSettingRef("AsyncOnSave", "true"));
    }

    [OpenProject365Fact]
    public void Parse_reads_the_settings_of_the_real_export_and_none_without_the_block()
    {
        AppModuleXml.Parse(OpenProject365App()).Settings.Should().Equal(
            new AppSettingRef("AppChannel", "1"), new AppSettingRef("m365copilotmodelappenabled", "2"));
        AppModuleXml.Parse(XElement.Parse("<AppModule><UniqueName>x_app</UniqueName></AppModule>")).Settings.Should().BeEmpty();
        AppModuleXml.Parse(XElement.Parse("<AppModule><appsettings><appsetting settingdefinitionid.uniquename=\"AsyncOnLoad\" /></appsettings></AppModule>"))
            .Settings.Should().Equal(new AppSettingRef("AsyncOnLoad", null));
    }

    [Fact]
    public void Parse_refuses_an_app_setting_without_its_definition()
    {
        var act = () => AppModuleXml.Parse(XElement.Parse("<AppModule><appsettings><appsetting><value>true</value></appsetting></appsettings></AppModule>"));
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*settingdefinitionid.uniquename*");
    }

    [Fact]
    public void Parse_refuses_a_setting_given_twice()
    {
        var act = () => AppModuleXml.Parse(XElement.Parse("""
            <AppModule><appsettings>
              <appsetting settingdefinitionid.uniquename="AsyncOnLoad"><value>true</value></appsetting>
              <appsetting settingdefinitionid.uniquename="asynconload"><value>false</value></appsetting>
            </appsettings></AppModule>
            """));
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*asynconload*");
    }
}
