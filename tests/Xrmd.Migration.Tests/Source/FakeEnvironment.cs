using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xrmd.Migration.Tests.Source;

/// <summary>
/// A small synthetic Dataverse environment on top of <see cref="FakeDataverse"/>: metadata built from a table list,
/// FetchXML pages with keyset paging, WhoAmI, solutions and the security checks.
/// </summary>
internal sealed class FakeEnvironment
{
    public sealed record Table(string Name, string Set, string Key, bool Custom, bool Intersect, (string Name, string Type, string[] Targets)[] Columns, List<string> Rows);

    public List<Table> Tables { get; } = [];
    public List<string> ManyToMany { get; } = [];
    /// <summary>Table → row index: every page starting at that row fails with 500 (the export stops after the retries).</summary>
    public Dictionary<string, int> FailFromRow { get; } = [];

    /// <summary>The status of those failures (401 stops at once, without the retries of a 5xx).</summary>
    public HttpStatusCode FailStatus { get; set; } = HttpStatusCode.BadGateway;
    public HashSet<string> Forbidden { get; } = [];

    /// <summary>Tables whose queries answer 405 (Dataverse: "This action is not supported").</summary>
    public HashSet<string> NotSupported { get; } = [];

    /// <summary>The answers of RetrieveTotalRecordCount, for the names asked; a name listed in <see cref="CountFailsFor"/> fails the whole call.</summary>
    public Dictionary<string, long> Counts { get; } = new() { ["queue"] = 42 };
    public HashSet<string> CountFailsFor { get; } = [];
    public string UserRoleTemplate { get; set; } = "627090ff-40a3-4053-8790-584edc5be201";

    public FakeDataverse Fake { get; } = new();

    /// <summary>The answer to the solutions query (read at request time).</summary>
    public string SolutionsJson { get; set; } = """
        {"value":[{"solutionid":"00000000-0000-0000-0000-0000000000d1","uniquename":"xv_core","version":"1.0.0.0","ismanaged":false,"publisherid":{"uniquename":"xv","customizationprefix":"xv"}}]}
        """;

    public Table Add(string name, string key, bool custom, bool intersect, params (string Name, string Type, string[] Targets)[] columns)
    {
        var t = new Table(name, name + "s", key, custom, intersect, [(key, "Uniqueidentifier", []), .. columns], []);
        Tables.Add(t);
        return t;
    }

    /// <summary>Tables whose metadata says IsManaged.</summary>
    public HashSet<string> Managed { get; } = [];

    /// <summary>Table → <c>TableType</c> (default Standard).</summary>
    public Dictionary<string, string> TableTypes { get; } = [];

    /// <summary>Downloads answer 404 (the file was deleted after its record was read).</summary>
    public bool FilesMissing { get; set; }

    /// <summary>Answer of WhoAmI's OrganizationId.</summary>
    public string OrganizationId { get; set; } = "00000000-0000-0000-0000-0000000000c1";

    public FakeEnvironment Build()
    {
        Fake.Strict = true;
        Fake.Get(@"^/api/data/v9\.2/WhoAmI$", (_, _) => FakeDataverse.Json($$"""{"UserId":"00000000-0000-0000-0000-0000000000a1","BusinessUnitId":"00000000-0000-0000-0000-0000000000b1","OrganizationId":"{{OrganizationId}}"}"""));
        Fake.GetJson(@"^/api/data/v9\.2/RetrieveCurrentOrganization", """{"Detail":{"EnvironmentId":"env-1"}}""");
        Fake.Get(@"/systemuserroles_association", (_, _) => FakeDataverse.Json($$"""{"value":[{"name":"Role","_roletemplateid_value":"{{UserRoleTemplate}}"}]}"""));
        Fake.Get(@"^/api/data/v9\.2/solutions\?", (_, _) => FakeDataverse.Json(SolutionsJson));
        Fake.On(HttpMethod.Post, "ExportSolution$", (_, _) => FakeDataverse.Json(JsonSerializer.Serialize(new
        {
            ExportSolutionFile = Convert.ToBase64String(SolutionZip.Build("xv_core", "1.0.0.0", false,
                "<ImportExportXml><Entities><Entity><Name>xv_project</Name></Entity><Entity><Name>xv_task</Name></Entity></Entities></ImportExportXml>")),
        })));
        Fake.Get(@"^/api/data/v9\.2/EntityDefinitions\?", (_, _) => FakeDataverse.Json(JsonSerializer.Serialize(new
        {
            value = Tables.Select(t => new
            {
                LogicalName = t.Name, EntitySetName = t.Set, PrimaryIdAttribute = t.Key, IsCustomEntity = t.Custom, IsActivity = false, IsIntersect = t.Intersect,
                TableType = TableTypes.GetValueOrDefault(t.Name, "Standard"), IsManaged = Managed.Contains(t.Name),
                Attributes = t.Columns.Select(c => new { LogicalName = c.Name, AttributeType = c.Type, IsCustomAttribute = t.Custom, IsValidForRead = true, AttributeOf = (string?)null, SourceType = 0 }),
            }),
        })));
        Fake.Get(@"EntityDefinitions\(LogicalName='([a-z_]+)'\)/Attributes/Microsoft\.Dynamics\.CRM\.LookupAttributeMetadata", (_, m) => FakeDataverse.Json(JsonSerializer.Serialize(new
        {
            value = Tables.Single(t => t.Name == m.Groups[1].Value).Columns.Where(c => c.Type == "Lookup").Select(c => new { LogicalName = c.Name, Targets = c.Targets }),
        })));
        Fake.GetJson(@"EntityDefinitions\(LogicalName='[a-z_]+'\)/Attributes/", """{"value":[]}""");
        Fake.Get(@"EntityDefinitions\(LogicalName='[a-z_]+'\)/Attributes\(LogicalName='([a-z_]+)'\)", (_, m) => FakeDataverse.Json($$"""{"LogicalName":"{{m.Groups[1].Value}}"}"""));
        Fake.GetJson(@"EntityDefinitions\(LogicalName='[a-z_]+'\)/ManyToOneRelationships", """{"value":[]}""");
        Fake.Get(@"EntityDefinitions\(LogicalName='([a-z_]+)'\)\?", (_, m) => FakeDataverse.Json($$"""{"LogicalName":"{{m.Groups[1].Value}}"}"""));
        Fake.Get(@"RelationshipDefinitions\(SchemaName='([a-z_]+)'\)", (_, m) => FakeDataverse.Json($$"""{"SchemaName":"{{m.Groups[1].Value}}"}"""));
        Fake.Get(@"RelationshipDefinitions/Microsoft\.Dynamics\.CRM\.ManyToManyRelationshipMetadata", (_, _) => FakeDataverse.Json(JsonSerializer.Serialize(new
        {
            value = ManyToMany.Select(name =>
            {
                var t = Tables.Single(x => x.Name == name);
                return new
                {
                    SchemaName = name, IntersectEntityName = name, Entity1LogicalName = t.Columns[1].Targets[0], Entity1IntersectAttribute = t.Columns[1].Name,
                    Entity2LogicalName = t.Columns[2].Targets[0], Entity2IntersectAttribute = t.Columns[2].Name, IsCustomRelationship = true,
                };
            }),
        })));
        Fake.GetJson(@"^/api/data/v9\.2/(solutioncomponents|systemforms|savedqueries|savedqueryvisualizations|roles|workflows)\?", """{"value":[]}""");
        Fake.Get(@"^/api/data/v9\.2/RetrieveTotalRecordCount\(EntityNames=@p\)\?@p=(.*)$", (_, m) =>
        {
            var names = JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(m.Groups[1].Value))!;
            if (names.Any(CountFailsFor.Contains)) return FakeDataverse.Json("""{"error":{"message":"not supported"}}""", HttpStatusCode.BadRequest);
            var known = names.Where(Counts.ContainsKey).ToList();
            return FakeDataverse.Json(JsonSerializer.Serialize(new { EntityRecordCountCollection = new { Keys = known, Values = known.Select(n => Counts[n]) } }));
        });
        Fake.Get(@"^/api/data/v9\.2/([a-z_]+)\?fetchXml=(.*)$", (_, m) => Page(m.Groups[1].Value, m.Groups[2].Value));
        Fake.Get(@"/\$value$", (_, _) =>
        {
            if (FilesMissing) return FakeDataverse.Json("""{"error":{"message":"gone"}}""", HttpStatusCode.NotFound);
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("file content"u8.ToArray()) };
            r.Headers.Add("x-ms-file-name", "f.txt");
            return r;
        });
        return this;
    }

    private HttpResponseMessage Page(string set, string fetch)
    {
        var table = Tables.Single(t => t.Set == set);
        if (Forbidden.Contains(table.Name)) return FakeDataverse.Json("""{"error":{"message":"no access"}}""", HttpStatusCode.Forbidden);
        if (NotSupported.Contains(table.Name)) return FakeDataverse.Json("""{"error":{"message":"This action is not supported."}}""", HttpStatusCode.MethodNotAllowed);
        var gt = Regex.Match(fetch, "operator=\"gt\" value=\"([^\"]+)\"");
        var start = gt.Success ? table.Rows.FindIndex(r => r.Contains(gt.Groups[1].Value, StringComparison.Ordinal)) + 1 : 0;
        if (FailFromRow.TryGetValue(table.Name, out var fail) && start == fail)
            return FakeDataverse.Json("""{"error":{"message":"boom"}}""", FailStatus);
        return FakeDataverse.Json($$"""{"value":[{{string.Join(',', table.Rows.Skip(start).Take(5000))}}]}""");
    }
}
