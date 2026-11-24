using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1g (posts), task 1: the seeded tables <c>post</c>, <c>postcomment</c> and <c>postregarding</c>, the targets of their
/// <c>regardingobjectid</c> columns (the tables with notes or activities), the Post privileges of Basic User and the
/// <c>xrmverse_TimelinePosts</c> app option.
/// </summary>
[Collection("xrmd")]
public sealed class PostTablesTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    private string[] Targets(string entity, string attribute) =>
        Store.GetEntity(entity)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == attribute).Targets;

    /// <summary>
    /// The saved relationships of a lookup (<c>meta.relationship</c>): the store also lists one implied relationship per target of any
    /// lookup (<c>post_regardingobjectid_account</c>), which is not a relationship of the organization.
    /// </summary>
    private async Task<int> SavedRelationshipCountAsync(string entity, string attribute) => Convert.ToInt32(await ScalarAsync(
        "SELECT COUNT(*) FROM meta.relationship WHERE JSON_VALUE(definition, '$.ReferencingEntity') = @entity " +
        "AND JSON_VALUE(definition, '$.ReferencingAttribute') = @attribute", ("@entity", entity), ("@attribute", attribute)));

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<List<string>> ColumnsAsync(string table)
    {
        var columns = new List<string>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT column_name FROM information_schema.columns WHERE CONCAT(table_schema, '.', table_name) = CAST(@t AS varchar(300))");
        cmd.Parameters.AddWithValue("@t", $"data.{table}");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) columns.Add(rd.GetString(0));
        return columns;
    }

    [Fact]
    public async Task Seed_defines_post_tables()
    {
        var post = Store.GetEntity(PostSchema.Post)!;
        (post.ObjectTypeCode, post.EntitySetName, post.PrimaryIdAttribute, post.PrimaryNameAttribute, post.OwnershipType)
            .Should().Be((8000, "posts", "postid", "text", OwnershipTypes.OrganizationOwned));
        var comment = Store.GetEntity(PostSchema.Comment)!;
        (comment.ObjectTypeCode, comment.EntitySetName, comment.PrimaryIdAttribute, comment.PrimaryNameAttribute)
            .Should().Be((8005, "postcomments", "postcommentid", "text"));
        comment.Attributes.Select(a => a.LogicalName).Should().NotContain(["modifiedon", "modifiedby"]);
        var regarding = Store.GetEntity(PostSchema.Regarding)!;
        (regarding.ObjectTypeCode, regarding.EntitySetName, regarding.PrimaryIdAttribute).Should().Be((8002, "postregardings", "postregardingid"));

        (await ColumnsAsync(PostSchema.Post)).Should().Contain(["postid", "text", "largetext", "regardingobjectid", "regardingobjectidtype",
            "source", "type", "postregardingid", "regardingobjectownerid", "regardingobjectowneridtype", "regardingobjectowningbusinessunit",
            "createdon", "createdby", "modifiedon", "modifiedby", "createdonbehalfby", "modifiedonbehalfby", "organizationid"]);
        (await ColumnsAsync(PostSchema.Comment)).Should().Contain(["postcommentid", "text", "largetext", "postid", "createdon", "createdby",
            "createdonbehalfby", "organizationid"]).And.NotContain(["modifiedon", "modifiedby"]);
        (await ColumnsAsync(PostSchema.Regarding)).Should().Contain(["postregardingid", "regardingobjectid", "regardingobjectidtype",
            "regardingobjectownerid", "regardingobjectowneridtype", "regardingobjectowningbusinessunit", "latestmanualpostmodifiedon"]);

        var toPost = Store.GetRelationship("post_PostRegardings").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (toPost.ReferencedEntity, toPost.ReferencingEntity, toPost.CascadeConfiguration.Delete).Should().Be(("postregarding", "post", CascadeType.Cascade));
        var comments = Store.GetRelationship("Post_Comments").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (comments.ReferencedEntity, comments.ReferencingEntity, comments.CascadeConfiguration.Delete).Should().Be(("post", "postcomment", CascadeType.Cascade));
    }

    [Fact]
    public async Task Regarding_targets_follow_notes_and_activities()
    {
        // The seeded tables with notes or activities are targets of both lookups.
        foreach (var holder in new[] { PostSchema.Post, PostSchema.Regarding })
            Targets(holder, "regardingobjectid").Should().Contain(["account", "contact", "email", "task", "phonecall", "appointment"], holder);
        var account = Store.GetRelationship("Account_PostRegardings").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (account.ReferencedEntity, account.ReferencingEntity, account.ReferencingAttribute, account.ReferencingEntityNavigationPropertyName)
            .Should().Be(("account", "postregarding", "regardingobjectid", "regardingobjectid_account"));
        account.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        account.CascadeConfiguration.Assign.Should().Be(CascadeType.NoCascade);
        (await SavedRelationshipCountAsync(PostSchema.Post, "regardingobjectid")).Should().Be(0, "post.regardingobjectid has no relationships, as on Learn");
        (await SavedRelationshipCountAsync(PostSchema.Regarding, "regardingobjectid")).Should().BeGreaterThanOrEqualTo(6);

        // A new custom table with notes joins both lookups; only postregarding gains a relationship.
        var name = Name("pt");
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(name, name, "Table " + name),
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        Targets(PostSchema.Post, "regardingobjectid").Should().NotContain(name);
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, HasNotes = true } });

        Targets(PostSchema.Post, "regardingobjectid").Should().Contain(name);
        Targets(PostSchema.Regarding, "regardingobjectid").Should().Contain(name);
        var custom = Store.GetRelationship($"{name}_PostRegardings").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (custom.ReferencedEntity, custom.ReferencingEntity, custom.ReferencingEntityNavigationPropertyName)
            .Should().Be((name, "postregarding", $"regardingobjectid_{name}"));
        (await SavedRelationshipCountAsync(PostSchema.Post, "regardingobjectid")).Should().Be(0);

        // The table-delete guard counts the posts regarding its records.
        var record = await Admin.CreateAsync(new Entity(name) { ["new_name"] = "P" });
        var code = Store.GetEntity(name)!.ObjectTypeCode!.Value;
        var post = Guid.NewGuid();
        await ScalarAsync("INSERT INTO data.post (postid, text, source, type, regardingobjectid, regardingobjectidtype) VALUES (@id, N'Hi', 2, 7, @record, @code)",
            ("@id", post), ("@record", record), ("@code", code));
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name }))).Should().Contain("1 posts");
        await ScalarAsync("DELETE FROM data.post WHERE postid = @id", ("@id", post));

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
        Targets(PostSchema.Post, "regardingobjectid").Should().NotContain(name);
        Targets(PostSchema.Regarding, "regardingobjectid").Should().NotContain(name);
        Store.GetRelationship($"{name}_PostRegardings").Should().BeNull();
    }

    /// <summary>
    /// A table with activities only (no notes) is a target of both lookups; dropping a target table deletes the
    /// <c>postregarding</c> rows of its records (<c>ActivityStore.DeletePostRegardingRowsAsync</c>), once its posts are gone.
    /// </summary>
    [Fact]
    public async Task Activities_only_table_is_a_target_and_dropping_it_deletes_its_postregarding_rows()
    {
        var name = Name("pa");
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(name, name, "Table " + name),
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, HasActivities = true } });
        Store.GetEntity(name)!.HasNotes.Should().NotBe(true);
        Targets(PostSchema.Post, "regardingobjectid").Should().Contain(name);
        Targets(PostSchema.Regarding, "regardingobjectid").Should().Contain(name);
        Store.GetRelationship($"{name}_PostRegardings").Should().NotBeNull();

        var record = await Admin.CreateAsync(new Entity(name) { ["new_name"] = "P" });
        var post = await Admin.CreateAsync(new Entity(PostSchema.Post) { ["text"] = "Hi", ["regardingobjectid"] = new EntityReference(name, record) });
        (await ScalarAsync("SELECT COUNT(*) FROM data.postregarding WHERE regardingobjectid = @id", ("@id", record))).Should().Be(1);
        // A post's Delete leaves the record's postregarding row; only the record's Delete (or its table's) removes it.
        await Admin.DeleteAsync(PostSchema.Post, post);
        (await ScalarAsync("SELECT COUNT(*) FROM data.postregarding WHERE regardingobjectid = @id", ("@id", record))).Should().Be(1);

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
        (await ScalarAsync("SELECT COUNT(*) FROM data.postregarding WHERE regardingobjectid = @id", ("@id", record))).Should().Be(0);
        Targets(PostSchema.Regarding, "regardingobjectid").Should().NotContain(name);
    }

    private (string Create, string Read, string Delete) PostPrivileges()
    {
        var post = Store.GetEntity(PostSchema.Post)!;
        return (PrivilegeNames.For(AccessRights.CreateAccess, post), PrivilegeNames.For(AccessRights.ReadAccess, post),
            PrivilegeNames.For(AccessRights.DeleteAccess, post));
    }

    [Fact]
    public async Task Basic_user_has_post_privileges()
    {
        var (create, read, delete) = PostPrivileges();
        (create, read, delete).Should().Be(("prvCreatePost", "prvReadPost", "prvDeletePost"));
        foreach (var table in new[] { PostSchema.Comment, PostSchema.Regarding })
            PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity(table)!).Should().Be("prvReadPost", table);

        // The catalog: the three Post privileges, Global only, linked to post.
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN (N'prvCreatePost', N'prvReadPost', N'prvDeletePost')")).Should().Be(3);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN (N'prvWritePost', N'prvAppendPost', N'prvAppendToPost', " +
            "N'prvAssignPost', N'prvSharePost')")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name IN (N'prvCreatePost', N'prvReadPost', N'prvDeletePost') " +
            "AND canbeglobal = 1 AND canbebasic = 0")).Should().Be(3);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes l JOIN data.privilege p ON p.privilegeid = l.privilegeid " +
            "WHERE p.name IN (N'prvCreatePost', N'prvReadPost', N'prvDeletePost') AND l.objecttypecode = N'post'")).Should().Be(3);

        foreach (var name in new[] { create, read, delete })
            (await ScalarAsync($"SELECT privilegedepthmask FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf(name)}'"))
                .Should().Be(8, name);
    }

    /// <summary>An organization from before the posts (no Post grants, no top-up recorded): startup grants them once.</summary>
    [Fact]
    public async Task Post_privileges_top_up_reaches_an_existing_organization_once()
    {
        const string step = "post-privileges";
        var (create, read, delete) = PostPrivileges();
        var ids = string.Join(", ", new[] { create, read, delete }.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({ids})";
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        await ScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}; DELETE FROM meta.upgrade_step WHERE name = N'{step}';");
        try
        {
            await bootstrap.EnsureAsync(default);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegedepthmask = 8")).Should().Be(3);
            (await ScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            await ScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}");
            await bootstrap.EnsureAsync(default);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(0);
        }
        finally
        {
            foreach (var name in new[] { create, read, delete })
                await ScalarAsync($"""
                    IF NOT EXISTS (SELECT 1 FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegeid = '{PrivilegeNames.IdOf(name)}')
                        INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask, createdon, modifiedon, statecode, statuscode)
                        VALUES (NEWID(), '{StandardRoles.BasicUser}', '{PrivilegeNames.IdOf(name)}', 8, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
                    """);
            Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task Timeline_posts_setting_seeded()
    {
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{AppModuleSchema.SettingDefinition} WHERE settingdefinitionid = @id " +
                "AND uniquename = @name AND displayname = @name AND defaultvalue = N'false'",
            ("@id", AppModuleSchema.TimelinePostsId), ("@name", AppModuleSchema.TimelinePosts))).Should().Be(1);
    }
}
