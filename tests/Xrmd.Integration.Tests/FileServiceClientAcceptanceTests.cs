using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 8 acceptance: file and image columns through the real <see cref="ServiceClient"/> (SOAP).</summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class FileServiceClientAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const int BlockSize = 4194304;
    private const int FileSize = 50 * 1024 * 1024;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)), useUniqueInstance: true, new TestOutputLogger(output));

    private async Task<string> NewTableAsync(ServiceClient client, bool image)
    {
        var name = "new_sc" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await fx.App.Services.GetRequiredService<IMetadataStore>().CreateEntityAsync(entity,
            new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        AttributeMetadata column = image
            ? new ImageAttributeMetadata { LogicalName = "new_pic", SchemaName = "new_pic", CanStoreFullImage = true }
            : new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", MaxSizeInKB = 100 * 1024 };
        client.Execute(new CreateAttributeRequest { EntityName = name, Attribute = column });
        return name;
    }

    [Fact]
    public async Task ServiceClient_uploads_and_downloads_50MB_file()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var table = await NewTableAsync(client, image: false);
        var id = client.Create(new Entity(table) { ["new_name"] = "a" });
        var target = new EntityReference(table, id);
        var content = FileLargeWebApiTests.SeededBytes(FileSize, 8001);
        var expected = SHA256.HashData(content);

        var init = (InitializeFileBlocksUploadResponse)client.Execute(new InitializeFileBlocksUploadRequest
        {
            Target = target, FileAttributeName = "new_doc", FileName = "big.bin",
        });
        var blocks = Enumerable.Range(0, (FileSize + BlockSize - 1) / BlockSize).ToArray();
        var ids = blocks.Select(i => Convert.ToBase64String(BitConverter.GetBytes(i))).ToArray();
        // Out of order: the BlockList defines the file, not the order of the UploadBlock calls.
        foreach (var i in Enumerable.Reverse(blocks))
            client.Execute(new UploadBlockRequest
            {
                FileContinuationToken = init.FileContinuationToken, BlockId = ids[i],
                BlockData = content[(i * BlockSize)..Math.Min((i + 1) * BlockSize, FileSize)],
            });
        var commit = (CommitFileBlocksUploadResponse)client.Execute(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = init.FileContinuationToken, BlockList = ids, FileName = "big.bin", MimeType = "application/octet-stream",
        });
        commit.FileSizeInBytes.Should().Be(FileSize);
        commit.FileId.Should().NotBeEmpty();

        var download = (InitializeFileBlocksDownloadResponse)client.Execute(new InitializeFileBlocksDownloadRequest
        {
            Target = target, FileAttributeName = "new_doc",
        });
        download.FileSizeInBytes.Should().Be(FileSize);
        download.FileName.Should().Be("big.bin");
        download.IsChunkingSupported.Should().BeTrue();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            var part = (DownloadBlockResponse)client.Execute(new DownloadBlockRequest
            {
                FileContinuationToken = download.FileContinuationToken, Offset = offset, BlockLength = Math.Min(BlockSize, FileSize - offset),
            });
            hash.AppendData(part.Data);
        }
        hash.GetHashAndReset().Should().Equal(expected);

        client.Execute(new DeleteFileRequest { FileId = commit.FileId });
        var after = client.Retrieve(table, id, new ColumnSet("new_doc"));
        after.Contains("new_doc").Should().BeFalse("DeleteFile cleared the column");
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task ServiceClient_sets_and_reads_image()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var table = await NewTableAsync(client, image: true);
        var id = client.Create(new Entity(table) { ["new_name"] = "a", ["new_pic"] = Png(300, 200) });

        var read = client.Retrieve(table, id, new ColumnSet("new_pic", "new_pic_url", "new_picid"));
        var thumbnail = read["new_pic"].Should().BeOfType<byte[]>().Subject;
        using var decoded = SKBitmap.Decode(thumbnail);
        (decoded.Width, decoded.Height).Should().Be((144, 144));
        read["new_pic_url"].Should().BeOfType<string>().Which.Should().StartWith("/Image/download.aspx");
        read["new_picid"].Should().BeOfType<Guid>();

        client.Update(new Entity(table, id) { ["new_pic"] = null });
        client.Retrieve(table, id, new ColumnSet("new_pic")).Contains("new_pic").Should().BeFalse();
    }
}
