using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Image columns: values on Create and Update (ruling F4), thumbnails (F2, F3), full-size images in the file store when
/// <c>CanStoreFullImage</c>, the upload and download messages on an image column, the companions <c>&lt;col&gt;id</c>,
/// <c>&lt;col&gt;_timestamp</c> and the computed <c>&lt;col&gt;_url</c> (F1), and <c>/Image/download.aspx</c> (F10).
/// The images are made with SkiaSharp here: no binary fixture is committed.
/// </summary>
[Collection("xrmd")]
public sealed class ImageColumnTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pic = "new_pic";
    private const string Pic2 = "new_pic2";
    private const string Db = "xrmverse_test";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private IFileStore Blobs => fx.App.Services.GetRequiredService<IFileStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A user-owned table with the image columns <paramref name="images"/> (the first one is the primary image).</summary>
    private async Task<string> NewTableAsync(params (string Name, bool Full, int? MaxSizeInKB)[] images)
    {
        var name = "new_im" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        foreach (var (column, full, maxSize) in images)
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new ImageAttributeMetadata { LogicalName = column, SchemaName = column, CanStoreFullImage = full, MaxSizeInKB = maxSize },
            });
        return name;
    }

    private Task<string> NewTableAsync(bool full = false) => NewTableAsync((Pic, full, null));

    /// <summary>A <paramref name="width"/> x <paramref name="height"/> image of four coloured quadrants.</summary>
    private static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint();
            paint.Color = SKColors.Blue;
            canvas.DrawRect(width / 2f, 0, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A PNG of random pixels: it does not compress, so it is about 4 bytes per pixel.</summary>
    private static byte[] Noise(int side)
    {
        using var bitmap = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var random = new Random(42);
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int Width, int Height, SKEncodedImageFormat Format) Describe(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }

    private async Task<byte[]?> BlobAsync(Guid fileId)
    {
        await fx.DrainFileDeletionsAsync();
        if (await Blobs.GetLengthAsync($"{Db}/{fileId}", default) is null) return null;
        await using var stream = await Blobs.OpenReadAsync($"{Db}/{fileId}", 0, null, default);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private async Task<HashSet<string>> BlobKeysAsync()
    {
        await fx.DrainFileDeletionsAsync();
        var keys = new HashSet<string>();
        await foreach (var key in Blobs.ListAsync(Db + "/", default)) keys.Add(key);
        return keys;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> AttachmentRowsAsync(Guid recordId) =>
        Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM data.fileattachment WHERE objectid = @id", ("@id", recordId)));

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act, int code)
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.ErrorCode.Should().Be(code, fault.Message);
        return fault;
    }

    private Task<Entity> RetrieveImageAsync(OrganizationService s, string table, Guid id, string column = Pic) =>
        s.RetrieveAsync(table, id, new ColumnSet(column, column + "id", column + "_timestamp", column + "_url"));

    private static string BlockId(int i) => Convert.ToBase64String(Encoding.ASCII.GetBytes($"block-{i:D6}"));

    /// <summary>Uploads <paramref name="content"/> to <paramref name="column"/> with the upload messages, in blocks of 4 MB.</summary>
    private static async Task<CommitFileBlocksUploadResponse> UploadAsync(OrganizationService s, string table, Guid id, string column,
        byte[] content, string fileName, string mimeType)
    {
        var token = ((InitializeFileBlocksUploadResponse)await s.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = column, FileName = fileName,
        })).FileContinuationToken;
        var ids = new List<string>();
        for (var offset = 0; offset < content.Length; offset += FileColumnService.MaxBlockSize, ids.Add(BlockId(ids.Count)))
            await s.ExecuteAsync(new UploadBlockRequest
            {
                FileContinuationToken = token, BlockId = BlockId(ids.Count),
                BlockData = content[offset..Math.Min(content.Length, offset + FileColumnService.MaxBlockSize)],
            });
        return (CommitFileBlocksUploadResponse)await s.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [.. ids], FileName = fileName, MimeType = mimeType,
        });
    }

    /// <summary>The full image with the download messages.</summary>
    private static async Task<(byte[] Content, string FileName)> DownloadAsync(OrganizationService s, string table, Guid id, string column = Pic)
    {
        var init = (InitializeFileBlocksDownloadResponse)await s.ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = column,
        });
        using var content = new MemoryStream();
        for (long offset = 0; offset < init.FileSizeInBytes; offset += FileColumnService.MaxBlockSize)
            content.Write(((DownloadBlockResponse)await s.ExecuteAsync(new DownloadBlockRequest
            {
                FileContinuationToken = init.FileContinuationToken, Offset = offset, BlockLength = FileColumnService.MaxBlockSize,
            })).Data);
        return (content.ToArray(), init.FileName);
    }

    private async Task<(Guid ObjectId, OrganizationService Service)> UserAsync(string table, params string[] rights)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        var schema = Store.GetEntity(table)!.SchemaName;
        await GrantAsync(fx, user, team: false, [.. rights.Select(r => ($"prv{r}{schema}", DepthMask.Global))]);
        return (objectId, ServiceFor(fx, objectId));
    }

    private Task<HttpResponseMessage> GetAsync(string url, Guid? objectId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (objectId is { } oid) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    // ---- Create and Update ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_image_update_takes_the_record_key_lock_before_its_row_lock()
    {
        // Plan 2.7b, Task 6 fix round 1: the image column's current value is read under the record's row lock, before the Core's
        // audit read takes its own row lock (LockRowAsync: key lock, then row lock). On PostgreSQL the key lock is a lock of its
        // own (an advisory lock), so the image read takes it first too: a transaction holding the key lock makes the Update wait
        // before it locks the row, and that transaction then takes the row lock without waiting. Were the row locked first, the
        // two would deadlock. On SQL Server the key lock is the key-range lock of the row read itself (no statement is sent), so
        // the Update does not wait for a key lock alone.
        var table = await NewTableAsync();
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = table, IsAuditEnabled = new BooleanManagedProperty(true) } });
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var organization = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT TOP 1 organizationid FROM data.organization"))!;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        try
        {
            var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await factory.OpenAsync();
            int spid;
            await using (var cmd = TestSql.Command(conn, TestSql.SessionIdQuery)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Task update;
            await using (var tx = await conn.BeginTransactionAsync())
            {
                var s = new DataSession(conn, tx);
                await s.KeyLockAsync(table, id, default);
                update = Admin.UpdateAsync(new Entity(table, id) { ["new_name"] = "b", [Pic] = Image(64, 64) });
                if (TestProvider.IsPostgres)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, TestSql.BlockedByQuery, ("@spid", spid))) == 0)
                    {
                        update.IsCompleted.Should().BeFalse("the Update must wait for the record's key lock");
                        DateTime.UtcNow.Should().BeBefore(deadline);
                        await Task.Delay(20);
                    }
                }
                else
                {
                    await update;
                }

                // The Core's row lock after the key lock: it must not wait for the Update.
                var key = s.Quote(SystemAttributes.PrimaryIdName(Store.GetEntity(table)!));
                await using (var cmd = s.Command(
                                 $"SELECT {key} FROM {s.DataTable(table)}{s.Dialect.LockHint(Xrmd.Abstractions.Storage.RowLock.UpdateRow)} " +
                                 $"WHERE {key} = @id{s.Dialect.LockClause(Xrmd.Abstractions.Storage.RowLock.UpdateRow)}"))
                {
                    cmd.AddParameter("@id", id);
                    var locked = cmd.ExecuteScalarAsync();
                    (await Task.WhenAny(locked, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(locked, "the row is not locked by the waiting Update");
                    (await locked).Should().Be(id);
                }
                update.IsCompleted.Should().Be(!TestProvider.IsPostgres);
                await tx.CommitAsync();
            }
            await update;

            var record = await Admin.RetrieveAsync(table, id, new ColumnSet("new_name", Pic));
            record.GetAttributeValue<string>("new_name").Should().Be("b");
            record.GetAttributeValue<byte[]>(Pic).Should().NotBeNull();
        }
        finally
        {
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
        }
    }

    [Fact]
    public async Task Create_with_primary_image_stores_thumbnail_and_companions()
    {
        var table = await NewTableAsync();
        var before = DateTime.UtcNow.Ticks;

        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = Image(300, 428) });

        var record = await RetrieveImageAsync(Admin, table, id);
        Describe(record.GetAttributeValue<byte[]>(Pic)).Should().Be((144, 144, SKEncodedImageFormat.Png));
        record.GetAttributeValue<Guid>(Pic + "id").Should().NotBe(Guid.Empty);
        record.GetAttributeValue<long>(Pic + "_timestamp").Should().BeInRange(before, DateTime.UtcNow.Ticks);
        record.GetAttributeValue<string>(Pic + "_url").Should().NotBeNullOrEmpty();
        (await AttachmentRowsAsync(id)).Should().Be(0, "CanStoreFullImage is false: only the thumbnail is kept");
    }

    [Fact]
    public async Task Create_with_non_primary_image_is_0x80090487()
    {
        var table = await NewTableAsync((Pic, false, null), (Pic2, false, null));

        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic2] = Image(10, 10) }),
            ErrorCodes.CannotUploadNonPrimaryImageAttributeOnCreate);

        fault.Message.Should().Be($"Only the primary image column can be set on create; set {Pic2} of {table} with an update.");
        fault.HttpStatus.Should().Be(400);
        // Null on a non-primary column sets nothing, and an Update may set any image column.
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "b", [Pic2] = null });
        await Admin.UpdateAsync(new Entity(table, id) { [Pic2] = Image(10, 10) });
        (await RetrieveImageAsync(Admin, table, id, Pic2)).Contains(Pic2).Should().BeTrue();
    }

    [Fact]
    public async Task Update_image_null_deletes_thumbnail_and_full()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var original = Image(200, 300);
        await Admin.UpdateAsync(new Entity(table, id) { [Pic] = original });
        var imageId = (await RetrieveImageAsync(Admin, table, id)).GetAttributeValue<Guid>(Pic + "id");
        (await BlobAsync(imageId)).Should().Equal(original);

        await Admin.UpdateAsync(new Entity(table, id) { [Pic] = null });

        var record = await RetrieveImageAsync(Admin, table, id);
        record.Attributes.Keys.Should().NotContain([Pic, Pic + "id", Pic + "_timestamp", Pic + "_url"]);
        (await AttachmentRowsAsync(id)).Should().Be(0);
        (await BlobAsync(imageId)).Should().BeNull("the full image is deleted after the commit");
    }

    [Fact]
    public async Task Update_with_a_new_image_replaces_the_full_image()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = Image(50, 50) });
        var first = (await RetrieveImageAsync(Admin, table, id)).GetAttributeValue<Guid>(Pic + "id");

        var second = Image(60, 40, SKEncodedImageFormat.Jpeg);
        await Admin.UpdateAsync(new Entity(table, id) { [Pic] = second });

        var record = await RetrieveImageAsync(Admin, table, id);
        var imageId = record.GetAttributeValue<Guid>(Pic + "id");
        imageId.Should().NotBe(first);
        Describe(record.GetAttributeValue<byte[]>(Pic)).Should().Be((40, 40, SKEncodedImageFormat.Jpeg));
        (await BlobAsync(first)).Should().BeNull();
        (await BlobAsync(imageId)).Should().Equal(second);
        (await AttachmentRowsAsync(id)).Should().Be(1);
    }

    [Fact]
    public async Task Image_value_that_is_not_an_image_is_ProcessImageFailure_and_writes_nothing()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var blobs = await BlobKeysAsync();

        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [Pic] = "not an image"u8.ToArray() }),
            ErrorCodes.ProcessImageFailure);

        fault.Message.Should().StartWith($"The image of column {Pic} of {table} record {id} could not be processed.");
        (await BlobKeysAsync()).Should().BeEquivalentTo(blobs);
        await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [Pic] = "text" }), ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Failed_create_deletes_the_full_image_it_wrote()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var blobs = await BlobKeysAsync();

        // The same id again: the INSERT fails after the full image was written, and the rollback deletes it.
        await FaultAsync(() => Admin.CreateAsync(new Entity(table, id) { ["new_name"] = "b", [Pic] = Image(30, 30) }), ErrorCodes.DuplicateRecord);

        (await BlobKeysAsync()).Should().BeEquivalentTo(blobs);
    }

    // ---- size and full images --------------------------------------------------------------------------------------

    [Fact]
    public async Task Image_over_MaxSizeInKB_is_0x80072553()
    {
        var table = await NewTableAsync((Pic, true, 1));
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var big = Noise(40);
        big.Length.Should().BeGreaterThan(1024);

        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [Pic] = big }), ErrorCodes.ProcessImageFailure);
        fault.Message.Should().StartWith("The image is too large for the column: 0 MB uploaded, the maximum is 0 MB.");
        fault.HttpStatus.Should().Be(400);
        await FaultAsync(() => Admin.CreateAsync(new Entity(table) { ["new_name"] = "b", [Pic] = big }), ErrorCodes.ProcessImageFailure);

        // The upload messages: the block that crosses MaxSizeInKB.
        await FaultAsync(() => UploadAsync(Admin, table, id, Pic, big, "big.png", "image/png"), ErrorCodes.ProcessImageFailure);
        (await RetrieveImageAsync(Admin, table, id)).Contains(Pic).Should().BeFalse();
        (await AttachmentRowsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Full_image_stored_only_when_CanStoreFullImage()
    {
        var table = await NewTableAsync((Pic, true, null), (Pic2, false, null));
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        byte[] full = Image(400, 300), notKept = Image(400, 300, SKEncodedImageFormat.Jpeg);

        await Admin.UpdateAsync(new Entity(table, id) { [Pic] = full, [Pic2] = notKept });

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Pic, Pic + "id", Pic2, Pic2 + "id"));
        var imageId = record.GetAttributeValue<Guid>(Pic + "id");
        (await AttachmentRowsAsync(id)).Should().Be(1);
        (await ScalarAsync("SELECT regardingfieldname FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", imageId))).Should().Be(Pic);
        (await ScalarAsync("SELECT mimetype FROM data.fileattachment WHERE fileattachmentid = @id", ("@id", imageId))).Should().Be("image/png");
        (await BlobAsync(imageId)).Should().Equal(full);
        (await BlobAsync(record.GetAttributeValue<Guid>(Pic2 + "id"))).Should().BeNull();
        var (downloaded, fileName) = await DownloadAsync(Admin, table, id);
        downloaded.Should().Equal(full);
        fileName.Should().NotBeNullOrEmpty();
        Describe(record.GetAttributeValue<byte[]>(Pic2)).Should().Be((144, 144, SKEncodedImageFormat.Jpeg));
    }

    [Fact]
    public async Task InitializeFileBlocksDownload_on_image_without_full_is_0x80040217()
    {
        var table = await NewTableAsync(full: false);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = Image(20, 20) });
        var imageId = (await RetrieveImageAsync(Admin, table, id)).GetAttributeValue<Guid>(Pic + "id");

        var fault = await FaultAsync(() => DownloadAsync(Admin, table, id), ErrorCodes.ObjectDoesNotExist);

        fault.Message.Should().Be($"No FileAttachment records found for imagedescriptorId: {imageId} for image attribute: {Pic} of {table} record with id {id}.");
        fault.HttpStatus.Should().Be(404);
        // An empty image column has no image at all.
        var empty = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "b" });
        await FaultAsync(() => DownloadAsync(Admin, table, empty), ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Upload_messages_on_an_image_column_set_thumbnail_full_image_and_companions()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var photo = Image(500, 200, SKEncodedImageFormat.Jpeg);

        var commit = await UploadAsync(Admin, table, id, Pic, photo, "photo.jpg", "image/jpeg");

        commit.FileSizeInBytes.Should().Be(photo.Length);
        var record = await RetrieveImageAsync(Admin, table, id);
        record.GetAttributeValue<Guid>(Pic + "id").Should().Be(commit.FileId);
        Describe(record.GetAttributeValue<byte[]>(Pic)).Should().Be((144, 144, SKEncodedImageFormat.Jpeg));
        var (downloaded, fileName) = await DownloadAsync(Admin, table, id);
        downloaded.Should().Equal(photo);
        fileName.Should().Be("photo.jpg");

        // A file that is not an image is refused at the commit, and the column keeps its image.
        await FaultAsync(() => UploadAsync(Admin, table, id, Pic, "%PDF-1.7"u8.ToArray(), "a.pdf", "application/pdf"), ErrorCodes.ProcessImageFailure);
        (await RetrieveImageAsync(Admin, table, id)).GetAttributeValue<Guid>(Pic + "id").Should().Be(commit.FileId);

        // DeleteFile on the image id clears the column like an Update to null.
        await Admin.ExecuteAsync(new DeleteFileRequest { FileId = commit.FileId });
        (await RetrieveImageAsync(Admin, table, id)).Contains(Pic).Should().BeFalse();
        (await BlobAsync(commit.FileId)).Should().BeNull();
    }

    [Fact]
    public async Task Download_messages_on_an_image_column_read_the_full_image()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var photo = Image(30, 60);
        await UploadAsync(Admin, table, id, Pic, photo, "p.png", "image/png");

        var init = (InitializeFileBlocksDownloadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Pic,
        });
        (init.FileSizeInBytes, init.FileName).Should().Be((photo.Length, "p.png"));
        var block = (DownloadBlockResponse)await Admin.ExecuteAsync(new DownloadBlockRequest
        {
            FileContinuationToken = init.FileContinuationToken, Offset = 0, BlockLength = photo.Length,
        });
        block.Data.Should().Equal(photo);
    }

    [Fact]
    public async Task Plugin_failure_on_an_image_update_keeps_the_old_image()
    {
        var table = await NewTableAsync(full: true);
        var original = Image(80, 80);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = original });
        var before = await RetrieveImageAsync(Admin, table, id);
        var oldId = before.GetAttributeValue<Guid>(Pic + "id");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, stage: 40, config: "invalid:no new image");
        var blobs = await BlobKeysAsync();

        (await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [Pic] = Image(90, 60) }), ErrorCodes.ISVAborted))
            .Message.Should().Be("no new image");
        (await FaultAsync(() => UploadAsync(Admin, table, id, Pic, Image(70, 50, SKEncodedImageFormat.Jpeg), "n.jpg", "image/jpeg"),
            ErrorCodes.ISVAborted)).Message.Should().Be("no new image");

        var after = await RetrieveImageAsync(Admin, table, id);
        after.GetAttributeValue<Guid>(Pic + "id").Should().Be(oldId);
        after.GetAttributeValue<long>(Pic + "_timestamp").Should().Be(before.GetAttributeValue<long>(Pic + "_timestamp"));
        after.GetAttributeValue<byte[]>(Pic).Should().Equal(before.GetAttributeValue<byte[]>(Pic));
        (await BlobAsync(oldId)).Should().Equal(original);
        (await AttachmentRowsAsync(id)).Should().Be(1);
        (await BlobKeysAsync()).Where(k => !k.Contains("/_upload/")).Should().BeEquivalentTo(blobs.Where(k => !k.Contains("/_upload/")),
            "the full images of the rolled-back updates are deleted");
    }

    // ---- reading ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retrieve_returns_thumbnail_bytes()
    {
        var table = await NewTableAsync();
        var original = Image(300, 428);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = original });

        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Pic));

        record.GetAttributeValue<byte[]>(Pic).Should().Equal(Thumbnailer.Create(new MemoryStream(original)).Bytes);
        // Learn: with AllColumns the query does not include image columns (their companions stay).
        var all = await Admin.RetrieveAsync(table, id, new ColumnSet(true));
        all.Contains(Pic).Should().BeFalse();
        all.Contains(Pic + "id").Should().BeTrue();
        all.Contains(Pic + "_url").Should().BeTrue();
    }

    [Fact]
    public async Task RetrieveMultiple_returns_thumbnails_without_store_reads()
    {
        var table = await NewTableAsync(full: true);
        for (var i = 0; i < 3; i++) await Admin.CreateAsync(new Entity(table) { ["new_name"] = "r" + i, [Pic] = Image(200 + i, 200) });
        var spy = new SpyFileStore(Blobs);
        var files = new FileColumnService(spy, Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        var service = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, files: files);

        var result = await service.RetrieveMultipleAsync(new QueryExpression(table) { ColumnSet = new ColumnSet(Pic, Pic + "_url") });

        result.Entities.Should().HaveCount(3);
        result.Entities.Should().OnlyContain(e => Describe(e.GetAttributeValue<byte[]>(Pic)).Width == 144);
        spy.Reads.Should().Be(0, "thumbnails are read from the row (ruling F2), never from the file store");
    }

    [Fact]
    public async Task Url_column_format_matches_dataverse()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = Image(10, 10) });
        var empty = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "b" });

        var record = await RetrieveImageAsync(Admin, table, id);

        // Learn, image column data, "Download URL".
        var imageId = record.GetAttributeValue<Guid>(Pic + "id");
        var timestamp = record.GetAttributeValue<long>(Pic + "_timestamp");
        record.GetAttributeValue<string>(Pic + "_url").Should().Be(
            $"/Image/download.aspx?Entity={table}&Attribute={Pic}&Id={imageId:D}&Timestamp={timestamp}");
        (await RetrieveImageAsync(Admin, table, empty)).Contains(Pic + "_url").Should().BeFalse("the url is null when the column is empty");

        // A condition on the url is refused: it is computed, not stored.
        await FaultAsync(() => Admin.RetrieveMultipleAsync(new QueryExpression(table)
        {
            Criteria = { Conditions = { new ConditionExpression(Pic + "_url", ConditionOperator.NotNull) } },
        }), ErrorCodes.InvalidArgument);

        // The Web API declares it (read-only) and $select reads it, with the thumbnail as base64.
        var set = Store.GetEntity(table)!.EntitySetName;
        var metadata = await (await GetAsync("/api/data/v9.2/$metadata", fx.AdminObjectId)).Content.ReadAsStringAsync();
        metadata.Should().Contain($"<Property Name=\"{Pic}_url\" Type=\"Edm.String\"");
        var json = JsonNode.Parse(await (await GetAsync($"/api/data/v9.2/{set}({id})?$select={Pic},{Pic}_url", fx.AdminObjectId))
            .Content.ReadAsStringAsync())!;
        json[Pic + "_url"]!.GetValue<string>().Should().Be(record.GetAttributeValue<string>(Pic + "_url"));
        Convert.FromBase64String(json[Pic]!.GetValue<string>()).Should().Equal(record.GetAttributeValue<byte[]>(Pic));
    }

    [Fact]
    public async Task Web_api_PATCH_sets_an_image_from_base64()
    {
        var table = await NewTableAsync();
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var set = Store.GetEntity(table)!.EntitySetName;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/data/v9.2/{set}({id})")
        {
            Content = new StringContent(new JsonObject { [Pic] = Convert.ToBase64String(Image(300, 300)) }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        (await fx.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        Describe((await Admin.RetrieveAsync(table, id, new ColumnSet(Pic))).GetAttributeValue<byte[]>(Pic)).Width.Should().Be(144);
    }

    // ---- /Image/download.aspx -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Image_download_aspx_serves_thumbnail_and_full_and_requires_read()
    {
        var table = await NewTableAsync((Pic, true, null), (Pic2, false, null));
        var original = Image(300, 200);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", [Pic] = original });
        await Admin.UpdateAsync(new Entity(table, id) { [Pic2] = Image(50, 50, SKEncodedImageFormat.Jpeg) });
        var record = await Admin.RetrieveAsync(table, id, new ColumnSet(Pic, Pic + "_url", Pic2, Pic2 + "_url"));
        var url = record.GetAttributeValue<string>(Pic + "_url");

        var thumbnail = await GetAsync(url, fx.AdminObjectId);
        thumbnail.StatusCode.Should().Be(HttpStatusCode.OK);
        thumbnail.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        // The url changes with the image (Timestamp), so a response may be cached by the caller's browser.
        (thumbnail.Headers.CacheControl!.Private, thumbnail.Headers.CacheControl.MaxAge).Should().Be((true, TimeSpan.FromDays(1)));
        (await thumbnail.Content.ReadAsByteArrayAsync()).Should().Equal(record.GetAttributeValue<byte[]>(Pic));

        var full = await GetAsync(url + "&Full=true", fx.AdminObjectId);
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        full.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (full.Headers.CacheControl!.Private, full.Headers.CacheControl.MaxAge).Should().Be((true, TimeSpan.FromDays(1)));
        (await full.Content.ReadAsByteArrayAsync()).Should().Equal(original);

        var jpeg = await GetAsync(record.GetAttributeValue<string>(Pic2 + "_url"), fx.AdminObjectId);
        jpeg.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await jpeg.Content.ReadAsByteArrayAsync()).Should().Equal(record.GetAttributeValue<byte[]>(Pic2));
        // Learn: a column that keeps no full-sized image "returns no data".
        (await GetAsync(record.GetAttributeValue<string>(Pic2 + "_url") + "&Full=true", fx.AdminObjectId)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        // Authentication and Read on the record.
        (await GetAsync(url, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var (writer, _) = await UserAsync(table, "Write");
        (await GetAsync(url, writer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(url + "&Full=true", writer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (reader, _) = await UserAsync(table, "Read");
        (await GetAsync(url, reader)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(url + "&Full=true", reader)).StatusCode.Should().Be(HttpStatusCode.OK);

        // An unknown image id, or an image that was replaced: 404. Missing parameters or an unknown column: 400.
        (await GetAsync($"/Image/download.aspx?Entity={table}&Attribute={Pic}&Id={Guid.NewGuid()}&Timestamp=1", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/Image/download.aspx?Entity={table}&Attribute=new_name&Id={Guid.NewGuid()}", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync($"/Image/download.aspx?Entity={table}&Attribute={Pic}", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync($"/Image/download.aspx?Entity=nope&Attribute={Pic}&Id={Guid.NewGuid()}", fx.AdminObjectId))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Image_download_aspx_serves_the_type_the_content_shows_with_nosniff()
    {
        var table = await NewTableAsync(full: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a" });
        var photo = Image(30, 60);
        // The client names a type the content is not.
        await UploadAsync(Admin, table, id, Pic, photo, "p.html", "text/html");
        var url = (await Admin.RetrieveAsync(table, id, new ColumnSet(Pic + "_url"))).GetAttributeValue<string>(Pic + "_url");

        foreach (var path in new[] { url, url + "&Full=true" })
        {
            var response = await GetAsync(path, fx.AdminObjectId);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png", path);
            response.Headers.GetValues("X-Content-Type-Options").Should().Equal(["nosniff"], path);
        }
    }

    [Theory]
    [InlineData("/Image/download.aspx")]
    [InlineData("/image/Download.aspx")]
    [InlineData("/Image/other")]
    public async Task Image_path_not_swallowed_by_spa(string path)
    {
        var anonymous = await GetAsync(path, null);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);

        var authenticated = await GetAsync(path, fx.AdminObjectId);
        ((int)authenticated.StatusCode).Should().BeOneOf([400, 404], path);
        authenticated.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html", path);
    }
}
