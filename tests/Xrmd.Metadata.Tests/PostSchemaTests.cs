using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 3.5 UI-1g (posts), task 1: the seed definitions of <c>post</c>, <c>postcomment</c> and <c>postregarding</c> (Global
/// Constraints), their privilege stem, the relationship of <c>postregarding.regardingobjectid</c> and the app option.
/// </summary>
public class PostSchemaTests
{
    private const int MemoMax = 1073741823;

    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static T Column<T>(EntityMetadata e, string name) where T : AttributeMetadata =>
        e.Attributes.Should().ContainSingle(a => a.LogicalName == name, name).Subject.Should().BeAssignableTo<T>(name).Subject;

    private static void ReadOnly(AttributeMetadata a) =>
        (a.IsValidForCreate, a.IsValidForUpdate).Should().Be((false, false), a.LogicalName);

    private static EntityMetadata Table(string name, string schemaName, int objectTypeCode)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = schemaName };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(true)!.Invoke(e, [objectTypeCode]);
        return e;
    }

    [Fact]
    public void Seed_defines_post_tables()
    {
        (PostSchema.Post, PostSchema.Comment, PostSchema.Regarding).Should().Be(("post", "postcomment", "postregarding"));
        (PostSchema.PostTypeCode, PostSchema.CommentTypeCode, PostSchema.RegardingTypeCode).Should().Be((8000, 8005, 8002));

        var post = Seed(PostSchema.Post);
        (post.SchemaName, post.EntitySetName, post.ObjectTypeCode, post.OwnershipType, post.PrimaryNameAttribute, post.PrimaryIdAttribute)
            .Should().Be(("Post", "posts", 8000, OwnershipTypes.OrganizationOwned, "text", "postid"));
        var text = Column<StringAttributeMetadata>(post, "text");
        (text.MaxLength, text.RequiredLevel.Value).Should().Be((2000, AttributeRequiredLevel.ApplicationRequired));
        Column<MemoAttributeMetadata>(post, "largetext").MaxLength.Should().Be(MemoMax);
        var regarding = Column<LookupAttributeMetadata>(post, "regardingobjectid");
        regarding.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        regarding.Targets.Should().BeEmpty("the metadata store maintains them");
        Column<EntityNameAttributeMetadata>(post, "regardingobjecttypecode").AttributeOf.Should().Be("regardingobjectid");
        var source = Column<PicklistAttributeMetadata>(post, "source");
        (source.OptionSet.Name, source.DefaultFormValue).Should().Be(("post_source", 2));
        source.OptionSet.Options.Select(o => (o.Value, o.Label.UserLocalizedLabel.Label))
            .Should().Equal((1, "Auto Post"), (2, "Manual Post"), (3, "ActionHub Post"));
        var type = Column<PicklistAttributeMetadata>(post, "type");
        (type.OptionSet.Name, type.DefaultFormValue).Should().Be(("post_type", 1));
        type.OptionSet.Options.Select(o => (o.Value, o.Label.UserLocalizedLabel.Label)).Should().Equal(
            (1, "Check-in"), (2, "Idea"), (3, "News"), (4, "Private Message"), (5, "Question"), (6, "Re-post"), (7, "Status"));
        var postRegarding = Column<LookupAttributeMetadata>(post, "postregardingid");
        (postRegarding.IsValidForRead, postRegarding.Targets).Should().BeEquivalentTo((false, new[] { "postregarding" }));
        ReadOnly(postRegarding);
        OwnerColumns(post);
        foreach (var name in new[] { "createdonbehalfby", "modifiedonbehalfby" })
        {
            var delegated = Column<LookupAttributeMetadata>(post, name);
            delegated.Targets.Should().Equal("systemuser");
            ReadOnly(delegated);
        }
        Column<LookupAttributeMetadata>(post, "organizationid").Targets.Should().Equal("organization");
        Column<IntegerAttributeMetadata>(post, "timezoneruleversionnumber");
        Column<IntegerAttributeMetadata>(post, "utcconversiontimezonecode");
        var all = SystemAttributes.Complete(post, post.Attributes).Select(a => a.LogicalName).ToList();
        all.Should().Contain(["postid", "createdon", "createdby", "modifiedon", "modifiedby"]);

        var comment = Seed(PostSchema.Comment);
        (comment.SchemaName, comment.EntitySetName, comment.ObjectTypeCode, comment.OwnershipType, comment.PrimaryNameAttribute)
            .Should().Be(("PostComment", "postcomments", 8005, OwnershipTypes.OrganizationOwned, "text"));
        var commentText = Column<StringAttributeMetadata>(comment, "text");
        (commentText.MaxLength, commentText.RequiredLevel.Value).Should().Be((1000, AttributeRequiredLevel.ApplicationRequired));
        Column<MemoAttributeMetadata>(comment, "largetext");
        var postId = Column<LookupAttributeMetadata>(comment, "postid");
        (postId.Targets, postId.RequiredLevel.Value).Should().BeEquivalentTo((new[] { "post" }, AttributeRequiredLevel.SystemRequired));
        Column<LookupAttributeMetadata>(comment, "createdonbehalfby").Targets.Should().Equal("systemuser");
        Column<LookupAttributeMetadata>(comment, "organizationid").Targets.Should().Equal("organization");
        comment.Attributes.Select(a => a.LogicalName).Should().NotContain(["modifiedon", "modifiedby", "modifiedonbehalfby"]);
        var commentColumns = SystemAttributes.Complete(comment, comment.Attributes).Select(a => a.LogicalName).ToList();
        commentColumns.Should().Contain(["postcommentid", "createdon", "createdby"]).And.NotContain(["modifiedon", "modifiedby"]);

        var regardingTable = Seed(PostSchema.Regarding);
        (regardingTable.SchemaName, regardingTable.EntitySetName, regardingTable.ObjectTypeCode, regardingTable.OwnershipType)
            .Should().Be(("PostRegarding", "postregardings", 8002, OwnershipTypes.OrganizationOwned));
        Column<LookupAttributeMetadata>(regardingTable, "regardingobjectid").Targets.Should().BeEmpty();
        Column<EntityNameAttributeMetadata>(regardingTable, "regardingobjecttypecode").AttributeOf.Should().Be("regardingobjectid");
        Column<DateTimeAttributeMetadata>(regardingTable, "latestmanualpostmodifiedon");
        OwnerColumns(regardingTable);

        // The relationships between the three tables (Delete Cascade).
        var toPost = post.ManyToOneRelationships.Should().ContainSingle(r => r.SchemaName == "post_PostRegardings").Subject;
        (toPost.ReferencedEntity, toPost.ReferencingEntity, toPost.ReferencingAttribute, toPost.CascadeConfiguration.Delete)
            .Should().Be(("postregarding", "post", "postregardingid", CascadeType.Cascade));
        var comments = comment.ManyToOneRelationships.Should().ContainSingle(r => r.SchemaName == "Post_Comments").Subject;
        (comments.ReferencedEntity, comments.ReferencingEntity, comments.ReferencingAttribute, comments.CascadeConfiguration.Delete)
            .Should().Be(("post", "postcomment", "postid", CascadeType.Cascade));
    }

    private static void OwnerColumns(EntityMetadata e)
    {
        var owner = Column<LookupAttributeMetadata>(e, "regardingobjectownerid");
        (owner.AttributeType, owner.Targets).Should().BeEquivalentTo((AttributeTypeCode.Owner, new[] { "systemuser", "team" }));
        ReadOnly(owner);
        Column<EntityNameAttributeMetadata>(e, "regardingobjectowneridtype").AttributeOf.Should().Be("regardingobjectownerid");
        var unit = Column<LookupAttributeMetadata>(e, "regardingobjectowningbusinessunit");
        unit.Targets.Should().Equal("businessunit");
        ReadOnly(unit);
    }

    [Fact]
    public void Post_tables_share_the_post_privilege_stem_and_keep_managed_regarding_lookups()
    {
        foreach (var table in new[] { PostSchema.Post, PostSchema.Comment, PostSchema.Regarding })
            ActivitySchema.PrivilegeStem(new EntityMetadata { LogicalName = table }).Should().Be(PostSchema.PrivilegeStem, table);
        PostSchema.PrivilegeStem.Should().Be("Post");
        ActivitySchema.IsManagedLookup(PostSchema.Post, "regardingobjectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup(PostSchema.Regarding, "regardingobjectid").Should().BeTrue();
        ActivitySchema.IsManagedLookup(PostSchema.Comment, "postid").Should().BeFalse();
    }

    [Fact]
    public void Regarding_targets_follow_notes_and_activities()
    {
        // The relationship of postregarding.regardingobjectid to each target: {Stem}_PostRegardings, navigation
        // regardingobjectid_{target}, Delete Cascade and every other action NoCascade.
        var account = PostSchema.PostRegardingsRelationship(Table("account", "Account", 1));
        (account.SchemaName, account.ReferencedEntity, account.ReferencingEntity, account.ReferencingAttribute,
                account.ReferencingEntityNavigationPropertyName)
            .Should().Be(("Account_PostRegardings", "account", "postregarding", "regardingobjectid", "regardingobjectid_account"));
        var c = account.CascadeConfiguration;
        (c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare, c.Archive, c.RollupView).Should().Be((CascadeType.NoCascade,
            CascadeType.Cascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade, CascadeType.NoCascade,
            CascadeType.NoCascade, CascadeType.NoCascade));
        PostSchema.PostRegardingsRelationship(Table("new_project", "new_Project", 10042)).SchemaName.Should().Be("new_project_PostRegardings");
    }

    [Fact]
    public void Timeline_posts_setting_seeded()
    {
        // The row is inserted by SystemSeed and checked against a database in PostTablesTests; here the name and id.
        AppModuleSchema.TimelinePosts.Should().Be("xrmverse_TimelinePosts");
        AppModuleSchema.TimelinePostsId.Should().Be(new Guid("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e03"));
    }
}
