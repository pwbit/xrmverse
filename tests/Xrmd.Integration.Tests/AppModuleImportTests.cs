using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1a, task 2: the solution import fills the app module columns, <c>appmodulecomponent</c> and <c>appmoduleroles</c>
/// from the <c>AppModule</c> of the real OpenProject365 export (a database of its own, like the other real export tests).
/// </summary>
[Collection("real-export")]
public sealed class AppModuleImportTests(XrmdFixture fx)
{
    private const string Unique = "op365_OpenProject365";
    private static readonly string FixturePath = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip");
    private static readonly Guid SystemAdministrator = Guid.Parse("627090ff-40a3-4053-8790-584edc5be201");
    private static readonly Guid SystemCustomizer = Guid.Parse("119f245c-3cc8-4b62-b31c-d1a046ced15d");

    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await rd.ReadAsync())
        {
            var values = new object[rd.FieldCount];
            rd.GetValues(values);
            rows.Add(values.Select(v => v is DBNull ? null : v).ToArray());
        }
        return rows;
    }

    private async Task<object?> ScalarAsync(string sql) => (await RowsAsync(sql)).SingleOrDefault()?[0];

    private async Task<Guid> ImportAsync(byte[]? file = null)
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var jobId = Guid.NewGuid();
        await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file ?? File.ReadAllBytes(FixturePath), ImportJobId = jobId });
        return jobId;
    }

    /// <summary>The fixture with <paramref name="edit"/> applied to its customizations.xml text.</summary>
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
                var bytes = new UTF8Encoding(false).GetBytes(edit(reader.ReadToEnd()));
                target.Write(bytes);
            }
        return output.ToArray();
    }

    private Task<List<object?[]>> AppAsync() => RowsAsync($"""
        SELECT appmoduleid, name, clienttype, formfactor, navigationtype, webresourceid, appmoduleidunique, statecode, statuscode, publisherid
        FROM data.appmodule WHERE uniquename = '{Unique}'
        """);

    private Task<List<object?[]>> ComponentsAsync() => RowsAsync($"""
        SELECT c.componenttype, c.objectid, c.rootcomponentbehavior, c.ismetadata, c.appmoduleidunique
        FROM data.appmodulecomponent c JOIN data.appmodule a ON a.appmoduleid = c.appmoduleidunique WHERE a.uniquename = '{Unique}'
        """);

    private Task<List<object?[]>> RolesAsync() => RowsAsync($"""
        SELECT r.roleid FROM data.appmoduleroles r JOIN data.appmodule a ON a.appmoduleid = r.appmoduleid WHERE a.uniquename = '{Unique}'
        """);

    [OpenProject365Fact]
    public async Task Import_fills_app_module_columns()
    {
        await ImportAsync();

        var row = (await AppAsync()).Should().ContainSingle().Subject;
        row[1].Should().Be("ProjectIQ");
        row[2].Should().Be(4);
        row[3].Should().Be(1);
        row[4].Should().Be(0);
        row[5].Should().Be(Guid.Parse("953b9fac-1e5e-e611-80d6-00155ded156f"));
        row[6].Should().Be(row[0], "appmoduleidunique is the app's own id");
        (row[7], row[8]).Should().Be((0, 1));
        var publisher = await ScalarAsync("SELECT p.publisherid FROM data.solution s JOIN data.publisher p ON p.publisherid = s.publisherid WHERE s.uniquename = 'OpenProject365'");
        publisher.Should().NotBeNull();
        row[9].Should().Be(publisher, "the app belongs to the importing solution's publisher");
    }

    [OpenProject365Fact]
    public async Task Import_creates_components()
    {
        var jobId = await ImportAsync();
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var siteMapId = (Guid)(await ScalarAsync($"SELECT sitemapid FROM data.sitemap WHERE sitemapnameunique = '{Unique}'"))!;
        var components = await ComponentsAsync();

        // The six forms keep their ids; the site map resolves to the sitemap row; a table resolves to its metadata id.
        components.Where(c => (int)c[0]! == 60).Select(c => (Guid)c[1]!).Should().BeEquivalentTo(new[]
        {
            "f83260a8-d6cf-f111-a05b-7ced8d2d53e4", "2c3360a8-d6cf-f111-a05b-7ced8d2d53e4", "37705131-eccf-f111-a05b-7ced8d2d53e4",
            "40705131-eccf-f111-a05b-7ced8d2d53e4", "49705131-eccf-f111-a05b-7ced8d2d53e4", "62ea5f60-00d0-f111-a05b-7ced8d2d53e4",
        }.Select(Guid.Parse));
        components.Single(c => (int)c[0]! == 62)[1].Should().Be(siteMapId);
        var entities = components.Where(c => (int)c[0]! == 1).ToList();
        entities.Should().Contain(c => (Guid)c[1]! == store.GetEntity("account")!.MetadataId!.Value);
        entities.Should().OnlyContain(c => store.GetAllEntities().Any(e => e.MetadataId == (Guid?)c[1]) && (bool)c[3]! && (int)c[2]! == 0);

        // A table the organization does not have is a warning of the job log, not a failure.
        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        var warnings = XElement.Parse(log).Descendants("result").Where(r => (string?)r.Attribute("result") == "warning").Select(r => (string?)r.Attribute("errortext")).ToList();
        var missing = new[] { "sharepointdocument", "entity", "sharepointdocumentlocation" }.Where(n => store.GetEntity(n) is null).ToList();
        components.Count(c => (int)c[0]! == 1).Should().Be(4 - missing.Count);
        foreach (var name in missing) warnings.Should().Contain(w => w!.Contains($"'{name}'") && w.Contains("skipped"));
    }

    [OpenProject365Fact]
    public async Task Import_links_roles()
    {
        await ImportAsync();

        (await RolesAsync()).Select(r => (Guid)r[0]!).Should().BeEquivalentTo([SystemAdministrator, SystemCustomizer]);
    }

    [OpenProject365Fact]
    public async Task Reimport_replaces_components_and_roles()
    {
        await ImportAsync();
        var (components, roles, app) = (await ComponentsAsync(), await RolesAsync(), (await AppAsync()).Single()[0]);

        await ImportAsync();

        (await ComponentsAsync()).Should().HaveCount(components.Count);
        (await RolesAsync()).Should().HaveCount(roles.Count);
        (await AppAsync()).Should().ContainSingle().Which[0].Should().Be(app);
    }

    [OpenProject365Fact]
    public async Task Reimport_removes_what_the_file_no_longer_names()
    {
        await ImportAsync();
        var shrunk = Edited(xml => xml.Replace("""<Role id="{119f245c-3cc8-4b62-b31c-d1a046ced15d}" />""", "")
            .Replace("""<AppModuleComponent type="60" id="{f83260a8-d6cf-f111-a05b-7ced8d2d53e4}" />""", ""));
        var before = (await ComponentsAsync()).Count;

        await ImportAsync(shrunk);

        (await RolesAsync()).Select(r => (Guid)r[0]!).Should().Equal(SystemAdministrator);
        (await ComponentsAsync()).Should().HaveCount(before - 1);
        await ImportAsync();
        (await RolesAsync()).Should().HaveCount(2);
    }

    [OpenProject365Fact]
    public async Task Missing_role_is_logged_not_fatal()
    {
        var missing = Guid.NewGuid();
        const string keep = "<Role id=\"{119f245c-3cc8-4b62-b31c-d1a046ced15d}\" />";
        var file = Edited(xml => xml.Replace(keep, $"<Role id=\"{{{missing}}}\" />" + keep));

        var jobId = await ImportAsync(file);

        (await RolesAsync()).Select(r => (Guid)r[0]!).Should().BeEquivalentTo([SystemAdministrator, SystemCustomizer]);
        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        XElement.Parse(log).Descendants("result").Should().Contain(r => (string?)r.Attribute("result") == "warning"
            && ((string?)r.Attribute("errortext") ?? "").Contains(missing.ToString("D")));
        (await ComponentsAsync()).Should().NotBeEmpty("the rest is imported");
    }

    [OpenProject365Fact]
    public async Task Import_warns_on_a_missing_view_chart_or_form_component()
    {
        var (view, chart, form) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const string keep = """<AppModuleComponent type="60" id="{f83260a8-d6cf-f111-a05b-7ced8d2d53e4}" />""";
        var file = Edited(xml => xml.Replace(keep, keep +
            $"<AppModuleComponent type=\"26\" id=\"{{{view}}}\" /><AppModuleComponent type=\"59\" id=\"{{{chart}}}\" />" +
            $"<AppModuleComponent type=\"60\" id=\"{{{form}}}\" />"));

        var jobId = await ImportAsync(file);

        var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
        var warnings = XElement.Parse(log).Descendants("result").Where(r => (string?)r.Attribute("result") == "warning")
            .Select(r => (string?)r.Attribute("errortext") ?? "").ToList();
        warnings.Should().ContainSingle(w => w.Contains(view.ToString("D")) && w.Contains("savedquery"));
        warnings.Should().ContainSingle(w => w.Contains(chart.ToString("D")) && w.Contains("savedqueryvisualization"));
        warnings.Should().ContainSingle(w => w.Contains(form.ToString("D")) && w.Contains("systemform"));
        // The six forms of the file exist: no warning names them.
        warnings.Should().NotContain(w => w.Contains("f83260a8-d6cf-f111-a05b-7ced8d2d53e4"));
        // Linked anyway: a later import may bring them.
        (await ComponentsAsync()).Select(c => (Guid)c[1]!).Should().Contain([view, chart, form]);
        await ImportAsync();
    }

    /// <summary>The fixture's app with <paramref name="block"/> in place of its own <c>appsettings</c> block.</summary>
    private static byte[] WithAppSettings(string block) => Edited(xml =>
    {
        var result = System.Text.RegularExpressions.Regex.Replace(xml, "<appsettings>.*?</appsettings>", block,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        result.Should().NotBe(xml, "the fixture's app has an appsettings block");
        return result;
    });

    private Task<List<object?[]>> SettingsAsync() => RowsAsync($"""
        SELECT d.uniquename, s.value, s.uniquename, s.displayname
        FROM data.appsetting s JOIN data.appmodule a ON a.appmoduleid = s.parentappmoduleid
        JOIN data.settingdefinition d ON d.settingdefinitionid = s.settingdefinitionid
        WHERE a.uniquename = '{Unique}' ORDER BY d.uniquename
        """);

    /// <summary>
    /// #129: the observed <c>appsettings</c> block writes one row per known definition; AppChannel, which the organization does not
    /// define, is a warning; a re-import without the block removes the rows. A Basic User reads them.
    /// </summary>
    [OpenProject365Fact]
    public async Task Import_writes_app_settings()
    {
        const string observed = """
            <appsettings>
              <appsetting settingdefinitionid.uniquename="AppChannel"><iscustomizable>1</iscustomizable><value>1</value></appsetting>
              <appsetting settingdefinitionid.uniquename="AsyncOnLoad"><iscustomizable>1</iscustomizable><value>true</value></appsetting>
              <appsetting settingdefinitionid.uniquename="AsyncOnSave"><iscustomizable>1</iscustomizable><value>true</value></appsetting>
            </appsettings>
            """;
        try
        {
            var jobId = await ImportAsync(WithAppSettings(observed));

            (await SettingsAsync()).Should().BeEquivalentTo(new[]
            {
                new object?[] { "AsyncOnLoad", "true", null, null }, new object?[] { "AsyncOnSave", "true", null, null },
            }, o => o.WithStrictOrdering());
            var log = (string)(await ScalarAsync($"SELECT data FROM data.importjob WHERE importjobid = '{jobId}'"))!;
            var warnings = XElement.Parse(log).Descendants("result").Where(r => (string?)r.Attribute("result") == "warning")
                .Select(r => (string?)r.Attribute("errortext") ?? "").ToList();
            warnings.Should().ContainSingle(w => w.Contains("'AppChannel'"));
            warnings.Should().NotContain(w => w.Contains("AsyncOnLoad") || w.Contains("AsyncOnSave"));

            // A Basic User (the shell of any user) reads the settings and their definitions.
            var basic = Guid.NewGuid();
            await AssignRoleAsync(fx, await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), basic), StandardRoles.BasicUser);
            var asBasic = ServiceFor(fx, basic);
            var appId = (Guid)(await AppAsync()).Single()[0]!;
            var query = new Microsoft.Xrm.Sdk.Query.QueryExpression(AppModuleSchema.AppSetting) { ColumnSet = new(true) };
            query.Criteria.AddCondition("parentappmoduleid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, appId);
            (await asBasic.RetrieveMultipleAsync(query)).Entities.Should().HaveCount(2);
            (await asBasic.RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression(AppModuleSchema.SettingDefinition)
                { ColumnSet = new("uniquename", "defaultvalue") })).Entities.Should().HaveCountGreaterThanOrEqualTo(2);

            // Without the block, the app has no settings any more.
            await ImportAsync(WithAppSettings(""));
            (await SettingsAsync()).Should().BeEmpty();
        }
        finally
        {
            await ImportAsync();
        }
    }

    /// <summary>#129: the settings of an app go with it.</summary>
    [OpenProject365Fact]
    public async Task Deleting_the_app_deletes_its_settings()
    {
        await ImportAsync(WithAppSettings("""
            <appsettings><appsetting settingdefinitionid.uniquename="AsyncOnSave"><value>true</value></appsetting></appsettings>
            """));
        var appId = (Guid)(await AppAsync()).Single()[0]!;
        (await SettingsAsync()).Should().ContainSingle();
        try
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync("appmodule", appId);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.appsetting WHERE parentappmoduleid = '{appId}'")).Should().Be(0);
        }
        finally
        {
            await ImportAsync();
        }
    }

    [OpenProject365Fact]
    public async Task Export_round_trip_unchanged()
    {
        await ImportAsync();
        var exported = ((ExportSolutionResponse)await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(
            new ExportSolutionRequest { SolutionName = "OpenProject365", Managed = false })).ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(exported);
        var (_, original, _) = SolutionZip.Read(File.ReadAllBytes(FixturePath));

        XmlNormalizer.Normalize(new XDocument(customizations.Descendants("AppModule").Single()))
            .Should().Be(XmlNormalizer.Normalize(new XDocument(original.Descendants("AppModule").Single())));
    }
}
