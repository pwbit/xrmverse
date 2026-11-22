using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Metadata;
using Xrmd.WebApi;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.4, task 7: elastic tables through the Web API — the key <c>(pk=id,partitionid='p')</c>, the <c>partitionId</c> query
/// parameter, the <c>x-ms-session-token</c> header, the deep insert refusal, the four bulk actions with their error details and
/// <c>@odata.bind</c> by elastic key.
/// </summary>
[Collection("xrmd")]
public sealed class ElasticWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string SessionToken = "x-ms-session-token";
    private const string BulkAnnotation = "@Microsoft.PowerApps.CDS.ErrorDetails.Plugin.BulkApiErrorDetails";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static (string Table, string Set, string Lookup)? _schema;

    private OrganizationService _svc = null!;

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The elastic table <c>new_elweb…</c> (<c>new_name</c>, <c>new_value</c>) and a lookup to it on account, created once.</summary>
    private async Task<(string Table, string Set, string Lookup)> SchemaAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_schema is { } done) return done;
            var name = "new_elweb" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Elastic web");
            entity.TableType = ElasticTables.TableTypeElastic;
            await _svc.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
            });
            await _svc.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata { LogicalName = "new_value", SchemaName = "new_Value", DisplayName = Label("Value") },
            });
            var lookup = "new_elwebref" + Guid.NewGuid().ToString("N")[..6];
            await _svc.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_account_{lookup}", ReferencedEntity = name, ReferencingEntity = "account",
                },
                Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Elastic") },
            });
            var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(name)!.EntitySetName;
            _schema = (name, set, lookup);
            return _schema.Value;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        configure?.Invoke(req);
        return await fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task ExpectAsync(HttpResponseMessage res, HttpStatusCode expected) =>
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());

    private static async Task<JsonObject> ErrorAsync(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        var error = (await JsonAsync(res, status))["error"]!.AsObject();
        error["code"]!.GetValue<string>().Should().Be(code);
        return error;
    }

    private static string? Token(HttpResponseMessage res) =>
        res.Headers.TryGetValues(SessionToken, out var values) ? values.Single() : null;

    private static Guid IdOf(HttpResponseMessage res)
    {
        var url = res.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    private async Task<Guid> PostRowAsync(string set, string? partition, int value = 1)
    {
        var body = new JsonObject { ["new_name"] = "row", ["new_value"] = value };
        if (partition is not null) body["partitionid"] = partition;
        var res = await SendAsync(HttpMethod.Post, set, body.ToJsonString());
        await ExpectAsync(res, HttpStatusCode.NoContent);
        return IdOf(res);
    }

    private async Task<Entity?> ReadAsync(string table, Guid id) =>
        (await _svc.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = { Conditions = { new ConditionExpression(table + "id", ConditionOperator.Equal, id) } },
        })).Entities.SingleOrDefault();

    private static string Key(string table, Guid id, string partition) => $"({table}id={id},partitionid='{partition.Replace("'", "''")}')";

    [Fact]
    public async Task Post_with_partition_and_ttl_returns_the_session_token()
    {
        var (table, set, _) = await SchemaAsync();

        var res = await SendAsync(HttpMethod.Post, set, """{ "new_name": "p", "partitionid": "dev-1", "ttlinseconds": 86400 }""");

        await ExpectAsync(res, HttpStatusCode.NoContent);
        Token(res).Should().StartWith("0:");
        var row = await ReadAsync(table, IdOf(res));
        row!.GetAttributeValue<string>("partitionid").Should().Be("dev-1");
        row.GetAttributeValue<int>("ttlinseconds").Should().Be(86400);
    }

    [Fact]
    public async Task Get_patch_delete_by_elastic_key()
    {
        var (table, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "it's");

        var got = await JsonAsync(await SendAsync(HttpMethod.Get, set + Key(table, id, "it's") + "?$select=new_value"), HttpStatusCode.OK);
        got["new_value"]!.GetValue<int>().Should().Be(1);

        var patched = await SendAsync(HttpMethod.Patch, set + Key(table, id, "it's"), """{ "new_value": 7 }""",
            r => r.Headers.IfMatch.Add(EntityTagHeaderValue.Any));
        await ExpectAsync(patched, HttpStatusCode.NoContent);
        Token(patched).Should().StartWith("0:");
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(7);

        // Another partition (case-sensitive) does not find the row.
        await ErrorAsync(await SendAsync(HttpMethod.Get, set + Key(table, id, "IT'S")), HttpStatusCode.NotFound, "0x80040217");
        await ErrorAsync(await SendAsync(HttpMethod.Patch, set + Key(table, id, "other"), """{ "new_value": 8 }"""), HttpStatusCode.NotFound, "0x80040217");
        await ErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})"), HttpStatusCode.NotFound, "0x80040217");

        var deleted = await SendAsync(HttpMethod.Delete, set + Key(table, id, "it's"));
        await ExpectAsync(deleted, HttpStatusCode.NoContent);
        Token(deleted).Should().BeNull("Delete returns no session token");
        (await ReadAsync(table, id)).Should().BeNull();
    }

    [Fact]
    public async Task Patch_without_partition_in_the_key_uses_the_partitionid_of_the_body()
    {
        var (table, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "body-p");

        await ExpectAsync(await SendAsync(HttpMethod.Patch, $"{set}({id})", """{ "partitionid": "body-p", "new_value": 3 }"""), HttpStatusCode.NoContent);

        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(3);
    }

    [Fact]
    public async Task Patch_of_a_missing_row_is_404_not_an_upsert()
    {
        var (table, set, _) = await SchemaAsync();
        var id = Guid.NewGuid();

        await ErrorAsync(await SendAsync(HttpMethod.Patch, set + Key(table, id, "p"), """{ "new_value": 3 }"""), HttpStatusCode.NotFound, "0x80040217");

        (await ReadAsync(table, id)).Should().BeNull();
    }

    [Fact]
    public async Task PartitionId_parameter_on_get_and_delete_of_a_record()
    {
        var (table, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "q-1", 4);

        var got = await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}({id})?partitionId=q-1&$select=new_value"), HttpStatusCode.OK);
        got["new_value"]!.GetValue<int>().Should().Be(4);
        // The quoted form is accepted too.
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}({id})?partitionId='q-1'"), HttpStatusCode.OK))["new_value"]!.GetValue<int>().Should().Be(4);
        await ErrorAsync(await SendAsync(HttpMethod.Get, $"{set}({id})?partitionId=Q-1"), HttpStatusCode.NotFound, "0x80040217");
        await ErrorAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})?partitionId=q-2"), HttpStatusCode.NotFound, "0x80040217");

        await ExpectAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})?partitionId=q-1"), HttpStatusCode.NoContent);
        (await ReadAsync(table, id)).Should().BeNull();
    }

    [Fact]
    public async Task PartitionId_parameter_filters_the_collection()
    {
        var (table, set, _) = await SchemaAsync();
        var partition = "c-" + Guid.NewGuid().ToString("N")[..8];
        var a = await PostRowAsync(set, partition);
        var b = await PostRowAsync(set, partition);
        await PostRowAsync(set, partition.ToUpperInvariant()); // exact match: another partition

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}?partitionId={partition}&$select={table}id"), HttpStatusCode.OK);

        json["value"]!.AsArray().Select(r => Guid.Parse(r![$"{table}id"]!.GetValue<string>())).Should().BeEquivalentTo([a, b]);
    }

    [Fact]
    public async Task PartitionId_parameter_on_a_standard_table_is_400()
    {
        await SchemaAsync();

        await ErrorAsync(await SendAsync(HttpMethod.Get, "accounts?partitionId=p1"), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task PartitionId_parameter_on_writes_is_ignored()
    {
        var (table, set, _) = await SchemaAsync();

        var posted = await SendAsync(HttpMethod.Post, set + "?partitionId=other", """{ "new_name": "x", "partitionid": "w-1" }""");
        await ExpectAsync(posted, HttpStatusCode.NoContent);
        var id = IdOf(posted);
        (await ReadAsync(table, id))!.GetAttributeValue<string>("partitionid").Should().Be("w-1");

        await ExpectAsync(await SendAsync(HttpMethod.Patch, set + Key(table, id, "w-1") + "?partitionId=other", """{ "new_value": 2 }"""),
            HttpStatusCode.NoContent);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(2);

        await ExpectAsync(await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.UpdateMultiple?partitionId=other",
            $$"""{ "Targets": [ { "@odata.type": "Microsoft.Dynamics.CRM.{{table}}", "{{table}}id": "{{id}}", "partitionid": "w-1", "new_value": 3 } ] }"""),
            HttpStatusCode.NoContent);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(3);
    }

    [Fact]
    public async Task Patch_with_a_body_partitionid_other_than_the_key_is_refused_and_the_row_stays()
    {
        var (table, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "k-1", 1);

        await ErrorAsync(await SendAsync(HttpMethod.Patch, set + Key(table, id, "k-1"), """{ "partitionid": "k-2", "new_value": 5 }"""),
            HttpStatusCode.BadRequest, "0x80040203");

        var row = await ReadAsync(table, id);
        row!.GetAttributeValue<string>("partitionid").Should().Be("k-1");
        row.GetAttributeValue<int>("new_value").Should().Be(1);
    }

    [Fact]
    public async Task File_route_with_an_elastic_key_naming_a_partition_is_400()
    {
        var (table, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "f-1");

        var error = await ErrorAsync(await SendAsync(HttpMethod.Get, set + Key(table, id, "f-1") + "/new_document"),
            HttpStatusCode.BadRequest, "0x80040203");

        error["message"]!.GetValue<string>().Should().Contain("elastic table");
    }

    [Fact]
    public async Task File_column_of_a_partitioned_row_is_uploaded_read_and_deleted_by_id()
    {
        // Final review I2: the route by id alone reaches the row whatever its partition (the id is unique, ruling E1).
        var name = "new_elwebf" + Guid.NewGuid().ToString("N")[..6];
        var entity = TestHelpers.Entity(name, name, "Elastic web files");
        entity.TableType = ElasticTables.TableTypeElastic;
        await _svc.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await _svc.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", DisplayName = Label("Doc") },
        });
        var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(name)!.EntitySetName;
        var id = await _svc.CreateAsync(new Entity(name) { ["new_name"] = "f", [ElasticTables.PartitionId] = "wf-1" });

        var upload = new ByteArrayContent([1, 2, 3]);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await ExpectAsync(await SendAsync(HttpMethod.Patch, $"{set}({id})/new_doc", configure: r =>
        {
            r.Content = upload;
            r.Headers.Add("x-ms-file-name", "a.txt");
        }), HttpStatusCode.NoContent);
        var fileId = (await ReadAsync(name, id))!.GetAttributeValue<Guid>("new_doc");
        fileId.Should().NotBeEmpty();

        var got = await JsonAsync(await SendAsync(HttpMethod.Get, $"{set}({id})/new_doc"), HttpStatusCode.OK);
        got["value"]!.GetValue<string>().Should().Be(fileId.ToString());
        using (var download = await SendAsync(HttpMethod.Get, $"{set}({id})/new_doc/$value"))
        {
            await ExpectAsync(download, HttpStatusCode.OK);
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
        }

        await ExpectAsync(await SendAsync(HttpMethod.Delete, $"{set}({id})/new_doc"), HttpStatusCode.NoContent);
        var row = await ReadAsync(name, id);
        row!.Contains("new_doc").Should().BeFalse();
        row.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("wf-1");
    }

    [Fact]
    public async Task Session_token_header_is_accepted()
    {
        var (_, set, _) = await SchemaAsync();
        var id = await PostRowAsync(set, "s-1");

        var res = await SendAsync(HttpMethod.Get, $"{set}({id})?partitionId=s-1", configure: r => r.Headers.Add("MSCRM.SessionToken", "0:123"));

        await ExpectAsync(res, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deep_insert_is_400()
    {
        var (_, set, _) = await SchemaAsync();

        var error = await ErrorAsync(await SendAsync(HttpMethod.Post, set, """{ "new_name": "x", "new_tasks": [ { "subject": "t" } ] }"""),
            HttpStatusCode.BadRequest, "0x80040203");

        error["message"]!.GetValue<string>().Should().Contain("deep insert");
    }

    [Fact]
    public async Task Bulk_actions_create_update_upsert_delete()
    {
        var (table, set, _) = await SchemaAsync();
        var type = $"Microsoft.Dynamics.CRM.{table}";
        var pk = table + "id";

        var created = await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.CreateMultiple", $$"""
            { "Targets": [
              { "@odata.type": "{{type}}", "new_name": "b0", "partitionid": "bulk", "ttlinseconds": 3600 },
              { "@odata.type": "{{type}}", "new_name": "b1", "partitionid": "bulk" } ] }
            """);
        var json = await JsonAsync(created, HttpStatusCode.OK);
        Token(created).Should().StartWith("0:");
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.CreateMultipleResponse");
        var ids = json["Ids"]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>())).ToArray();
        ids.Should().HaveCount(2);

        var updated = await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.UpdateMultiple", $$"""
            { "Targets": [ { "@odata.type": "{{type}}", "{{pk}}": "{{ids[0]}}", "partitionid": "bulk", "new_value": 10 } ] }
            """);
        await ExpectAsync(updated, HttpStatusCode.NoContent);
        Token(updated).Should().StartWith("0:");
        (await ReadAsync(table, ids[0]))!.GetAttributeValue<int>("new_value").Should().Be(10);

        var fresh = Guid.NewGuid();
        var upserted = await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.UpsertMultiple", $$"""
            { "Targets": [
              { "@odata.type": "{{type}}", "{{pk}}": "{{ids[1]}}", "partitionid": "bulk", "new_name": "replaced" },
              { "@odata.type": "{{type}}", "{{pk}}": "{{fresh}}", "partitionid": "bulk", "new_name": "new" } ] }
            """);
        await ExpectAsync(upserted, HttpStatusCode.NoContent);
        Token(upserted).Should().StartWith("0:");
        (await ReadAsync(table, ids[1]))!.GetAttributeValue<string>("new_name").Should().Be("replaced");
        (await ReadAsync(table, fresh))!.GetAttributeValue<string>("new_name").Should().Be("new");

        var deleted = await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.DeleteMultiple", $$"""
            { "Targets": [
              { "@odata.type": "{{type}}", "{{pk}}": "{{ids[0]}}", "partitionid": "bulk" },
              { "@odata.type": "{{type}}", "{{pk}}": "{{ids[1]}}", "partitionid": "bulk" },
              { "@odata.type": "{{type}}", "{{pk}}": "{{fresh}}", "partitionid": "bulk" } ] }
            """);
        await ExpectAsync(deleted, HttpStatusCode.NoContent);
        Token(deleted).Should().BeNull();
        foreach (var id in ids.Append(fresh)) (await ReadAsync(table, id)).Should().BeNull();
    }

    [Fact]
    public async Task ExecuteCosmosSqlQuery_function_returns_expando()
    {
        var (table, set, _) = await SchemaAsync();
        var partition = "cosmos-" + Guid.NewGuid().ToString("N")[..8];
        await PostRowAsync(set, partition, 7);
        await PostRowAsync(set, partition, 3);

        var path = "ExecuteCosmosSqlQuery(QueryText=@p1,EntityLogicalName=@p2,QueryParameters=@p3,PageSize=@p4,PartitionId=@p5)"
            + "?@p1=" + Uri.EscapeDataString("'SELECT c.props.new_name AS n, c.props.new_value FROM c WHERE c.props.new_value > @min'")
            + "&@p2=" + Uri.EscapeDataString($"'{table}'")
            + "&@p3=" + Uri.EscapeDataString("""{"Keys":["@min"],"Values":[{"Type":"System.Int32","Value":"5"}]}""")
            + "&@p4=50&@p5=" + Uri.EscapeDataString($"'{partition}'");
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.OK);

        body["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#expando/$entity");
        body["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.expando");
        body["HasMore"]!.GetValue<bool>().Should().BeFalse();
        body.ContainsKey("PagingCookie").Should().BeFalse();
        body["Result@odata.type"]!.GetValue<string>().Should().Be("#Collection(Microsoft.Dynamics.CRM.expando)");
        var result = body["Result"].Should().BeOfType<JsonArray>().Subject;
        result.Should().HaveCount(1);
        var item = result[0]!.AsObject();
        item.First().Key.Should().Be("@odata.type");
        item["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.expando");
        item["n"]!.GetValue<string>().Should().Be("row");
        item["new_value"]!.GetValue<int>().Should().Be(7);

        // A refused construct is a 400 OData error.
        var refused = "ExecuteCosmosSqlQuery(QueryText=@p1,EntityLogicalName=@p2)?@p1=" + Uri.EscapeDataString("'SELECT * FROM c'")
            + "&@p2=" + Uri.EscapeDataString($"'{table}'");
        var error = await ErrorAsync(await SendAsync(HttpMethod.Get, refused), HttpStatusCode.BadRequest, "0x80040203");
        error["message"]!.GetValue<string>().Should().Contain("SELECT *");
    }

    [Fact]
    public async Task ExecuteCosmosSqlQuery_function_pages_with_the_paging_cookie()
    {
        var (table, set, _) = await SchemaAsync();
        var partition = "cosmos-pg-" + Guid.NewGuid().ToString("N")[..8];
        foreach (var v in new[] { 4, 1, 5, 3, 2 }) await PostRowAsync(set, partition, v);

        var seen = new List<int>();
        string? cookie = null;
        var pages = 0;
        bool hasMore;
        do
        {
            var path = "ExecuteCosmosSqlQuery(QueryText=@p1,EntityLogicalName=@p2,PageSize=@p4,PartitionId=@p5,PagingCookie=@p6)"
                + "?@p1=" + Uri.EscapeDataString("'SELECT c.props.new_value FROM c ORDER BY c.props.new_value'")
                + "&@p2=" + Uri.EscapeDataString($"'{table}'")
                + "&@p4=2&@p5=" + Uri.EscapeDataString($"'{partition}'")
                + "&@p6=" + (cookie is null ? "null" : Uri.EscapeDataString($"'{cookie}'"));
            var body = await JsonAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.OK);
            pages++;
            pages.Should().BeLessThan(10);
            seen.AddRange(body["Result"]!.AsArray().Select(r => r!["new_value"]!.GetValue<int>()));
            hasMore = body["HasMore"]!.GetValue<bool>();
            if (hasMore)
            {
                cookie = body["PagingCookie"]!.GetValue<string>();
                cookie.Should().NotBeNullOrEmpty();
            }
            else
            {
                body.ContainsKey("PagingCookie").Should().BeFalse();
            }
        }
        while (hasMore);

        pages.Should().Be(3);
        seen.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task ExecuteCosmosSqlQuery_function_null_alias_means_no_partition_and_non_member_is_403()
    {
        var (table, set, _) = await SchemaAsync();
        await PostRowAsync(set, "cosmos-null-" + Guid.NewGuid().ToString("N")[..6], 1);

        var path = "ExecuteCosmosSqlQuery(QueryText=@p1,EntityLogicalName=@p2,PartitionId=@p5,PagingCookie=@p6)"
            + "?@p1=" + Uri.EscapeDataString("'SELECT c.props.new_value FROM c'")
            + "&@p2=" + Uri.EscapeDataString($"'{table}'") + "&@p5=null&@p6=null";
        var body = await JsonAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.OK);
        body["Result"]!.AsArray().Count.Should().BeGreaterThan(0);

        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(Guid.NewGuid(), fx.BaseUrl));
        using var denied = await fx.Client.SendAsync(req);
        await ExpectAsync(denied, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bulk_target_without_odata_type_is_400()
    {
        var (table, set, _) = await SchemaAsync();

        foreach (var message in new[] { "CreateMultiple", "UpdateMultiple", "UpsertMultiple", "DeleteMultiple" })
        {
            var error = await ErrorAsync(await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.{message}",
                $$"""{ "Targets": [ { "{{table}}id": "{{Guid.NewGuid()}}" } ] }"""), HttpStatusCode.BadRequest, "0x80040203");
            error["message"]!.GetValue<string>().Should().Contain("@odata.type", message);
        }
    }

    [Fact]
    public async Task Bulk_partial_failure_carries_the_details_only_when_asked()
    {
        var (table, set, _) = await SchemaAsync();
        var type = $"Microsoft.Dynamics.CRM.{table}";
        var body = $$"""
            { "Targets": [
              { "@odata.type": "{{type}}", "new_name": "ok" },
              { "@odata.type": "{{type}}", "new_name": "bad", "ttlinseconds": 0 } ] }
            """;

        var plain = await ErrorAsync(await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.CreateMultiple", body),
            HttpStatusCode.BadRequest, "0x80040203");
        plain.ContainsKey(BulkAnnotation).Should().BeFalse();

        var annotated = await ErrorAsync(await SendAsync(HttpMethod.Post, $"{set}/Microsoft.Dynamics.CRM.CreateMultiple", body,
            r => r.Headers.Add("Prefer", "odata.include-annotations=\"*\"")), HttpStatusCode.BadRequest, "0x80040203");
        var details = JsonSerializer.Deserialize<BulkApiErrorDetail[]>(annotated[BulkAnnotation]!.GetValue<string>());
        details.Should().Equal(new BulkApiErrorDetail(1, "", 400));
    }

    [Fact]
    public async Task Bind_by_elastic_key_sets_the_lookup_and_its_partition()
    {
        var (table, set, lookup) = await SchemaAsync();
        var id = await PostRowAsync(set, "bind-p");
        var account = await _svc.CreateAsync(new Entity("account") { ["name"] = "bound" });
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var accountMeta = store.GetEntity("account")!;
        var navigation = ODataEdmModelBuilder.NavigationNames(accountMeta,
            (LookupAttributeMetadata)accountMeta.Attributes.Single(a => a.LogicalName == lookup), store.GetOneToManyRelationships())[0].Name;

        await ExpectAsync(await SendAsync(HttpMethod.Patch, $"accounts({account})",
            $$"""{ "{{navigation}}@odata.bind": "/{{set}}{{Key(table, id, "bind-p")}}" }"""), HttpStatusCode.NoContent);

        var read = await _svc.RetrieveAsync("account", account, new ColumnSet(lookup, ElasticTables.PartitionColumnOf(lookup)));
        read.GetAttributeValue<EntityReference>(lookup).Id.Should().Be(id);
        read.GetAttributeValue<string>(ElasticTables.PartitionColumnOf(lookup)).Should().Be("bind-p");

        await ErrorAsync(await SendAsync(HttpMethod.Patch, $"accounts({account})",
            $$"""{ "{{navigation}}@odata.bind": "/{{set}}{{Key(table, id, "other")}}" }"""), HttpStatusCode.NotFound, "0x80040217");
    }
}
