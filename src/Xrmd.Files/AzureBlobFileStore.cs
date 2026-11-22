using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

namespace Xrmd.Files;

/// <summary>
/// <see cref="IFileStore"/> over one Azure Storage blob container. Keys are blob names. Every write ends in a single
/// commit (a Put Blob or one block list), so readers see the old or the new content, never a partial one.
/// </summary>
public sealed class AzureBlobFileStore : IFileStore
{
    /// <summary>Size of the blocks a streamed upload (and a streamed concat) stages.</summary>
    internal const int BlockSize = 4 * 1024 * 1024;

    /// <summary>The service's limit of committed blocks per blob.</summary>
    internal const int MaxBlocks = 50_000;

    /// <summary>Largest source range copied by one server-side block.</summary>
    private const long CopyBlockSize = 100L * 1024 * 1024;

    private static readonly TimeSpan SasLifetime = TimeSpan.FromHours(1);

    private readonly BlobContainerClient _container;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<UserDelegationKey>>? _delegationKey;

    public AzureBlobFileStore(AzureBlobFileStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Messages here never include a setting's value: the connection string carries a key.
        if (string.IsNullOrWhiteSpace(options.Container))
            throw new ArgumentException("The required setting Files:Azure:Container is missing.", nameof(options));
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        var hasServiceUri = !string.IsNullOrWhiteSpace(options.ServiceUri);
        if (hasConnectionString == hasServiceUri)
            throw new ArgumentException("Set exactly one of Files:Azure:ConnectionString and Files:Azure:ServiceUri.", nameof(options));

        if (hasConnectionString)
        {
            try
            {
                _container = new BlobContainerClient(options.ConnectionString, options.Container);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or UriFormatException or InvalidOperationException)
            {
                // Dropped on purpose, inner exception included: parser messages may quote the string.
                throw new ArgumentException("Files:Azure:ConnectionString is not a valid Azure Storage connection string.", nameof(options));
            }
        }
        else
        {
            if (!Uri.TryCreate(options.ServiceUri, UriKind.Absolute, out var serviceUri) || serviceUri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Files:Azure:ServiceUri is not a valid absolute https URL.", nameof(options));
            var credential = new DefaultAzureCredential();
            var service = new BlobServiceClient(serviceUri, credential);
            _container = service.GetBlobContainerClient(options.Container);
            _delegationKey = async (start, expiry, ct) => (await service.GetUserDelegationKeyAsync(start, expiry, ct)).Value;
        }
    }

    internal AzureBlobFileStore(BlobContainerClient container, Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<UserDelegationKey>>? delegationKey = null)
    {
        _container = container;
        _delegationKey = delegationKey;
    }

    /// <summary>Test seam: forces one concat path. <see cref="ConcatStrategy.Auto"/> is what production runs.</summary>
    internal ConcatStrategy Strategy { get; set; } = ConcatStrategy.Auto;

    /// <summary>Test seam: the path the last <see cref="ConcatAsync"/> took.</summary>
    internal ConcatPath LastConcatPath { get; private set; }

    /// <summary>Test seam: runs before source <c>index</c> is staged or read, after the lengths were checked.</summary>
    internal Func<int, CancellationToken, Task>? BeforeSource { get; set; }

    internal enum ConcatStrategy { Auto, ServerSide, Streamed }

    internal enum ConcatPath { None, ServerSide, Streamed }

    public async Task<FileStoreCheck> CheckAsync(CancellationToken ct)
    {
        var label = $"Azure Blob container '{_container.Name}'";
        try
        {
            return (await _container.ExistsAsync(ct).ConfigureAwait(false)).Value
                ? FileStoreCheck.Success($"{label} is reachable.")
                : FileStoreCheck.Failure($"{label} does not exist; xrmd does not create it.");
        }
        catch (RequestFailedException e)
        {
            // Status and a fixed hint only: neither the service's message nor its error code can carry a key or SAS token.
            return FileStoreCheck.Failure(e.Status is 401 or 403
                ? $"{label}: access denied (HTTP {e.Status}); check the credentials and the role assignment on the container."
                : $"{label} is not accessible (HTTP {e.Status}).");
        }
        catch (Exception e) when (e is AuthenticationFailedException or HttpRequestException)
        {
            return FileStoreCheck.Failure($"{label} cannot be reached ({e.GetType().Name}); check the credentials and the endpoint.");
        }
    }

    public async Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);

        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            await WriteBufferedAsync(key, content, buffer, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteBufferedAsync(string key, Stream content, byte[] buffer, CancellationToken ct)
    {
        var filled = await FillAsync(content, buffer.AsMemory(0, BlockSize), ct);
        var blob = _container.GetBlockBlobClient(key);
        if (filled < BlockSize)
        {
            // Fits in one request. Put Blob is atomic and also handles the empty blob.
            using var body = new MemoryStream(buffer, 0, filled, writable: false);
            await blob.UploadAsync(body, cancellationToken: ct);
            return;
        }

        // Staged blocks stay invisible until the block list is committed; a failure before that leaves the old blob.
        var nonce = NewNonce();
        var ids = new List<string>();
        while (filled > 0)
        {
            if (ids.Count >= MaxBlocks) throw new IOException($"The blob needs more than {MaxBlocks} blocks.");
            var id = BlockId(nonce, ids.Count);
            using (var body = new MemoryStream(buffer, 0, filled, writable: false))
                await blob.StageBlockAsync(id, body, cancellationToken: ct);
            ids.Add(id);
            // A short fill means the stream ended; skip the extra read.
            filled = filled < BlockSize ? 0 : await FillAsync(content, buffer.AsMemory(0, BlockSize), ct);
        }
        await blob.CommitBlockListAsync(ids, cancellationToken: ct);
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0)
        {
            if (await GetLengthAsync(key, ct) is null) throw NotFound(key, null);
            return Stream.Null;
        }

        var options = new BlobDownloadOptions();
        // A whole-blob read sends no Range header: a range on an empty blob is unsatisfiable.
        if (offset > 0 || length != null) options.Range = new HttpRange(offset, length);
        try
        {
            var response = await _container.GetBlobClient(key).DownloadStreamingAsync(options, ct);
            return response.Value.Content;
        }
        catch (RequestFailedException e) when (e.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            // Only BlobNotFound: a missing container is a configuration error and must propagate.
            throw NotFound(key, e);
        }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The offset is at or past the end of an existing blob. A range that merely ends past the end is clamped by the service.
            return Stream.Null;
        }
    }

    /// <remarks>Uses HEAD, whose response has no body, so a missing container is indistinguishable from a missing blob and also yields null.</remarks>
    public async Task<long?> GetLengthAsync(string key, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        try
        {
            return (await _container.GetBlobClient(key).GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
        }
        catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        FileKeys.ValidateKey(key);
        await _container.GetBlobClient(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        if (prefix.Length == 0) throw new ArgumentException("DeletePrefix needs a non-empty prefix; refusing to delete everything.", nameof(prefix));

        var batch = new List<Task>(16);
        await foreach (var key in ListAsync(prefix, ct))
        {
            batch.Add(_container.GetBlobClient(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct));
            if (batch.Count == 16)
            {
                await Task.WhenAll(batch);
                batch.Clear();
            }
        }
        await Task.WhenAll(batch);
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        FileKeys.ValidatePrefix(prefix);
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix.Length == 0 ? null : prefix, ct))
            yield return item.Name;
    }

    public async Task ConcatAsync(IReadOnlyList<string> sourceKeys, string targetKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);
        if (sourceKeys.Count == 0) throw new ArgumentException("Concat needs at least one source.", nameof(sourceKeys));
        foreach (var source in sourceKeys) FileKeys.ValidateKey(source, nameof(sourceKeys));
        FileKeys.ValidateKey(targetKey, nameof(targetKey));

        // Resolve every source first: a missing one fails before anything is staged.
        var lengths = new long[sourceKeys.Count];
        for (var i = 0; i < lengths.Length; i++)
            lengths[i] = await GetLengthAsync(sourceKeys[i], ct) ?? throw NotFound(sourceKeys[i], null);

        var blocks = lengths.Sum(l => (l + CopyBlockSize - 1) / CopyBlockSize);
        if (Strategy != ConcatStrategy.Streamed && blocks <= MaxBlocks)
        {
            if (await TryServerSideConcatAsync(sourceKeys, lengths, targetKey, ct)) return;
            if (Strategy == ConcatStrategy.ServerSide)
                throw new NotSupportedException("Server-side concat is not available for this store.");
        }

        LastConcatPath = ConcatPath.Streamed;
        await using var joined = new ConcatReadStream(this, sourceKeys, BeforeSource);
        await WriteAsync(targetKey, joined, ct);
    }

    /// <summary>
    /// Stages each source as blocks of the target with Put Block From URL and commits once. Returns false, with nothing
    /// committed, when the copy cannot be authorized or the service does not support it.
    /// </summary>
    private async Task<bool> TryServerSideConcatAsync(IReadOnlyList<string> sourceKeys, long[] lengths, string targetKey, CancellationToken ct)
    {
        var sources = await CreateSourceUrisAsync(sourceKeys, ct);
        if (sources is null) return false;

        var target = _container.GetBlockBlobClient(targetKey);
        var nonce = NewNonce();
        var ids = new List<string>();
        for (var i = 0; i < sourceKeys.Count; i++)
        {
            if (BeforeSource != null) await BeforeSource(i, ct);
            // An empty source contributes no block: a zero-length block is not allowed.
            for (long from = 0; from < lengths[i]; from += CopyBlockSize)
            {
                var id = BlockId(nonce, ids.Count);
                try
                {
                    await target.StageBlockFromUriAsync(sources[i], id,
                        new StageBlockFromUriOptions { SourceRange = new HttpRange(from, Math.Min(CopyBlockSize, lengths[i] - from)) }, ct);
                }
                catch (RequestFailedException e) when (e.Status == (int)HttpStatusCode.NotFound && e.ErrorCode is "BlobNotFound" or "CannotVerifyCopySource")
                {
                    // The service reports a source that vanished after the length check as CannotVerifyCopySource (404).
                    throw NotFound(sourceKeys[i], e);
                }
                catch (RequestFailedException e) when (ids.Count == 0 && Strategy == ConcatStrategy.Auto && IsUnsupportedOrUnreachable(e))
                {
                    // E.g. Azurite, which cannot fetch the source URL it is given. Nothing was staged; stream instead.
                    return false;
                }
                ids.Add(id);
            }
        }
        await target.CommitBlockListAsync(ids, cancellationToken: ct);
        LastConcatPath = ConcatPath.ServerSide;
        return true;
    }

    private static bool IsUnsupportedOrUnreachable(RequestFailedException e) =>
        e.Status is (int)HttpStatusCode.NotImplemented or (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden
            or (int)HttpStatusCode.InternalServerError or (int)HttpStatusCode.BadGateway or (int)HttpStatusCode.ServiceUnavailable;

    /// <summary>Read-only SAS URLs valid for an hour, one per source, or null when this store cannot sign them.</summary>
    private async Task<Uri[]?> CreateSourceUrisAsync(IReadOnlyList<string> sourceKeys, CancellationToken ct)
    {
        var expiry = DateTimeOffset.UtcNow + SasLifetime;
        var uris = new Uri[sourceKeys.Count];
        if (_container.CanGenerateSasUri)
        {
            for (var i = 0; i < uris.Length; i++)
                uris[i] = _container.GetBlobClient(sourceKeys[i]).GenerateSasUri(BlobSasPermissions.Read, expiry);
            return uris;
        }
        if (_delegationKey is null) return null;

        UserDelegationKey key;
        try
        {
            key = await _delegationKey(DateTimeOffset.UtcNow.AddMinutes(-5), expiry, ct);
        }
        catch (Exception e) when (e is RequestFailedException or AuthenticationFailedException)
        {
            // E.g. the identity lacks the right to request a delegation key: copy through this process instead (ruling B3).
            return null;
        }
        for (var i = 0; i < uris.Length; i++)
        {
            var blob = _container.GetBlobClient(sourceKeys[i]);
            var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiry)
            {
                BlobContainerName = _container.Name,
                BlobName = blob.Name,
                Resource = "b",
            };
            uris[i] = new BlobUriBuilder(blob.Uri) { Sas = sas.ToSasQueryParameters(key, _container.AccountName) }.ToUri();
        }
        return uris;
    }

    /// <summary>
    /// Base64 of a fixed-width string: a 16-hex per-operation nonce and an 8-digit index. Every block id of a blob has the
    /// same length (a service rule), and the nonce keeps concurrent writers of one key from mixing uncommitted blocks.
    /// </summary>
    internal static string BlockId(string nonce, int index) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(nonce + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)));

    private static string NewNonce() => Guid.NewGuid().ToString("N")[..16];

    private static FileNotFoundException NotFound(string key, Exception? inner) =>
        new($"No file stored under '{key}'.", key, inner);

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
}
