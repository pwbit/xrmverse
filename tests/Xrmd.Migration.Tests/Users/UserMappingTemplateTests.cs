using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;
using Xrmd.Migration.Users;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Migration.Tests.Users;

public sealed class UserMappingTemplateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Guid Anna = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bruno = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Flow = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Carla = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    private static readonly Guid System = Guid.Parse("00000000-0000-0000-0000-0000000000a5");
    private static readonly Guid Dario = Guid.Parse("00000000-0000-0000-0000-0000000000a6");

    private static MigrationRecord User(Guid id, string name, string upn, Guid? objectId, bool disabled, int accessMode) => new(id, new Dictionary<string, MigrationValue>
    {
        ["fullname"] = new StringValue(name),
        ["domainname"] = new StringValue(upn),
        ["azureactivedirectoryobjectid"] = objectId is { } o ? new GuidValue(o) : NullValue.Instance,
        ["isdisabled"] = new BoolValue(disabled),
        ["accessmode"] = new OptionValue(accessMode),
    });

    private string Package()
    {
        var folder = Path.Combine(_root, "pkg");
        using var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase);
        IReadOnlyList<string> chunks;
        using (var chunk = new ChunkWriter(writer, "security/systemuser"))
        {
            chunk.Add(User(Anna, "Anna \"Rossi\": admin", "anna@example.com", Guid.Parse("11111111-0000-0000-0000-000000000001"), false, 0));
            chunk.Add(User(Bruno, "Bruno Bianchi", "bruno@example.com", Guid.Parse("11111111-0000-0000-0000-000000000002"), true, 0));
            chunk.Add(User(Flow, "# Flow app", "flow@example.com", Guid.Parse("11111111-0000-0000-0000-000000000003"), false, 4));
            chunk.Add(User(Carla, "Carla Verdi", "carla@contoso.local", null, false, 0));
            chunk.Add(new MigrationRecord(System, new Dictionary<string, MigrationValue> { ["fullname"] = new StringValue("SYSTEM"), ["domainname"] = NullValue.Instance }));
            chunk.Add(new MigrationRecord(Dario, new Dictionary<string, MigrationValue>(User(Dario, "Dario", "dario@example.com", Guid.NewGuid(), false, 0).Values)
                { ["islicensed"] = new BoolValue(false) }));
            chunk.Complete();
            chunks = chunk.Chunks;
        }
        writer.Complete(new PackageManifest { Tables = [new PackageTable("systemuser", "systemuserid", 6, chunks)] });
        return folder;
    }

    [Fact]
    public async Task Users_template_defaults_by_kind()
    {
        using var reader = PackageReader.Open(Package(), SyntheticPackage.Passphrase);
        var output = new StringWriter();
        await UserMappingTemplate.WriteAsync(reader, output, CancellationToken.None);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(output.ToString()));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var defaults = (YamlMappingNode)root["defaults"];
        defaults["licensed"].ToString().Should().Be("entra");
        var users = ((YamlSequenceNode)root["users"]).Cast<YamlMappingNode>().ToDictionary(u => Guid.Parse(u["id"].ToString()));
        users[Anna]["action"].ToString().Should().Be("entra");
        users[Anna]["name"].ToString().Should().Be("Anna \"Rossi\": admin");
        users[Bruno]["action"].ToString().Should().Be("stub");
        users[Flow]["action"].ToString().Should().Be("stub");
        users[Flow]["name"].ToString().Should().Be("# Flow app");
        users[Carla]["action"].ToString().Should().Be("local");
        users[Carla]["username"].ToString().Should().Be("carla");
        users.Should().NotContainKey(System, "SYSTEM is mapped onto the target's system user");
        users[Dario]["action"].ToString().Should().Be("stub");
        output.ToString().Should().Contain("anna@example.com");
    }

    [Fact]
    public async Task Package_without_users_gives_an_empty_list()
    {
        var folder = Path.Combine(_root, "empty");
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase)) writer.Complete(new PackageManifest());
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var output = new StringWriter();
        await UserMappingTemplate.WriteAsync(reader, output, CancellationToken.None);
        output.ToString().Should().Contain("users: []");
    }
}
