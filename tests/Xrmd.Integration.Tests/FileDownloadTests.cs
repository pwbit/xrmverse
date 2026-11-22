using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The file download messages (InitializeFileBlocksDownload, DownloadBlock) through the organization service, the download
/// sessions (ruling F6) and the Read demanded on the record. The Web API's downloads run the same messages (FileWebApiTests).
/// </summary>
[Collection("xrmd")]
public sealed class FileDownloadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Doc = "new_doc";
    private const int BlockSize = 4194304;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private FileColumnService Files => fx.App.Services.GetRequiredService<FileColumnService>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private async Task<string> NewTableAsync(bool image = false)
    {
        var name = "new_fd" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc },
        });
        if (image)
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = "new_pic", SchemaName = "new_pic" },
            });
        return name;
    }

    private static string BlockId(int i) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"block-{i:D6}"));

    private static byte[] Content(int length) => RandomNumberGenerator.GetBytes(length);

    /// <summary>Uploads <paramref name="content"/> in blocks of 4 MB with the upload messages; returns the file id.</summary>
    private async Task<Guid> UploadAsync(string table, Guid id, byte[] content, string fileName = "report.pdf", string mimeType = "application/pdf")
    {
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc, FileName = fileName,
        })).FileContinuationToken;
        var ids = new List<string>();
        for (var offset = 0; offset < content.Length; offset += BlockSize, ids.Add(BlockId(ids.Count)))
            await Admin.ExecuteAsync(new UploadBlockRequest
            {
                FileContinuationToken = token, BlockId = BlockId(ids.Count),
                BlockData = content[offset..Math.Min(content.Length, offset + BlockSize)],
            });
        return ((CommitFileBlocksUploadResponse)await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [.. ids], FileName = fileName, MimeType = mimeType,
        })).FileId;
    }

    private static async Task<InitializeFileBlocksDownloadResponse> InitializeAsync(OrganizationService s, string table, Guid id,
        string attribute = Doc) =>
        (InitializeFileBlocksDownloadResponse)await s.ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = attribute,
        });

    private static async Task<byte[]> BlockAsync(OrganizationService s, string token, long offset, long length) =>
        ((DownloadBlockResponse)await s.ExecuteAsync(new DownloadBlockRequest
        {
            FileContinuationToken = token, Offset = offset, BlockLength = length,
        })).Data;

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        return fault;
    }

    private async Task<(Guid UserId, OrganizationService Service)> UserAsync(string table, params string[] rights)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        var schema = Store.GetEntity(table)!.SchemaName;
        await GrantAsync(fx, user, team: false, [.. rights.Select(r => ($"prv{r}{schema}", DepthMask.Global))]);
        return (user, ServiceFor(fx, objectId));
    }

    // ---- DownloadBlock ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Download_blocks_reassemble_to_identical_sha256()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var content = Content(12 * 1024 * 1024 + 123);
        await UploadAsync(table, id, content);

        var init = await InitializeAsync(Admin, table, id);
        init.FileSizeInBytes.Should().Be(content.Length);
        init.FileName.Should().Be("report.pdf");
        init.IsChunkingSupported.Should().BeTrue();
        using var assembled = new MemoryStream();
        for (long offset = 0; offset < init.FileSizeInBytes; offset += BlockSize)
            assembled.Write(await BlockAsync(Admin, init.FileContinuationToken, offset, BlockSize));

        SHA256.HashData(assembled.ToArray()).Should().Equal(SHA256.HashData(content));
    }

    [Fact]
    public async Task BlockLength_constant_last_block_returns_remainder()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var content = Content(2500);
        await UploadAsync(table, id, content);
        var token = (await InitializeAsync(Admin, table, id)).FileContinuationToken;

        (await BlockAsync(Admin, token, 0, 1000)).Should().Equal(content[..1000]);
        (await BlockAsync(Admin, token, 2000, 1000)).Should().Equal(content[2000..], "the last block is the remainder");
        (await BlockAsync(Admin, token, 2499, BlockSize)).Should().Equal(content[2499..]);
    }

    [Fact]
    public async Task Offset_past_the_end_and_invalid_lengths_are_InvalidArgument()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, Content(100));
        var token = (await InitializeAsync(Admin, table, id)).FileContinuationToken;

        await FaultAsync(() => BlockAsync(Admin, token, 100, 10), ErrorCodes.InvalidArgument);
        await FaultAsync(() => BlockAsync(Admin, token, 5000, 10), ErrorCodes.InvalidArgument);
        await FaultAsync(() => BlockAsync(Admin, token, -1, 10), ErrorCodes.InvalidArgument);
        await FaultAsync(() => BlockAsync(Admin, token, 0, 0), ErrorCodes.InvalidArgument);
        await FaultAsync(() => BlockAsync(Admin, token, 0, BlockSize + 1), ErrorCodes.InvalidArgument);
        (await BlockAsync(Admin, token, 0, 10)).Should().HaveCount(10, "the session survives a refused block");
    }

    [Fact]
    public async Task Empty_file_downloads_as_zero_bytes()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, []);

        var init = await InitializeAsync(Admin, table, id);

        init.FileSizeInBytes.Should().Be(0);
        (await BlockAsync(Admin, init.FileContinuationToken, 0, 10)).Should().BeEmpty();
    }

    // ---- Initialize ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Empty_column_initialize_is_ObjectDoesNotExist()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });

        var fault = await FaultAsync(() => InitializeAsync(Admin, table, id), ErrorCodes.ObjectDoesNotExist);
        fault.HttpStatus.Should().Be(404);
        await FaultAsync(() => InitializeAsync(Admin, table, Guid.NewGuid()), ErrorCodes.ObjectDoesNotExist);
        await FaultAsync(() => InitializeAsync(Admin, table, id, "new_name"), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task DownloadBlock_after_the_file_was_replaced_is_ObjectDoesNotExist()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, Content(100));
        var token = (await InitializeAsync(Admin, table, id)).FileContinuationToken;

        await UploadAsync(table, id, Content(200)); // the session's file and its blob are gone

        var fault = await FaultAsync(() => BlockAsync(Admin, token, 0, 100), ErrorCodes.ObjectDoesNotExist);
        fault.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task A_blob_that_vanishes_between_its_length_and_its_open_is_ObjectDoesNotExist()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, Content(100));
        var spy = new SpyFileStore(fx.App.Services.GetRequiredService<IFileStore>());
        var files = new FileColumnService(spy, Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        var service = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, files: files);
        var token = (await InitializeAsync(service, table, id)).FileContinuationToken;
        spy.VanishOnOpen = true;

        (await FaultAsync(() => BlockAsync(service, token, 0, 100), ErrorCodes.ObjectDoesNotExist)).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Download_requires_read_on_record()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var content = Content(300);
        await UploadAsync(table, id, content);
        var (_, writer) = await UserAsync(table, "Write");
        var (_, reader) = await UserAsync(table, "Read");

        await FaultAsync(() => InitializeAsync(writer, table, id), ErrorCodes.PrivilegeDenied);
        var init = await InitializeAsync(reader, table, id);
        (await BlockAsync(reader, init.FileContinuationToken, 0, 300)).Should().Equal(content);
    }

    // ---- tokens -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Foreign_download_token_refused()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, Content(300));
        var token = (await InitializeAsync(Admin, table, id)).FileContinuationToken;
        var (_, other) = await UserAsync(table, "Read");
        var unknown = FileSessionTokens.Derive(new byte[32], Guid.NewGuid()).Issue(Guid.NewGuid(), Guid.NewGuid());

        var foreign = await FaultAsync(() => BlockAsync(other, token, 0, 10), ErrorCodes.InvalidArgument);
        var missing = await FaultAsync(() => BlockAsync(other, unknown, 0, 10), ErrorCodes.InvalidArgument);
        var tampered = await FaultAsync(() => BlockAsync(Admin, token[..^2] + (token[^1] == 'A' ? "BB" : "AA"), 0, 10), ErrorCodes.InvalidArgument);

        foreign.Message.Should().Be(missing.Message).And.Be(tampered.Message);
        (await BlockAsync(Admin, token, 0, 10)).Should().HaveCount(10, "the owner's session is untouched");
    }

    [Fact]
    public async Task Upload_token_is_not_a_download_token()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        await UploadAsync(table, id, Content(300));
        var uploadToken = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc, FileName = "b.bin",
        })).FileContinuationToken;

        await FaultAsync(() => BlockAsync(Admin, uploadToken, 0, 10), ErrorCodes.InvalidArgument);
    }
}
