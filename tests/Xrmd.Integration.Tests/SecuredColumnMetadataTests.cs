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
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>IsSecured</c> on columns (plan 14, task 2, ruling F7): set and read through the SDK and the Web API, carried by an
/// unmanaged solution, refused on a column that cannot be secured, computed <c>CanBeSecuredFor*</c>, the removal of the
/// column's permissions and field shares when it is unsecured, and the notification after the commit.
/// </summary>
[Collection("solution-import")]
public sealed class SecuredColumnMetadataTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Project = "xv_project";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await CreateTablesAsync();
    }

    /// <summary>
    /// The fixture's tables as these tests use them, made through the metadata store (the import is not under test here):
    /// <c>xv_project</c> (xv_name String(100) primary name, xv_description Memo, xv_count Integer, xv_ratio Decimal) and
    /// <c>xv_task</c> (xv_name String(100) primary name, xv_projectid Lookup→xv_project).
    /// </summary>
    private async Task CreateTablesAsync()
    {
        var project = new EntityMetadata { LogicalName = Project, SchemaName = "xv_Project", OwnershipType = OwnershipTypes.UserOwned };
        project.SetAttributes(
        [
            new MemoAttributeMetadata { LogicalName = "xv_description", SchemaName = "xv_Description", MaxLength = 2000 },
            new IntegerAttributeMetadata { LogicalName = "xv_count", SchemaName = "xv_Count" },
            new DecimalAttributeMetadata { LogicalName = "xv_ratio", SchemaName = "xv_Ratio", Precision = 2 },
        ]);
        await Store.CreateEntityAsync(project, new StringAttributeMetadata { LogicalName = "xv_name", SchemaName = "xv_Name", MaxLength = 100 });
        var task = new EntityMetadata { LogicalName = "xv_task", SchemaName = "xv_Task", OwnershipType = OwnershipTypes.UserOwned };
        task.SetAttributes([new LookupAttributeMetadata { LogicalName = "xv_projectid", SchemaName = "xv_ProjectId", Targets = [Project] }]);
        await Store.CreateEntityAsync(task, new StringAttributeMetadata { LogicalName = "xv_name", SchemaName = "xv_Name", MaxLength = 100 });
    }

    /// <summary>The fixture imported in place of <see cref="CreateTablesAsync"/>'s tables (the tests of the import itself).</summary>
    private async Task ImportFixtureAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() });
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DELETE FROM data.{FieldSecurityTables.Share} WHERE objecttypecode = N'{Project}'");
        await ExecuteAsync($"DELETE FROM data.{FieldSecurityTables.Permission} WHERE entityname = N'{Project}'");
        await ExecuteAsync($"DELETE FROM data.{FieldSecurityTables.Profile} WHERE name LIKE N'sc-%'");
        await SolutionImportTests.ClearImportAsync(fx);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task ExecuteAsync(string sql) => TestSql.ExecAsync(fx.ConnectionString, sql);

    private async Task<int> CountAsync(string sql, params (string Name, object? Value)[] parameters) =>
        (int)await TestSql.CountAsync(fx.ConnectionString, sql, parameters);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<AttributeMetadata> RetrieveAttributeAsync(string entity, string attribute) =>
        ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute }))
        .AttributeMetadata;

    private static AttributeMetadata Secured<T>(string logicalName, bool secured) where T : AttributeMetadata, new() =>
        new T { LogicalName = logicalName, IsSecured = secured };

    private Task SecureAsync<T>(string entity, string attribute, bool secured) where T : AttributeMetadata, new() =>
        Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = entity, Attribute = Secured<T>(attribute, secured) });

    private static async Task NotSecurableAsync(Func<Task> call, string attribute)
    {
        var fault = await FaultAsync(call);
        (fault.ErrorCode, fault.HttpStatus).Should().Be((unchecked((int)0x8004F501), 400), fault.Message);
        fault.Message.Should().Contain(attribute);
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        });
    }

    private static string? ExportedIsSecured(byte[] zip, string column)
    {
        var customizations = XDocument.Load(new MemoryStream(Entries(zip)["customizations.xml"]));
        return customizations.Descendants("attribute")
            .Single(a => string.Equals((string?)a.Attribute("PhysicalName"), column, StringComparison.OrdinalIgnoreCase)
                         && a.Ancestors("Entity").Any(e => string.Equals(e.Element("Name")?.Value, Project, StringComparison.OrdinalIgnoreCase)))
            .Element("IsSecured")?.Value;
    }

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IsSecured_round_trips_through_sdk_and_web_api()
    {
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeFalse("a column starts unsecured");

        // SDK: UpdateAttribute sets it, RetrieveAttribute and RetrieveEntity read it.
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeTrue();
        Store.GetEntity(Project)!.Attributes.Single(a => a.LogicalName == "xv_description").IsSecured.Should().BeTrue();
        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest
        {
            LogicalName = Project, EntityFilters = EntityFilters.Attributes,
        })).EntityMetadata;
        entity.Attributes.Single(a => a.LogicalName == "xv_description").IsSecured.Should().BeTrue();

        // Web API: the attribute shows it, and a PUT of the retrieved body with the flag changed applies it.
        var path = $"EntityDefinitions(LogicalName='{Project}')/Attributes(LogicalName='xv_count')";
        var count = await GetJsonAsync(path);
        count["IsSecured"]!.GetValue<bool>().Should().BeFalse();
        count["CanBeSecuredForRead"]!.GetValue<bool>().Should().BeTrue();
        count["IsSecured"] = true;
        var put = await SendAsync(HttpMethod.Put, path, count.ToJsonString());
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        (await GetJsonAsync(path))["IsSecured"]!.GetValue<bool>().Should().BeTrue();
        (await GetJsonAsync($"EntityDefinitions(LogicalName='{Project}')/Attributes(LogicalName='xv_description')"))["IsSecured"]!
            .GetValue<bool>().Should().BeTrue();
        (await RetrieveAttributeAsync(Project, "xv_count")).IsSecured.Should().BeTrue();
    }

    [Fact]
    public async Task IsSecured_round_trips_through_a_solution()
    {
        await ImportFixtureAsync();
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        await SecureAsync<IntegerAttributeMetadata>(Project, "xv_count", true);

        // The export writes the flag, and an import of that export sets it back after it was turned off.
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = SolutionFixtureZip.FixtureName, Managed = false,
        })).ExportSolutionFile;
        ExportedIsSecured(exported, "xv_description").Should().Be("1");
        ExportedIsSecured(exported, "xv_count").Should().Be("1");
        ExportedIsSecured(exported, "xv_ratio").Should().Be("0");

        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", false);
        await SecureAsync<IntegerAttributeMetadata>(Project, "xv_count", false);
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeFalse();

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = exported, ImportJobId = Guid.NewGuid() });
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeTrue("the import applies the file's flag");
        (await RetrieveAttributeAsync(Project, "xv_count")).IsSecured.Should().BeTrue();
        (await RetrieveAttributeAsync(Project, "xv_ratio")).IsSecured.Should().BeFalse();
    }

    [Fact]
    public async Task Non_securable_columns_are_refused()
    {
        // The primary name, a lookup, createdon and a column of a field security table.
        await NotSecurableAsync(() => SecureAsync<StringAttributeMetadata>(Project, "xv_name", true), "xv_name");
        await NotSecurableAsync(() => SecureAsync<LookupAttributeMetadata>("xv_task", "xv_projectid", true), "xv_projectid");
        await NotSecurableAsync(() => SecureAsync<DateTimeAttributeMetadata>(Project, "createdon", true), "createdon");
        await NotSecurableAsync(() => SecureAsync<PicklistAttributeMetadata>(FieldSecurityTables.Permission, "canread", true), "canread");

        // The Web API PUT of the primary name with IsSecured true: the same error.
        var namePath = $"EntityDefinitions(LogicalName='{Project}')/Attributes(LogicalName='xv_name')";
        var nameBody = await GetJsonAsync(namePath);
        nameBody["IsSecured"] = true;
        var refused = await SendAsync(HttpMethod.Put, namePath, nameBody.ToJsonString());
        var refusedBody = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, refusedBody);
        JsonNode.Parse(refusedBody)!["error"]!["code"]!.GetValue<string>().Should().Be("0x8004f501");
        (await GetJsonAsync(namePath))["IsSecured"]!.GetValue<bool>().Should().BeFalse();

        foreach (var (table, column) in new[] { (Project, "xv_name"), ("xv_task", "xv_projectid"), (Project, "createdon"), (FieldSecurityTables.Permission, "canread") })
        {
            var attribute = await RetrieveAttributeAsync(table, column);
            attribute.IsSecured.Should().NotBe(true, column);
            (attribute.CanBeSecuredForCreate, attribute.CanBeSecuredForRead, attribute.CanBeSecuredForUpdate)
                .Should().Be((false, false, false), $"{table}.{column}");
        }

        // A custom string column can be secured: created secured, its flags are true.
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = Project,
            Attribute = new StringAttributeMetadata
            {
                LogicalName = "xv_code", SchemaName = "xv_Code", MaxLength = 20, DisplayName = Label("Code"), IsSecured = true,
            },
        });
        var code = await RetrieveAttributeAsync(Project, "xv_code");
        code.IsSecured.Should().BeTrue();
        (code.CanBeSecuredForCreate, code.CanBeSecuredForRead, code.CanBeSecuredForUpdate).Should().Be((true, true, true));

        // A create that secures a column that cannot be secured fails as well: the primary name, a column of a security table.
        var newTable = "xv_secret";
        await NotSecurableAsync(() => Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(newTable, "xv_Secret", "Secret"),
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "xv_name", SchemaName = "xv_Name", MaxLength = 100, DisplayName = Label("Name"), IsSecured = true,
            },
        }), "xv_name");
        Store.GetEntity(newTable).Should().BeNull();
        await NotSecurableAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = FieldSecurityTables.Profile,
            Attribute = new StringAttributeMetadata { LogicalName = "xv_note", SchemaName = "xv_Note", MaxLength = 20, DisplayName = Label("Note"), IsSecured = true },
        }), "xv_note");
        Store.GetEntity(FieldSecurityTables.Profile)!.Attributes.Should().NotContain(a => a.LogicalName == "xv_note");

        // A stored CanBeSecuredForRead does not override the rule.
        await ExecuteAsync($"""
            UPDATE meta.attribute SET definition = JSON_MODIFY(definition, '$.CanBeSecuredForRead', CAST(1 AS bit))
            WHERE entity_logical_name = N'{Project}' AND logical_name = N'xv_name'
            """);
        await Store.LoadAsync();
        (await RetrieveAttributeAsync(Project, "xv_name")).CanBeSecuredForRead.Should().BeFalse();
        await NotSecurableAsync(() => SecureAsync<StringAttributeMetadata>(Project, "xv_name", true), "xv_name");
    }

    [Fact]
    public async Task Unsecuring_removes_permissions_and_shares()
    {
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        await SecureAsync<IntegerAttributeMetadata>(Project, "xv_count", true);
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "sc-" + Guid.NewGuid().ToString("N")[..8] });
        foreach (var column in new[] { "xv_description", "xv_count" })
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Project, [FieldSecurityTables.AttributeLogicalName] = column,
                [FieldSecurityTables.CanRead] = new OptionSetValue(FieldSecurityTables.Allowed),
            });
        var record = await Admin.CreateAsync(new Entity(Project) { ["xv_name"] = "secured" });
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        Guid ColumnId(string column) => Store.GetEntity(Project)!.Attributes.Single(a => a.LogicalName == column).MetadataId!.Value;
        foreach (var column in new[] { "xv_description", "xv_count" })
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Share)
            {
                [FieldSecurityTables.ShareObjectId] = new EntityReference(Project, record), [FieldSecurityTables.ShareObjectTypeCodeColumn] = Project,
                [FieldSecurityTables.ShareAttributeId] = ColumnId(column),
                [FieldSecurityTables.SharePrincipalId] = new EntityReference("systemuser", user),
                [FieldSecurityTables.ShareReadAccess] = true,
            });

        Task<int> PermissionsAsync(string column) => CountAsync(
            $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Permission} WHERE entityname = @e AND attributelogicalname = @a", ("@e", Project), ("@a", column));
        Task<int> SharesAsync(string column) => CountAsync(
            $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Share} WHERE attributeid = @id AND objecttypecode = @e", ("@id", ColumnId(column)), ("@e", Project));
        (await PermissionsAsync("xv_description")).Should().Be(1);
        (await SharesAsync("xv_description")).Should().Be(1);

        // Setting the flag it already has removes nothing.
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        (await PermissionsAsync("xv_description")).Should().Be(1);

        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", false);
        (await PermissionsAsync("xv_description")).Should().Be(0);
        (await SharesAsync("xv_description")).Should().Be(0);
        (await PermissionsAsync("xv_count")).Should().Be(1, "the permissions of another column stay");
        (await SharesAsync("xv_count")).Should().Be(1);

        // A failed transaction removes nothing: the change and the removal are one transaction.
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await Store.UpdateAttributeAsync(session, Project, Secured<IntegerAttributeMetadata>("xv_count", false), default);
            await tx.RollbackAsync();
            session.RolledBack();
        }
        (await PermissionsAsync("xv_count")).Should().Be(1);
        (await SharesAsync("xv_count")).Should().Be(1);
        (await RetrieveAttributeAsync(Project, "xv_count")).IsSecured.Should().BeTrue();
    }

    [Fact]
    public async Task Unsecuring_needs_read_create_and_update_on_the_column()
    {
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "sc-" + Guid.NewGuid().ToString("N")[..8] });
        var permission = await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
        {
            [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
            [FieldSecurityTables.EntityName] = Project, [FieldSecurityTables.AttributeLogicalName] = "xv_description",
            [FieldSecurityTables.CanRead] = new OptionSetValue(FieldSecurityTables.Allowed),
            [FieldSecurityTables.CanUpdate] = new OptionSetValue(FieldSecurityTables.Allowed),
        });
        Task<int> PermissionsAsync() => CountAsync(
            $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Permission} WHERE entityname = @e AND attributelogicalname = @a", ("@e", Project), ("@a", "xv_description"));
        var root = await RootBusinessUnitAsync(fx);
        var customizerOid = Guid.NewGuid();
        var customizer = await CreateUserAsync(fx, root, customizerOid);
        await AssignRoleAsync(fx, customizer, StandardRoles.SystemCustomizer);
        var unsecure = new UpdateAttributeRequest { EntityName = Project, Attribute = Secured<MemoAttributeMetadata>("xv_description", false) };

        // A System Customizer with no profile, then with read and update only: refused, nothing removed.
        var fault = await FaultAsync(() => ServiceFor(fx, customizerOid).ExecuteAsync(unsecure));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.UnsecureWithoutPermissions, 403), fault.Message);
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", customizer), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        fault = await FaultAsync(() => ServiceFor(fx, customizerOid).ExecuteAsync(unsecure));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.UnsecureWithoutPermissions, 403), "create is missing");
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeTrue();
        (await PermissionsAsync()).Should().Be(1);

        // With read, create and update the same customizer may.
        await Admin.UpdateAsync(new Entity(FieldSecurityTables.Permission, permission)
        {
            [FieldSecurityTables.CanCreate] = new OptionSetValue(FieldSecurityTables.Allowed),
        });
        await ServiceFor(fx, customizerOid).ExecuteAsync(unsecure);
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeFalse();
        (await PermissionsAsync()).Should().Be(0);

        // Securing needs no permission, and an administrator unsecures with no profile.
        await ServiceFor(fx, customizerOid).ExecuteAsync(new UpdateAttributeRequest { EntityName = Project, Attribute = Secured<MemoAttributeMetadata>("xv_description", true) });
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", false);
    }

    [Fact]
    public async Task A_legacy_secured_flag_on_a_non_securable_column_is_read_unsecured()
    {
        // Stored before the securable rule: IsSecured true on the primary name and on createdon; a custom column keeps its flag.
        await SecureAsync<MemoAttributeMetadata>(Project, "xv_description", true);
        await ExecuteAsync($"""
            UPDATE meta.attribute SET definition = JSON_MODIFY(definition, '$.IsSecured', CAST(1 AS bit))
            WHERE entity_logical_name = N'{Project}' AND logical_name IN (N'xv_name', N'createdon')
            """);
        await Store.LoadAsync();

        foreach (var column in new[] { "xv_name", "createdon" })
        {
            Store.GetEntity(Project)!.Attributes.Single(a => a.LogicalName == column).IsSecured.Should().BeFalse(column);
            (await RetrieveAttributeAsync(Project, column)).IsSecured.Should().BeFalse(column);
        }
        (await RetrieveAttributeAsync(Project, "xv_description")).IsSecured.Should().BeTrue();
    }

    private sealed class Recorder : IAttributeSecurityChangedHandler
    {
        public List<(string Entity, string Attribute)> Calls { get; } = [];

        public void OnAttributeSecurityChanged(string entity, string attribute)
        {
            lock (Calls) Calls.Add((entity, attribute));
        }
    }

    [Fact]
    public async Task Attribute_security_changed_fires_after_the_commit()
    {
        var recorder = new Recorder();
        var store = new MetadataStore(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), securityChangedHandlers: [recorder]);
        await store.LoadAsync();
        try
        {
            await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
            {
                await store.UpdateAttributeAsync(s, Project, Secured<IntegerAttributeMetadata>("xv_count", true), default);
                recorder.Calls.Should().BeEmpty("the notification waits for the commit");
            });
            recorder.Calls.Should().Equal((Project, "xv_count"));

            // No change, a label change, a rolled-back change: no notification.
            recorder.Calls.Clear();
            await TestEntities.InSessionAsync(fx.ConnectionString,
                s => store.UpdateAttributeAsync(s, Project, Secured<IntegerAttributeMetadata>("xv_count", true), default));
            await TestEntities.InSessionAsync(fx.ConnectionString,
                s => store.UpdateAttributeAsync(s, Project, new IntegerAttributeMetadata { LogicalName = "xv_count", DisplayName = Label("Count") }, default));
            await FluentActions.Awaiting(() => TestEntities.InSessionAsync(fx.ConnectionString, async s =>
            {
                await store.UpdateAttributeAsync(s, Project, Secured<IntegerAttributeMetadata>("xv_count", false), default);
                throw new InvalidOperationException("roll back");
            })).Should().ThrowAsync<InvalidOperationException>();
            recorder.Calls.Should().BeEmpty();

            // Turning it off and creating a secured column notify too.
            await TestEntities.InSessionAsync(fx.ConnectionString,
                s => store.UpdateAttributeAsync(s, Project, Secured<IntegerAttributeMetadata>("xv_count", false), default));
            await TestEntities.InSessionAsync(fx.ConnectionString, s => store.AddAttributeAsync(s, Project,
                new StringAttributeMetadata { LogicalName = "xv_pin", SchemaName = "xv_Pin", MaxLength = 10, DisplayName = Label("Pin"), IsSecured = true }, default));
            recorder.Calls.Should().Equal((Project, "xv_count"), (Project, "xv_pin"));
        }
        finally
        {
            await Store.LoadAsync(); // the host's store reads what this one wrote
        }
    }

    [Fact]
    public async Task An_import_leaves_a_non_securable_column_unsecured_with_a_warning()
    {
        await ImportFixtureAsync();
        var file = SolutionFixtureZip.Build(c =>
        {
            var name = c.Descendants("attribute").First(a => (string?)a.Attribute("PhysicalName") == "xv_Name");
            name.Element("IsSecured")!.Value = "1";
            var createdOn = c.Descendants("attribute").First(a => (string?)a.Attribute("PhysicalName") == "CreatedOn");
            createdOn.Element("IsSecured")!.Value = "1";
            var description = c.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == "xv_Description");
            description.Element("IsSecured")!.Value = "1";
        });
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = jobId });

        var project = Store.GetEntity(Project)!;
        project.Attributes.Single(a => a.LogicalName == "xv_name").IsSecured.Should().NotBe(true);
        project.Attributes.Single(a => a.LogicalName == "createdon").IsSecured.Should().NotBe(true);
        project.Attributes.Single(a => a.LogicalName == "xv_description").IsSecured.Should().BeTrue();
        var log = (string)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
        log.Should().Contain("xv_project.xv_name").And.Contain("cannot be secured");
        log.Should().Contain("xv_project.createdon");

        // A first import (the table is new) applies the same rule.
        await SolutionImportTests.ClearImportAsync(fx);
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });
        Store.GetEntity(Project)!.Attributes.Single(a => a.LogicalName == "xv_name").IsSecured.Should().NotBe(true);
        Store.GetEntity(Project)!.Attributes.Single(a => a.LogicalName == "xv_description").IsSecured.Should().BeTrue();
    }
}
