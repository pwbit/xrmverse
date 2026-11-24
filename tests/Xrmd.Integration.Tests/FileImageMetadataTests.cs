using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// File and image columns (rulings F1, F4, F5): the companion attributes, the MaxSizeInKB limits, the primary image, the
/// <c>{table}_FileAttachments</c> relationship and the targets of <c>fileattachment.objectid</c> that follow the columns,
/// the Edm types, the refusals of the public messages and the seed of <c>fileattachment</c> on an existing database.
/// </summary>
[Collection("xrmd")]
public sealed class FileImageMetadataTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<string> NewEntityAsync()
    {
        var name = "new_fi" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        return name;
    }

    private Task AddAsync(string entity, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = entity, Attribute = attribute });

    private async Task<T> RetrieveAsync<T>(string entity, string attribute) where T : AttributeMetadata =>
        (T)((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute })).AttributeMetadata;

    private static FileAttributeMetadata File(string name, int? size = null) =>
        new() { LogicalName = name, SchemaName = name, MaxSizeInKB = size };

    private static ImageAttributeMetadata Image(string name, bool? primary = null, int? size = null) =>
        new() { LogicalName = name, SchemaName = name, IsPrimaryImage = primary, MaxSizeInKB = size };

    /// <summary>
    /// The refusal is a 400 InvalidArgument (0x80040203): an XrmdFaultException from the Core, or an XrmdValidationException from the
    /// metadata store, which both transports write with the same code (see <see cref="Refused_over_the_web_api_is_0x80040203"/>).
    /// </summary>
    private static async Task FaultAsync(Func<Task> act, string message)
    {
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Message.Should().Contain(message);
        if (ex.Which is XrmdFaultException fault) (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        else ex.Which.Should().BeOfType<XrmdValidationException>();
    }

    private static int? SizeOf(AttributeMetadata a) => a switch
    {
        FileAttributeMetadata f => f.MaxSizeInKB,
        ImageAttributeMetadata i => i.MaxSizeInKB,
        _ => null,
    };

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private Task<bool> ColumnExistsAsync(string table, string column) => TestSql.ColumnExistsAsync(fx.ConnectionString, "data", table, column);

    private string[] ObjectIdTargets() => ((LookupAttributeMetadata)Store.GetEntity("fileattachment")!.Attributes
        .Single(a => a.LogicalName == "objectid")).Targets;

    [Fact]
    public async Task Create_file_column_adds_name_companion_and_relationship()
    {
        var table = await NewEntityAsync();

        await AddAsync(table, File("new_doc"));

        var column = await RetrieveAsync<FileAttributeMetadata>(table, "new_doc");
        column.MaxSizeInKB.Should().Be(32768);
        var companion = await RetrieveAsync<StringAttributeMetadata>(table, "new_doc_name");
        companion.MaxLength.Should().Be(255);
        companion.AttributeOf.Should().Be("new_doc");
        companion.IsValidForCreate.Should().BeFalse();
        (await TestSql.ColumnTypeAsync(fx.ConnectionString, "data", table, "new_doc"))!.Value.Type
            .Should().Be(TestProvider.IsPostgres ? "uuid" : "uniqueidentifier");
        // nvarchar(255) on SQL Server; citext on PostgreSQL, where MaxLength is enforced by validation (ruling P5).
        if (TestProvider.IsPostgres) (await ColumnExistsAsync(table, "new_doc_name")).Should().BeTrue();
        else (await ScalarAsync("SELECT COL_LENGTH(@t, 'new_doc_name')", ("@t", $"data.{table}"))).Should().Be((short)510);

        var relationship = Store.GetRelationship($"{table}_FileAttachments").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        relationship.ReferencedEntity.Should().Be(table);
        relationship.ReferencingEntity.Should().Be("fileattachment");
        relationship.ReferencingAttribute.Should().Be("objectid");
        ObjectIdTargets().Should().Contain(table);

        // A second column of the table reuses the relationship.
        await AddAsync(table, File("new_doc2"));
        Store.GetOneToManyRelationships().Count(r => r.ReferencedEntity == table && r.ReferencingEntity == "fileattachment").Should().Be(1);
        ObjectIdTargets().Count(t => t == table).Should().Be(1);
    }

    [Fact]
    public async Task Create_image_column_adds_id_timestamp_url_and_is_primary_when_first()
    {
        var table = await NewEntityAsync();

        await AddAsync(table, Image("new_photo"));

        var image = await RetrieveAsync<ImageAttributeMetadata>(table, "new_photo");
        image.IsPrimaryImage.Should().BeTrue();
        image.MaxSizeInKB.Should().Be(10240);
        image.CanStoreFullImage.Should().BeFalse();
        image.MaxHeight.Should().Be(144);
        image.MaxWidth.Should().Be(144);
        (await RetrieveAsync<UniqueIdentifierAttributeMetadata>(table, "new_photoid")).AttributeOf.Should().Be("new_photo");
        (await RetrieveAsync<BigIntAttributeMetadata>(table, "new_photo_timestamp")).AttributeOf.Should().Be("new_photo");
        (await RetrieveAsync<StringAttributeMetadata>(table, "new_photo_url")).AttributeOf.Should().Be("new_photo");
        (await TestSql.ColumnTypeAsync(fx.ConnectionString, "data", table, "new_photo"))!.Value.Type
            .Should().Be(TestProvider.IsPostgres ? "bytea" : "varbinary");
        (await ColumnExistsAsync(table, "new_photoid")).Should().BeTrue();
        (await ColumnExistsAsync(table, "new_photo_timestamp")).Should().BeTrue();
        (await ColumnExistsAsync(table, "new_photo_url")).Should().BeFalse(); // computed on read
        Store.GetRelationship($"{table}_FileAttachments").Should().NotBeNull();

        // Companions are the server's: they cannot be deleted or defined by the caller.
        await Admin.Invoking(a => a.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_photoid" }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.Message.Contains("system attribute"));
    }

    [Fact]
    public async Task Second_image_column_is_not_primary_and_setting_true_moves_the_flag()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, Image("new_a"));

        await AddAsync(table, Image("new_b"));
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeFalse();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_a")).IsPrimaryImage.Should().BeTrue();

        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Image("new_b", primary: true) });
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeTrue();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_a")).IsPrimaryImage.Should().BeFalse();

        // Created as primary while another image column exists: ignored (Learn, "Update image columns"); only an update moves it.
        await AddAsync(table, Image("new_c", primary: true));
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_c")).IsPrimaryImage.Should().BeFalse();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeTrue();
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Image("new_c", primary: true) });
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_c")).IsPrimaryImage.Should().BeTrue();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeFalse();

        // The flag is metadata: it survives a reload of the store.
        var reloaded = new MetadataStore(TestProvider.Factory(fx.ConnectionString));
        await reloaded.LoadAsync();
        ((ImageAttributeMetadata)reloaded.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_c")).IsPrimaryImage.Should().BeTrue();

        // Deleting the primary makes the oldest remaining image column primary.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_c" });
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_a")).IsPrimaryImage.Should().BeTrue();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeFalse();
    }

    [Fact]
    public async Task Oldest_remaining_image_is_promoted_by_creation_order_after_a_reload()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, Image("new_m"));
        await AddAsync(table, Image("new_z"));
        await AddAsync(table, Image("new_a"));
        await Store.LoadAsync(); // the cache now lists attributes alphabetically

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_m" });

        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_z")).IsPrimaryImage.Should().BeTrue();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_a")).IsPrimaryImage.Should().BeFalse();
    }

    [Fact]
    public async Task The_image_order_is_not_part_of_the_metadata_ColumnNumber_is_as_for_any_attribute()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, Image("new_a"));
        await AddAsync(table, Image("new_b"));
        await Store.LoadAsync();

        var plain = await RetrieveAsync<AttributeMetadata>(table, "new_name");
        foreach (var name in new[] { "new_a", "new_b", "new_aid" })
            (await RetrieveAsync<AttributeMetadata>(table, name)).ColumnNumber.Should().Be(plain.ColumnNumber);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = @e AND definition LIKE '%imageordinal%'", ("@e", table)))
            .Should().Be(2); // stored, but only in the stored definition
    }

    [Fact]
    public async Task Two_inline_images_both_primary_the_first_is_primary_and_the_second_is_ignored()
    {
        var table = "new_fi" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([Image("new_first", primary: true), Image("new_second", primary: true)]);

        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 },
        });

        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_first")).IsPrimaryImage.Should().BeTrue();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_second")).IsPrimaryImage.Should().BeFalse(
            "IsPrimaryImage true is ignored on the creation of an image column when another one exists");
    }

    [Fact]
    public async Task IsPrimaryImage_false_on_a_column_that_is_not_primary_is_an_accepted_no_op()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, Image("new_a"));
        await AddAsync(table, Image("new_b"));

        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Image("new_b", primary: false) });

        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_b")).IsPrimaryImage.Should().BeFalse();
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_a")).IsPrimaryImage.Should().BeTrue();
    }

    [Fact]
    public async Task CreateEntity_with_file_and_image_columns_through_the_sdk_builds_the_same_as_CreateAttribute()
    {
        var table = "new_fi" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([File("new_doc"), Image("new_pic1"), Image("new_pic2", primary: true)]);

        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 },
        });

        (await RetrieveAsync<StringAttributeMetadata>(table, "new_doc_name")).AttributeOf.Should().Be("new_doc");
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_pic1")).IsPrimaryImage.Should().BeTrue("the first image column is primary");
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_pic2")).IsPrimaryImage.Should().BeFalse("true is ignored on create when another image column exists");
        await RetrieveAsync<BigIntAttributeMetadata>(table, "new_pic1_timestamp");
        await RetrieveAsync<UniqueIdentifierAttributeMetadata>(table, "new_pic2id");
        (await ColumnExistsAsync(table, "new_doc_name")).Should().BeTrue();
        Store.GetRelationship($"{table}_FileAttachments").Should().NotBeNull();
        ObjectIdTargets().Should().Contain(table);

        // The same table can have its last file/image columns dropped one by one afterwards.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_doc" });
        Store.GetRelationship($"{table}_FileAttachments").Should().NotBeNull();
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_pic1" });
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_pic2" });
        Store.GetRelationship($"{table}_FileAttachments").Should().BeNull();
        ObjectIdTargets().Should().NotContain(table);
    }

    [Fact]
    public async Task Post_EntityDefinitions_with_file_and_image_attributes_builds_the_same_as_CreateAttribute()
    {
        var table = "new_fi" + Guid.NewGuid().ToString("N")[..10];
        var body = $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
              "LogicalName": "{{table}}", "SchemaName": "{{table}}", "OwnershipType": "UserOwned",
              "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] },
              "Attributes": [
                { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name", "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true },
                { "@odata.type": "Microsoft.Dynamics.CRM.FileAttributeMetadata", "LogicalName": "new_doc", "SchemaName": "new_doc", "MaxSizeInKB": 2048 },
                { "@odata.type": "Microsoft.Dynamics.CRM.ImageAttributeMetadata", "LogicalName": "new_pic", "SchemaName": "new_pic" }
              ]
            }
            """;
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/EntityDefinitions")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await RetrieveAsync<FileAttributeMetadata>(table, "new_doc")).MaxSizeInKB.Should().Be(2048);
        (await RetrieveAsync<StringAttributeMetadata>(table, "new_doc_name")).AttributeOf.Should().Be("new_doc");
        var image = await RetrieveAsync<ImageAttributeMetadata>(table, "new_pic");
        image.IsPrimaryImage.Should().BeTrue();
        image.MaxHeight.Should().Be(144);
        image.MaxSizeInKB.Should().Be(10240);
        await RetrieveAsync<UniqueIdentifierAttributeMetadata>(table, "new_picid");
        Store.GetRelationship($"{table}_FileAttachments").Should().NotBeNull();
        ObjectIdTargets().Should().Contain(table);
    }

    [Fact]
    public async Task Setting_IsPrimaryImage_false_is_refused()
    {
        var table = await NewEntityAsync();

        await FaultAsync(() => AddAsync(table, Image("new_first", primary: false)), "IsPrimaryImage cannot be false");
        await AddAsync(table, Image("new_first"));
        await FaultAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Image("new_first", primary: false) }),
            "IsPrimaryImage cannot be set to false");
        (await RetrieveAsync<ImageAttributeMetadata>(table, "new_first")).IsPrimaryImage.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, null, 32768)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 10485760, 10485760)]
    [InlineData(false, null, 10240)]
    [InlineData(false, 1, 1)]
    [InlineData(false, 30720, 30720)]
    public async Task MaxSizeInKB_defaults_and_ranges(bool file, int? requested, int expected)
    {
        var table = await NewEntityAsync();

        await AddAsync(table, file ? File("new_c", requested) : Image("new_c", size: requested));

        var stored = (await RetrieveAsync<AttributeMetadata>(table, "new_c"));
        SizeOf(stored).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 10485761)]
    [InlineData(false, 0)]
    [InlineData(false, 30721)]
    public async Task MaxSizeInKB_out_of_range_is_refused_on_create_and_update(bool file, int size)
    {
        var table = await NewEntityAsync();
        var max = file ? 10485760 : 30720;

        await FaultAsync(() => AddAsync(table, file ? File("new_big", size) : Image("new_big", size: size)), $"MaxSizeInKB {size} is outside 1..{max}");
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_big");
        Store.GetRelationship($"{table}_FileAttachments").Should().BeNull();

        await AddAsync(table, file ? File("new_ok") : Image("new_ok"));
        await FaultAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = table, Attribute = file ? File("new_ok", size) : Image("new_ok", size: size),
        }), $"MaxSizeInKB {size} is outside 1..{max}");

        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = file ? File("new_ok", 5) : Image("new_ok", size: 5) });
        var stored = await RetrieveAsync<AttributeMetadata>(table, "new_ok");
        SizeOf(stored).Should().Be(5);
    }

    [Fact]
    public async Task Dropping_the_last_file_column_removes_the_relationship()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, File("new_a"));
        await AddAsync(table, Image("new_b"));

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_a" });
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_a" || a.LogicalName == "new_a_name");
        (await ColumnExistsAsync(table, "new_a_name")).Should().BeFalse();
        Store.GetRelationship($"{table}_FileAttachments").Should().NotBeNull(); // the image column still needs it
        ObjectIdTargets().Should().Contain(table);

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_b" });
        Store.GetEntity(table)!.Attributes.Select(a => a.LogicalName).Where(n => n.StartsWith("new_b") || n.StartsWith("new_a"))
            .Should().BeEmpty();
        (await ColumnExistsAsync(table, "new_bid")).Should().BeFalse();
        Store.GetRelationship($"{table}_FileAttachments").Should().BeNull();
        ObjectIdTargets().Should().NotContain(table);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name = @n", ("@n", $"{table}_FileAttachments"))).Should().Be(0);

        // The link is persisted, not only cached.
        var reloaded = new MetadataStore(TestProvider.Factory(fx.ConnectionString));
        await reloaded.LoadAsync();
        ((LookupAttributeMetadata)reloaded.GetEntity("fileattachment")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets.Should().NotContain(table);
    }

    [Fact]
    public async Task Deleting_a_table_with_file_columns_removes_its_target_and_relationship()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, File("new_a"));

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        Store.GetEntity(table).Should().BeNull();
        Store.GetRelationship($"{table}_FileAttachments").Should().BeNull();
        ObjectIdTargets().Should().NotContain(table);
    }

    [Fact]
    public async Task Edm_types_are_Guid_and_Binary()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, File("new_doc"));
        await AddAsync(table, Image("new_pic"));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/$metadata");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        using var reader = new StringReader(await res.Content.ReadAsStringAsync());
        using var xml = System.Xml.XmlReader.Create(reader);
        Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(xml, out var model, out _).Should().BeTrue();
        var type = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType($"Microsoft.Dynamics.CRM.{table}");
        type.FindProperty("new_doc").Type.Definition.TypeKind.Should().Be(Microsoft.OData.Edm.EdmTypeKind.Primitive);
        ((Microsoft.OData.Edm.IEdmPrimitiveType)type.FindProperty("new_doc").Type.Definition).PrimitiveKind.Should().Be(Microsoft.OData.Edm.EdmPrimitiveTypeKind.Guid);
        ((Microsoft.OData.Edm.IEdmPrimitiveType)type.FindProperty("new_pic").Type.Definition).PrimitiveKind.Should().Be(Microsoft.OData.Edm.EdmPrimitiveTypeKind.Binary);
    }

    [Fact]
    public async Task EntityDefinitions_attribute_casts_return_the_file_and_image_properties()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, File("new_doc", 100));
        await AddAsync(table, Image("new_pic"));

        async Task<JsonObject> GetAsync(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/" + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            var res = await fx.Client.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            res.IsSuccessStatusCode.Should().BeTrue(body);
            return JsonNode.Parse(body)!.AsObject();
        }

        var file = await GetAsync($"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_doc')/Microsoft.Dynamics.CRM.FileAttributeMetadata");
        file["MaxSizeInKB"]!.GetValue<int>().Should().Be(100);
        file["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.FileAttributeMetadata");

        var image = await GetAsync($"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_pic')/Microsoft.Dynamics.CRM.ImageAttributeMetadata");
        image["MaxSizeInKB"]!.GetValue<int>().Should().Be(10240);
        image["CanStoreFullImage"]!.GetValue<bool>().Should().BeFalse();
        image["IsPrimaryImage"]!.GetValue<bool>().Should().BeTrue();
        image["MaxHeight"]!.GetValue<int>().Should().Be(144);
        image["MaxWidth"]!.GetValue<int>().Should().Be(144);
    }

    [Fact]
    public async Task Refused_over_the_web_api_is_0x80040203()
    {
        var table = await NewEntityAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/data/v9.2/EntityDefinitions(LogicalName='{table}')/Attributes")
        {
            Content = new StringContent("""{ "@odata.type": "Microsoft.Dynamics.CRM.FileAttributeMetadata", "LogicalName": "new_big", "SchemaName": "new_big", "MaxSizeInKB": 10485761 }""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        var res = await fx.Client.SendAsync(req);

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var error = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should().Contain("MaxSizeInKB 10485761 is outside 1..10485760");
    }

    [Fact]
    public async Task fileattachment_direct_create_update_delete_are_refused()
    {
        var id = Guid.NewGuid();
        const string reason = "its rows are written by the file and image column messages";

        await FaultAsync(() => Admin.CreateAsync(new Entity("fileattachment") { ["filename"] = "x.txt" }), reason);
        await FaultAsync(() => Admin.UpdateAsync(new Entity("fileattachment", id) { ["filename"] = "x.txt" }), reason);
        await FaultAsync(() => Admin.DeleteAsync("fileattachment", id), reason);
    }

    [Fact]
    public async Task fileattachment_has_the_dataverse_columns_and_is_not_owned()
    {
        var entity = Store.GetEntity("fileattachment")!;

        entity.EntitySetName.Should().Be("fileattachments");
        entity.OwnershipType.Should().Be(OwnershipTypes.None);
        entity.PrimaryIdAttribute.Should().Be("fileattachmentid");
        entity.PrimaryNameAttribute.Should().Be("filename");
        entity.Attributes.Select(a => a.LogicalName).Should().Contain(
            ["fileattachmentid", "filename", "mimetype", "filesizeinbytes", "regardingfieldname", "objecttypecode", "objectid", "createdon", "createdby"]);
        entity.Attributes.Should().NotContain(a => a.LogicalName == "ownerid");
        entity.Attributes.Single(a => a.LogicalName == "filesizeinbytes").Should().BeOfType<BigIntAttributeMetadata>();
        (await ColumnExistsAsync("fileattachment", "objectidtype")).Should().BeTrue();
    }

    [Fact]
    public async Task The_objectid_lookup_and_its_relationships_are_managed_by_the_columns()
    {
        var table = await NewEntityAsync();
        await AddAsync(table, File("new_doc"));
        var name = $"{table}_FileAttachments";

        await FaultAsync(() => Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = name }), "is managed by the file and image columns");
        await FaultAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = "fileattachment",
            Attribute = new LookupAttributeMetadata { LogicalName = "objectid", SchemaName = "ObjectId" },
        }), "is managed by the file and image columns");
        await FaultAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = "new_hijack" + Guid.NewGuid().ToString("N")[..6],
                ReferencedEntity = table,
                ReferencingEntity = "fileattachment",
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "objectid", SchemaName = "ObjectId" },
        }), "is managed by the file and image columns");
        await Admin.Invoking(a => a.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "fileattachment", LogicalName = "objectid" }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied);

        Store.GetRelationship(name).Should().NotBeNull();
        ObjectIdTargets().Should().Contain(table);
    }
}
