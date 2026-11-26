using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Tests.Mapping;

public sealed class PackageTranslationTests : IDisposable
{
    private const string Yaml = """
        version: 1
        tables:
          - source: account
            target: xv_company
            columns:
              name: xv_name
        relationships:
          - source: xv_account_contact
            target: xv_company_contact
        """;

    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContactId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Target : IMapTarget
    {
        public bool HasColumn(string table, string column) => false;
        public bool IsActivity(string table) => false;
        public bool CanBeParty(string table) => true;
        public IReadOnlyList<(int Value, string Label)> Options(string table, string column) => [];
        public IReadOnlyList<(int State, int Status, string Label)> Statuses(string table) => [];
        public (int State, int Status) DefaultStatus(string table) => (0, 1);
        public bool IsWholeNumber(string table, string column) => false;
    }

    private sealed class NoDerived : IDerivedValues
    {
        public MigrationValue? Get(string sourceTable, Guid id, string column) => null;
        public bool IsShared(string sourceTable, Guid id) => false;
    }

    private static MapPackageTranslator Translator() => new(new RecordTranslator(
        MapSet.Combine([MapReader.Parse(Yaml, "t.yaml")]),
        new SourceCatalog([new SourceCatalogTable("account", "accountid", 1, "user", false, [new SourceCatalogColumn("name", "String", [], false)])], []),
        new Target(), new NoDerived()));

    private string BuildPackage()
    {
        var folder = Path.Combine(_root, "pkg");
        using var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase);
        using var accounts = new ChunkWriter(writer, "data/account");
        accounts.Add(new MigrationRecord(AccountId, new Dictionary<string, MigrationValue> { ["name"] = new StringValue("Contoso Example") }));
        accounts.Complete();
        using var contacts = new ChunkWriter(writer, "data/contact");
        contacts.Add(new MigrationRecord(ContactId, new Dictionary<string, MigrationValue>
        {
            ["emailaddress1"] = new StringValue("anna@example.com"),
            ["parentcustomerid"] = new LookupValue("account", AccountId),
        }));
        contacts.Complete();
        writer.Complete(new PackageManifest
        {
            Tables = [new PackageTable("account", "accountid", 1, accounts.Chunks), new PackageTable("contact", "contactid", 1, contacts.Chunks)],
            Relationships = [new PackageRelationship("xv_account_contact", 1, ["nn/xv_account_contact/00001.jsonl.gz"]), new PackageRelationship("other_nn", 0, [])],
        });
        return folder;
    }

    [Fact]
    public void Manifest_shows_target_names_and_raw_manifest_keeps_source_names()
    {
        using var reader = PackageReader.Open(BuildPackage(), SyntheticPackage.Passphrase);
        reader.Manifest.Should().BeSameAs(reader.RawManifest);

        reader.Translator = Translator();

        reader.Manifest.Tables.Select(t => (t.LogicalName, t.PrimaryKey)).Should().Equal(("xv_company", "xv_companyid"), ("contact", "contactid"));
        reader.Manifest.Tables[0].Chunks.Should().Equal("data/account/00001.jsonl.gz");
        reader.Manifest.Relationships.Select(r => r.SchemaName).Should().Equal("xv_company_contact", "other_nn");
        reader.Manifest.Entries.Should().Equal(reader.RawManifest.Entries);
        reader.RawManifest.Tables.Select(t => t.LogicalName).Should().Equal("account", "contact");
        reader.RawManifest.Relationships.Select(r => r.SchemaName).Should().Equal("xv_account_contact", "other_nn");

        reader.Translator = null;
        reader.Manifest.Should().BeSameAs(reader.RawManifest);
    }

    [Fact]
    public async Task ChunkReader_translates_records_of_mapped_and_other_tables()
    {
        using var reader = PackageReader.Open(BuildPackage(), SyntheticPackage.Passphrase);
        reader.Translator = Translator();

        var account = await ReadAll(reader, "data/account/00001.jsonl.gz");
        account.Id.Should().Be(AccountId);
        account.Values.Should().ContainKey("xv_name").WhoseValue.Should().Be(new StringValue("Contoso Example"));
        account.Values.Should().NotContainKey("name");

        var contact = await ReadAll(reader, "data/contact/00001.jsonl.gz");
        contact.Values["emailaddress1"].Should().Be(new StringValue("anna@example.com"));
        contact.Values["parentcustomerid"].Should().Be(new LookupValue("xv_company", AccountId));

        reader.Translator = null;
        (await ReadAll(reader, "data/account/00001.jsonl.gz")).Values.Should().ContainKey("name");
    }

    [Fact]
    public void Nn_and_unknown_chunks_pass_through()
    {
        var record = new MigrationRecord(AccountId, new Dictionary<string, MigrationValue> { ["name"] = new StringValue("x") });
        Translator().Translate("nn/xv_account_contact/00001.jsonl.gz", record).Should().BeSameAs(record);
        Translator().Translate("data/account/00001.jsonl.gz", record).Values.Should().ContainKey("xv_name");
        Translator().Translate("security/account/00001.jsonl.gz", record).Values.Should().ContainKey("xv_name");
    }

    [Fact]
    public async Task Verify_still_checks_raw_entries()
    {
        using var reader = PackageReader.Open(BuildPackage(), SyntheticPackage.Passphrase);
        reader.Translator = Translator();

        var result = await reader.VerifyAsync(null, CancellationToken.None);

        result.Problems.Should().BeEmpty();
        result.EntriesChecked.Should().Be(reader.RawManifest.Entries.Count).And.BeGreaterThan(0);
    }

    private static async Task<MigrationRecord> ReadAll(PackageReader reader, string chunk)
    {
        var records = new List<MigrationRecord>();
        await foreach (var r in ChunkReader.ReadAsync(reader, [chunk], CancellationToken.None)) records.Add(r);
        return records.Single();
    }
}
