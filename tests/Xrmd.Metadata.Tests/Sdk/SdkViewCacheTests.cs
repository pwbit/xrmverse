using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Metadata.Tests.Golden;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Tests.Sdk;

public sealed class SdkViewCacheTests
{
    private static TableDefinition Load(string name = "tables/email") =>
        MetadataJson.DeserializeTable(MetadataCorpus.Entries.First(e => e.Name == name).Json);

    [Fact]
    public void Same_model_instance_gives_same_sdk_instance()
    {
        var table = Load();
        SdkViewCache.Of(table).Should().BeSameAs(SdkViewCache.Of(table));
    }

    [Fact]
    public void New_model_instance_gives_new_sdk_instance()
    {
        SdkViewCache.Of(Load()).Should().NotBeSameAs(SdkViewCache.Of(Load()));
    }

    [Fact]
    public void Concurrent_first_reads_give_one_instance()
    {
        var table = Load();
        var results = new object[64];
        Parallel.For(0, results.Length, i => results[i] = SdkViewCache.Of(table));
        results.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public void View_matches_bridge_output()
    {
        var table = Load();
        MetadataSerializer.Serialize(SdkViewCache.Of(table))
            .Should().Be(MetadataSerializer.Serialize(SdkMetadataBridge.ToSdk(table)));
    }

    [Fact]
    public void Column_choiceset_and_relationship_views_match_bridge_and_are_cached()
    {
        var table = Load();
        var column = table.Attributes![0];
        SdkViewCache.Of(column).Should().BeSameAs(SdkViewCache.Of(column));
        MetadataSerializer.SerializeAttribute(SdkViewCache.Of(column))
            .Should().Be(MetadataSerializer.SerializeAttribute(SdkMetadataBridge.ToSdk(column)));

        var relationship = table.ManyToOneRelationships![0];
        SdkViewCache.Of(relationship).Should().BeSameAs(SdkViewCache.Of(relationship));
        MetadataSerializer.SerializeRelationship(SdkViewCache.Of(relationship))
            .Should().Be(MetadataSerializer.SerializeRelationship(SdkMetadataBridge.ToSdk(relationship)));

        var choice = table.Attributes!.OfType<ChoiceColumnBase>().Select(c => c.OptionSet).First(o => o is not null)!;
        SdkViewCache.Of(choice).Should().BeSameAs(SdkViewCache.Of(choice));
        MetadataSerializer.SerializeOptionSet(SdkViewCache.Of(choice))
            .Should().Be(MetadataSerializer.SerializeOptionSet(SdkMetadataBridge.ToSdk(choice)));
    }

    [Fact]
    public void Cached_list_keeps_element_identity()
    {
        IReadOnlyList<TableDefinition> tables = [Load("tables/email"), Load("tables/audit")];
        var view = SdkViewCache.Of<TableDefinition, Microsoft.Xrm.Sdk.Metadata.EntityMetadata>(tables);
        view.Should().HaveCount(2);
        view[0].Should().BeSameAs(SdkViewCache.Of(tables[0]));
        view[1].Should().BeSameAs(SdkViewCache.Of(tables[1]));
        SdkViewCache.Of<TableDefinition, Microsoft.Xrm.Sdk.Metadata.EntityMetadata>(tables).Should().BeSameAs(view);
    }

    [Fact]
    public void Cached_list_is_read_only()
    {
        IReadOnlyList<TableDefinition> tables = [Load()];
        var view = SdkViewCache.Of<TableDefinition, EntityMetadata>(tables);
        view.Should().NotBeOfType<EntityMetadata[]>();
        ((ICollection<EntityMetadata>)view).IsReadOnly.Should().BeTrue();
        FluentActions.Invoking(() => ((IList<EntityMetadata>)view)[0] = new EntityMetadata()).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_list_viewed_with_another_sdk_type_is_refused_clearly()
    {
        IReadOnlyList<RelationshipDefinition> relationships = [.. Load().ManyToOneRelationships!];
        SdkViewCache.Of<RelationshipDefinition, RelationshipMetadataBase>(relationships);

        FluentActions.Invoking(() => SdkViewCache.Of<RelationshipDefinition, OneToManyRelationshipMetadata>(relationships))
            .Should().Throw<InvalidOperationException>().WithMessage("*converted before*OneToManyRelationshipMetadata*");
    }

    [Fact]
    public void Model_of_an_sdk_instance_is_cached_and_its_view_is_that_instance()
    {
        var entity = SdkMetadataBridge.ToSdk(Load());
        var model = SdkViewCache.ModelOf(entity);
        SdkViewCache.ModelOf(entity).Should().BeSameAs(model);
        SdkViewCache.Of(model).Should().BeSameAs(entity);
        MetadataJson.Serialize(model).Should().Be(MetadataSerializer.Serialize(entity));

        var optionSet = SdkViewCache.Of(Load().Attributes!.OfType<ChoiceColumnBase>().Select(c => c.OptionSet).First(o => o is not null)!);
        SdkViewCache.Of(SdkViewCache.ModelOf(optionSet)).Should().BeSameAs(optionSet);
        var relationship = SdkMetadataBridge.ToSdk(Load().ManyToOneRelationships![0]);
        SdkViewCache.Of(SdkViewCache.ModelOf(relationship)).Should().BeSameAs(relationship);
    }
}
