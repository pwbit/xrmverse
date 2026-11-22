using Xrmd.Integration.Tests.Postgres;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xrmd.Admin;
using Xrmd.Core.Errors;
using Xrmd.Files;
using System.Formats.Tar;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The organization lifecycle of the Admin API: create, list, delete, backup and restore.</summary>
[Collection("xrmd")]
public sealed class AdminOrgTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly List<string> _created = [];
    private Guid _nonAdminOid;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _nonAdminOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _nonAdminOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "admin-org-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadpb_Project", DepthMask.Global)));
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await DropAsync(db);
    }

    private string ServedDatabase => TestProvider.DatabaseOf(fx.ConnectionString);

    private string NewName() => "org_" + Guid.NewGuid().ToString("N")[..10];

    private string ConnectionStringOf(string database) => TestProvider.WithDatabase(fx.ConnectionString, database);

    private async Task<T> ScalarAsync<T>(string database, string sql, params (string, object)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(ConnectionStringOf(database));
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        var value = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL.
        return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private Task DropAsync(string database) => TestProvider.DropDatabaseAsync(fx.ConnectionString, database);

    private Task<bool> DatabaseExistsAsync(string database) => TestProvider.DatabaseExistsAsync(fx.ConnectionString, database);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? oid, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (oid is { } o) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(o, fx.BaseUrl));
        if (body is not null) req.Content = JsonContent.Create(body);
        return fx.Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> AsAdminAsync(HttpMethod method, string path, object? body = null) => SendAsync(method, path, fx.AdminObjectId, body);

    private static async Task<JsonNode?> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<JsonNode> CreateOrgAsync(string name, Guid? adminOid = null)
    {
        var res = await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name, adminObjectId = adminOid ?? Guid.NewGuid(), adminName = "New Admin" });
        _created.Add(name);
        return (await JsonAsync(res, HttpStatusCode.Created))!;
    }

    [Fact]
    public async Task Create_org_creates_a_seeded_database_with_its_administrator()
    {
        var name = NewName();
        var adminOid = Guid.NewGuid();
        var org = await CreateOrgAsync(name, adminOid);

        org["name"]!.GetValue<string>().Should().Be(name);
        org["database"]!.GetValue<string>().Should().Be(name);
        var rootId = org["rootBusinessUnitId"]!.GetValue<Guid>();
        var adminId = org["adminUserId"]!.GetValue<Guid>();

        (await ScalarAsync<int>(name, "SELECT COUNT(*) FROM data.solution WHERE uniquename = N'Default'")).Should().Be(1);
        (await ScalarAsync<Guid>(name, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL")).Should().Be(rootId);
        (await ScalarAsync<Guid>(name, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", adminOid)))
            .Should().Be(adminId);
        (await ScalarAsync<int>(name, """
            SELECT COUNT(*) FROM data.systemuserroles ur JOIN data.role r ON r.roleid = ur.roleid
            WHERE ur.systemuserid = @u AND r.name = N'System Administrator'
            """, ("@u", adminId))).Should().Be(1);
        // Read committed snapshot is a SQL Server database option; PostgreSQL's read committed reads never wait for writers.
        if (!TestProvider.IsPostgres)
            (await ScalarAsync<int>(name, "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = @db", ("@db", name))).Should().Be(1);
    }

    [Fact]
    public async Task Create_org_with_an_existing_database_is_409()
    {
        var name = NewName();
        await CreateOrgAsync(name);
        var res = await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name, adminObjectId = Guid.NewGuid() });
        await JsonAsync(res, HttpStatusCode.Conflict);

        // The served database exists too.
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name = ServedDatabase, adminObjectId = Guid.NewGuid() }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_marks_the_served_database()
    {
        var name = NewName();
        await CreateOrgAsync(name);
        // A database that is not an xrmverse one is not listed.
        var foreign = NewName();
        _created.Add(foreign);
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, foreign);

        var list = (await JsonAsync(await AsAdminAsync(HttpMethod.Get, "/admin/orgs"), HttpStatusCode.OK))!.AsArray();
        var served = list.Single(o => o!["database"]!.GetValue<string>() == ServedDatabase)!;
        served["served"]!.GetValue<bool>().Should().BeTrue();
        served["name"]!.GetValue<string>().Should().Be(XrmdFixture.OrgName);
        served["schemaVersion"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(3);
        var other = list.Single(o => o!["database"]!.GetValue<string>() == name)!;
        other["served"]!.GetValue<bool>().Should().BeFalse();
        other["name"]!.GetValue<string>().Should().Be(name);
        list.Any(o => o!["database"]!.GetValue<string>() == foreign).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_removes_the_database()
    {
        var name = NewName();
        await CreateOrgAsync(name);
        (await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + name)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DatabaseExistsAsync(name)).Should().BeFalse();
        (await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + name)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_served_database_is_409()
    {
        await JsonAsync(await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + ServedDatabase), HttpStatusCode.Conflict);
        (await DatabaseExistsAsync(ServedDatabase)).Should().BeTrue();
    }

    [BackupFact]
    public async Task Delete_or_restore_of_a_database_served_by_another_process_is_409()
    {
        var name = NewName();
        await CreateOrgAsync(name);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{name}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var message = $"The database '{name}' is served by another xrmd process; stop it first.";

        var other = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Org", ConnectionStringOf(name));
            b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(fx.Provider));
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Seed:OrgName", name);
            b.UseSetting("Seed:AdminObjectId", fx.AdminObjectId.ToString());
            b.UseSetting("Pipeline:AsyncWorkerEnabled", "false");
            b.UseSetting("Files:FileSystem:Root", fx.FilesRoot);
        });
        try
        {
            (await other.CreateClient().GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);

            var delete = await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + name);
            (await JsonAsync(delete, HttpStatusCode.Conflict))!["error"]!["message"]!.GetValue<string>().Should().Be(message);
            (await DatabaseExistsAsync(name)).Should().BeTrue();

            var restore = await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{name}/restore", new { file, overwrite = true });
            (await JsonAsync(restore, HttpStatusCode.Conflict))!["error"]!["message"]!.GetValue<string>().Should().Be(message);
            (await other.CreateClient().GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await OrganizationDatabase.IsInstanceLockHeldByAnotherAsync(ConnectionStringOf(name), fx.Provider, CancellationToken.None))
                .Should().BeTrue("the other process still holds its lock");
        }
        finally
        {
            await other.DisposeAsync();
        }

        // Once the other process stopped, both are allowed again.
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{name}/restore", new { file, overwrite = true })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + name)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_non_xrmverse_database_is_400()
    {
        var foreign = NewName();
        _created.Add(foreign);
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, foreign);

        await JsonAsync(await AsAdminAsync(HttpMethod.Delete, "/admin/orgs/" + foreign), HttpStatusCode.BadRequest);
        (await DatabaseExistsAsync(foreign)).Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_names_are_400_without_sql()
    {
        var tooLong = "a" + new string('b', 100); // 101 characters
        foreach (var bad in new[] { "x];DROP", "", tooLong, "1abc", "a b" })
        {
            var create = await JsonAsync(await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name = bad, adminObjectId = Guid.NewGuid() }),
                HttpStatusCode.BadRequest);
            create!["error"]!["message"]!.GetValue<string>().Should().Be($"Invalid organization database name '{bad}'.");
        }

        foreach (var bad in new[] { "x];DROP", tooLong, "1abc" })
        {
            var path = "/admin/orgs/" + Uri.EscapeDataString(bad);
            foreach (var request in new[] { (HttpMethod.Delete, path), (HttpMethod.Post, path + "/backup"), (HttpMethod.Post, path + "/restore") })
            {
                var res = await JsonAsync(await AsAdminAsync(request.Item1, request.Item2, new { file = "/x.bak", overwrite = false }), HttpStatusCode.BadRequest);
                res!["error"]!["message"]!.GetValue<string>().Should().Be($"Invalid organization database name '{bad}'.");
            }
        }

        (await DatabaseExistsAsync("x];DROP")).Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_name_from_a_non_admin_is_403()
    {
        // The role check answers before the input is validated.
        foreach (var res in new[]
        {
            await SendAsync(HttpMethod.Post, "/admin/orgs", _nonAdminOid, new { name = "x];DROP", adminObjectId = Guid.NewGuid() }),
            await SendAsync(HttpMethod.Delete, "/admin/orgs/" + Uri.EscapeDataString("x];DROP"), _nonAdminOid),
            await SendAsync(HttpMethod.Post, "/admin/orgs/" + Uri.EscapeDataString("x];DROP") + "/backup", _nonAdminOid),
            await SendAsync(HttpMethod.Post, "/admin/orgs/" + Uri.EscapeDataString("x];DROP") + "/restore", _nonAdminOid, new { file = "x" }),
            await SendAsync(HttpMethod.Get, "/admin/orgs", _nonAdminOid),
        })
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/admin/orgs", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [BackupFact]
    public async Task Backup_then_restore_into_a_new_database_roundtrips_rows()
    {
        var source = NewName();
        var org = await CreateOrgAsync(source);
        var adminId = org["adminUserId"]!.GetValue<Guid>();
        await using (var conn = await TestProvider.OpenAsync(ConnectionStringOf(source)))
        {
            await TestSql.Command(conn, "UPDATE data.systemuser SET fullname = N'Round Trip' WHERE systemuserid = '" + adminId + "'").ExecuteNonQueryAsync();
        }

        var backup = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!;
        var file = backup["file"]!.GetValue<string>();
        file.Should().Contain(source + "-").And.EndWith(fx.BackupExtension);
        backup["sizeBytes"]!.GetValue<long>().Should().BeGreaterThan(0);

        var target = NewName();
        _created.Add(target);
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await ScalarAsync<string>(target, "SELECT fullname FROM data.systemuser WHERE systemuserid = @u", ("@u", adminId))).Should().Be("Round Trip");
        (await ScalarAsync<int>(target, "SELECT COUNT(*) FROM data.solution WHERE uniquename = N'Default'")).Should().Be(1);
        // The source is untouched (COPY_ONLY) and the restored database is listed.
        (await ScalarAsync<int>(source, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = N'Round Trip'")).Should().Be(1);
        var list = (await JsonAsync(await AsAdminAsync(HttpMethod.Get, "/admin/orgs"), HttpStatusCode.OK))!.AsArray();
        list.Any(o => o!["database"]!.GetValue<string>() == target).Should().BeTrue();

        // Over an existing database with overwrite: the rows come back.
        await using (var conn = await TestProvider.OpenAsync(ConnectionStringOf(target)))
        {
            await TestSql.Command(conn, "UPDATE data.systemuser SET fullname = N'Changed'").ExecuteNonQueryAsync();
        }

        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = true })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await ScalarAsync<string>(target, "SELECT fullname FROM data.systemuser WHERE systemuserid = @u", ("@u", adminId))).Should().Be("Round Trip");
    }

    [BackupFact]
    public async Task Restore_over_existing_without_overwrite_is_409()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/restore", new { file, overwrite = false }), HttpStatusCode.Conflict);
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/restore", new { file }), HttpStatusCode.Conflict);
    }

    [BackupFact]
    public async Task Restore_over_the_served_database_is_409()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        foreach (var overwrite in new[] { false, true })
            await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{ServedDatabase}/restore", new { file, overwrite }), HttpStatusCode.Conflict);
    }

    [BackupFact]
    public async Task Restore_from_outside_the_backup_directory_is_400()
    {
        var target = NewName();
        foreach (var file in new[] { "/tmp/evil.bak", "/var/opt/mssql/data/../evil.bak", "evil.bak", "" })
            await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false }), HttpStatusCode.BadRequest);
        (await DatabaseExistsAsync(target)).Should().BeFalse();
    }

    [BackupFact]
    public async Task Backup_and_restore_of_an_unknown_database_or_file()
    {
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, "/admin/orgs/nosuchorg/backup"), HttpStatusCode.NotFound);
        var dir = fx.BackupDirectory;
        var res = await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{NewName()}/restore", new { file = dir + "/missing.bak", overwrite = false });
        await JsonAsync(res, HttpStatusCode.NotFound);
    }

    // ---- files in backup and restore (ruling F9) ------------------------------------------------------------------------

    private IFileStore Store => fx.App.Services.GetRequiredService<IFileStore>();

    /// <summary>The archive beside a backup file, as this process sees it (<c>Admin:FilesBackupDirectory</c> of the fixture).</summary>
    private string ArchiveOf(string bakFile) => Path.Combine(fx.FilesBackupDirectory, Path.GetFileNameWithoutExtension(bakFile) + ".files.tar");

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private async Task WriteBlobAsync(string key, byte[] content)
    {
        using var ms = new MemoryStream(content);
        await Store.WriteAsync(key, ms, CancellationToken.None);
    }

    private async Task<byte[]> ReadBlobAsync(string key)
    {
        await using var s = await Store.OpenReadAsync(key, 0, null, CancellationToken.None);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static List<string> TarEntries(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new TarReader(fs);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } e) names.Add(e.Name);
        return names;
    }

    [BackupFact]
    public async Task Backup_writes_files_archive_beside_bak()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        var a = Bytes(5000);
        var b = Bytes(3 * 1024 * 1024);
        await WriteBlobAsync($"{source}/{Guid.NewGuid()}", a);
        await WriteBlobAsync($"{source}/{Guid.NewGuid()}", b);
        await WriteBlobAsync($"{source}x/{Guid.NewGuid()}", Bytes(10)); // another database whose name starts with ours

        var backup = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!;
        var file = backup["file"]!.GetValue<string>();
        var archive = ArchiveOf(file);
        File.Exists(archive).Should().BeTrue();
        backup["filesFile"]!.GetValue<string>().Should().EndWith(".files.tar");
        backup["filesCount"]!.GetValue<int>().Should().Be(2);
        backup["filesSizeBytes"]!.GetValue<long>().Should().Be(new FileInfo(archive).Length);
        var names = TarEntries(archive);
        names.Should().HaveCount(2).And.OnlyContain(n => n.StartsWith(source + "/", StringComparison.Ordinal));
    }

    [BackupFact]
    public async Task Backup_excludes_upload_staging()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        await WriteBlobAsync($"{source}/{Guid.NewGuid()}", Bytes(100));
        await WriteBlobAsync($"{source}/_upload/{Guid.NewGuid()}/0", Bytes(100));

        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        TarEntries(ArchiveOf(file)).Should().ContainSingle().Which.Should().NotContain("_upload");
    }

    [BackupFact]
    public async Task Restore_with_archive_restores_blobs_under_target_prefix()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var big = Bytes(9 * 1024 * 1024);
        var small = Bytes(321);
        var empty = Guid.NewGuid();
        await WriteBlobAsync($"{source}/{empty}", []); // zero-length blobs are archived and restored too
        await WriteBlobAsync($"{source}/{id1}", big);
        await WriteBlobAsync($"{source}/{id2}", small);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();

        var target = NewName();
        _created.Add(target);
        // A stale blob of the target's prefix is replaced, not merged.
        var stale = Guid.NewGuid();
        await WriteBlobAsync($"{target}/{stale}", Bytes(10));
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        SHA256.HashData(await ReadBlobAsync($"{target}/{id1}")).Should().Equal(SHA256.HashData(big));
        (await ReadBlobAsync($"{target}/{id2}")).Should().Equal(small);
        (await Store.GetLengthAsync($"{target}/{empty}", CancellationToken.None)).Should().Be(0);
        (await Store.GetLengthAsync($"{target}/{stale}", CancellationToken.None)).Should().BeNull();
        // The source keeps its blobs.
        (await ReadBlobAsync($"{source}/{id1}")).Should().Equal(big);
    }

    [BackupFact]
    public async Task Restore_into_another_name_serves_the_file_from_the_restored_organization()
    {
        // A real file column on the served organization; its backup restored as another database, which a second host then serves.
        var table = "new_bk" + Guid.NewGuid().ToString("N")[..10];
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc" } });
        var recordId = await admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var content = Bytes(6 * 1024 * 1024 + 17);
        var token = ((InitializeFileBlocksUploadResponse)await admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
            { Target = new EntityReference(table, recordId), FileAttributeName = "new_doc", FileName = "a.bin" })).FileContinuationToken;
        var blockIds = new List<string>();
        for (var offset = 0; offset < content.Length; offset += 4194304)
        {
            var id = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"block-{blockIds.Count:D6}"));
            blockIds.Add(id);
            await admin.ExecuteAsync(new UploadBlockRequest
                { FileContinuationToken = token, BlockId = id, BlockData = content[offset..Math.Min(content.Length, offset + 4194304)] });
        }
        await admin.ExecuteAsync(new CommitFileBlocksUploadRequest { FileContinuationToken = token, BlockList = [.. blockIds], FileName = "a.bin", MimeType = "application/octet-stream" });

        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{ServedDatabase}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var target = NewName();
        _created.Add(target);
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var second = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Org", ConnectionStringOf(target));
            b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(fx.Provider));
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Seed:OrgName", XrmdFixture.OrgName);
            b.UseSetting("Seed:AdminObjectId", fx.AdminObjectId.ToString());
            b.UseSetting("Pipeline:AsyncWorkerEnabled", "false");
            b.UseSetting("Files:FileSystem:Root", fx.FilesRoot);
        });
        var restored = TestServices.NewOrganizationService(second.Services, fx.AdminObjectId);
        var init = (InitializeFileBlocksDownloadResponse)await restored.ExecuteAsync(new InitializeFileBlocksDownloadRequest
            { Target = new EntityReference(table, recordId), FileAttributeName = "new_doc" });
        init.FileSizeInBytes.Should().Be(content.Length);
        using var assembled = new MemoryStream();
        for (long offset = 0; offset < init.FileSizeInBytes; offset += 4194304)
            assembled.Write(((DownloadBlockResponse)await restored.ExecuteAsync(new DownloadBlockRequest
                { FileContinuationToken = init.FileContinuationToken, Offset = offset, BlockLength = 4194304 })).Data);
        SHA256.HashData(assembled.ToArray()).Should().Equal(SHA256.HashData(content));
    }

    [BackupFact]
    public async Task Restore_without_archive_fails_explicitly_unless_files_false()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        var blob = Guid.NewGuid();
        await WriteBlobAsync($"{source}/{blob}", Bytes(50));
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var archive = ArchiveOf(file);
        File.Delete(archive);

        var target = NewName();
        var res = await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false });
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, text);
        text.Should().Contain(".files.tar").And.Contain("files");
        (await DatabaseExistsAsync(target)).Should().BeFalse("the archive is checked before SQL Server is touched");

        _created.Add(target);
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false, files = false })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await Store.GetLengthAsync($"{target}/{blob}", CancellationToken.None)).Should().BeNull("files=false restores no blobs");
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{NewName()}/restore", new { file, files = "no" }), HttpStatusCode.BadRequest);
    }

    [BackupFact]
    public async Task Restore_with_a_corrupt_archive_fails_before_touching_sql_or_the_store()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        await WriteBlobAsync($"{source}/{Guid.NewGuid()}", Bytes(50));
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{source}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var archive = ArchiveOf(file);
        // An entry outside any database prefix / with a bad key.
        await using (var fs = File.Create(archive))
        await using (var w = new TarWriter(fs))
            await w.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "../evil") { DataStream = new MemoryStream([1, 2, 3]) });

        var target = NewName();
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false }), HttpStatusCode.BadRequest);
        (await DatabaseExistsAsync(target)).Should().BeFalse();
    }

    [BackupFact]
    public async Task Restore_forgets_the_cached_token_key_of_the_database()
    {
        // The files service of a second host serving the target caches the target's token key; the provisioner (here built on that
        // service) restores over it with a database whose meta.filesecret differs.
        var table = "new_fg" + Guid.NewGuid().ToString("N")[..10];
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc" } });
        var recordId = await admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var uploadToken = ((InitializeFileBlocksUploadResponse)await admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
            { Target = new EntityReference(table, recordId), FileAttributeName = "new_doc", FileName = "a.bin" })).FileContinuationToken;
        var block = Convert.ToBase64String("block-000000"u8.ToArray());
        await admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = uploadToken, BlockId = block, BlockData = Bytes(1000) });
        await admin.ExecuteAsync(new CommitFileBlocksUploadRequest { FileContinuationToken = uploadToken, BlockList = [block], FileName = "a.bin", MimeType = "x/y" });

        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{ServedDatabase}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var target = NewName();
        _created.Add(target);
        (await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        // The target now has another secret than the backup.
        await using (var conn = await TestProvider.OpenAsync(ConnectionStringOf(target)))
        {
            await TestSql.Command(conn, TestProvider.IsPostgres
                ? "UPDATE meta.filesecret SET secret = decode(md5(random()::text) || md5(random()::text), 'hex')"
                : "UPDATE meta.filesecret SET secret = CRYPT_GEN_RANDOM(32)").ExecuteNonQueryAsync();
        }

        await using var second = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Org", ConnectionStringOf(target));
            b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(fx.Provider));
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Auth:Audience", "http://localhost/");
            b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
            b.UseSetting("Seed:OrgName", XrmdFixture.OrgName);
            b.UseSetting("Seed:AdminObjectId", fx.AdminObjectId.ToString());
            b.UseSetting("Pipeline:AsyncWorkerEnabled", "false");
            b.UseSetting("Files:FileSystem:Root", fx.FilesRoot);
        });
        var restored = TestServices.NewOrganizationService(second.Services, fx.AdminObjectId);
        async Task<InitializeFileBlocksDownloadResponse> InitAsync() =>
            (InitializeFileBlocksDownloadResponse)await restored.ExecuteAsync(new InitializeFileBlocksDownloadRequest
                { Target = new EntityReference(table, recordId), FileAttributeName = "new_doc" });
        async Task<int> ReadAsync(string token) =>
            ((DownloadBlockResponse)await restored.ExecuteAsync(new DownloadBlockRequest { FileContinuationToken = token, Offset = 0, BlockLength = 10 })).Data.Length;

        var old = (await InitAsync()).FileContinuationToken; // caches the key derived from the changed secret
        (await ReadAsync(old)).Should().Be(10);

        var filesOfSecond = second.Services.GetRequiredService<FileColumnService>();
        // A database another process serves is not restored over (ruling M6): the second host releases its instance lock (its
        // watchdog stops, as when the host stops); its services, and the key its files service cached, stay usable.
        await second.Services.GetRequiredService<Xrmd.Host.InstanceLockWatchdog>().StopAsync(CancellationToken.None);
        var provisioner = BackupFileFixtures.NewProvisioner(fx, second.Services.GetRequiredService<IFileStore>(), filesOfSecond);
        await provisioner.RestoreAsync(target, file, overwrite: true, files: true, CancellationToken.None);

        filesOfSecond.ForgetDatabase(target).Should().BeFalse("the restore already dropped the cached key");
        var fresh = (await InitAsync()).FileContinuationToken; // derived again from the restored secret
        (await ReadAsync(fresh)).Should().Be(10);
        var stale = () => ReadAsync(old);
        await stale.Should().ThrowAsync<XrmdFaultException>("a token of the replaced secret is not accepted");
    }

    [BackupFact]
    public async Task Restore_reports_files_the_archive_lacks_because_they_were_deleted_after_the_bak()
    {
        var (table, recordId, fileId) = await BackupFileFixtures.CreateFileAsync(fx, Bytes(2000));
        try
        {
            // The file disappears between BACKUP DATABASE (which still references it) and the archive listing.
            var provisioner = BackupFileFixtures.NewProvisioner(fx);
            provisioner.Seams().BeforeArchive = async ct => await Store.DeleteAsync($"{ServedDatabase}/{fileId}", ct);
            var backup = await provisioner.BackupAsync(ServedDatabase, CancellationToken.None);
            TarEntries(backup.FilesFile!).Should().NotContain($"{ServedDatabase}/{fileId}");

            var target = NewName();
            _created.Add(target);
            var res = await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file = backup.File, overwrite = false });
            var body = (await JsonAsync(res, HttpStatusCode.OK))!;
            body["restored"]!.GetValue<bool>().Should().BeTrue();
            body["warning"]!.GetValue<string>().Should().Contain("not in the file store");
            var missing = body["missingFiles"]!.AsArray().Single(m => m!["fileAttachmentId"]!.GetValue<Guid>() == fileId)!;
            missing["entity"]!.GetValue<string>().Should().Be(table);
            missing["attribute"]!.GetValue<string>().Should().Be(BackupFileFixtures.Attribute);
            missing["recordId"]!.GetValue<Guid>().Should().Be(recordId);
            body["missingCount"]!.GetValue<int>().Should().BeGreaterOrEqualTo(1);
        }
        finally
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync(table, recordId); // drops the row; the blob is already gone
        }
    }

    [BackupFact]
    public async Task Restore_check_lists_the_prefix_once_instead_of_reading_each_blob()
    {
        var (table, recordId, fileId) = await BackupFileFixtures.CreateFileAsync(fx, Bytes(500));
        try
        {
            var backup = await BackupFileFixtures.NewProvisioner(fx).BackupAsync(ServedDatabase, CancellationToken.None);
            var target = NewName();
            _created.Add(target);
            var spy = new SpyFileStore(Store);
            var provisioner = BackupFileFixtures.NewProvisioner(fx, spy);

            var result = await provisioner.RestoreAsync(target, backup.File, overwrite: false, files: false, CancellationToken.None);

            result.MissingFiles.Should().Contain(m => m.FileAttachmentId == fileId);
            spy.Reads.Should().Be(0, "the check compares the rows with one listing of the prefix, not one read per row");
        }
        finally
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync(table, recordId);
        }
    }

    [BackupFact]
    public async Task Restore_without_files_reports_the_blobs_the_database_references()
    {
        var (table, recordId, fileId) = await BackupFileFixtures.CreateFileAsync(fx, Bytes(500));
        try
        {
            var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{ServedDatabase}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
            var target = NewName();
            _created.Add(target);
            var body = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/restore", new { file, overwrite = false, files = false }), HttpStatusCode.OK))!;
            body["warning"]!.GetValue<string>().Should().Contain("files=false");
            body["missingFiles"]!.AsArray().Any(m => m!["fileAttachmentId"]!.GetValue<Guid>() == fileId).Should().BeTrue();
        }
        finally
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync(table, recordId);
        }
    }

    [BackupFact]
    public async Task Restore_checks_the_archive_entries_again_when_it_changes_after_validation()
    {
        var source = NewName();
        await CreateOrgAsync(source);
        await WriteBlobAsync($"{source}/{Guid.NewGuid()}", Bytes(100));
        var provisioner = BackupFileFixtures.NewProvisioner(fx);
        var backup = await provisioner.BackupAsync(source, CancellationToken.None);

        var target = NewName();
        _created.Add(target);
        // Swapped in place after the validation pass: a staged-upload entry, which the validation would have refused.
        provisioner.Seams().BeforeFilesRestore = async _ =>
        {
            await using var fs = new FileStream(backup.FilesFile!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            await using var w = new TarWriter(fs);
            await w.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"{source}/_upload/{Guid.NewGuid()}/0") { DataStream = new MemoryStream([1]) });
        };
        var act = () => provisioner.RestoreAsync(target, backup.File, overwrite: false, files: true, CancellationToken.None);
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.Message.Should().Contain("not a blob of an organization").And.Contain("files are incomplete");
        await foreach (var key in Store.ListAsync(target + "/", CancellationToken.None)) key.Should().NotContain("_upload");
    }

    /// <summary>The run's provisioner with the creation seams (plan 2.7a, Task 6: the PostgreSQL one has them too).</summary>
    private IOrgProvisioner NewProvisioner(Func<CancellationToken, Task>? before = null, Func<CancellationToken, Task>? after = null)
    {
        if (TestProvider.IsPostgres)
            return new PostgresOrgProvisioner(fx.App.Services.GetRequiredService<IConfiguration>(), new ListLogger<PostgresOrgProvisioner>(),
                fx.App.Services.GetRequiredService<IFileStore>(), fx.App.Services.GetRequiredService<FileColumnService>())
            {
                BeforeCreateDatabase = before, AfterCreateDatabase = after,
            };
        var provisioner = NewProvisioner(new ListLogger<SqlServerOrgProvisioner>());
        provisioner.BeforeCreateDatabase = before;
        provisioner.AfterCreateDatabase = after;
        return provisioner;
    }

    private SqlServerOrgProvisioner NewProvisioner(ListLogger<SqlServerOrgProvisioner> logger) =>
        new(fx.App.Services.GetRequiredService<IConfiguration>(), logger, fx.App.Services.GetRequiredService<IFileStore>(),
            fx.App.Services.GetRequiredService<FileColumnService>());

    [Fact]
    public async Task Create_org_racing_another_creator_is_409_and_leaves_the_other_database_alone()
    {
        var name = NewName();
        _created.Add(name);
        // Someone else creates the database between the existence check and the CREATE.
        var provisioner = NewProvisioner(before: async ct =>
        {
            await TestProvider.CreateDatabaseAsync(fx.ConnectionString, name);
            await TestSql.ExecAsync(ConnectionStringOf(name), "CREATE SCHEMA other_owner");
            await TestSql.ExecAsync(ConnectionStringOf(name), "CREATE TABLE other_owner.t (id int)");
        });

        var act = () => provisioner.CreateAsync(name, Guid.NewGuid(), null, CancellationToken.None);
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(409);

        // Not migrated into, not dropped.
        (await DatabaseExistsAsync(name)).Should().BeTrue();
        (await TestSql.TableExistsAsync(ConnectionStringOf(name), "other_owner", "t")).Should().BeTrue();
        (await TestSql.TableExistsAsync(ConnectionStringOf(name), "meta", "schema_version")).Should().BeFalse();
    }

    [Fact]
    public async Task Create_org_that_fails_after_create_drops_its_own_database()
    {
        var name = NewName();
        _created.Add(name);
        var provisioner = NewProvisioner(after: _ => throw new InvalidOperationException("boom"));

        var act = () => provisioner.CreateAsync(name, Guid.NewGuid(), null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        (await DatabaseExistsAsync(name)).Should().BeFalse();
    }

    /// <summary>
    /// The instance lock in <paramref name="database"/>, tried without waiting as a starting xrmd process does (sp_getapplock or a
    /// session advisory lock), and released at once: true when it was granted.
    /// </summary>
    private async Task<bool> TryTakeInstanceLockAsync(string database, CancellationToken ct)
    {
        var held = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(database), fx.Provider, ct);
        if (held is null) return false;
        await held.DisposeAsync();
        return true;
    }

    [BackupFact]
    public async Task Restore_stamps_restored_at_so_earlier_change_tokens_expire()
    {
        var name = NewName();
        await CreateOrgAsync(name);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{name}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        async Task<DateTime?> RestoredAtAsync(string database)
        {
            await using var conn = await TestProvider.OpenAsync(ConnectionStringOf(database));
            await using var cmd = TestSql.Command(conn, "SELECT restored_at FROM meta.change_tracking_state");
            return await cmd.ExecuteScalarAsync() as DateTime?;
        }
        (await RestoredAtAsync(name)).Should().BeNull("a database that was never restored has no restore time");
        var target = NewName();
        _created.Add(target);
        var before = DateTime.UtcNow.AddSeconds(-5);

        await BackupFileFixtures.NewProvisioner(fx).RestoreAsync(target, file, overwrite: false, files: true, CancellationToken.None);
        await BackupFileFixtures.NewProvisioner(fx).RestoreAsync(name, file, overwrite: true, files: true, CancellationToken.None);

        (await RestoredAtAsync(target)).Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
        (await RestoredAtAsync(name)).Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
    }

    /// <summary>
    /// Plan 2.7b, Task 9 (both engines): a backup runs beside a write that is still open on the database and does not wait for it
    /// (BACKUP DATABASE; pg_dump's snapshot and ACCESS SHARE locks); the restored database has the committed state, without the
    /// write. The write commits afterwards.
    /// </summary>
    [BackupFact]
    public async Task Backup_runs_beside_an_open_write_and_restores_the_committed_state()
    {
        var source = NewName();
        var adminId = (await CreateOrgAsync(source))["adminUserId"]!.GetValue<Guid>();
        await using var writer = await TestProvider.OpenAsync(ConnectionStringOf(source));
        await using var tx = await writer.BeginTransactionAsync();
        await using (var update = TestSql.Command(writer, $"UPDATE data.systemuser SET fullname = 'Uncommitted' WHERE systemuserid = '{adminId}'"))
        {
            update.Transaction = tx;
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var backup = await BackupFileFixtures.NewProvisioner(fx).BackupAsync(source, timeout.Token);
        await tx.CommitAsync();

        var target = NewName();
        _created.Add(target);
        await BackupFileFixtures.NewProvisioner(fx).RestoreAsync(target, backup.File, overwrite: false, files: true, CancellationToken.None);
        (await ScalarAsync<string>(target, "SELECT fullname FROM data.systemuser WHERE systemuserid = @u", ("@u", adminId))).Should().Be("New Admin");
        (await ScalarAsync<string>(source, "SELECT fullname FROM data.systemuser WHERE systemuserid = @u", ("@u", adminId))).Should().Be("Uncommitted");
    }

    [BackupFact]
    public async Task Delete_and_restore_hold_the_instance_lock_from_their_check_to_single_user()
    {
        // A process starting between the check and SINGLE_USER WITH ROLLBACK IMMEDIATE is refused (its lock is not granted),
        // instead of starting and having its sessions ended under it.
        var name = NewName();
        await CreateOrgAsync(name);
        var file = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{name}/backup"), HttpStatusCode.OK))!["file"]!.GetValue<string>();
        var provisioner = BackupFileFixtures.NewProvisioner(fx);
        var results = new List<bool>();
        provisioner.Seams().AfterInstanceCheck = async ct => results.Add(await TryTakeInstanceLockAsync(name, ct));

        await provisioner.RestoreAsync(name, file, overwrite: true, files: true, CancellationToken.None);
        await provisioner.DropAsync(name, CancellationToken.None);

        results.Should().Equal([false, false], "the provisioner holds the lock: a process starting meanwhile is not granted it");
        (await DatabaseExistsAsync(name)).Should().BeFalse();
    }

    [SqlServerFact("SINGLE_USER and its MULTI_USER revert are SQL Server's; a failed PostgreSQL restore leaves the target untouched (PostgresBackupTests)")]
    public async Task Failed_restore_whose_multi_user_revert_fails_says_the_database_may_be_single_user()
    {
        var target = NewName();
        await CreateOrgAsync(target);
        var backup = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{target}/backup"), HttpStatusCode.OK))!;
        var file = backup["file"]!.GetValue<string>();

        var logger = new ListLogger<SqlServerOrgProvisioner>();
        var provisioner = NewProvisioner(logger);
        // The restore's connection is killed after SINGLE_USER: RESTORE fails and so does the MULTI_USER revert on it.
        provisioner.BeforeRestore = async (master, ct) =>
        {
            await using var killer = await TestProvider.OpenAsync(ConnectionStringOf("master"));
            await TestSql.Command(killer, $"KILL {master.ServerProcessId}").ExecuteNonQueryAsync(ct);
        };

        var act = () => provisioner.RestoreAsync(target, file, overwrite: true, CancellationToken.None);
        var thrown = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        thrown.Message.Should().Contain($"'{target}' may still be in SINGLE_USER mode")
            .And.Contain($"ALTER DATABASE [{target}] SET MULTI_USER");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("SINGLE_USER"));
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
