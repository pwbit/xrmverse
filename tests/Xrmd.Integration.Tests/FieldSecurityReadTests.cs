using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Field-level security on every read path (plan 14, task 4, rulings F3, F4 for downloads and F11; Review Focus 1 and 2): two users
/// with different profiles read the same record and get different secured columns (null in the SDK, absent from the Web API JSON);
/// a filter, order, aggregate or link join on a secured column they cannot read is refused with 0x8004F504, never answered with an
/// empty result; a field share reveals the column on its record only; secured file columns need read to be downloaded; audit
/// details leave out what the caller cannot read.
/// </summary>
[Collection("xrmd")]
public sealed class FieldSecurityReadTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = "new_fsread", Child = "new_fsreadchild";
    private const string S1 = "new_fss1", S2 = "new_fss2", N1 = "new_fsn1", Plain = "new_fsplain", Doc = "new_fsdoc", Img = "new_fsimg", Salary = "new_fssalary", Parent = "new_fsparentid";
    private const string Navigation = "new_FsParentId";
    private const string Api = "/api/data/v9.2/";
    private const string Denied = "0x8004f504";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private Guid _root;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Set => Store.GetEntity(Table)!.EntitySetName;
    private string ChildSet => Store.GetEntity(Child)!.EntitySetName;

    public async Task InitializeAsync()
    {
        _root = await RootBusinessUnitAsync(fx);
        await Lock.WaitAsync();
        try
        {
            await EnsureTablesAsync();
            if (Store.GetEntity(Table)!.Attributes.All(a => a.LogicalName != Img))
                await Admin.ExecuteAsync(new CreateAttributeRequest
                {
                    EntityName = Table, Attribute = new ImageAttributeMetadata { LogicalName = Img, SchemaName = "new_FsImg", IsSecured = true },
                });
            if (Store.GetEntity(Table)!.Attributes.All(a => a.LogicalName != Salary))
                await Admin.ExecuteAsync(new CreateAttributeRequest
                {
                    EntityName = Table, Attribute = new MoneyAttributeMetadata { LogicalName = Salary, SchemaName = "new_FsSalary", IsSecured = true },
                });
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task EnsureTablesAsync()
    {
        if (Store.GetEntity(Child) is not null) return;
        if (Store.GetEntity(Table) is not null) throw new InvalidOperationException($"'{Table}' exists without '{Child}': reset the test database.");
        // new_fss1, new_fss2 and new_fsn1 are secured, new_fsplain is not; new_fsdoc is a secured file column.
        var entity = new EntityMetadata { LogicalName = Table, SchemaName = "new_FsRead", OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = S1, SchemaName = "new_FsS1", MaxLength = 100, IsSecured = true },
            new StringAttributeMetadata { LogicalName = S2, SchemaName = "new_FsS2", MaxLength = 100, IsSecured = true },
            new IntegerAttributeMetadata { LogicalName = N1, SchemaName = "new_FsN1", IsSecured = true },
            new StringAttributeMetadata { LogicalName = Plain, SchemaName = "new_FsPlain", MaxLength = 100 },
        ]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = Table, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = "new_FsDoc", IsSecured = true },
        });

        var child = new EntityMetadata { LogicalName = Child, SchemaName = "new_FsReadChild", OwnershipType = OwnershipTypes.UserOwned };
        child.SetAttributes([]);
        await Store.CreateEntityAsync(child, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = "new_fsreadchild_parent", ReferencedEntity = Table, ReferencingEntity = Child },
            Lookup = new LookupAttributeMetadata { LogicalName = Parent, SchemaName = Navigation, DisplayName = new Label("Parent", 1033) },
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A user that reads both tables (Global) and holds a new profile reading <paramref name="readable"/>.</summary>
    private async Task<(Guid Id, Guid ObjectId)> UserAsync(params string[] readable)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, _root, oid);
        await GrantAsync(fx, user, team: false, ("prvReadnew_FsRead", DepthMask.Global), ("prvReadnew_FsReadChild", DepthMask.Global));
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "fsr-" + Guid.NewGuid().ToString("N")[..8] });
        foreach (var attribute in readable)
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Table, [FieldSecurityTables.AttributeLogicalName] = attribute,
                [FieldSecurityTables.CanRead] = new OptionSetValue(4),
            });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        return (user, oid);
    }

    /// <summary>A record with every column set, and a child record pointing to it.</summary>
    private async Task<(Guid Id, Guid ChildId, string Name)> RecordAsync()
    {
        var name = "fsr-" + Guid.NewGuid().ToString("N")[..10];
        var id = await Admin.CreateAsync(new Entity(Table) { ["new_name"] = name, [S1] = "one", [S2] = "two", [N1] = 7, [Plain] = "plain" });
        var childId = await Admin.CreateAsync(new Entity(Child) { ["new_name"] = name, [Parent] = new EntityReference(Table, id) });
        return (id, childId, name);
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdFaultException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The call was expected to fail.");
    }

    private static async Task DeniedAsync(Func<Task> act, string because)
    {
        var fault = await FaultAsync(act);
        fault.ErrorCode.Should().Be(ErrorCodes.FieldReadDenied, because);
        fault.HttpStatus.Should().Be(403, because);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid oid, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> JsonAsync(Guid oid, string path)
    {
        var res = await GetAsync(oid, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task WebDeniedAsync(Guid oid, string path)
    {
        var res = await GetAsync(oid, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"GET {path}: {body}");
        body.Should().Contain(Denied);
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(20, 20);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string Fetch(string inner, string attributes = "<attribute name='new_name' />", bool aggregate = false) =>
        $"<fetch{(aggregate ? " aggregate='true'" : "")}><entity name='{Table}'>{attributes}{inner}</entity></fetch>";

    // ---- F5: no masking --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnMaskedData_true_is_refused_with_400()
    {
        var (id, _, _) = await RecordAsync();
        var retrieve = new RetrieveRequest { Target = new EntityReference(Table, id), ColumnSet = new ColumnSet(S1) };
        var query = new RetrieveMultipleRequest { Query = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) } };

        // Leaving it out or false changes nothing: values are never masked.
        retrieve["UnMaskedData"] = false;
        ((RetrieveResponse)await Admin.ExecuteAsync(retrieve)).Entity[S1].Should().Be("one");

        foreach (OrganizationRequest request in new OrganizationRequest[] { retrieve, query })
        {
            request["UnMaskedData"] = true;
            var fault = await FaultAsync(() => Admin.ExecuteAsync(request));
            fault.HttpStatus.Should().Be(400, request.RequestName);
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            fault.Message.Should().Contain("UnMaskedData").And.Contain("mask");
        }

        // The core operations refuse it too, so a plug-in's service (which goes straight to the pipeline) is covered.
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.IPipelineCore>();
        foreach (var make in new Func<OrganizationRequest>[]
                 {
                     () => new RetrieveRequest { Target = new EntityReference(Table, id), ColumnSet = new ColumnSet(S1), ["UnMaskedData"] = true },
                     () => new RetrieveMultipleRequest { Query = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) }, ["UnMaskedData"] = true },
                 })
        {
            var request = make();
            XrmdFaultException? coreFault = null;
            await TestEntities.InSessionAsync(fx.ConnectionString, async session =>
            {
                coreFault = await FaultAsync(() => core.ExecuteCoreAsync(request, Caller.Internal, session, default));
            });
            (coreFault!.HttpStatus, coreFault.ErrorCode).Should().Be((400, ErrorCodes.InvalidArgument), request.RequestName);
        }

        string[] paths =
            [$"{Set}?UnMaskedData=true", $"{Set}({id})?$select={S1}&UnMaskedData=true", $"{Set}({id})/{Doc}?UnMaskedData=true", $"{Set}({id})/{Img}/$value?UnMaskedData=true"];
        foreach (var path in paths)
        {
            var res = await GetAsync(fx.AdminObjectId, path);
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"GET {path}: {body}");
            body.Should().Contain("UnMaskedData");
        }
        (await JsonAsync(fx.AdminObjectId, $"{Set}({id})?$select={S1}&UnMaskedData=false"))[S1]!.GetValue<string>().Should().Be("one");
    }

    // ---- Review Focus 1 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_users_see_different_secured_columns()
    {
        var (id, childId, name) = await RecordAsync();
        var (_, oidA) = await UserAsync(S1);
        var (_, oidB) = await UserAsync(S2);

        foreach (var (oid, readable, hidden, value) in new[] { (oidA, S1, S2, "one"), (oidB, S2, S1, "two") })
        {
            var service = ServiceFor(fx, oid);

            // Retrieve, AllColumns and named columns: the hidden column is there, null.
            foreach (var columns in new[] { new ColumnSet(true), new ColumnSet(S1, S2, Plain) })
            {
                var e = await service.RetrieveAsync(Table, id, columns);
                e[readable].Should().Be(value);
                e.Attributes.Should().ContainKey(hidden).WhoseValue.Should().BeNull();
                e.FormattedValues.Should().NotContainKey(hidden);
                e[Plain].Should().Be("plain");
            }
            // A column not asked for is not added.
            (await service.RetrieveAsync(Table, id, new ColumnSet(Plain))).Attributes.Should().NotContainKey(hidden);
            (await service.RetrieveAsync(Table, id, new ColumnSet(true)))[N1].Should().BeNull("new_fsn1 is in neither profile");

            // RetrieveMultiple with a QueryExpression.
            var query = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1, S2) };
            query.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
            var row = (await service.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle().Subject;
            row[readable].Should().Be(value);
            row.Attributes.Should().ContainKey(hidden).WhoseValue.Should().BeNull();

            // FetchXML with a link-entity to the table: the link's hidden column is an aliased null.
            var fetch = $"""
                <fetch><entity name='{Child}'><attribute name='new_name' /><filter><condition attribute='new_name' operator='eq' value='{name}' /></filter>
                <link-entity name='{Table}' from='new_fsreadid' to='{Parent}' alias='p'><attribute name='{S1}' /><attribute name='{S2}' /></link-entity>
                </entity></fetch>
                """;
            var linked = (await service.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Should().ContainSingle().Subject;
            linked.GetAttributeValue<AliasedValue>("p." + readable).Value.Should().Be(value);
            linked.Attributes.Should().ContainKey("p." + hidden);
            linked.GetAttributeValue<AliasedValue>("p." + hidden).Value.Should().BeNull();

            // Web API: GET with and without $select, a collection, $expand and FetchXML; the hidden column is absent.
            foreach (var path in new[]
                     {
                         $"{Set}({id})?$select={S1},{S2},{Plain}", $"{Set}({id})",
                     })
            {
                var json = await JsonAsync(oid, path);
                json[readable]!.GetValue<string>().Should().Be(value, path);
                json.ContainsKey(hidden).Should().BeFalse(path);
                json[Plain]!.GetValue<string>().Should().Be("plain");
            }
            var list = (await JsonAsync(oid, $"{Set}?$select={S1},{S2}&$filter=new_name eq '{name}'"))["value"]!.AsArray().Single()!.AsObject();
            list[readable]!.GetValue<string>().Should().Be(value);
            list.ContainsKey(hidden).Should().BeFalse();

            var expanded = (await JsonAsync(oid, $"{ChildSet}({childId})?$expand={Navigation}($select={S1},{S2})"))[Navigation]!.AsObject();
            expanded[readable]!.GetValue<string>().Should().Be(value);
            expanded.ContainsKey(hidden).Should().BeFalse();
            var expandedList = (await JsonAsync(oid, $"{ChildSet}?$filter=new_name eq '{name}'&$expand={Navigation}($select={S1},{S2})"))["value"]!
                .AsArray().Single()![Navigation]!.AsObject();
            expandedList[readable]!.GetValue<string>().Should().Be(value);
            expandedList.ContainsKey(hidden).Should().BeFalse();

            var fetched = (await JsonAsync(oid, $"{ChildSet}?fetchXml={Uri.EscapeDataString(fetch)}"))["value"]!.AsArray().Single()!.AsObject();
            fetched["p." + readable]!.GetValue<string>().Should().Be(value);
            fetched.ContainsKey("p." + hidden).Should().BeFalse();
        }

        // A real null is still a null: the administrator reads everything.
        var all = await Admin.RetrieveAsync(Table, id, new ColumnSet(true));
        (all[S1], all[S2], all[N1]).Should().Be(("one", "two", 7));
    }

    // ---- Review Focus 2 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Filter_on_secured_column_is_refused_everywhere()
    {
        var (_, _, name) = await RecordAsync();
        var (_, oidA) = await UserAsync(S1, N1);
        var (_, oidB) = await UserAsync(S2);
        var a = ServiceFor(fx, oidA);
        var b = ServiceFor(fx, oidB);
        var byName = $"<filter><condition attribute='new_name' operator='eq' value='{name}' /></filter>";

        var fetches = new (string Because, string Xml)[]
        {
            ("condition", Fetch($"<filter><condition attribute='new_name' operator='eq' value='{name}' /><condition attribute='{S1}' operator='eq' value='one' /></filter>")),
            ("nested filter", Fetch($"<filter><condition attribute='new_name' operator='eq' value='{name}' /><filter type='or'><condition attribute='{Plain}' operator='null' /><filter><condition attribute='{S1}' operator='like' value='o%' /></filter></filter></filter>")),
            ("order", Fetch(byName + $"<order attribute='{S1}' />")),
            ("link from and to", Fetch(byName + $"<link-entity name='{Table}' from='{S1}' to='{S1}' alias='x'>{byName}</link-entity>")),
            ("link-entity filter", Fetch(byName + $"<link-entity name='{Table}' from='new_fsreadid' to='new_fsreadid' alias='x'><filter><condition attribute='{S1}' operator='not-null' /></filter></link-entity>")),
            ("sum", Fetch(byName, $"<attribute name='{N1}' alias='total' aggregate='sum' />", aggregate: true)),
            ("countcolumn", Fetch(byName, $"<attribute name='{N1}' alias='total' aggregate='countcolumn' />", aggregate: true)),
            ("groupby", Fetch(byName, $"<attribute name='{N1}' alias='g' groupby='true' /><attribute name='new_fsreadid' alias='n' aggregate='count' />", aggregate: true)),
        };
        foreach (var (because, xml) in fetches)
        {
            await DeniedAsync(() => b.RetrieveMultipleAsync(new FetchExpression(xml)), because);
            (await a.RetrieveMultipleAsync(new FetchExpression(xml))).Entities.Should().ContainSingle(because);
        }

        // count(*) and countcolumn on the primary key read no secured value: allowed.
        var count = Fetch(byName, $"<attribute name='{N1}' alias='n' aggregate='count' /><attribute name='new_fsreadid' alias='ids' aggregate='countcolumn' />", aggregate: true);
        var counted = (await b.RetrieveMultipleAsync(new FetchExpression(count))).Entities.Should().ContainSingle().Subject;
        counted.GetAttributeValue<AliasedValue>("n").Value.Should().Be(1);
        // Its formatted value is the count's, never a label or value of the secured column (plan 3.5 UI-1f, task 4b).
        counted.FormattedValues["n"].Should().Be("1");

        // QueryExpression, nested and through a link-entity alias, and QueryByAttribute.
        var expression = new QueryExpression(Table) { ColumnSet = new ColumnSet("new_name") };
        expression.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
        expression.Criteria.AddFilter(LogicalOperator.Or).AddCondition(S1, ConditionOperator.Equal, "one");
        await DeniedAsync(() => b.RetrieveMultipleAsync(expression), "QueryExpression");
        (await a.RetrieveMultipleAsync(expression)).Entities.Should().ContainSingle();

        var throughLink = new QueryExpression(Child) { ColumnSet = new ColumnSet("new_name") };
        throughLink.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
        throughLink.AddLink(Table, Parent, "new_fsreadid").EntityAlias = "p";
        throughLink.Criteria.AddCondition("p", S1, ConditionOperator.Equal, "one");
        await DeniedAsync(() => b.RetrieveMultipleAsync(throughLink), "condition through a link alias");
        (await a.RetrieveMultipleAsync(throughLink)).Entities.Should().ContainSingle();

        var byAttribute = new QueryByAttribute(Table) { ColumnSet = new ColumnSet("new_name") };
        byAttribute.AddAttributeValue("new_name", name);
        byAttribute.AddAttributeValue(S1, "one");
        await DeniedAsync(() => b.RetrieveMultipleAsync(byAttribute), "QueryByAttribute");
        (await a.RetrieveMultipleAsync(byAttribute)).Entities.Should().ContainSingle();

        // Web API: $filter, $orderby, FetchXML; $filter inside $expand is not supported at all (400, never data).
        var nameFilter = Uri.EscapeDataString($"new_name eq '{name}'");
        foreach (var path in new[]
                 {
                     $"{Set}?$select=new_name&$filter={nameFilter} and {S1} eq 'one'",
                     $"{Set}?$select=new_name&$filter={nameFilter}&$orderby={S1} desc",
                     $"{Set}?fetchXml={Uri.EscapeDataString(fetches[0].Xml)}",
                 })
        {
            await WebDeniedAsync(oidB, path);
            (await JsonAsync(oidA, path))["value"]!.AsArray().Should().ContainSingle(path);
        }
        var expandFilter = await GetAsync(oidB, $"{ChildSet}?$filter={nameFilter}&$expand={Navigation}($filter={S1} eq 'one')");
        expandFilter.IsSuccessStatusCode.Should().BeFalse("$filter inside a single-valued $expand is refused");
        (await expandFilter.Content.ReadAsStringAsync()).Should().NotContain(name);
    }

    // ---- companions (final review C1) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Companions_of_a_secured_column_follow_its_rights()
    {
        var (id, _, name) = await RecordAsync();
        await Admin.UpdateAsync(new Entity(Table, id) { [Salary] = new Money(1234.5m), [Img] = Png() });
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(Table, id), FileAttributeName = Doc, FileName = "merger.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String(BitConverter.GetBytes(0L));
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = "x"u8.ToArray() });
        await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "merger.txt", MimeType = "text/plain",
        });

        string[] companions = [Salary + "_base", Doc + "_name", Img + "id", Img + "_timestamp", Img + "_url"];
        var (_, oidNone) = await UserAsync(S1);
        var (_, oidAll) = await UserAsync(Salary, Doc, Img);
        var none = ServiceFor(fx, oidNone);
        var all = ServiceFor(fx, oidAll);

        // Without read on the main column, every companion reads as null, through AllColumns and when named.
        foreach (var columns in new[] { new ColumnSet(true), new ColumnSet(companions) })
        {
            var hidden = await none.RetrieveAsync(Table, id, columns);
            foreach (var companion in companions)
                hidden.Attributes.Should().ContainKey(companion).WhoseValue.Should().BeNull(companion);
        }
        var shown = await all.RetrieveAsync(Table, id, new ColumnSet(companions));
        shown.GetAttributeValue<Money>(Salary + "_base").Value.Should().Be(1234.5m);
        shown[Doc + "_name"].Should().Be("merger.txt");
        shown[Img + "id"].Should().NotBeNull();
        shown[Img + "_timestamp"].Should().NotBeNull();

        // A filter or an order on a companion is refused like one on its column; the reader of the column may.
        var byName = $"<filter><condition attribute='new_name' operator='eq' value='{name}' /></filter>";
        foreach (var (because, xml) in new[]
                 {
                     ("money base", Fetch($"<filter><condition attribute='new_name' operator='eq' value='{name}' /><condition attribute='{Salary}_base' operator='gt' value='1000' /></filter>")),
                     ("file name", Fetch($"<filter><condition attribute='new_name' operator='eq' value='{name}' /><condition attribute='{Doc}_name' operator='like' value='merger%' /></filter>")),
                     ("image id", Fetch(byName + $"<order attribute='{Img}id' />")),
                     ("sum of the base", Fetch(byName, $"<attribute name='{Salary}_base' alias='total' aggregate='sum' />", aggregate: true)),
                 })
        {
            await DeniedAsync(() => none.RetrieveMultipleAsync(new FetchExpression(xml)), because);
            (await all.RetrieveMultipleAsync(new FetchExpression(xml))).Entities.Should().ContainSingle(because);
        }

        // Web API: absent from the JSON, refused in $filter.
        var json = await JsonAsync(oidNone, $"{Set}({id})?$select={Salary}_base,{Doc}_name,{Plain}");
        json.ContainsKey(Salary + "_base").Should().BeFalse();
        json.ContainsKey(Doc + "_name").Should().BeFalse();
        await WebDeniedAsync(oidNone, $"{Set}?$select=new_name&$filter={Salary}_base gt 1000");
        (await JsonAsync(oidAll, $"{Set}({id})?$select={Salary}_base"))[Salary + "_base"]!.GetValue<decimal>().Should().Be(1234.5m);

        // A field share on the money column covers its base on that record.
        var (userShared, oidShared) = await UserAsync(S1);
        await InsertShareAsync(id, Salary, userShared);
        var shared = await ServiceFor(fx, oidShared).RetrieveAsync(Table, id, new ColumnSet(Salary, Salary + "_base", Doc + "_name"));
        shared.GetAttributeValue<Money>(Salary).Value.Should().Be(1234.5m);
        shared.GetAttributeValue<Money>(Salary + "_base").Value.Should().Be(1234.5m);
        shared.Attributes.Should().ContainKey(Doc + "_name").WhoseValue.Should().BeNull("the share is on the money column only");
    }

    private async Task InsertShareAsync(Guid record, string column, Guid principal)
    {
        var attributeId = Store.GetEntity(Table)!.Attributes.Single(x => x.LogicalName == column).MetadataId!.Value;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"""
                INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                    attributeid, principalid, principalidtype, readaccess, updateaccess)
                VALUES (NEWID(), @record, @otc, @table, @attribute, @principal, 8, 1, 0)
                """);
            cmd.Parameters.AddWithValue("@record", record);
            cmd.Parameters.AddWithValue("@otc", Store.GetEntity(Table)!.ObjectTypeCode!.Value);
            cmd.Parameters.AddWithValue("@table", Table);
            cmd.Parameters.AddWithValue("@attribute", attributeId);
            cmd.Parameters.AddWithValue("@principal", principal);
            await cmd.ExecuteNonQueryAsync();
        }
        Cache(fx).Invalidate();
    }

    // ---- field shares -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Field_share_reveals_the_column_on_its_record_only()
    {
        var (shared, _, sharedName) = await RecordAsync();
        var (other, _, otherName) = await RecordAsync();
        var (userB, oidB) = await UserAsync(S2);
        var attributeId = Store.GetEntity(Table)!.Attributes.Single(x => x.LogicalName == S1).MetadataId!.Value;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"""
                INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                    attributeid, principalid, principalidtype, readaccess, updateaccess)
                VALUES (NEWID(), @record, @otc, @table, @attribute, @principal, 8, 1, 0)
                """);
            cmd.Parameters.AddWithValue("@record", shared);
            cmd.Parameters.AddWithValue("@otc", Store.GetEntity(Table)!.ObjectTypeCode!.Value);
            cmd.Parameters.AddWithValue("@table", Table);
            cmd.Parameters.AddWithValue("@attribute", attributeId);
            cmd.Parameters.AddWithValue("@principal", userB);
            await cmd.ExecuteNonQueryAsync();
        }
        Cache(fx).Invalidate();
        var b = ServiceFor(fx, oidB);

        (await b.RetrieveAsync(Table, shared, new ColumnSet(S1, S2)))[S1].Should().Be("one", "the share grants read on this record");
        var unshared = await b.RetrieveAsync(Table, other, new ColumnSet(S1, S2));
        unshared.Attributes.Should().ContainKey(S1).WhoseValue.Should().BeNull("the share is for the other record");

        var query = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) };
        query.Criteria.AddCondition("new_name", ConditionOperator.In, sharedName, otherName);
        var rows = (await b.RetrieveMultipleAsync(query)).Entities.ToDictionary(e => e.Id);
        rows[shared][S1].Should().Be("one");
        rows[other].Attributes.Should().ContainKey(S1).WhoseValue.Should().BeNull();

        // Through a link-entity too.
        var fetch = $"""
            <fetch><entity name='{Child}'><attribute name='new_name' /><filter><condition attribute='new_name' operator='in'><value>{sharedName}</value><value>{otherName}</value></condition></filter>
            <link-entity name='{Table}' from='new_fsreadid' to='{Parent}' alias='p'><attribute name='{S1}' /><attribute name='new_name' /></link-entity>
            </entity></fetch>
            """;
        var linked = (await b.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities
            .ToDictionary(e => (string)e.GetAttributeValue<AliasedValue>("p.new_name").Value);
        linked[sharedName].GetAttributeValue<AliasedValue>("p." + S1).Value.Should().Be("one");
        linked[otherName].GetAttributeValue<AliasedValue>("p." + S1).Value.Should().BeNull();

        // The Web API writes it on the shared record only.
        (await JsonAsync(oidB, $"{Set}({shared})?$select={S1}"))[S1]!.GetValue<string>().Should().Be("one");
        (await JsonAsync(oidB, $"{Set}({other})?$select={S1}")).ContainsKey(S1).Should().BeFalse();

        // A share never allows a filter on the column (F3).
        var filtered = new QueryExpression(Table) { ColumnSet = new ColumnSet(S1) };
        filtered.Criteria.AddCondition(S1, ConditionOperator.Equal, "one");
        await DeniedAsync(() => b.RetrieveMultipleAsync(filtered), "a share does not allow filtering");
    }

    [Fact]
    public async Task Field_share_on_a_secured_lookup_keeps_its_target_and_name()
    {
        // Lookups cannot be secured through metadata; the in-memory definition of a Customer column is marked secured for this
        // test, to check the companions (target table, name) of a polymorphic lookup a share grants.
        var column = Store.GetEntity("contact")!.Attributes.Single(a => a.LogicalName == "parentcustomerid");
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, _root, oid);
        await GrantAsync(fx, user, team: false, ("prvReadContact", DepthMask.Global), ("prvReadAccount", DepthMask.Global));
        var accountName = "fsr-acc-" + Guid.NewGuid().ToString("N")[..8];
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = accountName });
        var shared = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "shared", ["parentcustomerid"] = new EntityReference("account", account) });
        var other = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "other", ["parentcustomerid"] = new EntityReference("account", account) });
        InMemoryMetadata.ChangeColumn(Store, "contact", "parentcustomerid", c => c.IsSecured = true);
        Cache(fx).Invalidate();
        try
        {
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var cmd = TestSql.Command(conn, $"""
                    INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                        attributeid, principalid, principalidtype, readaccess, updateaccess)
                    VALUES (NEWID(), @record, 2, 'contact', @attribute, @principal, 8, 1, 0)
                    """);
                cmd.Parameters.AddWithValue("@record", shared);
                cmd.Parameters.AddWithValue("@attribute", column.MetadataId!.Value);
                cmd.Parameters.AddWithValue("@principal", user);
                await cmd.ExecuteNonQueryAsync();
            }
            Cache(fx).Invalidate();
            var service = ServiceFor(fx, oid);

            var granted = await service.RetrieveAsync("contact", shared, new ColumnSet("parentcustomerid"));
            var reference = granted.GetAttributeValue<EntityReference>("parentcustomerid");
            (reference.LogicalName, reference.Id, reference.Name).Should().Be(("account", account, accountName));
            granted.FormattedValues["parentcustomerid"].Should().Be(accountName);

            var hidden = await service.RetrieveAsync("contact", other, new ColumnSet("parentcustomerid"));
            hidden.Attributes.Should().ContainKey("parentcustomerid").WhoseValue.Should().BeNull();
            hidden.FormattedValues.Should().NotContainKey("parentcustomerid");
        }
        finally
        {
            InMemoryMetadata.ChangeColumn(Store, "contact", "parentcustomerid", c => c.IsSecured = false);
            Cache(fx).Invalidate();
        }
    }

    // ---- F4: downloads ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Secured_file_column_download_needs_read()
    {
        var (id, _, _) = await RecordAsync();
        var content = "secret file"u8.ToArray();
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(Table, id), FileAttributeName = Doc, FileName = "secret.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String(BitConverter.GetBytes(0L));
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = content });
        await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "secret.txt", MimeType = "text/plain",
        });

        var (_, oidA) = await UserAsync(S1, Doc);
        var (_, oidB) = await UserAsync(S2);
        var download = new InitializeFileBlocksDownloadRequest { Target = new EntityReference(Table, id), FileAttributeName = Doc };

        await DeniedAsync(() => ServiceFor(fx, oidB).ExecuteAsync(download), "the user cannot read new_fsdoc");
        await WebDeniedAsync(oidB, $"{Set}({id})/{Doc}/$value");
        (await ServiceFor(fx, oidB).RetrieveAsync(Table, id, new ColumnSet(Doc))).Attributes.Should().ContainKey(Doc)
            .WhoseValue.Should().BeNull("the file id of a column the user cannot read is hidden too");

        // The thumbnail of a secured image column ($value without size=full) needs read too.
        await Admin.UpdateAsync(new Entity(Table, id) { [Img] = Png() });
        await WebDeniedAsync(oidB, $"{Set}({id})/{Img}/$value");
        var (_, oidImage) = await UserAsync(Img);
        var thumbnail = await GetAsync(oidImage, $"{Set}({id})/{Img}/$value");
        thumbnail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await thumbnail.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();

        var started = (InitializeFileBlocksDownloadResponse)await ServiceFor(fx, oidA).ExecuteAsync(download);
        started.FileSizeInBytes.Should().Be(content.Length);
        var res = await GetAsync(oidA, $"{Set}({id})/{Doc}/$value");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsByteArrayAsync()).Should().Equal(content);
    }

    [Fact]
    public async Task Attachment_rows_of_an_unreadable_secured_file_column_are_hidden()
    {
        var (id, _, _) = await RecordAsync();
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(Table, id), FileAttributeName = Doc, FileName = "board-minutes.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String(BitConverter.GetBytes(0L));
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = "minutes"u8.ToArray() });
        await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "board-minutes.txt", MimeType = "text/plain",
        });
        var query = new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("filename", "regardingfieldname") };
        query.Criteria.AddCondition("objectid", ConditionOperator.Equal, id);
        var attachmentId = (await Admin.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle().Subject.Id;
        var linked = new FetchExpression(Fetch($"<filter><condition attribute='{Table}id' operator='eq' value='{id}' /></filter>"
            + $"<link-entity name='fileattachment' from='objectid' to='{Table}id' alias='f'><attribute name='filename' /></link-entity>"));

        var (_, oidNone) = await UserAsync(S1);
        var (_, oidReader) = await UserAsync(Doc);
        var none = ServiceFor(fx, oidNone);

        // Without read on the column: no row through RetrieveMultiple, Retrieve by id or a link from the record (the Web API has
        // no collection-valued $expand).
        (await none.RetrieveMultipleAsync(query)).Entities.Should().BeEmpty("the row would reveal the secured file's name");
        var fault = await FaultAsync(() => none.RetrieveAsync("fileattachment", attachmentId, new ColumnSet("filename")));
        fault.HttpStatus.Should().BeOneOf(403, 404);
        (await none.RetrieveMultipleAsync(linked)).Entities.Should().BeEmpty("the inner link finds no visible attachment");

        // A profile reader sees the row everywhere.
        var reader = ServiceFor(fx, oidReader);
        (await reader.RetrieveMultipleAsync(query)).Entities.Should().ContainSingle()
            .Which.GetAttributeValue<string>("filename").Should().Be("board-minutes.txt");
        (await reader.RetrieveAsync("fileattachment", attachmentId, new ColumnSet("filename"))).Id.Should().Be(attachmentId);
        (await reader.RetrieveMultipleAsync(linked)).Entities.Should().ContainSingle()
            .Which.GetAttributeValue<AliasedValue>("f.filename").Value.Should().Be("board-minutes.txt");
    }

    // ---- F11: auditing ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_details_hide_unreadable_secured_columns()
    {
        var organization = (await Admin.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet(false) })).Entities[0].Id;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = Table, IsAuditEnabled = new BooleanManagedProperty(true) } });
        try
        {
            var (id, _, _) = await RecordAsync();
            await Admin.UpdateAsync(new Entity(Table, id) { [S1] = "one-2", [S2] = "two-2", [Plain] = "plain-2" });
            var (userB, oidB) = await UserAsync(S2);
            await GrantAsync(fx, userB, team: false, (PrivilegeNames.ReadAuditSummary, DepthMask.Global),
                (PrivilegeNames.ReadRecordAuditHistory, DepthMask.Global));
            var b = ServiceFor(fx, oidB);
            var target = new EntityReference(Table, id);

            var history = ((RetrieveRecordChangeHistoryResponse)await b.ExecuteAsync(new RetrieveRecordChangeHistoryRequest { Target = target }))
                .AuditDetailCollection.AuditDetails.Cast<AttributeAuditDetail>().ToList();
            history.Should().HaveCount(2);
            foreach (var detail in history)
            {
                detail.NewValue.Attributes.Should().NotContainKey(S1).And.NotContainKey(N1).And.ContainKey(S2);
                detail.OldValue.Attributes.Should().NotContainKey(S1);
                detail.NewValue.FormattedValues.Should().NotContainKey(N1);
                detail.DeletedAttributes.Values.Should().NotContain(S1);
                detail.AuditRecord.GetAttributeValue<string>("changedata").Should().NotContain("one").And.Contain("two");
            }
            var update = history[0];
            update.OldValue[S2].Should().Be("two");
            update.NewValue[Plain].Should().Be("plain-2");

            var single = ((RetrieveAuditDetailsResponse)await b.ExecuteAsync(new RetrieveAuditDetailsRequest { AuditId = update.AuditRecord.Id }))
                .AuditDetail.Should().BeOfType<AttributeAuditDetail>().Subject;
            single.NewValue.Attributes.Should().NotContainKey(S1).And.ContainKey(S2);

            await DeniedAsync(() => b.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest { Target = target, AttributeLogicalName = S1 }),
                "the attribute history of a column the user cannot read");
            ((RetrieveAttributeChangeHistoryResponse)await b.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest { Target = target, AttributeLogicalName = S2 }))
                .AuditDetailCollection.AuditDetails.Should().HaveCount(2);

            // The audit table queried directly (F11): the change data has no value of the column, the filter on it is refused.
            var rows = new QueryExpression("audit") { ColumnSet = new ColumnSet(true) };
            rows.Criteria.AddCondition("objectid", ConditionOperator.Equal, id);
            var changeData = (await b.RetrieveMultipleAsync(rows)).Entities.Select(e => e.GetAttributeValue<string>("changedata")).ToList();
            changeData.Should().HaveCount(2).And.OnlyContain(c => c != null && !c.Contains("one") && c.Contains("two"));
            var fetched = (await b.RetrieveMultipleAsync(new FetchExpression(
                    $"<fetch><entity name='audit'><attribute name='changedata' /><filter><condition attribute='objectid' operator='eq' value='{id}' /></filter></entity></fetch>")))
                .Entities.Select(e => e.GetAttributeValue<string>("changedata")).ToList();
            fetched.Should().HaveCount(2).And.OnlyContain(c => c != null && !c.Contains("one") && c.Contains("two"));
            var probe = new QueryExpression("audit") { ColumnSet = new ColumnSet("auditid") };
            probe.Criteria.AddCondition("changedata", ConditionOperator.Like, "%one%");
            await DeniedAsync(() => b.RetrieveMultipleAsync(probe), "the change data cannot be filtered on by a field-restricted caller");
            (await Admin.RetrieveMultipleAsync(rows)).Entities.Select(e => e.GetAttributeValue<string>("changedata"))
                .Should().Contain(c => c.Contains("one-2"), "the reader of every column gets the change data in full");

            // The administrator still reads everything: the rows are stored in full.
            var full = ((RetrieveRecordChangeHistoryResponse)await Admin.ExecuteAsync(new RetrieveRecordChangeHistoryRequest { Target = target }))
                .AuditDetailCollection.AuditDetails.Cast<AttributeAuditDetail>().First();
            full.NewValue[S1].Should().Be("one-2");
        }
        finally
        {
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = Table, IsAuditEnabled = new BooleanManagedProperty(false) } });
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
            await RevokeAuditPrivilegesAsync(fx);
        }
    }
}
