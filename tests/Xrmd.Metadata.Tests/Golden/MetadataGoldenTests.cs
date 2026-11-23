using FluentAssertions;

namespace Xrmd.Metadata.Tests.Golden;

public sealed class MetadataGoldenTests
{
    private const string UpdateVariable = "XRMD_UPDATE_GOLDEN";

    [Fact]
    public void Golden_files_match_the_current_serializer()
    {
        var generated = MetadataCorpus.Generate();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            var root = MetadataCorpus.SourceDirectory;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (var (name, _, json) in generated)
            {
                var path = Path.Combine(root, name + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            Assert.Fail("golden files written: review and commit them");
        }

        MetadataCorpus.Entries.Select(e => e.Name).Should().Equal(generated.Select(g => g.Name), "no golden file is missing or stale");
        var byName = MetadataCorpus.Entries.ToDictionary(e => e.Name);
        foreach (var (name, root, json) in generated)
        {
            var entry = byName[name];
            entry.Root.Should().Be(root, name);
            entry.Json.Should().Be(json, name);
        }
    }

    [Fact]
    public void Generate_is_deterministic_within_one_process()
    {
        MetadataCorpus.Generate().Should().Equal(MetadataCorpus.Generate());
    }

    // Known gaps of the reflection fill, each with its reason. Anything else that cannot be filled fails the tests.
    private static readonly string[] KnownSkipped =
    [
        // Entity is a data record, not metadata; it is reached through EntitySetting.Value and left null.
        "EntitySetting.Value: Entity (a data record) is not filled",
        "EntitySetting.Value: no value for Entity",
        // EntityReference.KeyAttributes is a dictionary type; it stays empty.
        "KeyAttributeCollection.Count: read-only, not a mutable collection",
        "KeyAttributeCollection.Keys: TargetInvocationException: Mutating a key collection derived from a dictionary is not allowed.",
    ];

    // Public properties of a corpus type that do not appear in its full file, each with its reason.
    private static readonly string[] KnownMissingKeys = [];

    [Fact]
    public void Nothing_is_skipped_except_the_known_gaps()
    {
        MetadataCorpus.Generate();
        MetadataCorpus.Skipped.Distinct().Order().Should().Equal(KnownSkipped.Order());
        // A type that cannot be instantiated drops its files: each of the 28 types has a full, an .empty and an .omitted file.
        MetadataCorpus.Generate().Count(e => e.Name.StartsWith("full/", StringComparison.Ordinal)).Should().Be(28 * 3);
    }

    [Fact]
    public void Every_public_property_appears_in_the_full_file()
    {
        var sdk = typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).Assembly;
        var missing = new List<string>();
        foreach (var (name, _, json) in MetadataCorpus.Entries.Where(e => e.Name.StartsWith("full/", StringComparison.Ordinal) && e.Name.Count(c => c == '.') == 0))
        {
            var typeName = name["full/".Length..];
            var type = sdk.GetType("Microsoft.Xrm.Sdk.Metadata." + typeName)!;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Option types are wrapped in an OptionSetMetadata: look at the wrapped option.
            if (typeof(Microsoft.Xrm.Sdk.Metadata.OptionMetadata).IsAssignableFrom(type)) root = root.GetProperty("Options")[0];
            var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() is null) continue;
                if (property.PropertyType.Name == "ExtensionDataObject") continue;
                if (!keys.Contains(property.Name)) missing.Add($"{typeName}.{property.Name}");
            }
        }
        missing.Order().Should().Equal(KnownMissingKeys.Order());
    }
}
