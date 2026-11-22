using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>GET /admin/schema</c> and <c>POST /admin/schema/apply</c>: the schema YAML (<c>Xrmd.Schema</c>) executed through the
/// Core as the caller, in one transaction. The tests use the publisher prefix <c>zq</c>.
/// </summary>
[Collection("xrmd")]
public sealed class AdminSchemaTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string AdminRequiredBody =
        """{"error":{"code":"0x80040220","message":"The Admin API requires the System Administrator role."}}""";

    private const string Schema = """
        publisher: zq
        choices:
          - name: zq_priority
            label: Priority
            options: [{ value: 100000000, label: Low }, { value: 100000001, label: High }]
        tables:
          - name: zq_project
            label: Project
            pluralLabel: Projects
            ownership: user
            primaryName: { name: zq_name, label: Name, maxLength: 200 }
            columns:
              - { name: zq_budget, type: money, label: Budget }
              - { name: zq_priority, type: choice, choice: zq_priority, label: Priority }
              - { name: zq_parent, type: lookup, target: zq_project, label: Parent }
              - { name: zq_notes, type: memo, maxLength: 2000, label: Notes }
            manyToMany:
              - { name: zq_project_zq_tag, with: zq_tag }
          - name: zq_tag
            label: Tag
            pluralLabel: Tags
            ownership: organization
            primaryName: { name: zq_name, label: Name }
            columns:
              - { name: zq_code, type: string, maxLength: 20, label: Code }
              - { name: zq_sizes, type: choices, label: Sizes, options: [{ value: 1, label: S }, { value: 2, label: M }] }
        """;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private Guid _nonAdminOid;

    public async Task InitializeAsync()
    {
        await CleanAsync();
        _nonAdminOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _nonAdminOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "schema-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadSolution", DepthMask.Global)));
    }

    public Task DisposeAsync() => CleanAsync();

    /// <summary>Drops everything with the prefix: relationships, tables, choices.</summary>
    private async Task CleanAsync()
    {
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            foreach (var r in Store.GetManyToManyRelationships().Where(r => r.SchemaName.StartsWith("zq_", StringComparison.Ordinal)).ToList())
                await Store.DeleteRelationshipAsync(s, r.SchemaName, default);
            // The relationships of a Customer column go with the table (one of them cannot be deleted alone).
            foreach (var r in Store.GetOneToManyRelationships().Where(r => r.ReferencingAttribute.StartsWith("zq_", StringComparison.Ordinal)
                         && Store.GetEntity(r.ReferencingEntity)?.Attributes.FirstOrDefault(a => a.LogicalName == r.ReferencingAttribute)?.AttributeType != AttributeTypeCode.Customer).ToList())
                await Store.DeleteRelationshipAsync(s, r.SchemaName, default);
        });
        foreach (var e in Store.GetAllEntities().Where(e => e.LogicalName.StartsWith("zq_", StringComparison.Ordinal)).ToList())
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, e.LogicalName, default));
        foreach (var o in Store.GetAllOptionSets().Where(o => o.Name.StartsWith("zq_", StringComparison.Ordinal)).ToList())
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteOptionSetAsync(s, o.Name, default));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? oid, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (oid is { } o) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(o, fx.BaseUrl));
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "text/yaml");
        return fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> ApplyAsync(string yaml, bool dryRun, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await SendAsync(HttpMethod.Post, $"/admin/schema/apply?dryRun={(dryRun ? "true" : "false")}", fx.AdminObjectId, yaml);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string[] Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Apply_creates_tables_and_is_idempotent()
    {
        var first = await ApplyAsync(Schema, dryRun: false);

        first["applied"]!.GetValue<bool>().Should().BeTrue();
        Strings(first["rejections"]).Should().BeEmpty();
        Strings(first["changes"]).Should().Contain(["create choice zq_priority", "create table zq_project", "create table zq_tag",
            "create column zq_project.zq_budget (money)", "create column zq_project.zq_parent (lookup to zq_project)",
            "create many-to-many zq_project_zq_tag"]);
        Store.GetEntity("zq_project")!.Attributes.Select(a => a.LogicalName).Should().Contain(["zq_name", "zq_budget", "zq_parent"]);
        Store.GetManyToManyRelationships().Should().Contain(r => r.SchemaName == "zq_project_zq_tag");

        var second = await ApplyAsync(Schema, dryRun: false);
        Strings(second["changes"]).Should().BeEmpty();
        second["applied"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var plan = await ApplyAsync(Schema, dryRun: true);

        plan["applied"]!.GetValue<bool>().Should().BeFalse();
        Strings(plan["changes"]).Should().Contain("create table zq_project");
        Store.GetEntity("zq_project").Should().BeNull();
        Store.GetEntity("zq_tag").Should().BeNull();
        Store.GetAllOptionSets().Should().NotContain(o => o.Name == "zq_priority");
    }

    [Fact]
    public async Task Failure_midway_rolls_back_everything()
    {
        var broken = """
            publisher: zq
            tables:
              - name: zq_good
                label: Good
                primaryName: { name: zq_name, label: Name }
              - name: zq_bad
                label: Bad
                primaryName: { name: zq_name, label: Name }
                columns:
                  - { name: zq_ref, type: lookup, target: zq_nowhere, label: Ref }
            """;

        var body = await ApplyAsync(broken, dryRun: false, HttpStatusCode.BadRequest);

        body["applied"]!.GetValue<bool>().Should().BeFalse();
        Strings(body["rejections"]).Should().ContainSingle().Which.Should().Contain("zq_nowhere");
        Store.GetEntity("zq_good").Should().BeNull();
        Store.GetEntity("zq_bad").Should().BeNull();
    }

    [Fact]
    public async Task A_value_the_server_refuses_is_the_same_rejection_in_the_dry_run_and_the_apply()
    {
        // The diff accepts the second table's column; the Core refuses its MaxLength after the first table was created.
        var broken = """
            publisher: zq
            tables:
              - name: zq_first
                label: First
                primaryName: { name: zq_name, label: Name }
              - name: zq_second
                label: Second
                primaryName: { name: zq_name, label: Name }
                columns:
                  - { name: zq_big, type: string, label: Dup, maxLength: 99999999 }
            """;

        var plan = await ApplyAsync(broken, dryRun: true, HttpStatusCode.BadRequest);
        plan["applied"]!.GetValue<bool>().Should().BeFalse();
        var rejection = Strings(plan["rejections"]).Should().ContainSingle().Which;
        rejection.Should().Contain("zq_second.zq_big");

        var applied = await ApplyAsync(broken, dryRun: false, HttpStatusCode.BadRequest);
        applied["applied"]!.GetValue<bool>().Should().BeFalse();
        Strings(applied["rejections"]).Should().Equal(rejection);
        Strings(applied["changes"]).Should().Equal(Strings(plan["changes"]));

        // Rolled back in both modes, the requests before the refused one included.
        Store.GetEntity("zq_first").Should().BeNull();
        Store.GetEntity("zq_second").Should().BeNull();
    }

    [Fact]
    public async Task Apply_without_dryRun_is_a_dry_run()
    {
        var res = await SendAsync(HttpMethod.Post, "/admin/schema/apply", fx.AdminObjectId, Schema);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        body["applied"]!.GetValue<bool>().Should().BeFalse();
        Strings(body["changes"]).Should().Contain(c => c.Contains("zq_project"));
        Store.GetEntity("zq_project").Should().BeNull();
    }

    [Fact]
    public async Task Every_column_type_applies_and_later_edits_update_in_place()
    {
        var all = """
            publisher: zq
            choices:
              - name: zq_level
                label: Level
                options: [{ value: 100000000, label: Low }]
            tables:
              - name: zq_wide
                label: Wide
                description: First description
                primaryName: { name: zq_name, label: Name, maxLength: 50 }
                columns:
                  - { name: zq_text, type: string, maxLength: 30, label: Text, description: A text }
                  - { name: zq_memo, type: memo, maxLength: 1000, label: Memo }
                  - { name: zq_count, type: int, label: Count }
                  - { name: zq_ratio, type: decimal, precision: 3, label: Ratio }
                  - { name: zq_amount, type: money, label: Amount }
                  - { name: zq_flag, type: bool, label: Flag }
                  - { name: zq_when, type: datetime, label: When }
                  - { name: zq_one, type: choice, label: One, options: [{ value: 1, label: A }, { value: 2, label: B }] }
                  - { name: zq_shared, type: choice, choice: zq_level, label: Shared }
                  - { name: zq_many, type: choices, label: Many, options: [{ value: 1, label: X }, { value: 2, label: Y }] }
                  - { name: zq_owner, type: lookup, target: zq_wide, label: Owner }
            """;
        var first = await ApplyAsync(all, dryRun: false);
        first["applied"]!.GetValue<bool>().Should().BeTrue();
        Strings(first["changes"]).Should().Contain("create column zq_wide.zq_ratio (decimal)");
        Strings((await ApplyAsync(all, dryRun: true))["changes"]).Should().BeEmpty();

        var edited = all.Replace("First description", "Second description").Replace("label: Text, description: A text", "label: Text 2, description: A text")
            .Replace("maxLength: 30", "maxLength: 60")
            .Replace("{ value: 100000000, label: Low }", "{ value: 100000000, label: Lower }, { value: 100000001, label: High }")
            .Replace("{ value: 2, label: B }", "{ value: 2, label: Bee }, { value: 3, label: C }");
        var second = await ApplyAsync(edited, dryRun: false);

        second["applied"]!.GetValue<bool>().Should().BeTrue();
        Strings(second["changes"]).Should().NotBeEmpty();
        Strings((await ApplyAsync(edited, dryRun: true))["changes"]).Should().BeEmpty();
        var text = (Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata)Store.GetEntity("zq_wide")!.Attributes.Single(a => a.LogicalName == "zq_text");
        text.MaxLength.Should().Be(60);
    }

    [Fact]
    public async Task Schema_yaml_customer_round_trips()
    {
        var yaml = """
            publisher: zq
            tables:
              - name: zq_deal
                label: Deal
                primaryName: { name: zq_name, label: Name }
                columns:
                  - { name: zq_customerid, type: customer, label: Customer }
            """;
        var applied = await ApplyAsync(yaml, dryRun: false);

        applied["applied"]!.GetValue<bool>().Should().BeTrue();
        Strings(applied["changes"]).Should().Contain("create column zq_deal.zq_customerid (customer)");
        var column = Store.GetEntity("zq_deal")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "zq_customerid");
        column.AttributeType.Should().Be(AttributeTypeCode.Customer);
        Store.GetOneToManyRelationships().Where(r => r.ReferencingAttribute == "zq_customerid").Select(r => r.SchemaName)
            .Should().BeEquivalentTo("zq_deal_zq_customerid_account", "zq_deal_zq_customerid_contact");
        Strings((await ApplyAsync(yaml, dryRun: true))["changes"]).Should().BeEmpty();

        var res = await SendAsync(HttpMethod.Get, "/admin/schema?publisher=zq", fx.AdminObjectId);
        var exported = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, exported);
        exported.Should().Contain("{name: zq_customerid, type: customer, label: Customer}").And.NotContain("zq_customeridtype").And.NotContain("target");
        Strings((await ApplyAsync(exported, dryRun: true))["changes"]).Should().BeEmpty();
        var again = await ApplyAsync(exported, dryRun: false);
        Strings(again["rejections"]).Should().BeEmpty();
        Strings(again["changes"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Schema_yaml_activity_and_flags_apply_and_round_trip()
    {
        var yaml = """
            publisher: zq
            tables:
              - name: zq_sms
                label: SMS
                pluralLabel: SMS messages
                isActivity: true
                primaryName: { name: subject, label: Subject }
                columns:
                  - { name: zq_number, type: string, maxLength: 20, label: Number }
              - name: zq_site
                label: Site
                primaryName: { name: zq_name, label: Name }
            """;
        Strings((await ApplyAsync(yaml, dryRun: false))["rejections"]).Should().BeEmpty();
        var sms = Store.GetEntity("zq_sms")!;
        (sms.IsActivity, sms.HasNotes, sms.PrimaryIdAttribute, sms.PrimaryNameAttribute).Should().Be((true, true, "activityid", "subject"));
        Store.GetEntity("zq_site")!.HasNotes.Should().NotBe(true);

        // The flags switch on with an update; switching one off again is a rejection.
        var on = yaml + "\n    hasNotes: true\n    hasActivities: true\n";
        var switched = await ApplyAsync(on, dryRun: false);
        Strings(switched["changes"]).Should().Equal("update table zq_site");
        (Store.GetEntity("zq_site")!.HasNotes, Store.GetEntity("zq_site")!.HasActivities).Should().Be((true, true));
        var off = await ApplyAsync(yaml + "\n    hasNotes: false\n", dryRun: true, HttpStatusCode.BadRequest);
        Strings(off["rejections"]).Should().ContainSingle().Which.Should().Contain("hasNotes").And.Contain("cannot be turned off");

        var res = await SendAsync(HttpMethod.Get, "/admin/schema?publisher=zq", fx.AdminObjectId);
        var exported = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, exported);
        exported.Should().Contain("isActivity: true").And.Contain("primaryName: {name: subject, label: Subject")
            .And.Contain("hasActivities: true").And.NotContain("schemaName: Subject");
        var again = await ApplyAsync(exported, dryRun: true);
        Strings(again["rejections"]).Should().BeEmpty();
        Strings(again["changes"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Yaml_apply_creates_file_and_image_columns_and_get_schema_emits_them()
    {
        var yaml = """
            publisher: zq
            tables:
              - name: zq_media
                label: Media
                primaryName: { name: zq_name, label: Name }
                columns:
                  - { name: zq_contract, type: file, maxSizeInKB: 65536, label: Contract }
                  - { name: zq_photo, type: image, label: Photo }
                  - { name: zq_logo, type: image, maxSizeInKB: 2048, canStoreFullImage: true, primary: true, label: Logo }
            """;
        var applied = await ApplyAsync(yaml, dryRun: false);

        applied["applied"]!.GetValue<bool>().Should().BeTrue();
        Strings(applied["changes"]).Should().Contain(["create column zq_media.zq_contract (file)", "create column zq_media.zq_logo (image)"]);
        var media = Store.GetEntity("zq_media")!;
        media.Attributes.OfType<FileAttributeMetadata>().Single().MaxSizeInKB.Should().Be(65536);
        var logo = media.Attributes.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "zq_logo");
        (logo.MaxSizeInKB, logo.CanStoreFullImage, logo.IsPrimaryImage).Should().Be((2048, true, true));
        media.Attributes.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "zq_photo").IsPrimaryImage.Should().BeFalse();
        media.Attributes.Should().Contain(a => a.LogicalName == "zq_contract_name" && a.AttributeOf == "zq_contract");
        Strings((await ApplyAsync(yaml, dryRun: true))["changes"]).Should().BeEmpty();

        var res = await SendAsync(HttpMethod.Get, "/admin/schema?publisher=zq", fx.AdminObjectId);
        var exported = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, exported);
        exported.Should().Contain("{name: zq_contract, type: file, label: Contract, maxSizeInKB: 65536}")
            .And.Contain("{name: zq_logo, type: image, label: Logo, maxSizeInKB: 2048, canStoreFullImage: true, primary: true}")
            .And.Contain("{name: zq_photo, type: image, label: Photo, maxSizeInKB: 10240}")
            .And.NotContain("zq_contract_name").And.NotContain("zq_photoid").And.NotContain("zq_photo_url");
        Strings((await ApplyAsync(exported, dryRun: true))["changes"]).Should().BeEmpty();

        var edited = yaml.Replace("maxSizeInKB: 65536", "maxSizeInKB: 1024", StringComparison.Ordinal)
            .Replace("{ name: zq_photo, type: image, label: Photo }", "{ name: zq_photo, type: image, primary: true, label: Photo }", StringComparison.Ordinal)
            .Replace(", primary: true, label: Logo", ", label: Logo", StringComparison.Ordinal);
        (await ApplyAsync(edited, dryRun: false))["applied"]!.GetValue<bool>().Should().BeTrue();
        media = Store.GetEntity("zq_media")!;
        media.Attributes.OfType<FileAttributeMetadata>().Single().MaxSizeInKB.Should().Be(1024);
        media.Attributes.OfType<ImageAttributeMetadata>().Single(a => a.IsPrimaryImage == true).LogicalName.Should().Be("zq_photo");
        Strings((await ApplyAsync(edited, dryRun: true))["changes"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Get_schema_roundtrips_through_apply()
    {
        await ApplyAsync(Schema, dryRun: false);

        var res = await SendAsync(HttpMethod.Get, "/admin/schema?publisher=zq", fx.AdminObjectId);
        var yaml = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK, yaml);
        res.Content.Headers.ContentType!.ToString().Should().Be("text/yaml; charset=utf-8");
        yaml.Should().Contain("zq_project").And.Contain("zq_parent").And.Contain("zq_project_zq_tag");
        var again = await ApplyAsync(yaml, dryRun: true);
        Strings(again["changes"]).Should().BeEmpty();
        Strings(again["rejections"]).Should().BeEmpty();
    }

    [Fact]
    public async Task Extras_are_reported_and_never_deleted()
    {
        await ApplyAsync(Schema, dryRun: false);

        var onlyTag = """
            publisher: zq
            tables:
              - name: zq_tag
                label: Tag
                pluralLabel: Tags
                ownership: organization
                primaryName: { name: zq_name, label: Name }
                columns:
                  - { name: zq_code, type: string, maxLength: 20, label: Code }
                  - { name: zq_sizes, type: choices, label: Sizes, options: [{ value: 1, label: S }, { value: 2, label: M }] }
            """;
        var plan = await ApplyAsync(onlyTag, dryRun: false);

        Strings(plan["extras"]).Should().Contain(["table zq_project", "choice zq_priority"]);
        Store.GetEntity("zq_project").Should().NotBeNull();
    }

    [Fact]
    public async Task Rejections_are_400_with_the_body()
    {
        await ApplyAsync(Schema, dryRun: false);

        var changed = Schema.Replace("{ name: zq_budget, type: money, label: Budget }", "{ name: zq_budget, type: int, label: Budget }");
        var body = await ApplyAsync(changed, dryRun: false, HttpStatusCode.BadRequest);

        body["applied"]!.GetValue<bool>().Should().BeFalse();
        Strings(body["rejections"]).Should().ContainSingle().Which.Should().Contain("zq_budget");
    }

    [Fact]
    public async Task Invalid_yaml_is_400_with_line_and_column()
    {
        var res = await SendAsync(HttpMethod.Post, "/admin/schema/apply", fx.AdminObjectId, "publisher: zq\ntables: [oops\n");
        var text = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        JsonNode.Parse(text)!["error"]!["message"]!.GetValue<string>().Should().MatchRegex(@"line \d+, column \d+");
    }

    [Fact]
    public async Task A_bad_dryRun_value_and_a_missing_publisher_are_400_naming_them()
    {
        var res = await SendAsync(HttpMethod.Post, "/admin/schema/apply?dryRun=maybe", fx.AdminObjectId, Schema);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("dryRun").And.Contain("maybe");

        var get = await SendAsync(HttpMethod.Get, "/admin/schema", fx.AdminObjectId);
        get.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await get.Content.ReadAsStringAsync()).Should().Contain("publisher");
    }

    [Fact]
    public async Task Schema_routes_need_a_token_and_the_administrator_role_before_any_validation()
    {
        (await SendAsync(HttpMethod.Get, "/admin/schema?publisher=zq", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/admin/schema/apply", null, Schema)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Invalid YAML and a missing publisher are 400 for an administrator; anybody else gets the role check first.
        var apply = await SendAsync(HttpMethod.Post, "/admin/schema/apply?dryRun=maybe", _nonAdminOid, "publisher: zq\ntables: [oops\n");
        apply.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await apply.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);
        var get = await SendAsync(HttpMethod.Get, "/admin/schema", _nonAdminOid);
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await get.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);
    }
}
