using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Field security profiles as solution components (plan 14, task 7, ruling F8): the import creates the profile and replaces its
/// permissions, skips a permission on a column that is not secured with a warning, ignores the System Administrator profile,
/// never touches the profile's users and teams, and invalidates the security cache; the export writes the profile back.
/// </summary>
[Collection("solution-import")]
public sealed class FieldSecuritySolutionTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly Guid ProfileId = new("2d3c1b4a-5e6f-4a1b-9c2d-3e4f5a6b7c81");
    private static readonly Guid AdminProfile = new("572329c1-a042-4e22-be47-367c6374ea45");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearAsync();
    }

    public Task DisposeAsync() => ClearAsync();

    private async Task ClearAsync()
    {
        await ExecAsync("""
            DELETE FROM data.fieldpermission WHERE fieldsecurityprofileid = @p;
            DELETE FROM data.systemuserprofiles WHERE fieldsecurityprofileid = @p;
            DELETE FROM data.teamprofiles WHERE fieldsecurityprofileid = @p;
            DELETE FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p;
            """, ("@p", ProfileId));
        await SolutionImportTests.ClearImportAsync(fx);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
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

    private async Task<List<(string Entity, string Attribute, int Read, int Create, int Update)>> PermissionsAsync(Guid profile)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            SELECT entityname, attributelogicalname, canread, cancreate, canupdate FROM data.fieldpermission
            WHERE fieldsecurityprofileid = @p ORDER BY entityname, attributelogicalname
            """, conn);
        cmd.Parameters.AddWithValue("@p", profile);
        var rows = new List<(string, string, int, int, int)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add((rd.GetString(0), rd.GetString(1), Convert.ToInt32(rd.GetValue(2)), Convert.ToInt32(rd.GetValue(3)), Convert.ToInt32(rd.GetValue(4))));
        return rows;
    }

    /// <summary>A permission in the shape of a real Dataverse export (child elements, in the export's order).</summary>
    private static XElement Permission(string attribute, int read, int create = 0, int update = 0, int unmasked = 0) =>
        new("FieldPermission", new XElement("EntityName", "xv_project"), new XElement("AttributeName", attribute),
            new XElement("CanRead", read), new XElement("CanUpdate", update), new XElement("CanCreate", create),
            new XElement("CanReadUnmasked", unmasked));

    private static XElement Profile(Guid id, string name, params XElement[] permissions) =>
        new("FieldSecurityProfile", new XAttribute("name", name), new XAttribute("fieldsecurityprofileid", id.ToString("B")),
            new XAttribute("description", "Imported profile"), new XElement("FieldPermissions", permissions));

    /// <summary>The fixture with <c>xv_project.xv_count</c> secured (<c>xv_ratio</c> stays unsecured) and <paramref name="profiles"/>.</summary>
    private static byte[] Build(params XElement[] profiles) => SolutionFixtureZip.Build(
        c =>
        {
            c.Root!.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Count").Element("IsSecured")!.Value = "1";
            c.Root!.Element("FieldSecurityProfiles")!.Add(profiles);
        },
        s =>
        {
            var roots = s.Root!.Element("SolutionManifest")!.Element("RootComponents")!;
            foreach (var profile in profiles)
                roots.Add(new XElement("RootComponent", new XAttribute("type", "70"),
                    new XAttribute("id", (string)profile.Attribute("fieldsecurityprofileid")!), new XAttribute("behavior", "0")));
        });

    private async Task<string> ImportAsync(byte[] zip)
    {
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId });
        return (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
    }

    private async Task<XDocument> ExportCustomizationsAsync()
    {
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = SolutionFixtureZip.FixtureName }))
            .ExportSolutionFile;
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("customizations.xml")!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>Puts the System Administrator profile in the fixture solution by SQL (AddSolutionComponent refuses it).</summary>
    private Task HoldAdminProfileAsync() => ExecAsync($"""
        INSERT INTO data.solutioncomponent (solutioncomponentid, solutionid, componenttype, objectid, rootcomponentbehavior, ismetadata,
            owningbusinessunit, createdon, modifiedon, statecode, statuscode)
        SELECT NEWID(), solutionid, 70, @p, 0, {TestSql.False}, owningbusinessunit, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1
        FROM data.solution WHERE uniquename = @n
        """, ("@p", AdminProfile), ("@n", SolutionFixtureZip.FixtureName));

    /// <summary>The body of <c>DELETE /admin/solutions/{fixture}?cascade=true</c> (200 expected).</summary>
    private async Task<JsonObject> CascadeDeleteAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/admin/solutions/{SolutionFixtureZip.FixtureName}?cascade=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_creates_the_profile_with_its_permissions_and_export_writes_it_back()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4, update: 4))));

        (await ScalarAsync("SELECT name FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p", ("@p", ProfileId)))
            .Should().Be("Project finance");
        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 4));
        (await ScalarAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent AS c JOIN data.solution AS s ON s.solutionid = c.solutionid
            WHERE s.uniquename = @n AND c.componenttype = 70 AND c.objectid = @p
            """, ("@n", SolutionFixtureZip.FixtureName), ("@p", ProfileId))).Should().Be(1);

        var exported = (await ExportCustomizationsAsync()).Root!.Element("FieldSecurityProfiles")!.Elements("FieldSecurityProfile").Single();
        Guid.Parse((string)exported.Attribute("fieldsecurityprofileid")!).Should().Be(ProfileId);
        exported.Attribute("name")!.Value.Should().Be("Project finance");
        var permission = exported.Descendants("FieldPermission").Single();
        permission.Element("AttributeName")!.Value.Should().Be("xv_count");
        permission.Element("CanRead")!.Value.Should().Be("4");
        permission.Element("CanUpdate")!.Value.Should().Be("4");
    }

    [Fact]
    public async Task Import_with_profiles_needs_the_profile_and_permission_privileges()
    {
        var root = await RootBusinessUnitAsync(fx);
        var customizerOid = Guid.NewGuid();
        var customizer = await CreateUserAsync(fx, root, customizerOid);
        await AssignRoleAsync(fx, customizer, StandardRoles.SystemCustomizer);
        var zip = Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4)));

        // A System Customizer holds no profile privilege (F9): refused before any write.
        var fault = await FaultAsync(() => ServiceFor(fx, customizerOid).ExecuteAsync(
            new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403), fault.Message);
        fault.Message.Should().Contain("field security profiles");
        (await ScalarAsync("SELECT COUNT(*) FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p", ("@p", ProfileId))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", SolutionFixtureZip.FixtureName))).Should().Be(0);

        // With create and write on both tables the same import goes through.
        await GrantAsync(fx, customizer, team: false,
            ("prvCreateFieldSecurityProfile", DepthMask.Global), ("prvWriteFieldSecurityProfile", DepthMask.Global),
            ("prvCreateFieldPermission", DepthMask.Global), ("prvWriteFieldPermission", DepthMask.Global));
        await ServiceFor(fx, customizerOid).ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 0));
    }

    [Fact]
    public async Task Import_that_unsecures_a_column_needs_its_rights()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4))));
        var root = await RootBusinessUnitAsync(fx);
        var customizerOid = Guid.NewGuid();
        var customizer = await CreateUserAsync(fx, root, customizerOid);
        await AssignRoleAsync(fx, customizer, StandardRoles.SystemCustomizer);

        // The plain fixture has xv_count unsecured: importing it turns the column off, which needs read, create and update on it.
        var fault = await FaultAsync(() => ServiceFor(fx, customizerOid).ExecuteAsync(
            new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.UnsecureWithoutPermissions, 403), fault.Message);
        Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_count").IsSecured.Should().BeTrue("nothing was imported");
        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 0));
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
        throw new InvalidOperationException("The call was expected to fail.");
    }

    [Fact]
    public async Task Import_warns_about_permissions_outside_the_FieldPermissions_element()
    {
        var profile = Profile(ProfileId, "Project finance", Permission("xv_count", read: 4));
        profile.Add(Permission("xv_count", read: 4, update: 4));
        var log = await ImportAsync(Build(profile));

        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 0));
        log.Should().Contain("1 FieldPermission element(s) outside a FieldPermissions element: they were not imported");
    }

    [Fact]
    public async Task Import_skips_permissions_on_unsecured_columns_with_warning()
    {
        var log = await ImportAsync(Build(Profile(ProfileId, "Project finance",
            Permission("xv_count", read: 4), Permission("xv_ratio", read: 4), Permission("xv_nosuchcolumn", read: 4))));

        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 0));
        log.Should().Contain("'xv_project.xv_ratio' was skipped: the column is not secured")
            .And.Contain("'xv_project.xv_nosuchcolumn' was skipped");
    }

    [Fact]
    public async Task Import_never_carries_user_or_team_associations()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4))));
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship(FieldSecurityTables.UserProfilesRelationship),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, ProfileId)],
        });

        // The re-import replaces the permissions (none left) and leaves the user's association as it is.
        await ImportAsync(Build(Profile(ProfileId, "Project finance")));

        (await PermissionsAsync(ProfileId)).Should().BeEmpty();
        (await ScalarAsync("SELECT COUNT(*) FROM data.systemuserprofiles WHERE fieldsecurityprofileid = @p AND systemuserid = @u",
            ("@p", ProfileId), ("@u", user))).Should().Be(1);
        var exported = (await ExportCustomizationsAsync()).Root!.Element("FieldSecurityProfiles")!;
        exported.ToString().Should().NotContain(user.ToString("D"), "users and teams of a profile never travel")
            .And.NotContainAny("systemuser", "team");
    }

    [Fact]
    public async Task Import_ignores_the_system_administrator_profile_with_warning()
    {
        var log = await ImportAsync(Build(Profile(AdminProfile, "System Administrator", Permission("xv_count", read: 0))));

        log.Should().Contain("is the System Administrator profile");
        (await PermissionsAsync(AdminProfile)).Should().BeEmpty();
        (await ScalarAsync("SELECT description FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p", ("@p", AdminProfile)))
            .Should().NotBe("Imported profile");
    }

    [Fact]
    public async Task Masking_in_the_file_is_imported_as_zero_with_warning()
    {
        var log = await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4, unmasked: 4))));

        (await ScalarAsync("SELECT canreadunmasked FROM data.fieldpermission WHERE fieldsecurityprofileid = @p", ("@p", ProfileId)))
            .Should().Be(0);
        log.Should().Contain("does not support masking");
    }

    [Fact]
    public async Task Import_invalidates_the_field_security_cache()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 0))));
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadxv_Project", DepthMask.Global));
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship(FieldSecurityTables.UserProfilesRelationship),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, ProfileId)],
        });
        var record = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "p", ["xv_count"] = 7 });
        var asUser = ServiceFor(fx, oid);
        (await asUser.RetrieveAsync("xv_project", record, new ColumnSet("xv_count"))).GetAttributeValue<int?>("xv_count").Should().BeNull();

        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4))));

        (await asUser.RetrieveAsync("xv_project", record, new ColumnSet("xv_count")))["xv_count"].Should().Be(7);
    }

    [Fact]
    public async Task Import_keeps_the_first_of_duplicate_permissions_with_warning()
    {
        var log = await ImportAsync(Build(Profile(ProfileId, "Project finance",
            Permission("xv_count", read: 4, update: 4), Permission("xv_count", read: 0))));

        (await PermissionsAsync(ProfileId)).Should().Equal(("xv_project", "xv_count", 4, 0, 4));
        log.Should().Contain("'xv_project.xv_count' more than once");
    }

    [Fact]
    public async Task AddSolutionComponent_of_the_system_administrator_profile_is_400()
    {
        await ImportAsync(Build());

        var act = () => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = ComponentType.FieldSecurityProfile, ComponentId = AdminProfile, SolutionUniqueName = SolutionFixtureZip.FixtureName,
        });

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("built in");
    }

    [Fact]
    public async Task Export_skips_the_system_administrator_profile()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4))));
        await HoldAdminProfileAsync();

        var profiles = (await ExportCustomizationsAsync()).Root!.Element("FieldSecurityProfiles")!.Elements("FieldSecurityProfile")
            .Select(p => Guid.Parse((string)p.Attribute("fieldsecurityprofileid")!));

        profiles.Should().Equal(ProfileId);
    }

    [Fact]
    public async Task Cascade_delete_removes_the_profile_and_its_permissions()
    {
        await ImportAsync(Build(Profile(ProfileId, "Project finance", Permission("xv_count", read: 4))));

        var body = await CascadeDeleteAsync();

        body["deleted"]!.AsArray().Select(d => (d!["type"]!.GetValue<int>(), d["name"]!.GetValue<string>()))
            .Should().Contain((ComponentType.FieldSecurityProfile, "Project finance"));
        (await ScalarAsync("SELECT COUNT(*) FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p", ("@p", ProfileId)))
            .Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.fieldpermission WHERE fieldsecurityprofileid = @p", ("@p", ProfileId)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Cascade_delete_keeps_the_system_administrator_profile()
    {
        await ImportAsync(Build());
        await HoldAdminProfileAsync();

        var body = await CascadeDeleteAsync();

        body["kept"]!.AsArray().Select(k => (k!["type"]!.GetValue<int>(), k["name"]!.GetValue<string>(), k["reason"]!.GetValue<string>()))
            .Should().Contain((ComponentType.FieldSecurityProfile, FieldSecurityTables.SystemAdministratorProfileName, "built-in profile"));
        body["deleted"]!.AsArray().Should().NotContain(d => d!["type"]!.GetValue<int>() == ComponentType.FieldSecurityProfile);
        (await ScalarAsync("SELECT COUNT(*) FROM data.fieldsecurityprofile WHERE fieldsecurityprofileid = @p", ("@p", AdminProfile)))
            .Should().Be(1);
    }
}
