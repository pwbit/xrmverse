using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The seeded tables of the timeline posts (plan 3.5 UI-1g, posts): <c>post</c>, <c>postcomment</c> and the technical
/// <c>postregarding</c>, defined in <c>fixtures/seed</c>. Names, column types and lengths follow the Learn table references.
/// The three tables share the Post privileges (stem <see cref="PrivilegeStem"/>). The targets of <c>post.regardingobjectid</c>
/// and <c>postregarding.regardingobjectid</c> are maintained by the metadata store, as for <c>annotation.objectid</c>: every table
/// with notes or activities that is not an intersect table. Only <c>postregarding</c> has a relationship per target
/// (<see cref="PostRegardingsRelationship"/>); <c>post.regardingobjectid</c> has none, as on Learn.
/// </summary>
/// <remarks>
/// The object type codes (8000, 8005, 8002) and the privilege names are from memory, not from Learn: they go on the acceptance
/// checklist, as in plan 2.11.
/// </remarks>
public static class PostSchema
{
    /// <summary>The tables.</summary>
    public const string Post = "post", Comment = "postcomment", Regarding = "postregarding";

    /// <summary>The object type codes of the tables (from memory: acceptance checklist).</summary>
    public const int PostTypeCode = 8000, CommentTypeCode = 8005, RegardingTypeCode = 8002;

    /// <summary>The privilege stem the three tables share: <c>prvCreatePost</c>, <c>prvReadPost</c>, <c>prvDeletePost</c>.</summary>
    public const string PrivilegeStem = "Post";

    /// <summary>The columns.</summary>
    public const string PostId = "postid", CommentId = "postcommentid", RegardingId = "postregardingid", Text = "text",
        LargeText = "largetext", Source = "source", Type = "type", LatestManualPostModifiedOn = "latestmanualpostmodifiedon";

    /// <summary>The relationships between the three tables (Delete Cascade).</summary>
    public const string PostToRegarding = "post_PostRegardings", CommentsOfPost = "Post_Comments";

    /// <summary>The refusals of the server rules (plan 3.5 UI-1g, Global Constraints): all 400.</summary>
    public const string NotUpdatable = "Posts and comments cannot be updated",
        ServerMaintained = "postregarding is maintained by the server",
        AutoPostsRefused = "Auto posts are not supported in this version";

    /// <summary>The <c>source</c> values of auto posts (1 Auto Post, 3 ActionHub Post), refused on Create.</summary>
    public static bool IsAutoSource(int source) => source is 1 or 3;

    /// <summary>True for one of the three tables.</summary>
    public static bool IsPostTable(string? logicalName) => logicalName is Post or Comment or Regarding;

    /// <summary>
    /// The relationship of <c>postregarding.regardingobjectid</c> to <paramref name="target"/>: <c>{Stem}_PostRegardings</c>
    /// (<c>Account_PostRegardings</c> for a seeded table, <c>{logicalname}_PostRegardings</c> for a custom one), navigation
    /// <c>regardingobjectid_{target}</c>, Delete Cascade and every other action NoCascade (deleting the record deletes its
    /// <c>postregarding</c> row and so its posts).
    /// </summary>
    public static OneToManyRelationshipMetadata PostRegardingsRelationship(EntityMetadata target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ActivitySchema.Relationship($"{ActivitySchema.RelationshipStem(target)}_PostRegardings", target.LogicalName, Regarding,
            ActivitySchema.RegardingObjectId, $"{ActivitySchema.RegardingObjectId}_{target.LogicalName}",
            ActivitySchema.Cascade(delete: CascadeType.Cascade));
    }
}
