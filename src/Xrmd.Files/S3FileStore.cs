using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Xrmd.Files;

/// <summary>
/// <see cref="IFileStore"/> over the plain S3 protocol (AWS S3, MinIO, Ceph, ...). Talks to a custom service URL with static
/// credentials; uses no AWS-specific feature. Uploads of unknown length are streamed in <see cref="PartSize"/> parts, so
/// memory use is bounded by one part regardless of the blob size.
/// </summary>
public sealed class S3FileStore : IFileStore, IDisposable
{
    /// <summary>S3's minimum size of every multipart part except the last.</summary>
    internal const int MinPartSize = 5 * 1024 * 1024;

    /// <summary>Size of the buffer a streamed upload fills before sending a part.</summary>
    internal const int PartSize = 8 * 1024 * 1024;

    private const int MaxParts = 10_000;

    // A pool of the store's own rather than ArrayPool<byte>.Shared: the shared pool is process-wide and keeps a returned
    // 8 MB buffer alive for a long time, so it would leak into unrelated stores and make live-memory measurements
    // depend on whatever ran before. At most two buffers are retained per store.
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(PartSize, 2);

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3FileStore(S3FileStoreOptions options)
        : this(CreateClient(options), options.Bucket)
    {
    }

    internal S3FileStore(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    private static AmazonS3Client CreateClient(S3FileStoreOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            // The SDK's default "always send a checksum" mode is an AWS-ism that not every S3-compatible server accepts.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public async Task<FileStoreCheck> CheckAsync(CancellationToken ct)
    {
        var label = $"S3 bucket '{_bucket}'";
        try
        {
            await _s3.HeadBucketAsync(new HeadBucketRequest { BucketName = _bucket }, ct).ConfigureAwait(false);
            return FileStoreCheck.Success($"{label} is reachable.");
        }
        catch (AmazonS3Exception e)
        {
            // Status and a fixed hint only: neither the service's message nor its error code can carry a key.
            return FileStoreCheck.Failure(e.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{label} does not exist; xrmd does not create it.",
                HttpStatusCode.Forbidden => $"{label}: access denied (check keys and bucket policy).",
                HttpStatusCode.MovedPermanently or HttpStatusCode.BadRequest => $"{label}: wrong region or endpoint (check Files:S3:ServiceUrl and Files:S3:Region).",
                _ => $"{label} is not accessible (HTTP {(int)e.StatusCode}).",
            });
        }
        catch (Exception e) when (e is AmazonClientException or HttpRequestException)
        {
            return FileStoreCheck.Failure($"{label} cannot be reached ({e.GetType().Name}); check Files:S3:ServiceUrl.");
        }
    }

    public void Dispose()
    {
        _s3.Dispose();
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);

        var buffer = _pool.Rent(PartSize);
        try
        {
            await WriteBufferedAsync(key, content, buffer, ct);
        }
        finally
        {
            _pool.Return(buffer);
        }
    }

    private async Task WriteBufferedAsync(string key, Stream content, byte[] buffer, CancellationToken ct)
    {
        var filled = await FillAsync(content, buffer.AsMemory(0, PartSize), ct);
        if (filled < PartSize)
        {
            // Fits in one request. PutObject is atomic: readers see the old or the new object.
            using var body = new MemoryStream(buffer, 0, filled, writable: false);
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = body }, ct);
            return;
        }

        var uploadId = (await _s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key }, ct)).UploadId;
        try
        {
            var parts = new List<PartETag>();
            var partNumber = 1;
            while (filled > 0)
            {
                if (partNumber > MaxParts) throw new IOException($"The blob needs more than {MaxParts} parts.");
                using var body = new MemoryStream(buffer, 0, filled, writable: false);
                var response = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket, Key = key, UploadId = uploadId, PartNumber = partNumber,
                    PartSize = filled, InputStream = body,
                }, ct);
                parts.Add(new PartETag(partNumber, response.ETag));
                partNumber++;
                // A short fill means the stream ended; skip the extra read.
                filled = filled < PartSize ? 0 : await FillAsync(content, buffer.AsMemory(0, PartSize), ct);
            }
            await CompleteAsync(key, uploadId, parts, ct);
        }
        catch
        {
            await AbortQuietlyAsync(key, uploadId);
            throw;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0)
        {
            if (await GetLengthAsync(key, ct) is null) throw new FileNotFoundException($"No file stored under '{key}'.", key);
            return Stream.Null;
        }

        var request = new GetObjectRequest { BucketName = _bucket, Key = key };
        if (offset > 0 || length != null)
            request.ByteRange = new ByteRange(offset, length is { } l ? offset + l - 1 : long.MaxValue / 2);
        try
        {
            return new ResponseStream(await _s3.GetObjectAsync(request, ct));
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchKey")
        {
            // Only NoSuchKey: a missing bucket (NoSuchBucket) is a configuration error and must propagate.
            throw new FileNotFoundException($"No file stored under '{key}'.", key, e);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The offset is at or past the end of the blob (the key exists, or the server would have answered 404).
            return Stream.Null;
        }
    }

    /// <remarks>
    /// Uses HEAD, whose response has no body, so a missing bucket is indistinguishable from a missing key (both are 404)
    /// and also yields null. A misconfigured bucket therefore surfaces on the first write or read, not here.
    /// </remarks>
    public async Task<long?> GetLengthAsync(string key, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        try
        {
            return (await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct)).ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct);
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        if (prefix.Length == 0) throw new ArgumentException("DeletePrefix needs a non-empty prefix; refusing to delete everything.", nameof(prefix));

        var batch = new List<KeyVersion>(1000);
        await foreach (var key in ListAsync(prefix, ct))
        {
            batch.Add(new KeyVersion { Key = key });
            if (batch.Count == 1000) await DeleteBatchAsync(batch, ct);
        }
        if (batch.Count > 0) await DeleteBatchAsync(batch, ct);
    }

    private async Task DeleteBatchAsync(List<KeyVersion> batch, CancellationToken ct)
    {
        var response = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = batch, Quiet = true }, ct);
        batch.Clear();
        if (response.DeleteErrors is { Count: > 0 } errors)
            throw new IOException($"Could not delete '{errors[0].Key}': {errors[0].Code} {errors[0].Message}");
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        string? token = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket, Prefix = prefix.Length == 0 ? null : prefix, ContinuationToken = token,
            }, ct);
            foreach (var o in page.S3Objects ?? []) yield return o.Key;
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token != null);
    }

    public async Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);
        if (sourceKeys.Count == 0) throw new ArgumentException("Concat needs at least one source.", nameof(sourceKeys));
        foreach (var source in sourceKeys) FileKeys.ValidateKey(source, nameof(sourceKeys));
        FileKeys.ValidateKey(targetKey, nameof(targetKey));

        // Resolve every source first: a missing one fails before anything is written, so no target is left behind.
        var lengths = new long[sourceKeys.Count];
        for (var i = 0; i < lengths.Length; i++)
            lengths[i] = await GetLengthAsync(sourceKeys[i], ct) ?? throw new FileNotFoundException($"No file stored under '{sourceKeys[i]}'.", sourceKeys[i]);

        // UploadPartCopy moves the bytes inside the server, but every part except the last must be >= 5 MB (and a part
        // cannot be empty). Otherwise (the 4 MB upload blocks) fall back to streaming through this process.
        var canCopy = sourceKeys.Count <= MaxParts && lengths[^1] > 0;
        for (var i = 0; canCopy && i < lengths.Length - 1; i++) canCopy = lengths[i] >= MinPartSize;

        if (canCopy) await CopyConcatAsync(sourceKeys, lengths, targetKey, ct);
        else
        {
            await using var joined = new ConcatReadStream(this, sourceKeys);
            await WriteAsync(targetKey, joined, ct);
        }
    }

    private async Task CopyConcatAsync(IReadOnlyList<string> sourceKeys, long[] lengths, string targetKey, CancellationToken ct)
    {
        var uploadId = (await _s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = _bucket, Key = targetKey }, ct)).UploadId;
        try
        {
            var parts = new List<PartETag>();
            for (var i = 0; i < sourceKeys.Count; i++)
            {
                var response = await _s3.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = _bucket, SourceKey = sourceKeys[i],
                    DestinationBucket = _bucket, DestinationKey = targetKey,
                    UploadId = uploadId, PartNumber = i + 1,
                    FirstByte = 0, LastByte = lengths[i] - 1,
                }, ct);
                parts.Add(new PartETag(i + 1, response.ETag));
            }
            await CompleteAsync(targetKey, uploadId, parts, ct);
        }
        catch
        {
            await AbortQuietlyAsync(targetKey, uploadId);
            throw;
        }
    }

    private Task CompleteAsync(string key, string uploadId, List<PartETag> parts, CancellationToken ct) =>
        _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = _bucket, Key = key, UploadId = uploadId, PartETags = parts,
        }, ct);

    /// <summary>Aborts an unfinished multipart upload. Not cancellable on purpose: the caller's token is probably the reason we are here.</summary>
    private async Task AbortQuietlyAsync(string key, string uploadId)
    {
        try
        {
            await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = uploadId });
        }
        catch (Exception)
        {
            // The original failure is the one worth reporting; an abort failure must never replace it.
        }
    }

    private static async Task<int> FillAsync(Stream source, Memory<byte> buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[filled..], ct);
            if (read == 0) break;
            filled += read;
        }
        return filled;
    }

    /// <summary>Disposing the stream also releases the HTTP response behind it.</summary>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }
}
