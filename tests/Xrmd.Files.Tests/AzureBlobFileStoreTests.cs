using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Azurite;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

/// <summary>One Azurite container shared by the Azure Blob tests; each test gets its own container.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    // Pinned by digest; it is mcr.microsoft.com/azure-storage/azurite:latest as pulled on 2026-11-12 (Azurite 3.37.0).
    private const string Image = "mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5";

    // The SDK speaks a newer service version than any Azurite build knows; skip its version check.
    private readonly AzuriteContainer _container = new AzuriteBuilder(Image)
        .WithCommand("--skipApiVersionCheck")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public sealed class AzureBlobFileStoreTests : FileStoreContract, IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _azurite;
    private BlobContainerClient _admin = null!;
    private AzureBlobFileStore _store = null!;

    public AzureBlobFileStoreTests(AzuriteFixture azurite) => _azurite = azurite;

    protected override async Task<IFileStore> CreateStore()
    {
        // The store never creates its container (ruling B1), so the test does.
        _admin = new BlobContainerClient(_azurite.ConnectionString, "t" + Guid.NewGuid().ToString("N"));
        await _admin.CreateAsync();
        _store = new AzureBlobFileStore(_admin);
        return _store;
    }

    protected override async Task CleanupAsync()
    {
        if (_admin != null) await _admin.DeleteIfExistsAsync();
    }

    [Fact]
    public async Task Check_is_ok_for_an_existing_container()
    {
        var check = await Store.CheckAsync(default);
        check.IsOk.Should().BeTrue(check.Message);
        check.Message.Should().Contain(_admin.Name);
    }

    [Fact]
    public async Task Check_fails_for_a_missing_container_and_does_not_create_it()
    {
        var name = "m" + Guid.NewGuid().ToString("N");
        var store = new AzureBlobFileStore(new BlobContainerClient(_azurite.ConnectionString, name));
        var check = await store.CheckAsync(default);
        check.IsOk.Should().BeFalse();
        check.Message.Should().Contain(name).And.Contain("does not exist").And.Contain("does not create it");
        (await new BlobContainerClient(_azurite.ConnectionString, name).ExistsAsync()).Value.Should().BeFalse();
    }

    [Fact]
    public async Task Check_failure_never_contains_the_account_key()
    {
        var key = Convert.ToBase64String("a-wrong-key-that-must-not-leak"u8.ToArray());
        var wrong = Regex.Replace(_azurite.ConnectionString, "AccountKey=[^;]+", "AccountKey=" + key);
        var store = new AzureBlobFileStore(new AzureBlobFileStoreOptions { ConnectionString = wrong, Container = _admin.Name });
        var check = await store.CheckAsync(default);
        check.IsOk.Should().BeFalse();
        check.Message.Should().NotContain(key).And.NotContain("AccountKey");
        check.Message.Should().Contain("access denied").And.Contain("403").And.Contain(_admin.Name);
    }

    private static byte[] Pattern(int index, int size)
    {
        var data = new byte[size];
        new Random(index).NextBytes(data);
        return data;
    }

    private async Task<List<string>> SeedAsync(string prefix, params int[] sizes)
    {
        var keys = new List<string>();
        for (var i = 0; i < sizes.Length; i++)
        {
            keys.Add($"{prefix}{i}");
            await Store.WriteAsync(keys[i], new MemoryStream(Pattern(i + 100, sizes[i])), default);
        }
        return keys;
    }

    private async Task<byte[]> ReadAllBytesAsync(string key)
    {
        await using var stream = await Store.OpenReadAsync(key, 0, null, default);
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        return all.ToArray();
    }

    private async Task<int> UncommittedBlockCountAsync(string key)
    {
        var list = await _admin.GetBlockBlobClient(key).GetBlockListAsync(Azure.Storage.Blobs.Models.BlockListTypes.Uncommitted);
        return list.Value.UncommittedBlocks.Count();
    }

    [Fact]
    public async Task Concat_of_many_4MB_parts_preserves_order()
    {
        const int blockSize = 4 * 1024 * 1024;
        const int blocks = 12;
        var parts = Enumerable.Range(0, blocks).Select(i => Pattern(i, blockSize)).ToArray();
        var keys = new List<string>();
        for (var i = 0; i < blocks; i++)
        {
            keys.Add($"org/_upload/s/{i}");
            await Store.WriteAsync(keys[i], new MemoryStream(parts[i]), default);
        }

        await Store.ConcatAsync(keys, "org/final", default);

        (await Store.GetLengthAsync("org/final", default)).Should().Be((long)blockSize * blocks);
        (await ReadAllBytesAsync("org/final")).Should().Equal(parts.SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task Concat_streamed_path_keeps_bytes_and_order()
    {
        var keys = await SeedAsync("org/s", 4 << 20, 100, 5 << 20, 1);
        _store.Strategy = AzureBlobFileStore.ConcatStrategy.Streamed;
        await Store.ConcatAsync(keys, "org/out", default);
        _store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.Streamed);
        var expected = new[] { 4 << 20, 100, 5 << 20, 1 }.Select((n, i) => Pattern(i + 100, n)).SelectMany(p => p).ToArray();
        (await ReadAllBytesAsync("org/out")).Should().Equal(expected);
    }

    [Fact]
    public async Task Concat_server_side_path_keeps_bytes_and_order()
    {
        var keys = await SeedAsync("org/ss", 3 << 20, 100, 0, 5 << 20);
        _store.Strategy = AzureBlobFileStore.ConcatStrategy.ServerSide;
        await Store.ConcatAsync(keys, "org/out", default);
        _store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.ServerSide);
        var expected = new[] { 3 << 20, 100, 0, 5 << 20 }.Select((n, i) => Pattern(i + 100, n)).SelectMany(p => p).ToArray();
        (await ReadAllBytesAsync("org/out")).Should().Equal(expected);
    }

    [Fact]
    public async Task Concat_auto_reports_the_path_it_took()
    {
        var keys = await SeedAsync("org/a", 10, 20);
        await Store.ConcatAsync(keys, "org/out", default);
        _store.LastConcatPath.Should().BeOneOf(AzureBlobFileStore.ConcatPath.ServerSide, AzureBlobFileStore.ConcatPath.Streamed);
        (await Store.GetLengthAsync("org/out", default)).Should().Be(30);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concat_of_empty_sources_writes_an_empty_target(bool serverSide)
    {
        var keys = await SeedAsync("org/e", 0, 0);
        _store.Strategy = serverSide ? AzureBlobFileStore.ConcatStrategy.ServerSide : AzureBlobFileStore.ConcatStrategy.Streamed;
        await Store.ConcatAsync(keys, "org/out", default);
        (await Store.GetLengthAsync("org/out", default)).Should().Be(0);
        (await UncommittedBlockCountAsync("org/out")).Should().Be(0, "no zero-length block may be staged");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concat_with_a_trailing_empty_source_has_the_right_length(bool serverSide)
    {
        var keys = await SeedAsync("org/t", 1000, 0);
        _store.Strategy = serverSide ? AzureBlobFileStore.ConcatStrategy.ServerSide : AzureBlobFileStore.ConcatStrategy.Streamed;
        await Store.ConcatAsync(keys, "org/out", default);
        (await ReadAllBytesAsync("org/out")).Should().Equal(Pattern(100, 1000));
        (await UncommittedBlockCountAsync("org/out")).Should().Be(0);
    }

    [Fact]
    public async Task Concat_without_a_signing_key_falls_back_to_streaming()
    {
        var keys = await SeedAsync("org/n", 1000, 2000);
        // A container SAS URL cannot sign further SAS tokens, so the store cannot authorize a server-side copy.
        var sasUri = _admin.GenerateSasUri(Azure.Storage.Sas.BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1));
        var store = new AzureBlobFileStore(new BlobContainerClient(sasUri));
        await store.ConcatAsync(keys, "org/out", default);
        store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.Streamed);
        (await ReadAllBytesAsync("org/out")).Should().Equal(Pattern(100, 1000).Concat(Pattern(101, 2000)).ToArray());
    }

    [Fact]
    public async Task Concat_falls_back_to_streaming_when_the_service_rejects_put_block_from_url()
    {
        var keys = await SeedAsync("org/u", 1000, 2000);
        var options = new BlobClientOptions();
        var rejected = 0;
        options.AddPolicy(new RejectCopySourcePolicy(() => rejected++), Azure.Core.HttpPipelinePosition.PerCall);
        var store = new AzureBlobFileStore(new BlobContainerClient(_azurite.ConnectionString, _admin.Name, options));
        await store.ConcatAsync(keys, "org/out", default);
        rejected.Should().BeGreaterThan(0, "the server-side attempt must have been made and refused");
        store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.Streamed);
        (await ReadAllBytesAsync("org/out")).Should().Equal(Pattern(100, 1000).Concat(Pattern(101, 2000)).ToArray());
        (await UncommittedBlockCountAsync("org/out")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concat_falls_back_to_streaming_when_the_delegation_key_cannot_be_obtained(bool authFailure)
    {
        var keys = await SeedAsync("org/k", 1000, 2000);
        var sasUri = _admin.GenerateSasUri(Azure.Storage.Sas.BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1));
        var calls = 0;
        var store = new AzureBlobFileStore(new BlobContainerClient(sasUri), (_, _, _) =>
        {
            calls++;
            throw authFailure
                ? new Azure.Identity.AuthenticationFailedException("injected")
                : new RequestFailedException(403, "injected", "AuthorizationPermissionMismatch", null);
        });
        await store.ConcatAsync(keys, "org/out", default);
        calls.Should().Be(1);
        store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.Streamed);
        (await ReadAllBytesAsync("org/out")).Should().Equal(Pattern(100, 1000).Concat(Pattern(101, 2000)).ToArray());
    }

    [Fact]
    public async Task Concurrent_multi_block_writes_to_one_key_never_mix()
    {
        for (var round = 0; round < 5; round++)
        {
            var a = Pattern(1000 + round, 9 * 1024 * 1024);
            var b = Pattern(2000 + round, 9 * 1024 * 1024);
            // The service drops uncommitted blocks that a commit does not list, so the slower writer may fail with
            // InvalidBlockList. What must never happen is a blob that mixes the two writers' blocks.
            var results = await Task.WhenAll(
                Attempt(Store.WriteAsync("org/race", new NonSeekableStream(a), default)),
                Attempt(Store.WriteAsync("org/race", new NonSeekableStream(b), default)));
            results.Should().Contain(ok => ok, "at least one writer must win");
            var final = await ReadAllBytesAsync("org/race");
            (final.AsSpan().SequenceEqual(a) || final.AsSpan().SequenceEqual(b)).Should().BeTrue($"round {round}: the blob must be exactly one writer's content");
        }
    }

    private static async Task<bool> Attempt(Task write)
    {
        try
        {
            await write;
            return true;
        }
        catch (RequestFailedException e) when (e.ErrorCode == "InvalidBlockList")
        {
            return false;
        }
    }

    [Fact]
    public async Task Server_side_concat_does_not_leak_the_sas_signature_into_logs()
    {
        var keys = await SeedAsync("org/l", 1000, 2000);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new Azure.Core.Diagnostics.AzureEventSourceListener((_, message) => lines.Enqueue(message), System.Diagnostics.Tracing.EventLevel.Verbose);
        var options = new BlobClientOptions();
        var store = new AzureBlobFileStore(new BlobContainerClient(_azurite.ConnectionString, _admin.Name, options));
        await store.ConcatAsync(keys, "org/out", default);
        store.LastConcatPath.Should().Be(AzureBlobFileStore.ConcatPath.ServerSide);
        lines.Should().NotBeEmpty("the listener must see the pipeline's events, otherwise it proves nothing");
        var text = string.Join("\n", lines);
        text.Should().Contain("x-ms-copy-source", "the copy-source header is logged, which makes the redaction check meaningful");
        System.Text.RegularExpressions.Regex.Matches(text, "sig=([^&\\s]*)").Select(m => m.Groups[1].Value)
            .Should().NotContain(v => v != "REDACTED", "a SAS signature must never reach the logs");
    }

    /// <summary>Answers every request that carries a copy source (Put Block From URL) with 501, like a server that lacks the operation.</summary>
    private sealed class RejectCopySourcePolicy(Action onReject) : Azure.Core.Pipeline.HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(Azure.Core.HttpMessage message)
        {
            if (!message.Request.Headers.Contains("x-ms-copy-source")) return;
            onReject();
            throw new RequestFailedException(501, "injected: operation not implemented", "NotImplemented", null);
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 1_000)]
    [InlineData(99, 1)]
    public async Task Read_range_past_the_end_is_clamped(long offset, long length)
    {
        var data = Pattern(1, 100);
        await Store.WriteAsync("org/r", new MemoryStream(data), default);
        await using var stream = await Store.OpenReadAsync("org/r", offset, length, default);
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        all.ToArray().Should().Equal(data.Skip((int)offset).Take((int)Math.Min(length, 100 - offset)).ToArray());
    }

    [Fact]
    public async Task Read_of_an_empty_blob_returns_empty_for_every_range()
    {
        await Store.WriteAsync("org/empty", new MemoryStream(), default);
        foreach (var (offset, length) in new (long, long?)[] { (0, null), (0, 5), (3, null), (3, 5) })
        {
            await using var stream = await Store.OpenReadAsync("org/empty", offset, length, default);
            (await stream.ReadAsync(new byte[1])).Should().Be(0, $"offset {offset} length {length}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concat_source_deleted_mid_way_leaves_no_target(bool serverSide)
    {
        var keys = await SeedAsync("org/d", 1000, 1000, 1000);
        _store.Strategy = serverSide ? AzureBlobFileStore.ConcatStrategy.ServerSide : AzureBlobFileStore.ConcatStrategy.Streamed;
        // After the length check and the first source, the second one disappears.
        _store.BeforeSource = async (index, ct) =>
        {
            if (index == 1) await _admin.GetBlobClient(keys[1]).DeleteIfExistsAsync(cancellationToken: ct);
        };
        var act = async () => await Store.ConcatAsync(keys, "org/out", default);
        await act.Should().ThrowAsync<FileNotFoundException>();
        (await Store.GetLengthAsync("org/out", default)).Should().BeNull();
    }

    [Fact]
    public async Task Write_of_an_unseekable_stream_larger_than_one_block_round_trips()
    {
        var data = Pattern(9, 11 * 1024 * 1024 + 17);
        await Store.WriteAsync("org/big", new NonSeekableStream(data), default);
        (await ReadAllBytesAsync("org/big")).Should().Equal(data);
        (await UncommittedBlockCountAsync("org/big")).Should().Be(0);
    }

    [Fact]
    public async Task Failed_block_write_leaves_no_blob()
    {
        var act = async () => await Store.WriteAsync("org/broken", new FailingAfterStream(10 * 1024 * 1024), default);
        await act.Should().ThrowAsync<IOException>();
        (await Store.GetLengthAsync("org/broken", default)).Should().BeNull();
    }

    [Fact]
    public async Task Failed_block_write_keeps_the_previous_content()
    {
        await Store.WriteAsync("org/keep", new MemoryStream([1, 2, 3]), default);
        var act = async () => await Store.WriteAsync("org/keep", new FailingAfterStream(10 * 1024 * 1024), default);
        await act.Should().ThrowAsync<IOException>();
        (await ReadAllBytesAsync("org/keep")).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task List_pages_through_more_than_one_page_of_keys()
    {
        // Azurite pages at 5000 blobs by default; ask the store for more than a small page by writing 1105 and listing all.
        var tasks = Enumerable.Range(0, 1105).Select(i => Store.WriteAsync($"many/k{i}", new MemoryStream([1]), default));
        foreach (var batch in tasks.Chunk(50)) await Task.WhenAll(batch);
        var keys = new List<string>();
        await foreach (var k in Store.ListAsync("many/", default)) keys.Add(k);
        keys.Should().HaveCount(1105).And.OnlyHaveUniqueItems();
        await Store.DeletePrefixAsync("many/", default);
        var left = new List<string>();
        await foreach (var k in Store.ListAsync("many/", default)) left.Add(k);
        left.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_container_surfaces_as_an_error_not_as_a_missing_file()
    {
        var missing = new AzureBlobFileStore(new BlobContainerClient(_azurite.ConnectionString, "nope" + Guid.NewGuid().ToString("N")));
        var act = async () => await missing.OpenReadAsync("org/x", 0, null, default);
        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<FileNotFoundException>();
    }

    [Fact]
    public void Both_auth_settings_are_refused_without_echoing_secrets()
    {
        const string secret = "AccountKey=SuperSecretKeyValue123";
        var act = () => new AzureBlobFileStore(new AzureBlobFileStoreOptions
        {
            ConnectionString = "DefaultEndpointsProtocol=https;AccountName=a;" + secret,
            ServiceUri = "https://a.blob.core.windows.net",
            Container = "c",
        });
        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("SuperSecretKeyValue123");
    }

    [Fact]
    public void Plain_http_service_uri_is_refused_by_the_constructor()
    {
        var act = () => new AzureBlobFileStore(new AzureBlobFileStoreOptions { ServiceUri = "http://a.blob.core.windows.net", Container = "c" });
        act.Should().Throw<ArgumentException>().WithMessage("Files:Azure:ServiceUri is not a valid absolute https URL.*");
    }

    [Fact]
    public async Task Deleting_one_key_from_a_missing_container_does_not_fail_but_a_prefix_delete_does()
    {
        var missing = new AzureBlobFileStore(new BlobContainerClient(_azurite.ConnectionString, "nope" + Guid.NewGuid().ToString("N")));
        await missing.DeleteAsync("org/x", default);
        var act = async () => await missing.DeletePrefixAsync("org/", default);
        (await act.Should().ThrowAsync<RequestFailedException>()).Which.ErrorCode.Should().Be("ContainerNotFound");
    }

    [Fact]
    public void Garbage_connection_string_is_refused_without_echoing_it()
    {
        const string bad = "this-is-not-a-connection-string-SuperSecretKeyValue123";
        var act = () => new AzureBlobFileStore(new AzureBlobFileStoreOptions { ConnectionString = bad, Container = "c" });
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ToString().Should().NotContain("SuperSecretKeyValue123");
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingAfterStream(long failAfter) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter) throw new IOException("injected read failure");
            var n = (int)Math.Min(count, failAfter - _position);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class AzureBlobRegistrationTests
{
    // Not a real key: the well-known public Azurite development account would do, but a marker makes leaks easy to spot.
    private const string Secret = "AccountKey=c2VjcmV0LW1hcmtlcg==";
    private const string ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" + Secret + ";BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    private static Dictionary<string, string?> WithConnectionString() => new()
    {
        ["Files:Store"] = "AzureBlob",
        ["Files:Azure:ConnectionString"] = ConnectionString,
        ["Files:Azure:Container"] = "xrmd",
    };

    private static IServiceCollection Add(Dictionary<string, string?> values) =>
        new ServiceCollection().AddXrmdFiles(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Theory]
    [InlineData("AzureBlob")]
    [InlineData("azureblob")]
    public void Connection_string_registers_the_azure_store(string store)
    {
        var values = WithConnectionString();
        values["Files:Store"] = store;
        using var provider = Add(values).BuildServiceProvider();
        provider.GetRequiredService<IFileStore>().Should().BeOfType<AzureBlobFileStore>();
        provider.GetRequiredService<FileStoreOptions>().Store.Should().Be(FileStoreKind.AzureBlob);
    }

    [Fact]
    public void Service_uri_registers_the_azure_store()
    {
        var values = new Dictionary<string, string?>
        {
            ["Files:Store"] = "AzureBlob",
            ["Files:Azure:ServiceUri"] = "https://account.blob.core.windows.net",
            ["Files:Azure:Container"] = "xrmd",
        };
        using var provider = Add(values).BuildServiceProvider();
        provider.GetRequiredService<IFileStore>().Should().BeOfType<AzureBlobFileStore>();
    }

    [Fact]
    public void Numeric_store_value_is_refused()
    {
        var values = WithConnectionString();
        values["Files:Store"] = "2";
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Files:Store*AzureBlob*");
    }

    [Fact]
    public void Missing_container_fails_startup()
    {
        var values = WithConnectionString();
        values.Remove("Files:Azure:Container");
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Files:Store is 'AzureBlob' but the required setting Files:Azure:Container is missing.");
    }

    [Fact]
    public void Neither_auth_setting_is_refused()
    {
        var values = WithConnectionString();
        values.Remove("Files:Azure:ConnectionString");
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Files:Store is 'AzureBlob': set exactly one of Files:Azure:ConnectionString and Files:Azure:ServiceUri.");
    }

    [Fact]
    public void Both_auth_settings_are_refused_without_echoing_secrets()
    {
        var values = WithConnectionString();
        values["Files:Azure:ServiceUri"] = "https://account.blob.core.windows.net";
        var act = () => Add(values);
        var e = act.Should().Throw<InvalidOperationException>()
            .WithMessage("Files:Store is 'AzureBlob': set exactly one of Files:Azure:ConnectionString and Files:Azure:ServiceUri.").Which;
        e.ToString().Should().NotContain(Secret).And.NotContain("c2VjcmV0LW1hcmtlcg");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://account.blob.core.windows.net")]
    public void Invalid_service_uri_is_refused(string uri)
    {
        var values = new Dictionary<string, string?>
        {
            ["Files:Store"] = "AzureBlob",
            ["Files:Azure:ServiceUri"] = uri,
            ["Files:Azure:Container"] = "xrmd",
        };
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage("Files:Azure:ServiceUri is not a valid absolute https URL.");
    }

    [Theory]
    [InlineData("http://account.blob.core.windows.net")]
    [InlineData("http://10.0.0.5:10000/devstoreaccount1")]
    [InlineData("http://storage.example.com")]
    [InlineData("http://localhost:10000/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1")]
    [InlineData("http://[::1]:10000/devstoreaccount1")]
    [InlineData("http://azurite:10000/devstoreaccount1")]
    public void Http_service_uri_is_refused(string uri)
    {
        var values = new Dictionary<string, string?>
        {
            ["Files:Store"] = "AzureBlob",
            ["Files:Azure:ServiceUri"] = uri,
            ["Files:Azure:Container"] = "xrmd",
        };
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage("Files:Azure:ServiceUri is not a valid absolute https URL.");
    }

    [Fact]
    public void Invalid_connection_string_is_rejected_at_registration_without_echoing_it()
    {
        var values = WithConnectionString();
        values["Files:Azure:ConnectionString"] = "garbage;" + Secret;
        var act = () => Add(values);
        var e = act.Should().Throw<InvalidOperationException>()
            .WithMessage("Files:Azure:ConnectionString is not a valid Azure Storage connection string.").Which;
        e.InnerException.Should().BeNull();
        e.ToString().Should().NotContain("c2VjcmV0LW1hcmtlcg");
    }
}
