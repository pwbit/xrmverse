using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>Files on the served organization, for the tests of backup and restore that need a real <c>fileattachment</c> row.</summary>
public static class BackupFileFixtures
{
    public const string Attribute = "new_doc";

    /// <summary>A new table with a file column, one record and a file of <paramref name="content"/> in it; the file's id.</summary>
    public static async Task<(string Table, Guid RecordId, Guid FileId)> CreateFileAsync(XrmdFixture fx, byte[] content)
    {
        var table = "new_bk" + Guid.NewGuid().ToString("N")[..10];
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await fx.App.Services.GetRequiredService<IMetadataStore>()
            .CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = Attribute, SchemaName = Attribute } });
        var recordId = await admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var token = ((InitializeFileBlocksUploadResponse)await admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
            { Target = new EntityReference(table, recordId), FileAttributeName = Attribute, FileName = "a.bin" })).FileContinuationToken;
        var block = Convert.ToBase64String("block-000000"u8.ToArray());
        await admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = content });
        var committed = (CommitFileBlocksUploadResponse)await admin.ExecuteAsync(new CommitFileBlocksUploadRequest
            { FileContinuationToken = token, BlockList = [block], FileName = "a.bin", MimeType = "x/y" });
        return (table, recordId, committed.FileId);
    }

    /// <summary>A provisioner of the fixture's engine, on the host's file store and files service (or on <paramref name="store"/> and <paramref name="files"/>).</summary>
    public static IOrgProvisioner NewProvisioner(XrmdFixture fx, IFileStore? store = null, FileColumnService? files = null)
    {
        var configuration = fx.App.Services.GetRequiredService<IConfiguration>();
        store ??= fx.App.Services.GetRequiredService<IFileStore>();
        files ??= fx.App.Services.GetRequiredService<FileColumnService>();
        return fx.Provider == Xrmd.Abstractions.Storage.StorageProvider.PostgreSql
            ? new PostgresOrgProvisioner(configuration, new ListLogger<PostgresOrgProvisioner>(), store, files)
            : new SqlServerOrgProvisioner(configuration, new ListLogger<SqlServerOrgProvisioner>(), store, files);
    }

    /// <summary>The test seams of backup and restore of <paramref name="provisioner"/> (both engines have them, plan 2.7b, Task 9).</summary>
    internal static IOrgBackupSeams Seams(this IOrgProvisioner provisioner) => (IOrgBackupSeams)provisioner;
}
