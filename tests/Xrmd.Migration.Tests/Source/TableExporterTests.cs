using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class TableExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly SourceTable Account = new("account", "accounts", "accountid", false, false, false,
    [
        new("accountid", "Uniqueidentifier", null, [], false, true),
        new("name", "String", null, [], false, true),
        new("xv_projectid", "Lookup", null, ["msdyn_project"], true, true),
        new("xv_contract", "File", null, [], true, true),
        new("xv_parties", "PartyList", null, [], true, true),
    ]);

    private static Guid IdOf(int i) => new(i, 0, 0, new byte[8]);

    /// <summary>Serves <paramref name="count"/> accounts with keyset paging on the id, 5000 per page.</summary>
    private static FakeDataverse Accounts(int count, int? failOnPage = null)
    {
        var page = 0;
        return new FakeDataverse().Get(@"^/api/data/v9\.2/accounts\?fetchXml=(.*)$", (_, m) =>
        {
            page++;
            if (page == failOnPage) return FakeDataverse.Json("""{"error":{"message":"bad"}}""", HttpStatusCode.BadRequest);
            var gt = Regex.Match(m.Groups[1].Value, "operator=\"gt\" value=\"([^\"]+)\"");
            var start = gt.Success ? BitConverter.ToInt32(Guid.Parse(gt.Groups[1].Value).ToByteArray(), 0) + 1 : 0;
            var rows = Enumerable.Range(start, Math.Max(0, Math.Min(5000, count - start))).Select(i => i == 7
                ? $$"""{"accountid":"{{IdOf(i)}}","name":"Row {{i}}","_xv_projectid_value":"{{IdOf(99999)}}","_xv_projectid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"msdyn_project"}"""
                : $$"""{"accountid":"{{IdOf(i)}}","name":"Row {{i}}"}""");
            return FakeDataverse.Json($$"""{"value":[{{string.Join(',', rows)}}]}""");
        });
    }

    private sealed class FakeFiles : IFileExporter
    {
        public List<(string Table, string Column, Guid Id)> Calls { get; } = [];

        public Task<MigrationValue?> ExportAsync(SourceTable table, SourceColumn column, Guid id, JsonElement row, CancellationToken ct)
        {
            Calls.Add((table.LogicalName, column.LogicalName, id));
            return Task.FromResult<MigrationValue?>(new FileValue($"files/{table.LogicalName}/{column.LogicalName}/{id}", "a.bin", "application/octet-stream", 1));
        }
    }

    private static DataverseClient Client(FakeDataverse fake) => new(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    private async Task<List<MigrationRecord>> ReadBack(string folder, IReadOnlyList<string> chunks)
    {
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var records = new List<MigrationRecord>();
        await foreach (var r in ChunkReader.ReadAsync(reader, chunks, CancellationToken.None)) records.Add(r);
        return records;
    }

    [Fact]
    public async Task Pages_with_keyset_and_writes_chunks()
    {
        var folder = Path.Combine(_root, "pkg");
        var fake = Accounts(12_001);
        TableProgress result;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            result = await new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", null, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        result.Done.Should().BeTrue();
        result.Records.Should().Be(12_001);
        result.Chunks.Should().Equal("data/account/00001.jsonl.gz", "data/account/00002.jsonl.gz", "data/account/00003.jsonl.gz");
        var fetches = fake.Requests.Select(r => r.Url).ToList();
        fetches.Should().HaveCount(3);
        fetches[0].Should().Contain("<all-attributes/>").And.Contain("<order attribute=\"accountid\"/>").And.Contain("count=\"5000\"").And.NotContain("operator=\"gt\"");
        fetches[1].Should().Contain($"operator=\"gt\" value=\"{IdOf(4999)}\"");
        fetches[2].Should().Contain($"operator=\"gt\" value=\"{IdOf(9999)}\"");
        (await ReadBack(folder, result.Chunks)).Select(r => r.Id).Should().Equal(Enumerable.Range(0, 12_001).Select(IdOf));
    }

    [Fact]
    public async Task Resume_continues_after_last_id_and_numbering()
    {
        var folder = Path.Combine(_root, "pkg");
        var checkpoints = new List<TableProgress>();
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(Accounts(12_001, failOnPage: 3)))
        {
            var run = () => new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", null, checkpoints.Add, CancellationToken.None);
            await run.Should().ThrowAsync<DataverseException>();
        }
        var resume = checkpoints[^1];
        resume.Chunks.Should().HaveCount(2);
        resume.LastId.Should().Be(IdOf(9999));
        resume.Done.Should().BeFalse();

        var fake = Accounts(12_001);
        TableProgress result;
        using (var writer = PackageWriter.OpenForResume(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            result = await new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", resume, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        fake.Requests.Should().ContainSingle().Which.Url.Should().Contain($"operator=\"gt\" value=\"{IdOf(9999)}\"");
        result.Chunks.Should().Equal("data/account/00001.jsonl.gz", "data/account/00002.jsonl.gz", "data/account/00003.jsonl.gz");
        result.Records.Should().Be(12_001);
        var ids = (await ReadBack(folder, result.Chunks)).Select(r => r.Id).ToList();
        ids.Should().HaveCount(12_001).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Done_table_is_not_read_again()
    {
        var fake = Accounts(10);
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(fake);
        var done = new TableProgress("account", IdOf(9), ["data/account/00001.jsonl.gz"], 10, true);
        (await new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", done, _ => { }, CancellationToken.None))
            .Should().Be(done);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Lookup_to_excluded_target_is_kept_and_counted()
    {
        var folder = Path.Combine(_root, "pkg");
        TableExporter exporter;
        TableProgress result;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(Accounts(10)))
        {
            exporter = new TableExporter(client, writer, new FakeFiles());
            result = await exporter.ExportAsync(Account, "data/account", null, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        exporter.LookupTargetsOutsideScope.Should().Equal(new Dictionary<string, long> { ["xv_projectid"] = 1 });
        (await ReadBack(folder, result.Chunks)).Single(r => r.Id == IdOf(7)).Values["xv_projectid"]
            .Should().Be(new LookupValue("msdyn_project", IdOf(99999)));
    }

    [Fact]
    public async Task Unsupported_columns_are_reported_and_not_written()
    {
        var folder = Path.Combine(_root, "pkg");
        TableExporter exporter;
        TableProgress result;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(Accounts(1)))
        {
            exporter = new TableExporter(client, writer, new FakeFiles());
            result = await exporter.ExportAsync(Account, "data/account", null, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        exporter.SkippedColumns.Should().BeEquivalentTo(["xv_parties"]);
        (await ReadBack(folder, result.Chunks)).Single().Values.Should().NotContainKey("xv_parties");
    }

    [Fact]
    public async Task Checkpoint_is_called_after_each_committed_chunk()
    {
        var folder = Path.Combine(_root, "pkg");
        var checkpoints = new List<(TableProgress Progress, bool Committed)>();
        using var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase);
        using var client = Client(Accounts(12_001));
        await new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", null,
            p => checkpoints.Add((p, p.Chunks.All(writer.HasEntry))), CancellationToken.None);
        checkpoints.Select(c => c.Progress.Chunks.Count).Should().Equal(1, 2, 3, 3);
        checkpoints.Should().OnlyContain(c => c.Committed);
        checkpoints[^1].Progress.Done.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_table_has_no_chunk()
    {
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(Accounts(0));
        var result = await new TableExporter(client, writer, new FakeFiles()).ExportAsync(Account, "data/account", null, _ => { }, CancellationToken.None);
        result.Should().BeEquivalentTo(new TableProgress("account", null, [], 0, true));
    }

    [Fact]
    public async Task File_columns_and_note_bodies_go_through_the_file_exporter()
    {
        var note = new SourceTable("annotation", "annotations", "annotationid", false, false, false,
        [
            new("annotationid", "Uniqueidentifier", null, [], false, true),
            new("subject", "String", null, [], false, true),
            new("isdocument", "Boolean", null, [], false, true),
            new("documentbody", "Memo", null, [], false, true),
        ]);
        var fake = new FakeDataverse().GetJson(@"^/api/data/v9\.2/annotations\?fetchXml=", $$"""
            {"value":[{"annotationid":"{{IdOf(1)}}","subject":"with file","isdocument":true},{"annotationid":"{{IdOf(2)}}","subject":"text only","isdocument":false}]}
            """);
        var files = new FakeFiles();
        var folder = Path.Combine(_root, "pkg");
        TableProgress result;
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = Client(fake))
        {
            result = await new TableExporter(client, writer, files).ExportAsync(note, "data/annotation", null, _ => { }, CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        var fetch = fake.Requests.Single().Url;
        fetch.Should().NotContain("all-attributes").And.NotContain("documentbody").And.Contain("<attribute name=\"subject\"/>");
        files.Calls.Should().Equal(("annotation", "documentbody", IdOf(1)));
        var records = await ReadBack(folder, result.Chunks);
        records[0].Values["documentbody"].Should().BeOfType<FileValue>();
        records[1].Values["documentbody"].Should().Be(NullValue.Instance);

        var withFile = new FakeDataverse().GetJson(@"^/api/data/v9\.2/accounts\?fetchXml=", $$"""
            {"value":[{"accountid":"{{IdOf(1)}}","xv_contract":"{{IdOf(5)}}"},{"accountid":"{{IdOf(2)}}"}]}
            """);
        var accountFiles = new FakeFiles();
        using (var writer = PackageWriter.Create(Path.Combine(_root, "pkg2"), SyntheticPackage.Passphrase))
        using (var client = Client(withFile))
            await new TableExporter(client, writer, accountFiles).ExportAsync(Account, "data/account", null, _ => { }, CancellationToken.None);
        accountFiles.Calls.Should().Equal(("account", "xv_contract", IdOf(1)));
    }
}
