using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

/// <summary>One MinIO container shared by the S3 tests; each test gets its own bucket.</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    // Pinned by digest; it is chainguard/minio:latest as pulled on 2026-11-09 (RELEASE.2026-10-23T20-06-51Z).
    // minio/minio (Docker Hub) and quay.io/minio/minio are not used: MinIO no longer publishes pullable images there.
    private const string Image = "chainguard/minio@sha256:a1ac4589a5f8136b616e667b48a0bea9719bb10bd45799ea946f24bbedfafde4";

    private readonly MinioContainer _container = new MinioBuilder(Image).Build();

    public string ServiceUrl => _container.GetConnectionString();
    public string AccessKey => _container.GetAccessKey();
    public string SecretKey => _container.GetSecretKey();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public S3FileStoreOptions Options(string bucket) => new()
    {
        ServiceUrl = ServiceUrl,
        Bucket = bucket,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
    };
}

public sealed class S3FileStoreTests : FileStoreContract, IClassFixture<MinioFixture>
{
    private readonly MinioFixture _minio;
    private string _bucket = "";
    private AmazonS3Client _admin = null!;

    public S3FileStoreTests(MinioFixture minio) => _minio = minio;

    protected override async Task<IFileStore> CreateStore()
    {
        _bucket = "t" + Guid.NewGuid().ToString("N");
        _admin = new AmazonS3Client(_minio.AccessKey, _minio.SecretKey,
            new AmazonS3Config { ServiceURL = _minio.ServiceUrl, ForcePathStyle = true });
        await _admin.PutBucketAsync(_bucket);
        return new S3FileStore(_minio.Options(_bucket));
    }

    protected override async Task CleanupAsync()
    {
        if (_admin == null) return;
        string? token = null;
        do
        {
            var page = await _admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, ContinuationToken = token });
            foreach (var o in page.S3Objects ?? []) await _admin.DeleteObjectAsync(_bucket, o.Key);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token != null);
        await _admin.DeleteBucketAsync(_bucket);
        _admin.Dispose();
    }

    [Fact]
    public async Task Check_is_ok_for_an_existing_bucket()
    {
        var check = await Store.CheckAsync(default);
        check.IsOk.Should().BeTrue(check.Message);
        check.Message.Should().Contain(_bucket);
    }

    [Fact]
    public async Task Check_fails_for_a_missing_bucket()
    {
        using var store = new S3FileStore(_minio.Options("missing" + Guid.NewGuid().ToString("N")));
        var check = await store.CheckAsync(default);
        check.IsOk.Should().BeFalse();
        check.Message.Should().Contain("missing").And.Contain("does not exist");
    }

    [Fact]
    public async Task Check_failure_never_contains_the_secret_key()
    {
        const string secret = "S3cr3t-Key-Must-Not-Leak-0123456789";
        var options = _minio.Options(_bucket);
        options.SecretKey = secret;
        using var store = new S3FileStore(options);
        var check = await store.CheckAsync(default);
        check.IsOk.Should().BeFalse();
        check.Message.Should().NotContain(secret).And.NotContain(_minio.SecretKey);
        check.Message.Should().Contain("access denied").And.Contain(_bucket);
    }

    private static byte[] Block(int index, int size)
    {
        var data = new byte[size];
        new Random(index).NextBytes(data);
        return data;
    }

    private async Task<List<string>> SeedBlocksAsync(int blocks, int blockSize)
    {
        var keys = new List<string>();
        for (var i = 0; i < blocks; i++)
        {
            var key = $"org/_upload/s/{i}";
            // Seeded through the admin client, not Store.WriteAsync: the store rents its part buffer from its own ArrayPool,
            // and a buffer the seeding returned there would already be live (pooled) in the baseline, hiding it from the sampler.
            await _admin.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(Block(i, blockSize)) });
            keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Runs the action while a background sampler records the maximum live managed memory above the baseline.
    /// Every sample forces a full collection, so garbage does not count, only bytes that are still referenced.
    /// </summary>
    private static async Task<long> PeakLiveBytesAsync(Func<Task> action)
    {
        // Let the seeding's state machines, HTTP buffers and finalizers drain so they are not in the baseline.
        await Task.Delay(300);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = long.MinValue;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                Thread.Sleep(2);
            }
        }, TaskCreationOptions.LongRunning);
        try
        {
            await action();
        }
        finally
        {
            stop.Cancel();
            await sampler;
        }
        return peak;
    }

    [Fact]
    public async Task Concat_of_many_4MB_parts_streams_without_buffering()
    {
        const int blockSize = 4 * 1024 * 1024;
        const int blocks = 13;
        var keys = await SeedBlocksAsync(blocks, blockSize);

        var peak = await PeakLiveBytesAsync(() => Store.ConcatAsync(keys, "org/final", default));

        Console.WriteLine($"Concat 13x4MB peak live-memory delta {peak / 1024.0 / 1024.0:F1} MB");
        // The streamed upload holds one part buffer; allow three part buffers of slack, far below the 52 MB result.
        peak.Should().BeLessThan(3L * S3FileStore.PartSize, "the 52 MB result must be streamed, not held in memory");
        peak.Should().BeGreaterThanOrEqualTo(S3FileStore.PartSize / 2, "the sampler must see the live part buffer, otherwise it proves nothing");

        (await Store.GetLengthAsync("org/final", default)).Should().Be((long)blockSize * blocks);
        await using var stream = await Store.OpenReadAsync("org/final", 0, null, default);
        var buffer = new byte[blockSize];
        for (var i = 0; i < blocks; i++)
        {
            await stream.ReadExactlyAsync(buffer);
            buffer.Should().Equal(Block(i, blockSize), $"block {i}");
        }
        (await stream.ReadAsync(buffer)).Should().Be(0);
    }

    [Fact]
    public async Task Concat_of_5MB_parts_uses_server_side_copy_and_keeps_bytes()
    {
        const int partSize = 5 * 1024 * 1024;
        var parts = new[] { Block(1, partSize), Block(2, partSize), Block(3, 1234) };
        var keys = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            keys.Add($"org/p{i}");
            await Store.WriteAsync(keys[i], new MemoryStream(parts[i]), default);
        }
        var counting = Counting();
        await counting.Store.ConcatAsync(keys, "org/copied", default);
        counting.Calls("CopyPartAsync").Should().Be(3);
        counting.Calls("UploadPartAsync").Should().Be(0);

        await using var stream = await Store.OpenReadAsync("org/copied", 0, null, default);
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        all.ToArray().Should().Equal(parts.SelectMany(p => p).ToArray());
    }

    [Fact]
    public async Task Write_of_an_unseekable_stream_larger_than_one_part_round_trips()
    {
        var data = Block(9, 11 * 1024 * 1024 + 17);
        await Store.WriteAsync("org/big", new NonSeekableStream(data), default);
        await using var stream = await Store.OpenReadAsync("org/big", 0, null, default);
        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        all.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task Failed_multipart_write_leaves_no_blob_and_no_open_upload()
    {
        var act = async () => await Store.WriteAsync("org/broken", new FailingAfterStream(12 * 1024 * 1024), default);
        await act.Should().ThrowAsync<IOException>();
        (await Store.GetLengthAsync("org/broken", default)).Should().BeNull();
        var uploads = await _admin.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = _bucket });
        uploads.MultipartUploads.Should().BeEmpty();
    }

    [Fact]
    public async Task List_pages_through_more_than_one_page_of_keys()
    {
        // MinIO pages at 1000 keys; 1105 forces a continuation token.
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
    public async Task Zero_length_blob_reads_empty()
    {
        await Store.WriteAsync("org/empty", new MemoryStream(), default);
        (await Store.GetLengthAsync("org/empty", default)).Should().Be(0);
        await using var stream = await Store.OpenReadAsync("org/empty", 0, null, default);
        (await stream.ReadAsync(new byte[1])).Should().Be(0);
    }

    private sealed record CountingStore(S3FileStore Store, Func<string, int> Calls);

    private CountingStore Counting(Action<string, int>? before = null)
    {
        var proxy = DispatchProxy.Create<IAmazonS3, CountingS3>();
        var counting = (CountingS3)(object)proxy;
        counting.Target = _admin;
        counting.Before = before;
        return new CountingStore(new S3FileStore(proxy, _bucket), name => counting.Count(name));
    }

    private async Task AssertNoOpenUploadAsync(string targetKey)
    {
        var uploads = await _admin.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = _bucket });
        uploads.MultipartUploads.Should().BeEmpty();
        (await Store.GetLengthAsync(targetKey, default)).Should().BeNull();
    }

    private async Task<List<string>> SeedAsync(string prefix, params int[] sizes)
    {
        var keys = new List<string>();
        for (var i = 0; i < sizes.Length; i++)
        {
            keys.Add($"{prefix}{i}");
            await Store.WriteAsync(keys[i], new MemoryStream(Block(i + 100, sizes[i])), default);
        }
        return keys;
    }

    [Fact]
    public async Task Concat_of_4MB_blocks_does_not_use_server_side_copy()
    {
        var keys = await SeedAsync("org/b", 4 << 20, 4 << 20, 100);
        var counting = Counting();
        await counting.Store.ConcatAsync(keys, "org/out", default);
        counting.Calls("CopyPartAsync").Should().Be(0);
        (await Store.GetLengthAsync("org/out", default)).Should().Be((8 << 20) + 100);
    }

    [Fact]
    public async Task Concat_copy_failure_aborts_the_upload_and_leaves_no_target()
    {
        var keys = await SeedAsync("org/c", 5 << 20, 5 << 20, 10);
        var counting = Counting((name, n) =>
        {
            if (name == "CopyPartAsync" && n == 2) throw new AmazonS3Exception("injected copy failure");
        });
        var act = async () => await counting.Store.ConcatAsync(keys, "org/out", default);
        (await act.Should().ThrowAsync<AmazonS3Exception>()).WithMessage("injected copy failure");
        await AssertNoOpenUploadAsync("org/out");
    }

    [Fact]
    public async Task Concat_cancelled_mid_multipart_aborts_the_upload_and_leaves_no_target()
    {
        var keys = await SeedAsync("org/d", 5 << 20, 5 << 20, 10);
        using var cts = new CancellationTokenSource();
        var counting = Counting((name, n) =>
        {
            if (name == "CopyPartAsync" && n == 2) cts.Cancel();
        });
        var act = async () => await counting.Store.ConcatAsync(keys, "org/out", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        await AssertNoOpenUploadAsync("org/out");
    }

    [Fact]
    public async Task Concat_streaming_source_failing_mid_read_aborts_the_upload_and_leaves_no_target()
    {
        var keys = await SeedAsync("org/e", 4 << 20, 4 << 20, 4 << 20);
        // The first 8 MB part is full after two sources, so the multipart upload is open when the third is read.
        var counting = Counting((name, n) =>
        {
            if (name == "GetObjectAsync" && n == 3) throw new AmazonS3Exception("injected read failure");
        });
        var act = async () => await counting.Store.ConcatAsync(keys, "org/out", default);
        (await act.Should().ThrowAsync<AmazonS3Exception>()).WithMessage("injected read failure");
        counting.Calls("UploadPartAsync").Should().BeGreaterThan(0, "the failure must happen with a multipart upload open");
        await AssertNoOpenUploadAsync("org/out");
    }

    [Fact]
    public async Task Reading_from_a_missing_bucket_propagates_instead_of_reporting_a_missing_file()
    {
        var store = new S3FileStore(_minio.Options("no-such-bucket-" + Guid.NewGuid().ToString("N")));
        var act = async () => await store.OpenReadAsync("org/x", 0, null, default);
        var thrown = await act.Should().ThrowAsync<AmazonS3Exception>();
        thrown.Which.ErrorCode.Should().Be("NoSuchBucket");
    }

    /// <summary>Forwards every call to <see cref="Target"/>, counting calls by method name and running <see cref="Before"/> first.</summary>
    public class CountingS3 : DispatchProxy
    {
        private readonly Dictionary<string, int> _counts = new();
        public IAmazonS3 Target { get; set; } = null!;
        public Action<string, int>? Before { get; set; }

        public int Count(string name)
        {
            lock (_counts) return _counts.GetValueOrDefault(name);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            int n;
            lock (_counts) _counts[name] = n = _counts.GetValueOrDefault(name) + 1;
            Before?.Invoke(name, n);
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 100_000));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingAfterStream(long failAfter) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= failAfter) throw new IOException("boom");
            var n = (int)Math.Min(count, Math.Min(1 << 20, failAfter - _read));
            _read += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class S3RegistrationTests
{
    private static Dictionary<string, string?> Full() => new()
    {
        ["Files:Store"] = "S3Compatible",
        ["Files:S3:ServiceUrl"] = "http://localhost:9000",
        ["Files:S3:Bucket"] = "xrmd",
        ["Files:S3:AccessKey"] = "ak",
        ["Files:S3:SecretKey"] = "sk",
    };

    private static IServiceCollection Add(Dictionary<string, string?> values) =>
        new ServiceCollection().AddXrmdFiles(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Theory]
    [InlineData("ServiceUrl")]
    [InlineData("Bucket")]
    [InlineData("AccessKey")]
    [InlineData("SecretKey")]
    public void Missing_settings_fail_startup_naming_the_key(string key)
    {
        var values = Full();
        values.Remove($"Files:S3:{key}");
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*Files:S3:{key}*");
    }

    [Fact]
    public void Invalid_service_url_is_rejected_without_echoing_it()
    {
        var values = Full();
        values["Files:S3:ServiceUrl"] = "ftp://user:secret@host";
        var act = () => Add(values);
        var message = act.Should().Throw<InvalidOperationException>().WithMessage("*Files:S3:ServiceUrl*").Which.Message;
        message.Should().NotContain("secret").And.NotContain("ftp://");
    }

    [Fact]
    public void Blank_setting_counts_as_missing()
    {
        var values = Full();
        values["Files:S3:Bucket"] = "  ";
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Files:S3:Bucket*");
    }

    [Fact]
    public void Complete_settings_register_the_s3_store_with_defaults()
    {
        var provider = Add(Full()).BuildServiceProvider();
        provider.GetRequiredService<IFileStore>().Should().BeOfType<S3FileStore>();
        var s3 = provider.GetRequiredService<FileStoreOptions>().S3;
        s3.Region.Should().Be("us-east-1");
        s3.ForcePathStyle.Should().BeTrue();
    }

    [Fact]
    public void Region_and_path_style_are_configurable_and_a_bad_bool_is_rejected()
    {
        var values = Full();
        values["Files:S3:Region"] = "eu-west-1";
        values["Files:S3:ForcePathStyle"] = "false";
        var s3 = Add(values).BuildServiceProvider().GetRequiredService<FileStoreOptions>().S3;
        s3.Region.Should().Be("eu-west-1");
        s3.ForcePathStyle.Should().BeFalse();

        values["Files:S3:ForcePathStyle"] = "maybe";
        var act = () => Add(values);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Files:S3:ForcePathStyle*");
    }
}
