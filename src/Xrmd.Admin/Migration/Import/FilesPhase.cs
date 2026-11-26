using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Files;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 8 of an import (spec §7.6 step 7, plan M4a task 5): the file columns (uploaded through the file column service in blocks of
/// 4 MiB), the images (an update with their bytes) and the note bodies (an update of <c>documentbody</c>), streamed from the package.
/// One transaction per file, recorded in the ledger as <c>files/&lt;path&gt;</c>. The organization's upload limit does not apply in the
/// migration scope; the column's own maximum size does. A file of a record that is not in the organization is skipped.
/// </summary>
public sealed class FilesPhase : IMigrationPhase
{
    public const int PhaseOrder = 8;

    private const int BlockSize = 4 * 1024 * 1024;

    /// <summary>The package folder of note bodies: <c>files/annotation/documentbody/&lt;id&gt;</c>.</summary>
    public const string NoteBodies = "files/annotation/documentbody/";

    public int Order => PhaseOrder;

    public string Name => "files";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services;
        var metadata = services.GetRequiredService<IMetadataStore>();
        var core = services.GetRequiredService<CoreOperations>();
        var files = services.GetRequiredService<FileColumnService>();
        var package = context.Package;
        var idMap = await context.Ledger.LoadMapAsync(ct);
        var chunks = new ImportChunkWriter(services.GetRequiredService<IDbConnectionFactory>(), context.Store, context.Ledger);
        var caller = context.Caller;

        // Tables with a file or an image in the package (files/<table>/<column>/<id>): their records say the column and file name.
        var withFiles = package.Manifest.Entries.Select(e => e.Path).Where(p => p.StartsWith("files/", StringComparison.Ordinal) && !p.StartsWith(NoteBodies, StringComparison.Ordinal))
            .Select(p => Owner(package, p).Table).ToHashSet(StringComparer.Ordinal);
        foreach (var table in RecordsPhase.DataTables(package.Manifest).Where(t => withFiles.Contains(t.LogicalName) && metadata.GetEntity(t.LogicalName) is not null))
        {
            context.Progress.Report($"files: {table.LogicalName}");
            await foreach (var record in ChunkReader.ReadAsync(package, table.Chunks, ct))
                foreach (var (column, value) in record.Values)
                {
                    if (value is not (FileValue or ImageValue)) continue;
                    var path = value is FileValue f ? f.Path : ((ImageValue)value).Path;
                    var target = new EntityReference(table.LogicalName, ImportValueMapper.Map(idMap, record.Id));
                    await chunks.WriteAsync(PhaseOrder, path, table.LogicalName, [record], async (s, _, token) =>
                    {
                        if (!await ExistsAsync(s, metadata, target, token)) return;
                        if (value is FileValue file) await UploadAsync(s, files, core, caller, metadata, target, column, file, package.OpenEntry(file.Path), token);
                        else
                        {
                            await using var image = package.OpenEntry(path);
                            var entity = new Entity(target.LogicalName, target.Id) { [column] = await ReadAllAsync(image, token) };
                            await ImportRecordWriter.KeepModifiedAsync(s, metadata, entity, token);
                            await core.UpdateAsync(s, caller, entity, token);
                        }
                    }, ct);
                }
        }

        var notes = package.Manifest.Entries.Where(e => e.Path.StartsWith(NoteBodies, StringComparison.Ordinal)).ToList();
        if (notes.Count > 0) context.Progress.Report($"files: {notes.Count} note bodies");
        foreach (var note in notes)
        {
            if (!Guid.TryParse(note.Path[NoteBodies.Length..], out var id)) continue;
            var target = new EntityReference("annotation", ImportValueMapper.Map(idMap, id));
            await chunks.WriteAsync(PhaseOrder, note.Path, "annotation", [new MigrationRecord(id, new Dictionary<string, MigrationValue>())], async (s, _, token) =>
            {
                if (!await ExistsAsync(s, metadata, target, token)) return;
                await using var body = package.OpenEntry(note.Path);
                var entity = new Entity("annotation", target.Id) { ["documentbody"] = Convert.ToBase64String(await ReadAllAsync(body, token)) };
                await ImportRecordWriter.KeepModifiedAsync(s, metadata, entity, token);
                await core.UpdateAsync(s, caller, entity, token);
            }, ct);
        }
    }

    /// <summary>
    /// The table and column of the file entry <c>files/&lt;table&gt;/&lt;column&gt;/&lt;id&gt;</c> under the target names: the path keeps
    /// the source's names (it is the entry's name in the package), a map renames them (R5).
    /// </summary>
    public static (string Table, string Column) Owner(PackageReader package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/');
        var (table, column) = (parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
        return package.Translator is MapPackageTranslator { Records: var translator }
            ? (translator.TableOf(table), translator.ColumnOf(table, column))
            : (table, column);
    }

    private static async Task UploadAsync(DataSession s, FileColumnService files, CoreOperations core, Caller caller, IMetadataStore metadata, EntityReference target, string column,
        FileValue file, Stream content, CancellationToken ct)
    {
        await using var _ = content;
        var token = await files.InitializeUploadAsync(caller, target, column, file.FileName, s, ct);
        var blocks = new List<string>();
        var buffer = new byte[BlockSize];
        while (true)
        {
            var read = await content.ReadAtLeastAsync(buffer, BlockSize, throwOnEndOfStream: false, ct);
            if (read == 0 && blocks.Count > 0) break;
            var blockId = Convert.ToBase64String(BitConverter.GetBytes(blocks.Count));
            await files.UploadBlockAsync(caller, token, blockId, new MemoryStream(buffer, 0, read, writable: false), s, ct);
            blocks.Add(blockId);
            if (read < BlockSize) break;
        }
        await files.CommitAsync(caller, token, [.. blocks], file.FileName, string.IsNullOrEmpty(file.MimeType) ? "application/octet-stream" : file.MimeType, s,
            async (entity, token2) =>
            {
                await ImportRecordWriter.KeepModifiedAsync(s, metadata, entity, token2);
                await core.UpdateAsync(s, caller, entity, token2);
            }, ct);
    }

    private static async Task<bool> ExistsAsync(DataSession s, IMetadataStore metadata, EntityReference target, CancellationToken ct) =>
        (await ImportRecordBuilder.ExistingAsync(s, metadata, [(target.LogicalName, target.Id)], ct)).Count > 0;

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }
}
