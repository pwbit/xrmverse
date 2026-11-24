using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Autonumber columns end to end (autonumber plan): the format on CreateEntity, CreateAttribute and UpdateAttribute, the
/// generated values on Create, the seed messages on the SDK and the Web API, and the sequence's life with its column.
/// </summary>
[Collection("xrmd")]
public sealed class AutoNumberTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A new table whose primary name has <paramref name="primaryFormat"/>.</summary>
    private async Task<string> NewTableAsync(string? primaryFormat = null, int maxLength = 100)
    {
        var name = $"new_an{_run}{Guid.NewGuid().ToString("N")[..4]}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table"), DisplayCollectionName = Label("Tables"),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = maxLength, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None), AutoNumberFormat = primaryFormat,
            },
        });
        return name;
    }

    private Task AddColumnAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    private static StringAttributeMetadata Text(string name, string? format, int maxLength = 100) => new()
    {
        LogicalName = name, SchemaName = name, MaxLength = maxLength, DisplayName = Label(name), AutoNumberFormat = format,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
    };

    private async Task<Entity> CreateAndReadAsync(string table, Entity? values = null)
    {
        var entity = values ?? new Entity(table);
        var id = await Admin.CreateAsync(entity);
        return await Admin.RetrieveAsync(table, id, new ColumnSet(true));
    }

    private Task<OrganizationResponse> SeedAsync(string table, string column, long value) =>
        Admin.ExecuteAsync(new SetAutoNumberSeedRequest { EntityName = table, AttributeName = column, Value = value });

    private async Task<long> GetSeedAsync(string table, string column) =>
        ((GetAutoNumberSeedResponse)await Admin.ExecuteAsync(new GetAutoNumberSeedRequest { EntityName = table, AttributeName = column }))
        .AutoNumberSeedValue;

    private async Task<long> NextAsync(string table, string column) =>
        ((GetNextAutoNumberValueResponse)await Admin.ExecuteAsync(new GetNextAutoNumberValueRequest { EntityName = table, AttributeName = column }))
        .NextAutoNumberValue;

    private async Task<bool> SequenceExistsAsync(Guid metadataId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM information_schema.sequences WHERE sequence_schema = 'autonumber' AND sequence_name = '{metadataId:D}'");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    // ---- create -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_fills_the_primary_name_from_the_sequence_starting_at_1000()
    {
        var table = await NewTableAsync("PRJ-{SEQNUM:5}");

        (await CreateAndReadAsync(table))["new_name"].Should().Be("PRJ-01000");
        (await CreateAndReadAsync(table))["new_name"].Should().Be("PRJ-01001");
        (await NextAsync(table, "new_name")).Should().Be(1002);
        (await GetSeedAsync(table, "new_name")).Should().Be(AutoNumberFormat.DefaultSeed);
    }

    [Fact]
    public async Task Create_keeps_a_given_value_and_fills_an_empty_one()
    {
        var table = await NewTableAsync("N-{SEQNUM:3}");

        (await CreateAndReadAsync(table, new Entity(table) { ["new_name"] = "mine" }))["new_name"].Should().Be("mine");
        (await CreateAndReadAsync(table, new Entity(table) { ["new_name"] = "" }))["new_name"].Should().Be("N-1000");
        (await CreateAndReadAsync(table, new Entity(table) { ["new_name"] = null }))["new_name"].Should().Be("N-1001");
    }

    [Fact]
    public async Task Update_never_generates()
    {
        var table = await NewTableAsync("N-{SEQNUM:3}");
        var created = await CreateAndReadAsync(table);
        await Admin.UpdateAsync(new Entity(table, created.Id) { ["new_name"] = null });

        (await Admin.RetrieveAsync(table, created.Id, new ColumnSet("new_name"))).Contains("new_name").Should().BeFalse();
        (await NextAsync(table, "new_name")).Should().Be(1001);
    }

    [Fact]
    public async Task A_column_with_random_and_date_placeholders_gets_them()
    {
        var table = await NewTableAsync();
        await AddColumnAsync(table, Text("new_code", "C-{RANDSTRING:4}-{DATETIMEUTC:yyyyMMdd}"));

        var value = (string)(await CreateAndReadAsync(table))["new_code"];

        value.Should().MatchRegex($"^C-[A-Z0-9]{{4}}-{DateTime.UtcNow:yyyyMMdd}$");
    }

    [Fact]
    public async Task A_bad_format_is_accepted_on_the_column_and_refused_when_a_record_is_saved()
    {
        var table = await NewTableAsync();
        await AddColumnAsync(table, Text("new_code", "C-{RANDSTRING:7}"));

        var message = await BadRequestAsync(() => Admin.CreateAsync(new Entity(table)));

        message.Should().Contain($"'{table}.new_code'").And.Contain("from 1 to 6");
    }

    [Fact]
    public async Task A_value_longer_than_MaxLength_is_refused_not_truncated()
    {
        var table = await NewTableAsync();
        await AddColumnAsync(table, Text("new_code", "LONG-{SEQNUM:8}", maxLength: 10));

        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(table)))).Should().Contain("longer than its MaxLength 10");
    }

    [Fact]
    public async Task A_failed_create_leaves_a_gap()
    {
        var table = await NewTableAsync("N-{SEQNUM:1}");
        await AddColumnAsync(table, Text("new_code", "C-{RANDSTRING:7}"));

        await BadRequestAsync(() => Admin.CreateAsync(new Entity(table)));

        (await NextAsync(table, "new_name")).Should().Be(1001, "the value the failed Create took is not given back");
    }

    // ---- metadata ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_format_is_shown_by_RetrieveAttribute_and_can_be_changed_and_removed()
    {
        var table = await NewTableAsync();
        await AddColumnAsync(table, Text("new_code", "A-{SEQNUM:2}"));
        var retrieve = new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = "new_code" };

        var attribute = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(retrieve)).AttributeMetadata;
        attribute.AutoNumberFormat.Should().Be("A-{SEQNUM:2}");
        (await CreateAndReadAsync(table))["new_code"].Should().Be("A-1000");

        attribute.AutoNumberFormat = "B-{SEQNUM:6}";
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = attribute });
        (await CreateAndReadAsync(table))["new_code"].Should().Be("B-001001", "changing the format keeps the numbering");

        var id = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_code").MetadataId!.Value;
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Text("new_code", "") });
        Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_code").AutoNumberFormat.Should().BeNull();
        (await CreateAndReadAsync(table)).Contains("new_code").Should().BeFalse();
        (await SequenceExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_existing_text_column_becomes_autonumber()
    {
        var table = await NewTableAsync();
        await AddColumnAsync(table, Text("new_code", null));
        await CreateAndReadAsync(table);

        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = Text("new_code", "X{SEQNUM:4}") });

        (await CreateAndReadAsync(table))["new_code"].Should().Be("X1000");
    }

    [Fact]
    public async Task A_format_on_another_column_is_refused()
    {
        var table = await NewTableAsync();

        (await BadRequestAsync(() => AddColumnAsync(table, new StringAttributeMetadata
        {
            LogicalName = "new_mail", SchemaName = "new_mail", MaxLength = 100, DisplayName = Label("Mail"), Format = StringFormat.Email,
            AutoNumberFormat = "M-{SEQNUM:3}", RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        }))).Should().Contain("format is Text");
        (await BadRequestAsync(() => AddColumnAsync(table, new IntegerAttributeMetadata
        {
            LogicalName = "new_count", SchemaName = "new_count", DisplayName = Label("Count"), AutoNumberFormat = "M-{SEQNUM:3}",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        }))).Should().Contain("only on a string column");
    }

    [Fact]
    public async Task The_sequence_goes_with_its_column_and_its_table()
    {
        var table = await NewTableAsync("N-{SEQNUM:3}");
        await AddColumnAsync(table, Text("new_code", "C-{SEQNUM:3}"));
        var attributes = Store.GetEntity(table)!.Attributes;
        var name = attributes.Single(a => a.LogicalName == "new_name").MetadataId!.Value;
        var code = attributes.Single(a => a.LogicalName == "new_code").MetadataId!.Value;
        (await SequenceExistsAsync(name)).Should().BeTrue();
        (await SequenceExistsAsync(code)).Should().BeTrue();

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_code" });
        (await SequenceExistsAsync(code)).Should().BeFalse();

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        (await SequenceExistsAsync(name)).Should().BeFalse();
    }

    // ---- seed messages ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetAutoNumberSeed_restarts_the_sequence()
    {
        var table = await NewTableAsync("N-{SEQNUM:5}");
        await CreateAndReadAsync(table);

        await SeedAsync(table, "new_name", 50000);

        (await GetSeedAsync(table, "new_name")).Should().Be(50000);
        (await NextAsync(table, "new_name")).Should().Be(50000);
        (await CreateAndReadAsync(table))["new_name"].Should().Be("N-50000");
        (await NextAsync(table, "new_name")).Should().Be(50001);
        (await GetSeedAsync(table, "new_name")).Should().Be(50000);
    }

    [Fact]
    public async Task The_seed_messages_refuse_a_column_without_a_format_and_a_negative_value()
    {
        var table = await NewTableAsync("N-{SEQNUM:5}");
        await AddColumnAsync(table, Text("new_plain", null));

        (await BadRequestAsync(() => NextAsync(table, "new_plain"))).Should().Contain("is not an autonumber column");
        (await BadRequestAsync(() => GetSeedAsync(table, "new_nope"))).Should().Contain("does not exist");
        (await BadRequestAsync(() => SeedAsync(table, "new_name", -1))).Should().Contain("cannot be negative");
    }

    [Fact]
    public async Task SetAutoNumberSeed_requires_prvWriteAttribute_and_the_reads_do_not()
    {
        var table = await NewTableAsync("N-{SEQNUM:5}");
        var objectId = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "autonumber-" + _run, ("prvReadAccount", DepthMask.Global)));
        var user = ServiceFor(fx, objectId);

        var fault = await FaultAsync(() => user.ExecuteAsync(new SetAutoNumberSeedRequest { EntityName = table, AttributeName = "new_name", Value = 5 }));
        fault.HttpStatus.Should().Be(403);
        ((GetNextAutoNumberValueResponse)await user.ExecuteAsync(new GetNextAutoNumberValueRequest { EntityName = table, AttributeName = "new_name" }))
            .NextAutoNumberValue.Should().Be(1000);
    }

    [Fact]
    public async Task The_seed_messages_work_on_the_Web_API()
    {
        var table = await NewTableAsync("N-{SEQNUM:5}");

        var set = await SendAsync(HttpMethod.Post, "SetAutoNumberSeed", $$"""{ "EntityName": "{{table}}", "AttributeName": "new_name", "Value": 7000 }""");
        set.StatusCode.Should().Be(HttpStatusCode.NoContent, await set.Content.ReadAsStringAsync());

        var seed = await SendAsync(HttpMethod.Get, $"GetAutoNumberSeed(EntityName='{table}',AttributeName='new_name')");
        var seedBody = await seed.Content.ReadAsStringAsync();
        seed.StatusCode.Should().Be(HttpStatusCode.OK, seedBody);
        JsonNode.Parse(seedBody)!["AutoNumberSeedValue"]!.GetValue<long>().Should().Be(7000);

        var next = await SendAsync(HttpMethod.Get, $"GetNextAutoNumberValue(EntityName=@e,AttributeName=@a)?@e='{table}'&@a='new_name'");
        var nextBody = await next.Content.ReadAsStringAsync();
        next.StatusCode.Should().Be(HttpStatusCode.OK, nextBody);
        JsonNode.Parse(nextBody)!["NextAutoNumberValue"]!.GetValue<long>().Should().Be(7000);

        var bad = await SendAsync(HttpMethod.Get, $"GetAutoNumberSeed(EntityName='{table}')");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_Web_API_creates_an_autonumber_column()
    {
        var table = await NewTableAsync();
        var body = """
            {
              "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
              "LogicalName": "new_serial", "SchemaName": "new_Serial", "MaxLength": 100, "AutoNumberFormat": "WID-{SEQNUM:5}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Serial", "LanguageCode": 1033 } ] },
              "RequiredLevel": { "Value": "None" }
            }
            """;

        var res = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", body);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());

        (await CreateAndReadAsync(table))["new_serial"].Should().Be("WID-01000");
    }
}
