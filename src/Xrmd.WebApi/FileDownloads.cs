using Microsoft.AspNetCore.Http;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Security;

namespace Xrmd.WebApi;

/// <summary>
/// The downloads of the Web API (<c>GET …/{column}/$value</c>) and of <c>/Image/download.aspx</c>, run as an SDK client would
/// run them, through <see cref="OrganizationService"/> and so through the pipeline (plug-in steps and security, rule "one
/// semantics"):
/// <list type="bullet">
/// <item>a file, or the full image of an image column: <c>InitializeFileBlocksDownload</c>, then one <c>DownloadBlock</c> per
/// 4 MB, each block written to the response as it comes (the file is never held whole in memory); a byte range is the offset
/// and length of those blocks;</item>
/// <item>the thumbnail of an image column: a <c>Retrieve</c> of that column (the thumbnail is the column's value, ruling F2).</item>
/// </list>
/// </summary>
public static class FileDownloads
{
    /// <summary>
    /// Opens the download of the file (or full image) of <paramref name="attribute"/> on <paramref name="target"/>, or of the bytes
    /// <paramref name="range"/> names in it: InitializeFileBlocksDownload and the first DownloadBlock run here, so that a refusal
    /// (a plug-in, the security guard, a missing file, a range past the end) is the request's error rather than a response cut
    /// short. The other blocks are read by <see cref="BlockDownload.CopyToAsync"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">The faults of the two messages; 400 for a range that starts past the end of the file.</exception>
    public static async Task<BlockDownload> StartAsync(HttpContext context, EntityReference target, string attribute,
        (long From, long? To)? range, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var service = context.RequestServices.GetRequiredService<OrganizationService>();
        var init = (InitializeFileBlocksDownloadResponse)await service.ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = target, FileAttributeName = attribute,
        }, ct);
        var download = new BlockDownload(context, init.FileContinuationToken, init.FileSizeInBytes, init.FileName ?? "");
        try
        {
            var (from, length) = Range(range, download.FileSize);
            await download.BeginAsync(from, length, ct);
            return download;
        }
        catch
        {
            await download.EndAsync();
            throw;
        }
    }

    /// <summary>
    /// The thumbnail of the image column <paramref name="attribute"/> of <paramref name="target"/>, read with a Retrieve of that
    /// column through the pipeline (Read demanded, Retrieve steps fired).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 when the record does not exist or the column is empty; the faults of Retrieve.</exception>
    public static async Task<byte[]> ThumbnailAsync(HttpContext context, EntityReference target, string attribute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        var service = context.RequestServices.GetRequiredService<OrganizationService>();
        var record = await service.RetrieveAsync(target.LogicalName, target.Id, new ColumnSet(attribute), ct);
        if (record.GetAttributeValue<byte[]>(attribute) is { } thumbnail) return thumbnail;
        // A secured image column the caller cannot read comes back as null (ruling F3): its download is refused (ruling F4).
        if (record.Attributes.ContainsKey(attribute)
            && (await service.UnreadableColumnsAsync([target.LogicalName], ct)).TryGetValue(target.LogicalName, out var unreadable)
            && unreadable.Contains(attribute))
            throw XrmdFaultException.FieldReadDenied(target.LogicalName, attribute,
                (await context.RequestServices.GetRequiredService<CallerResolver>().ResolveAsync(ct)).UserId);
        throw FileColumnService.NoImage(target.LogicalName, attribute, target.Id);
    }

    /// <summary>The offset and length of <paramref name="range"/> (<c>to</c> inclusive, cut to the end) in a file of <paramref name="size"/> bytes.</summary>
    /// <exception cref="XrmdFaultException">400 when the range starts past the end of a non-empty file.</exception>
    public static (long From, long Length) Range((long From, long? To)? range, long size)
    {
        if (range is not { } r) return (0, size);
        if (r.From < 0 || r.To is { } t && t < r.From) throw XrmdFaultException.InvalidArgument($"The byte range {r.From}-{r.To} is not valid.");
        if (r.From >= size && !(size == 0 && r.From == 0))
            throw XrmdFaultException.InvalidArgument($"The byte range starts at {r.From}, past the end of the file, which is {size} bytes long.");
        return (r.From, (r.To is { } to ? Math.Min(to, size - 1) : size - 1) - r.From + 1);
    }
}

/// <summary>
/// A download session opened by <see cref="FileDownloads.StartAsync"/>: the file's size and name, the range being read and its
/// first block. <see cref="CopyToAsync"/> writes the range, reading the blocks after the first with DownloadBlock; the session
/// is ended when it is done.
/// </summary>
public sealed class BlockDownload
{
    private readonly HttpContext _context;
    private byte[] _first = [];

    internal BlockDownload(HttpContext context, string token, long fileSize, string fileName)
    {
        _context = context;
        Token = token;
        FileSize = fileSize;
        FileName = fileName;
    }

    /// <summary>The download session's file continuation token.</summary>
    public string Token { get; }

    /// <summary>The size of the whole file.</summary>
    public long FileSize { get; }

    /// <summary>The file name InitializeFileBlocksDownload answered.</summary>
    public string FileName { get; }

    /// <summary>The first byte of the range.</summary>
    public long From { get; private set; }

    /// <summary>The bytes of the range.</summary>
    public long Length { get; private set; }

    /// <summary>The first bytes of the range (at most one block; empty for an empty range), read when the download started.</summary>
    public ReadOnlyMemory<byte> FirstBlock => _first;

    private OrganizationService Service => _context.RequestServices.GetRequiredService<OrganizationService>();

    internal async Task BeginAsync(long from, long length, CancellationToken ct)
    {
        From = from;
        Length = length;
        // An empty file has no block to read (an SDK client's loop, while offset < size, reads none either).
        if (length > 0) _first = await BlockAsync(from, Math.Min(length, FileColumnService.MaxBlockSize), ct);
    }

    /// <summary>
    /// Writes the range to <paramref name="output"/>: the first block, then one DownloadBlock of at most 4 MB at a time, each
    /// written before the next is read. A failure after the first block can only cut the response short (its status is sent).
    /// </summary>
    public async Task CopyToAsync(Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            await output.WriteAsync(_first, ct);
            var offset = From + _first.Length;
            _first = []; // written: let it go, so a download holds one block at a time
            var end = From + Length;
            while (offset < end)
            {
                var block = await BlockAsync(offset, Math.Min(end - offset, FileColumnService.MaxBlockSize), ct);
                if (block.Length == 0)
                    throw new InvalidOperationException($"DownloadBlock returned no bytes at offset {offset} of a file of {FileSize} bytes.");
                await output.WriteAsync(block, ct);
                offset += block.Length;
            }
        }
        finally
        {
            await EndAsync();
        }
    }

    /// <summary>Ends the download session (best effort: an SDK client's session expires instead; Dataverse has no message for it).</summary>
    internal async Task EndAsync()
    {
        var services = _context.RequestServices;
        try
        {
            var caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(CancellationToken.None);
            await services.GetRequiredService<FileColumnService>().EndDownloadAsync(caller, Token);
        }
        catch
        {
            // The cleanup sweep deletes the session once it expires.
        }
    }

    private async Task<byte[]> BlockAsync(long offset, long length, CancellationToken ct) =>
        ((DownloadBlockResponse)await Service.ExecuteAsync(new DownloadBlockRequest
        {
            FileContinuationToken = Token, Offset = offset, BlockLength = length,
        }, ct)).Data;
}
