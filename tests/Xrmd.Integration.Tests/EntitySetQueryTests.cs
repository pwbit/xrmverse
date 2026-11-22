using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

/// <summary><c>GET {entityset}</c> with OData query options, paging and formatted values over HTTP.</summary>
[Collection("xrmd")]
public sealed class EntitySetQueryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Set = TestEntities.ProjectSet;
    private const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";

    private readonly string _tag = "ESQ" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _ids = [];
    private Guid _adminId;
    private OrganizationService _svc = null!;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(store);
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
            _adminId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        var svc = _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        for (var i = 1; i <= 12; i++)
            _ids.Add(await svc.CreateAsync(new Entity(TestEntities.Project)
            {
                ["pb_name"] = $"{_tag}-{i:00}",
                ["pb_count"] = i,
                ["pb_priority"] = new OptionSetValue(i % 3 + 1),
                ["pb_customerid"] = i == 12 ? null : new EntityReference("systemuser", _adminId),
            }));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> GetAsync(string pathAndQuery, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery.StartsWith("http", StringComparison.Ordinal) ? pathAndQuery : Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private string Tagged => $"startswith(pb_name,'{_tag}')";

    [Fact]
    public async Task Count_annotations_only_when_preferred()
    {
        const string prefer = "odata.include-annotations=\"Microsoft.Dynamics.CRM.totalrecordcount,Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded\"";
        var query = $"{Set}?$select=pb_name&$filter={Tagged} and pb_count gt 6";

        var plain = await JsonAsync(await GetAsync(query + "&$count=true"));
        plain["@odata.count"]!.GetValue<int>().Should().Be(6);
        plain.ContainsKey("@Microsoft.Dynamics.CRM.totalrecordcount").Should().BeFalse();

        var preferred = await JsonAsync(await GetAsync(query + "&$count=true", ("Prefer", prefer)));
        preferred["@odata.count"]!.GetValue<int>().Should().Be(6);
        preferred["@Microsoft.Dynamics.CRM.totalrecordcount"]!.GetValue<int>().Should().Be(6);
        preferred["@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded"]!.GetValue<bool>().Should().BeFalse();

        var uncounted = await JsonAsync(await GetAsync(query, ("Prefer", "odata.include-annotations=\"Microsoft.Dynamics.CRM.*\"")));
        uncounted.ContainsKey("@odata.count").Should().BeFalse();
        uncounted["@Microsoft.Dynamics.CRM.totalrecordcount"]!.GetValue<int>().Should().Be(-1);
        uncounted["@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded"]!.GetValue<bool>().Should().BeFalse();

        // returntotalrecordcount has the same result as $count (Learn: "Count rows using FetchXml").
        var fetch = $"<fetch count='2' returntotalrecordcount='true'><entity name='{TestEntities.Project}'><attribute name='pb_name' />"
            + $"<filter><condition attribute='pb_name' operator='like' value='{_tag}%' /><condition attribute='pb_count' operator='gt' value='6' /></filter></entity></fetch>";
        var fetched = await JsonAsync(await GetAsync($"{Set}?fetchXml={Uri.EscapeDataString(fetch)}", ("Prefer", "odata.include-annotations=\"*\"")));
        fetched["@odata.count"]!.GetValue<int>().Should().Be(6);
        fetched["@Microsoft.Dynamics.CRM.totalrecordcount"]!.GetValue<int>().Should().Be(6);
        fetched["@Microsoft.Dynamics.CRM.totalrecordcountlimitexceeded"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Get_entity_set_applies_select_filter_orderby_and_count()
    {
        var json = await JsonAsync(await GetAsync(
            $"{Set}?$select=pb_name&$filter={Tagged} and pb_count gt 6&$orderby=pb_count desc&$count=true"));

        json["@odata.context"]!.GetValue<string>().Should().EndWith($"{Api}$metadata#{Set}(pb_name)");
        json["@odata.count"]!.GetValue<int>().Should().Be(6);
        json.ContainsKey("@odata.nextLink").Should().BeFalse();
        var value = json["value"]!.AsArray();
        value.Should().HaveCount(6);
        value.Select(v => v!["pb_name"]!.GetValue<string>()).Should()
            .Equal(Enumerable.Range(7, 6).Reverse().Select(i => $"{_tag}-{i:00}"));
        var first = value[0]!.AsObject();
        first.ContainsKey("@odata.context").Should().BeFalse();
        first["@odata.etag"]!.GetValue<string>().Should().StartWith("W/\"");
        first["pb_projectid"]!.GetValue<string>().Should().Be(_ids[11].ToString());
        first.ContainsKey("pb_count").Should().BeFalse();
        first.ContainsKey("versionnumber").Should().BeFalse();
        first.Select(p => p.Key).Should().NotContain(k => k.Contains(FormattedValue));
    }

    [Fact]
    public async Task Get_entity_set_pages_with_max_page_size_and_next_link()
    {
        var url = $"{Set}?$select=pb_name&$filter={Tagged}";
        var ids = new List<Guid>();
        var pages = 0;
        while (url is not null)
        {
            var res = await GetAsync(url, ("Prefer", "odata.maxpagesize=5"));
            var json = await JsonAsync(res);
            pages++;
            var value = json["value"]!.AsArray();
            value.Count.Should().BeLessOrEqualTo(5);
            ids.AddRange(value.Select(v => Guid.Parse(v!["pb_projectid"]!.GetValue<string>())));
            url = json["@odata.nextLink"]?.GetValue<string>();
            if (url is not null)
            {
                url.Should().StartWith($"{fx.Client.BaseAddress!.ToString().TrimEnd('/')}{Api}{Set}?").And.Contain("$skiptoken=");
                Uri.UnescapeDataString(url).Should().Contain("<cookie pagenumber=\"" + (pages + 1) + "\"");
            }
            pages.Should().BeLessThan(10);
        }

        pages.Should().Be(3);
        ids.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(_ids);
    }

    [Fact]
    public async Task Get_entity_set_pages_by_page_number_when_ordered_by_another_column()
    {
        var url = $"{Set}?$select=pb_name&$filter={Tagged}&$orderby=pb_count desc";
        var names = new List<string>();
        while (url is not null)
        {
            var json = await JsonAsync(await GetAsync(url, ("Prefer", "odata.maxpagesize=5")));
            names.AddRange(json["value"]!.AsArray().Select(v => v!["pb_name"]!.GetValue<string>()));
            url = json["@odata.nextLink"]?.GetValue<string>();
            names.Count.Should().BeLessThan(20);
        }

        names.Should().Equal(Enumerable.Range(1, 12).Reverse().Select(i => $"{_tag}-{i:00}"));
    }

    [Fact]
    public async Task Get_entity_set_with_include_annotations_writes_formatted_values()
    {
        var res = await GetAsync($"{Set}?$select=pb_name,pb_priority,_pb_customerid_value&$filter={Tagged} and pb_count eq 4",
            ("Prefer", "odata.include-annotations=\"*\""));
        var json = await JsonAsync(res);

        res.Headers.GetValues("Preference-Applied").Single().Should().Be("odata.include-annotations=\"*\"");
        var record = json["value"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        record["pb_priority"]!.GetValue<int>().Should().Be(2);
        record["pb_priority" + FormattedValue]!.GetValue<string>().Should().Be("Medium");
        record["_pb_customerid_value"]!.GetValue<string>().Should().Be(_adminId.ToString());
        record["_pb_customerid_value" + FormattedValue]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        record.ContainsKey("pb_name" + FormattedValue).Should().BeFalse();
    }

    [Fact]
    public async Task Get_entity_set_with_the_formatted_value_annotation_only_writes_formatted_values()
    {
        var res = await GetAsync($"{Set}?$select=pb_priority&$filter={Tagged} and pb_count eq 4",
            ("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\""));
        var json = await JsonAsync(res);

        json["value"]![0]!["pb_priority" + FormattedValue]!.GetValue<string>().Should().Be("Medium");
    }

    [Fact]
    public async Task Get_single_record_with_include_annotations_writes_formatted_values()
    {
        var res = await GetAsync($"{Set}({_ids[3]})?$select=pb_priority,pb_count", ("Prefer", "odata.include-annotations=\"*\""));
        var json = await JsonAsync(res);

        res.Headers.GetValues("Preference-Applied").Single().Should().Be("odata.include-annotations=\"*\"");
        json["pb_priority" + FormattedValue]!.GetValue<string>().Should().Be("Medium");
        json["pb_count" + FormattedValue]!.GetValue<string>().Should().Be("4");

        var plain = await JsonAsync(await GetAsync($"{Set}({_ids[3]})?$select=pb_priority"));
        plain.Select(p => p.Key).Should().NotContain(k => k.Contains(FormattedValue));
    }

    [Fact]
    public async Task Get_entity_set_with_skip_is_a_400()
    {
        var json = await JsonAsync(await GetAsync($"{Set}?$skip=2"), HttpStatusCode.BadRequest);

        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
        json["error"]!["message"]!.GetValue<string>().Should().Contain("$skip");
    }

    [Theory]
    [InlineData("$filter=length(pb_name) eq 3")]
    [InlineData("$filter=pb_unknown eq 3")]
    [InlineData("$skiptoken=garbage")]
    [InlineData("$expand=ownerid")]
    [InlineData("$apply=groupby((pb_priority))&$select=pb_name")]
    public async Task Get_entity_set_with_invalid_or_unsupported_options_is_a_400(string query)
    {
        var json = await JsonAsync(await GetAsync($"{Set}?{query}"), HttpStatusCode.BadRequest);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
    }

    [Fact]
    public async Task Get_entity_set_with_max_page_size_over_5000_is_a_400()
    {
        var json = await JsonAsync(await GetAsync($"{Set}?$top=1", ("Prefer", "odata.maxpagesize=5001")), HttpStatusCode.BadRequest);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
    }
    // ---- fetchXml ----------------------------------------------------------------------------------------------

    private string TaggedFetch(string attributes, string fetchAttributes = "", string extraFilter = "", string links = "") =>
        $"<fetch{fetchAttributes}><entity name='pb_project'>{attributes}<filter><condition attribute='pb_name' operator='begins-with' value='{_tag}'/>" +
        $"{extraFilter}</filter>{links}</entity></fetch>";

    private Task<HttpResponseMessage> FetchAsync(string fetchXml, params (string Name, string Value)[] headers) =>
        GetAsync($"{Set}?fetchXml={Uri.EscapeDataString(fetchXml)}", headers);

    [Fact]
    public async Task Get_entity_set_with_fetchXml_writes_link_columns_as_alias_dot_attribute()
    {
        var fetch = TaggedFetch("<attribute name='pb_name'/>", extraFilter: "<condition attribute='pb_count' operator='eq' value='4'/>",
            links: "<link-entity name='systemuser' from='systemuserid' to='pb_customerid' alias='u'><attribute name='fullname'/>" +
                   "<attribute name='systemuserid'/><attribute name='businessunitid'/></link-entity>");
        var json = await JsonAsync(await FetchAsync(fetch, ("Prefer", "odata.include-annotations=\"*\"")));

        json.ContainsKey("@odata.nextLink").Should().BeFalse();
        json.ContainsKey("@Microsoft.Dynamics.CRM.morerecords").Should().BeFalse();
        var record = json["value"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        record["pb_name"]!.GetValue<string>().Should().Be($"{_tag}-04");
        record["u.fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        record["u.systemuserid"]!.GetValue<string>().Should().Be(_adminId.ToString());
        record.ContainsKey("u.fullname" + FormattedValue).Should().BeFalse(); // strings have no formatted value
        Guid.TryParse(record["u.businessunitid"]!.GetValue<string>(), out _).Should().BeTrue();
        record["u.businessunitid" + FormattedValue]!.GetValue<string>().Should().NotBeNullOrEmpty();
        record["pb_projectid"]!.GetValue<string>().Should().Be(_ids[3].ToString());
    }

    [Fact]
    public async Task Get_entity_set_with_fetchXml_pages_with_the_paging_cookie_annotation()
    {
        var ids = new List<Guid>();
        string? cookie = null;
        for (var page = 1; page <= 3; page++)
        {
            var pagingCookie = cookie is null ? "" : $" paging-cookie='{System.Security.SecurityElement.Escape(cookie)}'";
            var json = await JsonAsync(await FetchAsync(TaggedFetch("<attribute name='pb_name'/>", $" count='5' page='{page}'{pagingCookie}")));
            var value = json["value"]!.AsArray();
            ids.AddRange(value.Select(v => Guid.Parse(v!["pb_projectid"]!.GetValue<string>())));
            if (page < 3)
            {
                value.Should().HaveCount(5);
                json["@Microsoft.Dynamics.CRM.morerecords"]!.GetValue<bool>().Should().BeTrue();
                var annotation = XElement.Parse(json["@Microsoft.Dynamics.CRM.fetchxmlpagingcookie"]!.GetValue<string>());
                annotation.Attribute("pagenumber")!.Value.Should().Be(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
                cookie = Uri.UnescapeDataString(Uri.UnescapeDataString(annotation.Attribute("pagingcookie")!.Value));
                cookie.Should().StartWith("<cookie page=\"" + page + "\">");
            }
            else
            {
                value.Should().HaveCount(2);
                json.ContainsKey("@Microsoft.Dynamics.CRM.morerecords").Should().BeFalse();
                json.ContainsKey("@Microsoft.Dynamics.CRM.fetchxmlpagingcookie").Should().BeFalse();
            }
            json.ContainsKey("@odata.nextLink").Should().BeFalse();
        }

        ids.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(_ids);
    }

    [Fact]
    public async Task Get_entity_set_with_fetchXml_aggregate_writes_aliases()
    {
        var fetch = TaggedFetch("<attribute name='pb_priority' groupby='true' alias='p'/><attribute name='pb_count' aggregate='sum' alias='total'/>" +
                                "<order alias='p'/>", " aggregate='true'");
        var json = await JsonAsync(await FetchAsync(fetch));

        var value = json["value"]!.AsArray();
        value.Select(v => (v!["p"]!.GetValue<int>(), v["total"]!.GetValue<int>())).Should().Equal((1, 30), (2, 22), (3, 26));
        value[0]!.AsObject().ContainsKey("@odata.etag").Should().BeFalse();
    }

    [Theory]
    [InlineData("<fetch><entity name='systemuser'><attribute name='fullname'/></entity></fetch>", "")]
    [InlineData("<fetch><entity name='pb_project'><attribute name='pb_name'/></entity></fetch>", "&$top=1")]
    [InlineData("<fetch><entity name='pb_project'><bogus/></entity></fetch>", "")]
    public async Task Get_entity_set_with_an_invalid_fetchXml_is_a_400(string fetchXml, string extra)
    {
        var json = await JsonAsync(await GetAsync($"{Set}?fetchXml={Uri.EscapeDataString(fetchXml)}{extra}"), HttpStatusCode.BadRequest);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
    }

    // ---- $apply ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_entity_set_with_apply_groupby_sum_returns_one_object_per_group()
    {
        var json = await JsonAsync(await GetAsync(
            $"{Set}?$apply=filter({Tagged})/groupby((pb_priority),aggregate(pb_count with sum as total,$count as c))&$orderby=pb_priority"));

        var value = json["value"]!.AsArray();
        value.Select(v => (v!["pb_priority"]!.GetValue<int>(), v["total"]!.GetValue<int>(), v["c"]!.GetValue<int>()))
            .Should().Equal((1, 30, 4), (2, 22, 4), (3, 26, 4));
        value[0]!.AsObject().ContainsKey("@odata.etag").Should().BeFalse();
    }

    [Fact]
    public async Task Get_entity_set_with_apply_groupby_lookup_writes_the_lookup_value_property()
    {
        var json = await JsonAsync(await GetAsync(
            $"{Set}?$apply=filter({Tagged})/groupby((_pb_customerid_value),aggregate($count as c))&$orderby=c desc"));

        var value = json["value"]!.AsArray();
        value.Should().HaveCount(2);
        value[0]!["_pb_customerid_value"]!.GetValue<string>().Should().Be(_adminId.ToString());
        value[0]!["c"]!.GetValue<int>().Should().Be(11);
        value[1]!.AsObject().ContainsKey("_pb_customerid_value").Should().BeFalse();
        value[1]!["c"]!.GetValue<int>().Should().Be(1);
    }

    // ---- $expand -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_entity_set_with_expand_writes_the_nested_record()
    {
        var res = await GetAsync($"{Set}?$select=pb_name&$filter={Tagged} and pb_count ge 11&$orderby=pb_count&$expand=pb_CustomerId($select=fullname)",
            ("Prefer", "odata.include-annotations=\"*\""));
        var json = await JsonAsync(res);

        var value = json["value"]!.AsArray();
        value.Should().HaveCount(2);
        var customer = value[0]!["pb_CustomerId"]!.AsObject();
        customer["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        customer["systemuserid"]!.GetValue<string>().Should().Be(_adminId.ToString());
        customer.ContainsKey("@odata.etag").Should().BeFalse();
        value[0]!.AsObject().Select(p => p.Key).Should().NotContain(k => k.Contains('.') && !k.StartsWith('@') && !k.Contains('@'));
        value[1]!.AsObject().ContainsKey("pb_CustomerId").Should().BeTrue();
        value[1]!["pb_CustomerId"].Should().BeNull();
    }

    [Fact]
    public async Task Get_entity_set_with_expand_pages_with_next_link()
    {
        var url = $"{Set}?$select=pb_name&$filter={Tagged}&$expand=pb_CustomerId($select=fullname)";
        var names = new List<string>();
        while (url is not null)
        {
            var json = await JsonAsync(await GetAsync(url, ("Prefer", "odata.maxpagesize=5")));
            foreach (var v in json["value"]!.AsArray())
            {
                names.Add(v!["pb_name"]!.GetValue<string>());
                if (v["pb_name"]!.GetValue<string>() != $"{_tag}-12")
                    v["pb_CustomerId"]!["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
            }
            url = json["@odata.nextLink"]?.GetValue<string>();
            names.Count.Should().BeLessThan(20);
        }
        names.Should().HaveCount(12).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Get_single_record_with_expand_writes_the_nested_record()
    {
        var json = await JsonAsync(await GetAsync($"{Set}({_ids[3]})?$select=pb_name&$expand=pb_CustomerId($select=fullname)"));

        json["@odata.context"]!.GetValue<string>().Should().EndWith("/$entity");
        json["pb_name"]!.GetValue<string>().Should().Be($"{_tag}-04");
        json["@odata.etag"]!.GetValue<string>().Should().StartWith("W/\"");
        json["pb_CustomerId"]!["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
    }

    [Fact]
    public async Task Get_single_record_with_expand_without_select_writes_all_target_columns_and_etag()
    {
        var json = await JsonAsync(await GetAsync($"{Set}({_ids[3]})?$expand=pb_CustomerId"));

        var customer = json["pb_CustomerId"]!.AsObject();
        customer["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        customer["@odata.etag"]!.GetValue<string>().Should().StartWith("W/\"");
    }

    [Fact]
    public async Task Get_single_record_with_fetchXml_is_a_400()
    {
        var fetch = Uri.EscapeDataString("<fetch><entity name='pb_project'><attribute name='pb_name'/></entity></fetch>");
        var json = await JsonAsync(await GetAsync($"{Set}({_ids[0]})?fetchXml={fetch}"), HttpStatusCode.BadRequest);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
    }

    [Fact]
    public async Task Get_missing_single_record_with_expand_is_a_404()
    {
        var res = await GetAsync($"{Set}({Guid.NewGuid()})?$expand=pb_CustomerId($select=fullname)");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- cross-frontend parity -------------------------------------------------------------------------------------

    /// <summary>
    /// The same query (name prefix, <c>pb_count</c> ≠ 5, ordered by name, two records per page) through QueryExpression,
    /// FetchXML and OData walks the same records in the same order, page after page.
    /// </summary>
    [Fact]
    public async Task Same_query_through_QueryExpression_FetchXml_and_OData_returns_the_same_pages()
    {
        const int pageSize = 2;
        const int excluded = 5;
        var expected = Enumerable.Range(1, 12).Where(i => i != excluded).Select(i => _ids[i - 1]).ToList();

        // QueryExpression, paging cookie from page to page.
        var qe = new Microsoft.Xrm.Sdk.Query.QueryExpression(TestEntities.Project)
        {
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("pb_name"),
            PageInfo = new Microsoft.Xrm.Sdk.Query.PagingInfo { Count = pageSize, PageNumber = 1 },
        };
        qe.Criteria.AddCondition("pb_name", Microsoft.Xrm.Sdk.Query.ConditionOperator.BeginsWith, _tag);
        qe.Criteria.AddCondition("pb_count", Microsoft.Xrm.Sdk.Query.ConditionOperator.NotEqual, excluded);
        qe.AddOrder("pb_name", Microsoft.Xrm.Sdk.Query.OrderType.Ascending);
        var viaQueryExpression = new List<Guid>();
        var qePages = new List<int>();
        while (true)
        {
            var page = await _svc.RetrieveMultipleAsync(qe);
            qePages.Add(page.Entities.Count);
            viaQueryExpression.AddRange(page.Entities.Select(e => e.Id));
            if (!page.MoreRecords) break;
            qe.PageInfo.PageNumber++;
            qe.PageInfo.PagingCookie = page.PagingCookie;
            qePages.Count.Should().BeLessThan(10);
        }

        // FetchXML (FetchExpression), page and paging-cookie attributes.
        var viaFetchXml = new List<Guid>();
        var fetchPages = new List<int>();
        string? cookie = null;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var cookieAttribute = cookie is null ? "" : $" paging-cookie='{System.Security.SecurityElement.Escape(cookie)}'";
            var fetch = $"<fetch count='{pageSize}' page='{pageNumber}'{cookieAttribute}><entity name='pb_project'><attribute name='pb_name'/>" +
                        $"<filter><condition attribute='pb_name' operator='begins-with' value='{_tag}'/>" +
                        $"<condition attribute='pb_count' operator='ne' value='{excluded}'/></filter>" +
                        "<order attribute='pb_name'/></entity></fetch>";
            var page = await _svc.RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.FetchExpression(fetch));
            fetchPages.Add(page.Entities.Count);
            viaFetchXml.AddRange(page.Entities.Select(e => e.Id));
            if (!page.MoreRecords) break;
            cookie = page.PagingCookie;
            fetchPages.Count.Should().BeLessThan(10);
        }

        // OData, following @odata.nextLink.
        var viaOData = new List<Guid>();
        var odataPages = new List<int>();
        string? url = $"{Set}?$select=pb_name&$filter={Tagged} and pb_count ne {excluded}&$orderby=pb_name";
        while (url is not null)
        {
            var json = await JsonAsync(await GetAsync(url, ("Prefer", $"odata.maxpagesize={pageSize}")));
            var value = json["value"]!.AsArray();
            odataPages.Add(value.Count);
            viaOData.AddRange(value.Select(v => Guid.Parse(v!["pb_projectid"]!.GetValue<string>())));
            url = json["@odata.nextLink"]?.GetValue<string>();
            odataPages.Count.Should().BeLessThan(10);
        }

        viaQueryExpression.Should().Equal(expected);
        viaFetchXml.Should().Equal(viaQueryExpression);
        viaOData.Should().Equal(viaQueryExpression);
        qePages.Should().Equal(2, 2, 2, 2, 2, 1);
        fetchPages.Should().Equal(qePages);
        odataPages.Should().Equal(qePages);
    }
}
