using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Source;

/// <summary>
/// Streams file columns (in 4 MiB ranges), full-size images and note bodies into <c>files/&lt;table&gt;/&lt;column&gt;/&lt;id&gt;</c>.
/// Nothing is held in memory beyond the copy buffer, except note bodies (at most 128 MB), read whole to tell base64 from raw bytes.
/// </summary>
public sealed class FileExporter(DataverseClient client, PackageWriter writer) : IFileExporter
{
    internal const long RangeSize = 4 * 1024 * 1024;

    public async Task<MigrationValue?> ExportAsync(SourceTable table, SourceColumn column, Guid id, JsonElement row, CancellationToken ct)
    {
        var path = $"files/{table.LogicalName}/{column.LogicalName}/{id}";
        if (table.LogicalName == "annotation" && column.LogicalName == "documentbody")
            return await NoteBodyAsync(row, id, path, ct);
        if (!row.TryGetProperty(column.LogicalName, out var v) || v.ValueKind == JsonValueKind.Null) return NullValue.Instance;
        var record = $"{table.EntitySetName}({id})/{column.LogicalName}/$value";
        if (column.AttributeType == "Image")
        {
            using var response = await Missing(path, () => client.GetStreamAsync(record + "?size=full", null, ct));
            await CopyAsync(response, path, ct);
            return new ImageValue(path);
        }
        return await FileAsync(record, path, row, column, ct);
    }

    private async Task<MigrationValue> FileAsync(string record, string path, JsonElement row, SourceColumn column, CancellationToken ct)
    {
        string? name = null, mime = null;
        long written = 0;
        long? size = null;
        using (var entry = writer.CreateEntry(path))
        {
            while (true)
            {
                using var response = await Missing(path, () => client.GetStreamAsync(record, (written, written + RangeSize - 1), ct));
                name ??= Header(response, "x-ms-file-name");
                mime ??= response.Content.Headers.ContentType?.MediaType;
                if (Header(response, "x-ms-file-size") is { } s && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var total)) size = total;
                var partial = response.StatusCode == HttpStatusCode.PartialContent;
                if (partial && response.Content.Headers.ContentRange?.From is { } from && from != written)
                    throw Broken(path, $"the source answered bytes from {from} instead of {written}");
                if (!partial && written > 0)
                    throw Broken(path, "the source stopped honouring byte ranges in the middle of the file");
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var read = await CopyCountingAsync(body, entry, ct);
                written += read;
                // A server that ignores the range sends the whole file at once.
                if (!partial || read == 0 || (size is { } n ? written >= n : read < RangeSize)) break;
            }
            if (size is { } expected && written != expected)
                throw Broken(path, $"{written} bytes received, {expected} announced");
            entry.Commit();
        }
        name ??= row.TryGetProperty(column.LogicalName + "_name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
        return new FileValue(path, name ?? "file", mime ?? "application/octet-stream", written);
    }

    private static DataverseException Broken(string path, string why) =>
        new(HttpStatusCode.BadGateway, $"The download of {path} is incomplete: {why}.");

    private static async Task<HttpResponseMessage> Missing(string path, Func<Task<HttpResponseMessage>> get)
    {
        try
        {
            return await get();
        }
        catch (DataverseException e) when (e.Status == HttpStatusCode.NotFound)
        {
            throw new SourceFileMissingException(path, e);
        }
    }

    /// <summary>
    /// A note body. The documentation says <c>documentbody/$value</c> answers base64 text; the OpenProject365 trial answered the file's
    /// own bytes (M6). The body is read whole (a note is at most 128 MB) and kept as sent when its length is the note's <c>filesize</c>
    /// or it is not base64; otherwise it is decoded. A source that refuses <c>$value</c> is read as JSON (base64 text).
    /// </summary>
    private async Task<MigrationValue> NoteBodyAsync(JsonElement row, Guid id, string path, CancellationToken ct)
    {
        byte[] sent;
        try
        {
            using var response = await Missing(path, () => client.GetStreamAsync($"annotations({id})/documentbody/$value", null, ct));
            sent = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (DataverseException e) when (e.Status == HttpStatusCode.BadRequest)
        {
            JsonDocument doc;
            try
            {
                doc = await client.GetJsonAsync($"annotations({id})?$select=documentbody", ct);
            }
            catch (DataverseException missing) when (missing.Status == HttpStatusCode.NotFound)
            {
                throw new SourceFileMissingException(path, missing);
            }
            using var _ = doc;
            sent = Encoding.UTF8.GetBytes(doc.RootElement.GetProperty("documentbody").GetString() ?? "");
        }
        var content = NoteContent(sent, row.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.TryGetInt64(out var n) ? n : null);
        using (var entry = writer.CreateEntry(path))
        {
            entry.Write(content);
            entry.Commit();
        }
        return new FileValue(path, Text(row, "filename") ?? "file", Text(row, "mimetype") ?? "application/octet-stream", content.Length);
    }

    /// <summary>The file's bytes from what the source sent: as sent when that is the expected size or not base64, decoded otherwise.</summary>
    internal static byte[] NoteContent(byte[] sent, long? expected)
    {
        if (expected is { } size && size > 0 && sent.Length == size) return sent;
        var compact = sent.Where(b => b is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')).ToArray();
        var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(compact.Length)];
        return Base64.DecodeFromUtf8(compact, decoded, out _, out var written) == OperationStatus.Done ? decoded[..written] : sent;
    }

    private async Task CopyAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        using var entry = writer.CreateEntry(path);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(entry, ct);
        entry.Commit();
    }

    private static async Task<long> CopyCountingAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }
        return total;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
