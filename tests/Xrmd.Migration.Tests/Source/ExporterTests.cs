using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class ExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Guid G(int table, int i) => new(i, (short)table, 0, new byte[8]);

    private static readonly ExportOptions Options = new(["xv_core"], false, new Dictionary<string, string>(), [], [], Parallel: 1);

    private static FakeEnvironment Environment(int projects = 5, Action<FakeEnvironment>? extra = null)
    {
        var env = new FakeEnvironment();
        var account = env.Add("account", "accountid", false, false, ("name", "String", []));
        for (var i = 0; i < 3; i++) account.Rows.Add($$"""{"accountid":"{{G(1, i)}}","name":"Account {{i}}"}""");
        var contact = env.Add("contact", "contactid", false, false, ("fullname", "String", []));
        for (var i = 0; i < 2; i++) contact.Rows.Add($$"""{"contactid":"{{G(2, i)}}","fullname":"Contact {{i}}"}""");
        var project = env.Add("xv_project", "xv_projectid", true, false, ("xv_name", "String", []), ("xv_accountid", "Lookup", ["account"]), ("xv_doc", "File", []));
        for (var i = 0; i < projects; i++)
            project.Rows.Add(i == 0
                ? $$"""{"xv_projectid":"{{G(3, i)}}","xv_name":"P{{i}}","_xv_accountid_value":"{{G(1, 0)}}","xv_doc":"{{G(9, 0)}}"}"""
                : $$"""{"xv_projectid":"{{G(3, i)}}","xv_name":"P{{i}}"}""");
        var nn = env.Add("xv_account_contact", "xv_account_contactid", true, true, ("accountid", "Uniqueidentifier", ["account"]), ("contactid", "Uniqueidentifier", ["contact"]));
        nn.Rows.Add($$"""{"xv_account_contactid":"{{G(4, 0)}}","accountid":"{{G(1, 0)}}","contactid":"{{G(2, 0)}}"}""");
        env.ManyToMany.Add("xv_account_contact");
        env.Add("systemuser", "systemuserid", false, false, ("fullname", "String", []), ("domainname", "String", []))
            .Rows.AddRange([$$"""{"systemuserid":"{{G(5, 0)}}","fullname":"Anna","domainname":"anna@example.com"}""", $$"""{"systemuserid":"{{G(5, 1)}}","fullname":"Bruno","domainname":"bruno@example.com"}"""]);
        env.Add("businessunit", "businessunitid", false, false, ("name", "String", [])).Rows.Add($$"""{"businessunitid":"{{G(6, 0)}}","name":"Root"}""");
        env.Add("team", "teamid", false, false, ("name", "String", [])).Rows.Add($$"""{"teamid":"{{G(7, 0)}}","name":"Root team"}""");
        env.Add("teammembership", "teammembershipid", false, true, ("systemuserid", "Uniqueidentifier", ["systemuser"]), ("teamid", "Uniqueidentifier", ["team"]))
            .Rows.Add($$"""{"teammembershipid":"{{G(8, 0)}}","systemuserid":"{{G(5, 0)}}","teamid":"{{G(7, 0)}}"}""");
        env.ManyToMany.Add("teammembership");
        env.Add("queue", "queueid", false, false);
        env.Add("asyncoperation", "asyncoperationid", false, false);
        env.Add("msdyn_project", "msdyn_projectid", true, false);
        extra?.Invoke(env);
        return env.Build();
    }

    private static DataverseClient Client(FakeEnvironment env) => new(env.Fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    private static IEnumerable<string> Fetches(FakeEnvironment env, string set) =>
        env.Fake.Requests.Where(r => r.Url.StartsWith($"/api/data/v9.2/{set}?fetchXml=", StringComparison.Ordinal)).Select(r => r.Url);

    private async Task<PackageManifest> Run(FakeEnvironment env, string folder, ExportOptions? options = null, bool resume = false)
    {
        using var writer = resume ? PackageWriter.OpenForResume(folder, SyntheticPackage.Passphrase)
            : PackageWriter.Create(folder, SyntheticPackage.Passphrase, options?.Maps is null ? 1 : PackageHeader.MappedVersion);
        using var client = Client(env);
        return await new Exporter(client, options ?? Options).RunAsync(writer, CancellationToken.None);
    }

    [Fact]
    public async Task Exports_synthetic_environment_end_to_end()
    {
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(Environment(), folder);

        manifest.Source.Should().Be(new PackageSource("https://contoso.example.com", "env-1", "00000000-0000-0000-0000-0000000000c1"));
        manifest.Solutions.Should().Equal(new PackageSolution(1, "xv_core", "1.0.0.0", false, "solutions/1-xv_core.zip"));
        manifest.Tables.ToDictionary(t => t.LogicalName, t => t.Records).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["account"] = 3, ["contact"] = 2, ["xv_project"] = 5, ["systemuser"] = 2, ["businessunit"] = 1, ["team"] = 1, ["teammembership"] = 1,
        });
        manifest.Tables.Single(t => t.LogicalName == "systemuser").Chunks.Should().Equal("security/systemuser/00001.jsonl.gz");
        manifest.Tables.Single(t => t.LogicalName == "account").Chunks.Should().Equal("data/account/00001.jsonl.gz");
        manifest.Relationships.Should().ContainSingle().Which.Should().BeEquivalentTo(new PackageRelationship("xv_account_contact", 1, ["nn/xv_account_contact/00001.jsonl.gz"]));
        manifest.Warnings.Should().Contain(w => w.Code == "not-supported-yet" && w.Message.Contains("queue: 42"));

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Manifest.Entries.Select(e => e.Path).Should().Contain([$"files/xv_project/xv_doc/{G(3, 0)}", SchemaDelta.DeltaPath, SchemaDelta.UnsupportedPath]);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_get_and_ExportSolution_are_sent()
    {
        var env = Environment();
        await Run(env, Path.Combine(_root, "pkg"));
        env.Fake.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => r.Url).Should().Equal("/api/data/v9.2/ExportSolution");
    }

    [Fact]
    public async Task Resume_after_failure_mid_table()
    {
        var folder = Path.Combine(_root, "pkg");
        var broken = Environment(12_001);
        broken.FailFromRow["xv_project"] = 10_000;
        await FluentActions.Awaiting(() => Run(broken, folder)).Should().ThrowAsync<DataverseException>();

        var env = Environment(12_001);
        var manifest = await Run(env, folder, resume: true);

        manifest.Tables.Single(t => t.LogicalName == "xv_project").Records.Should().Be(12_001);
        Fetches(env, "xv_projects").Should().ContainSingle().Which.Should().Contain($"value=\"{G(3, 9_999)}\"");
        Fetches(env, "accounts").Should().BeEmpty();
        Fetches(env, "contacts").Should().BeEmpty();
        Fetches(env, "systemusers").Should().BeEmpty();
        env.Fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        manifest.Solutions.Should().ContainSingle();

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var ids = new List<Guid>();
        await foreach (var r in Xrmd.Migration.Records.ChunkReader.ReadAsync(reader, manifest.Tables.Single(t => t.LogicalName == "xv_project").Chunks, CancellationToken.None))
            ids.Add(r.Id);
        ids.Should().HaveCount(12_001).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Unreadable_table_is_a_warning()
    {
        var env = Environment();
        env.Forbidden.Add("contact");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-readable" && w.Message.StartsWith("contact:"));
        manifest.Tables.Should().NotContain(t => t.LogicalName == "contact");
        manifest.Tables.Should().Contain(t => t.LogicalName == "account");
    }

    [Fact]
    public async Task Not_supported_yet_tables_are_counted_not_exported()
    {
        var env = Environment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        Fetches(env, "queues").Should().BeEmpty();
        manifest.Tables.Should().NotContain(t => t.LogicalName == "queue");
        manifest.Warnings.Single(w => w.Code == "not-supported-yet").Message.Should().Contain("42");
    }

    [Fact]
    public async Task Excluded_first_party_table_is_never_read()
    {
        var env = Environment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        Fetches(env, "msdyn_projects").Should().BeEmpty();
        Fetches(env, "asyncoperations").Should().BeEmpty();
        env.Fake.Requests.Should().NotContain(r => r.Url.Contains("LogicalName='msdyn_project'") || r.Url.Contains("LogicalName='asyncoperation'"));
        manifest.Relationships.Should().NotContain(r => r.SchemaName == "teammembership");
    }

    [Fact]
    public async Task Excluded_option_wins()
    {
        var env = Environment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { Exclude = ["contact"] });
        Fetches(env, "contacts").Should().BeEmpty();
        manifest.Relationships.Should().BeEmpty("the N:N needs both of its tables");
    }

    [Fact]
    public async Task Non_admin_is_a_warning_or_an_error_with_require_admin()
    {
        var env = Environment();
        env.UserRoleTemplate = "00000000-0000-0000-0000-000000000099";
        (await Run(env, Path.Combine(_root, "pkg"))).Warnings.Should().Contain(w => w.Code == "not-system-administrator");

        var strict = Environment();
        strict.UserRoleTemplate = "00000000-0000-0000-0000-000000000099";
        await FluentActions.Awaiting(() => Run(strict, Path.Combine(_root, "pkg2"), Options with { RequireAdmin = true }))
            .Should().ThrowAsync<MigrationExportException>();
    }

    [Fact]
    public async Task Unknown_solution_is_an_error()
    {
        await FluentActions.Awaiting(() => Run(Environment(), Path.Combine(_root, "pkg"), Options with { Solutions = ["nope"] }))
            .Should().ThrowAsync<MigrationExportException>().WithMessage("*nope*");
    }

    [Fact]
    public async Task Parallel_export_gives_the_same_package()
    {
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(Environment(12_001), folder, Options with { Parallel = 4 });
        manifest.Tables.Single(t => t.LogicalName == "xv_project").Records.Should().Be(12_001);
        manifest.Relationships.Should().ContainSingle();
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Exclude_applies_to_security_tables()
    {
        var env = Environment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { Exclude = ["teammembership"] });
        Fetches(env, "teammemberships").Should().BeEmpty();
        manifest.Tables.Should().NotContain(t => t.LogicalName == "teammembership");
    }

    [Fact]
    public async Task Resume_against_another_environment_is_refused()
    {
        var folder = Path.Combine(_root, "pkg");
        var broken = Environment();
        broken.FailFromRow["xv_project"] = 0;
        broken.FailStatus = System.Net.HttpStatusCode.Unauthorized;
        await FluentActions.Awaiting(() => Run(broken, folder)).Should().ThrowAsync<DataverseException>();

        var other = Environment();
        other.OrganizationId = "00000000-0000-0000-0000-0000000000c9";
        await FluentActions.Awaiting(() => Run(other, folder, resume: true)).Should().ThrowAsync<MigrationExportException>().WithMessage("*same --source*");

        await FluentActions.Awaiting(() => Run(Environment(), folder, Options with { Exclude = ["contact"] }, resume: true))
            .Should().ThrowAsync<MigrationExportException>().WithMessage("*same options*");
    }

    [Fact]
    public async Task Installed_managed_solution_without_file_stops_even_with_all_unmanaged()
    {
        var env = Environment();
        env.SolutionsJson = """
            {"value":[
              {"solutionid":"00000000-0000-0000-0000-0000000000d1","uniquename":"xv_core","version":"1.0.0.0","ismanaged":false,"publisherid":{"uniquename":"xv","customizationprefix":"xv"}},
              {"solutionid":"00000000-0000-0000-0000-0000000000d2","uniquename":"isv_app","version":"2.0.0.0","ismanaged":true,"publisherid":{"uniquename":"isv","customizationprefix":"isv"}},
              {"solutionid":"00000000-0000-0000-0000-0000000000d3","uniquename":"msdynce_Sales","version":"9.0.0.0","ismanaged":true,"publisherid":{"uniquename":"microsoftdynamics","customizationprefix":"msdynce"}}
            ]}
            """;
        await FluentActions.Awaiting(() => Run(env, Path.Combine(_root, "pkg"), Options with { Solutions = [], AllUnmanaged = true }))
            .Should().ThrowAsync<MigrationExportException>().WithMessage("*isv_app*");
    }

    [Fact]
    public async Task Refusal_after_the_first_page_stops_the_export()
    {
        var env = Environment(12_001);
        env.FailFromRow["xv_project"] = 5_000;
        env.FailStatus = System.Net.HttpStatusCode.BadRequest;
        await FluentActions.Awaiting(() => Run(env, Path.Combine(_root, "pkg"))).Should().ThrowAsync<DataverseException>();
    }

    [Fact]
    public async Task Deleted_file_is_a_warning_not_a_lost_table()
    {
        var env = Environment();
        env.FilesMissing = true;
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        manifest.Tables.Single(t => t.LogicalName == "xv_project").Records.Should().Be(5);
        manifest.Warnings.Should().Contain(w => w.Code == "file-missing" && w.Message.StartsWith("xv_project.xv_doc: 1"));
    }

    [Fact]
    public async Task Virtual_tables_are_not_read_and_are_reported()
    {
        var env = Environment();
        env.TableTypes["contact"] = "Virtual";
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        Fetches(env, "contacts").Should().BeEmpty();
        manifest.Warnings.Should().Contain(w => w.Code == "virtual-table" && w.Message.StartsWith("contact:"));
    }

    [Fact]
    public async Task Relationship_to_an_excluded_table_is_reported()
    {
        var manifest = await Run(Environment(), Path.Combine(_root, "pkg"), Options with { Exclude = ["contact"] });
        manifest.Warnings.Should().Contain(w => w.Code == "relationship-outside-scope" && w.Message.StartsWith("xv_account_contact:"));
    }

    [Fact]
    public async Task Method_not_allowed_table_is_a_warning()
    {
        var env = Environment();
        env.NotSupported.Add("contact");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-readable" && w.Message.StartsWith("contact:"));
        manifest.Tables.Should().NotContain(t => t.LogicalName == "contact");
        manifest.Tables.Should().Contain(t => t.LogicalName == "account");
    }

    [Fact]
    public async Task Standard_tables_xrmverse_does_not_have_are_counted_not_read()
    {
        var env = Environment();
        env.Add("lead", "leadid", false, false, ("subject", "String", [])).Rows.Add($$"""{"leadid":"{{G(10, 0)}}","subject":"L"}""");
        env.Add("delveactionhub", "delveactionhubid", false, false);
        env.Add("attribute", "attributeid", false, false);
        env.NotSupported.Add("delveactionhub");
        env.Counts["lead"] = 7;
        env.Counts["attribute"] = 0;
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { ArchiveCandidates = false });

        Fetches(env, "leads").Should().BeEmpty();
        Fetches(env, "delveactionhubs").Should().BeEmpty();
        Fetches(env, "attributes").Should().BeEmpty();
        manifest.Tables.Select(t => t.LogicalName).Should().NotContain(["lead", "delveactionhub", "attribute"]);
        manifest.Tables.Should().Contain(t => t.LogicalName == "xv_project");
        var notInXrmverse = manifest.Warnings.Where(w => w.Code == "table-not-in-xrmverse").Select(w => w.Message).ToList();
        notInXrmverse.Should().ContainSingle(m => m.StartsWith("lead: 7 record(s)"));
        notInXrmverse.Should().NotContain(m => m.StartsWith("attribute:"));
        notInXrmverse.Should().NotContain(m => m.StartsWith("delveactionhub:"), "a table without a count is only in the summary line");
    }

    [Fact]
    public async Task Tables_without_a_count_are_listed_in_one_warning()
    {
        var env = Environment();
        env.Add("lead", "leadid", false, false);
        env.Add("delveactionhub", "delveactionhubid", false, false);
        env.CountFailsFor.Add("delveactionhub");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        manifest.Warnings.Where(w => w.Code == "table-not-in-xrmverse").Should().ContainSingle()
            .Which.Message.Should().Contain("delveactionhub").And.Contain("lead");
    }

    [Fact]
    public async Task Relationship_to_a_table_xrmverse_does_not_have_is_not_read()
    {
        var env = Environment();
        env.Add("lead", "leadid", false, false);
        var nn = env.Add("xv_lead_contact", "xv_lead_contactid", true, true, ("leadid", "Uniqueidentifier", ["lead"]), ("contactid", "Uniqueidentifier", ["contact"]));
        nn.Rows.Add($$"""{"xv_lead_contactid":"{{G(11, 0)}}","leadid":"{{G(10, 0)}}","contactid":"{{G(2, 0)}}"}""");
        env.ManyToMany.Add("xv_lead_contact");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        Fetches(env, "xv_lead_contacts").Should().BeEmpty();
        manifest.Relationships.Should().NotContain(r => r.SchemaName == "xv_lead_contact");
        manifest.Warnings.Should().Contain(w => w.Code == "relationship-outside-scope" && w.Message.StartsWith("xv_lead_contact:"));
    }

    [Fact]
    public async Task Value_of_an_unexpected_type_is_left_out_and_reported()
    {
        var env = Environment();
        env.Tables.Single(t => t.Name == "contact").Rows[0] = $$"""{"contactid":"{{G(2, 0)}}","fullname":5}""";
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        manifest.Tables.Single(t => t.LogicalName == "contact").Records.Should().Be(2);
        manifest.Warnings.Should().Contain(w => w.Code == "value-not-readable" && w.Message.StartsWith("contact.fullname: 1 value(s)"));
    }

    [Fact]
    public async Task Managed_custom_tables_outside_the_exported_solutions_are_not_read()
    {
        var env = Environment();
        env.Add("appaction", "appactionid", true, false).Rows.Add($$"""{"appactionid":"{{G(12, 0)}}"}""");
        env.Add("xv_task", "xv_taskid", true, false).Rows.Add($$"""{"xv_taskid":"{{G(13, 0)}}"}""");
        env.Managed.UnionWith(["appaction", "xv_task"]);
        env.Counts["appaction"] = 825;
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        Fetches(env, "appactions").Should().BeEmpty();
        manifest.Tables.Should().Contain(t => t.LogicalName == "xv_task", "a managed table of an exported solution exists in the target");
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("appaction: 825 record(s)"));
    }

    [Fact]
    public async Task Managed_tables_of_the_solutions_are_still_read_on_resume()
    {
        var folder = Path.Combine(_root, "pkg");
        var broken = Environment();
        broken.Add("xv_task", "xv_taskid", true, false).Rows.Add($$"""{"xv_taskid":"{{G(13, 0)}}"}""");
        broken.Managed.Add("xv_task");
        broken.FailFromRow["xv_project"] = 0;
        await FluentActions.Awaiting(() => Run(broken, folder)).Should().ThrowAsync<DataverseException>();

        var env = Environment();
        env.Add("xv_task", "xv_taskid", true, false).Rows.Add($$"""{"xv_taskid":"{{G(13, 0)}}"}""");
        env.Managed.Add("xv_task");
        var manifest = await Run(env, folder, resume: true);
        manifest.Tables.Should().Contain(t => t.LogicalName == "xv_task");
    }

    [Fact]
    public async Task File_attachment_rows_are_not_exported_as_data()
    {
        var env = Environment();
        env.Add("fileattachment", "fileattachmentid", false, false).Rows.Add($$"""{"fileattachmentid":"{{G(14, 0)}}"}""");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));
        Fetches(env, "fileattachments").Should().BeEmpty();
        manifest.Tables.Should().NotContain(t => t.LogicalName == "fileattachment");
    }

    private const string LeadMap = "version: 1\ntables:\n  - source: lead\n    target: xv_lead\n    columns:\n      subject: xv_subject\n"
        + "relationships:\n  - source: leadcompetitors\n    target: xv_leadcompetitors\n";

    private static ExportOptions Mapped(string yaml = LeadMap, ExportOptions? basis = null) =>
        (basis ?? Options) with { Maps = MapSet.Combine([MapReader.Parse(yaml, "lead-map.yaml")]), MapTexts = [("lead-map", yaml)] };

    /// <summary>A source with a lead table xrmverse lacks (two rows, a custom column, a choice and a status column) and four N:N relationships.</summary>
    private static FakeEnvironment LeadEnvironment() => Environment(extra: env =>
    {
        env.Tables.Single(t => t.Name == "account").Rows.Clear();
        env.Add("new_tag", "new_tagid", true, false, ("new_name", "String", [])).Rows.Add($$"""{"new_tagid":"{{G(10, 0)}}","new_name":"vip"}""");
        var lead = env.Add("lead", "leadid", false, false, ("subject", "String", []), ("description", "String", []), ("new_score", "Integer", []),
            ("industrycode", "Picklist", []), ("statuscode", "Status", []));
        lead.Rows.Add($$"""{"leadid":"{{G(11, 0)}}","subject":"First","description":"d","new_score":7,"industrycode":1,"statuscode":1}""");
        lead.Rows.Add($$"""{"leadid":"{{G(11, 1)}}","subject":"Second","description":"e","new_score":9,"industrycode":2,"statuscode":2}""");
        env.CustomColumns.Add("lead.new_score");
        env.ObjectTypeCodes["lead"] = 4;
        env.Choices.Add(("lead", "industrycode", "Picklist", [(1, null, "Hot", "Heiss"), (2, null, "Cold", "Kalt")]));
        env.Choices.Add(("lead", "statuscode", "Status", [(1, 0, "New", "Neu"), (2, 1, "Won", "Gewonnen")]));
        env.Choices.Add(("account", "industrycode", "Picklist", [(1, null, "Retail", "Handel")]));
        foreach (var (name, other) in new[] { ("leadcompetitors", "account"), ("new_lead_new_tag", "new_tag"), ("leadaddresses_x", "contact") })
        {
            env.Add(name, name + "id", true, true, ("leadid", "Uniqueidentifier", ["lead"]), (other + "id", "Uniqueidentifier", [other]))
                .Rows.Add($$"""{"{{name}}id":"{{G(12, name.Length)}}","leadid":"{{G(11, 0)}}","{{other}}id":"{{G(1, 0)}}"}""");
            env.ManyToMany.Add(name);
        }
        env.StandardManyToMany.Add("leadcompetitors");
        env.StandardManyToMany.Add("leadaddresses_x");
        env.Counts["lead"] = 2;
    });

    [Fact]
    public async Task Mapped_table_is_exported_although_xrmverse_lacks_it()
    {
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(LeadEnvironment(), folder, Mapped());

        var lead = manifest.Tables.Single(t => t.LogicalName == "lead");
        lead.Chunks.Should().Equal("data/lead/00001.jsonl.gz");
        lead.Records.Should().Be(2);
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var records = new List<MigrationRecord>();
        await foreach (var r in ChunkReader.ReadAsync(reader, lead.Chunks, CancellationToken.None)) records.Add(r);
        records.Should().HaveCount(2);
        records[0].Values.Keys.Should().Contain(["subject", "description", "new_score", "industrycode", "statuscode"]);
    }

    [Fact]
    public async Task No_table_not_in_xrmverse_warning_for_mapped_tables()
    {
        var manifest = await Run(LeadEnvironment(), Path.Combine(_root, "pkg"), Mapped());
        manifest.Warnings.Should().NotContain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("lead:"));
    }

    [Fact]
    public async Task Without_the_map_the_lead_table_is_only_counted()
    {
        var env = LeadEnvironment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { ArchiveCandidates = false });
        manifest.Tables.Should().NotContain(t => t.LogicalName == "lead");
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("lead:"));
        Fetches(env, "leads").Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_has_options_with_base_language_labels_for_every_exported_table()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(LeadEnvironment(), folder, Mapped());

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var (_, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        catalog.Options.Should().Contain([
            new SourceCatalogOption("lead", "industrycode", 1, null, "Hot"), new SourceCatalogOption("lead", "industrycode", 2, null, "Cold"),
            new SourceCatalogOption("lead", "statuscode", 1, 0, "New"), new SourceCatalogOption("lead", "statuscode", 2, 1, "Won"),
            new SourceCatalogOption("account", "industrycode", 1, null, "Retail")]);
        var lead = catalog.Tables.Single(t => t.LogicalName == "lead");
        lead.ObjectTypeCode.Should().Be(4);
        lead.Ownership.Should().Be("user");
        lead.IsActivity.Should().BeFalse();
        lead.PrimaryKey.Should().Be("leadid");
        lead.Columns.Should().Contain(c => c.LogicalName == "new_score" && c.AttributeType == "Integer" && c.IsCustom);
        lead.Columns.Should().Contain(c => c.LogicalName == "subject" && c.AttributeType == "String" && !c.IsCustom);
    }

    private static FakeEnvironment QuoteEnvironment() => Environment(extra: env =>
    {
        var quote = env.Add("quote", "quoteid", false, false, ("name", "String", []), ("totalamount", "Money", []), ("statuscode", "Status", []), ("new_note", "String", []));
        quote.Rows.Add($$"""{"quoteid":"{{G(20, 0)}}","name":"Q1","totalamount":10.5,"statuscode":1}""");
        quote.Rows.Add($$"""{"quoteid":"{{G(20, 1)}}","name":"Q2","totalamount":3,"statuscode":1}""");
        env.Counts["quote"] = 2;
        env.Choices.Add(("quote", "statuscode", "Status", [(1, 0, "Draft", "Entwurf")]));
        env.CustomColumns.Add("quote.new_note");
        env.DisplayNames["quote"] = "Quote";
        env.DisplayNames["quote.name"] = "Name";
        env.MaxLengths["quote.name"] = 100;
        env.Precisions["quote.totalamount"] = 2;
    });

    [Fact]
    public async Task Candidate_rows_are_exported_by_default()
    {
        var env = QuoteEnvironment();
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(env, folder);

        manifest.Tables.Single(t => t.LogicalName == "quote").Chunks.Should().Equal("data/quote/00001.jsonl.gz");
        manifest.ArchiveCandidates.Should().ContainSingle().Which.Should().Match<PackageTable>(t => t.LogicalName == "quote" && t.Records == 2);
        Fetches(env, "quotes").Should().NotBeEmpty();
        manifest.Warnings.Should().NotContain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("quote:"));
    }

    [Fact]
    public async Task No_archive_candidates_skips_them()
    {
        var env = QuoteEnvironment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { ArchiveCandidates = false });

        Fetches(env, "quotes").Should().BeEmpty();
        manifest.ArchiveCandidates.Should().BeEmpty();
        manifest.Tables.Should().NotContain(t => t.LogicalName == "quote");
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("quote: 2 record(s)"));
    }

    [Fact]
    public async Task First_party_and_catalog_tables_are_never_candidates()
    {
        var env = QuoteEnvironment();
        env.Add("msdyn_thing", "msdyn_thingid", false, false).Rows.Add($$"""{"msdyn_thingid":"{{G(21, 0)}}"}""");
        env.Counts["msdyn_thing"] = 1;
        env.Counts["asyncoperation"] = 5;
        env.Counts["empty_standard"] = 0;
        env.Add("empty_standard", "empty_standardid", false, false);
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        manifest.ArchiveCandidates.Select(t => t.LogicalName).Should().Equal("quote");
        Fetches(env, "msdyn_things").Should().BeEmpty();
        Fetches(env, "asyncoperations").Should().BeEmpty();
        Fetches(env, "empty_standards").Should().BeEmpty("a table without rows is not a candidate");
    }

    [Fact]
    public async Task System_tables_the_metadata_marks_as_not_searchable_are_not_candidates()
    {
        var env = QuoteEnvironment();
        env.Add("calendar", "calendarid", false, false).Rows.Add($$"""{"calendarid":"{{G(23, 0)}}"}""");
        env.Counts["calendar"] = 3;
        env.NotSearchable.Add("calendar");
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

        manifest.ArchiveCandidates.Select(t => t.LogicalName).Should().Equal("quote");
        Fetches(env, "calendars").Should().BeEmpty();
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("calendar: 3 record(s)"));
    }

    [Fact]
    public async Task An_included_catalog_table_is_not_a_candidate()
    {
        var env = QuoteEnvironment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Options with { Include = ["queue", "asyncoperation"] });
        manifest.ArchiveCandidates.Select(t => t.LogicalName).Should().Equal("quote");
    }

    [Fact]
    public async Task An_archive_table_leaves_nothing_in_the_schema_delta()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(QuoteEnvironment(), folder, Mapped("version: 1\ntables:\n  - source: quote\n    archive: true\n"));

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        using var delta = await System.Text.Json.JsonDocument.ParseAsync(reader.OpenEntry(SchemaDelta.DeltaPath));
        delta.RootElement.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("table").GetString()).Should().NotContain("quote");
        delta.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("table").GetString()).Should().NotContain("quote");
        delta.RootElement.ToString().Should().NotContain("quote");
    }

    [Fact]
    public async Task Column_details_are_requested_only_for_candidate_and_archive_tables()
    {
        var env = QuoteEnvironment();
        await Run(env, Path.Combine(_root, "pkg"));

        var detail = env.Fake.Requests.Select(r => r.Url).Where(u => u.Contains("StringAttributeMetadata", StringComparison.Ordinal)).ToList();
        detail.Should().NotBeEmpty().And.OnlyContain(u => u.Contains("LogicalName='quote'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Display_names_are_read_in_the_organization_language()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(QuoteEnvironment().Also(e => e.OrganizationLanguage = 1031), folder);
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var (_, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        catalog.Tables.Single(t => t.LogicalName == "quote").DisplayName.Should().Be("xQuote");
    }

    [Fact]
    public async Task A_mapped_table_is_not_a_candidate()
    {
        var manifest = await Run(LeadEnvironment(), Path.Combine(_root, "pkg"), Mapped());
        manifest.ArchiveCandidates.Should().BeEmpty();
        manifest.Tables.Should().Contain(t => t.LogicalName == "lead");
    }

    [Fact]
    public async Task Catalog_carries_column_details()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(QuoteEnvironment(), folder, Mapped("version: 1\ntables:\n  - source: quote\n    archive: true\n"));

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var (_, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        var quote = catalog.Tables.Single(t => t.LogicalName == "quote");
        quote.DisplayName.Should().Be("Quote");
        var name = quote.Columns.Single(c => c.LogicalName == "name");
        name.DisplayName.Should().Be("Name");
        name.MaxLength.Should().Be(100);
        name.Format.Should().Be("Text");
        quote.Columns.Single(c => c.LogicalName == "totalamount").Precision.Should().Be(2);
        var status = quote.Columns.Single(c => c.LogicalName == "statuscode");
        status.OptionSetName.Should().Be("quote_statuscode");
        status.IsGlobalOptionSet.Should().BeFalse();
        catalog.Options.Should().Contain(new SourceCatalogOption("quote", "statuscode", 1, 0, "Draft"));
    }

    [Fact]
    public async Task Candidates_without_a_map_make_a_version_2_package_with_the_catalog()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(QuoteEnvironment(), folder);
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Header.FormatVersion.Should().Be(PackageHeader.MappedVersion);
        reader.Manifest.FormatVersion.Should().Be(PackageHeader.MappedVersion);
        reader.Manifest.Maps.Should().BeEmpty();
        var (files, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        files.Should().BeEmpty();
        catalog.Tables.Single(t => t.LogicalName == "quote").Columns.Single(c => c.LogicalName == "name").MaxLength.Should().Be(100);
    }

    [Fact]
    public async Task No_candidates_and_no_map_leave_the_package_at_version_1()
    {
        foreach (var (env, options, name) in new[] { (QuoteEnvironment(), Options with { ArchiveCandidates = false }, "off"), (Environment(), Options, "none") })
        {
            var folder = Path.Combine(_root, name);
            await Run(env, folder, options);
            using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
            reader.Header.FormatVersion.Should().Be(PackageHeader.CurrentVersion);
            reader.Manifest.FormatVersion.Should().Be(PackageHeader.CurrentVersion);
            reader.Manifest.Entries.Should().NotContain(e => e.Path == PackageMaps.SourcePath);
        }
    }

    [Fact]
    public async Task A_resumed_export_keeps_the_candidates_of_its_first_run()
    {
        var env = QuoteEnvironment();
        env.FailFromRow["xv_project"] = 0;
        env.FailStatus = System.Net.HttpStatusCode.Unauthorized;
        var folder = Path.Combine(_root, "pkg");
        await FluentActions.Awaiting(() => Run(env, folder)).Should().ThrowAsync<DataverseException>();

        // Between the runs a new table gains rows, and quote loses its.
        env.FailFromRow.Clear();
        env.Add("invoice", "invoiceid", false, false).Rows.Add($$"""{"invoiceid":"{{G(22, 0)}}"}""");
        env.Counts["invoice"] = 1;
        env.Counts["quote"] = 0;
        var manifest = await Run(env, folder, resume: true);

        manifest.ArchiveCandidates.Select(t => t.LogicalName).Should().Equal("quote");
        manifest.Tables.Should().NotContain(t => t.LogicalName == "invoice");
        manifest.Warnings.Should().Contain(w => w.Code == "table-not-in-xrmverse" && w.Message.StartsWith("invoice: 1 record(s)"));
    }

    [Fact]
    public async Task Catalog_uses_the_organization_language_for_labels_and_ownership_of_the_table()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(LeadEnvironment().Also(e => { e.OrganizationLanguage = 1031; e.Ownership["lead"] = "OrganizationOwned"; }), folder, Mapped());

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        var (_, catalog) = await PackageMaps.ReadAsync(reader, CancellationToken.None);
        catalog.Options.Should().Contain(new SourceCatalogOption("lead", "industrycode", 1, null, "Heiss"));
        catalog.Tables.Single(t => t.LogicalName == "lead").Ownership.Should().Be("organization");
    }

    [Fact]
    public async Task Map_files_are_embedded_and_format_is_2()
    {
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(LeadEnvironment(), folder, Mapped());

        manifest.Maps.Should().Equal("map/1-lead-map.yaml");
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Manifest.FormatVersion.Should().Be(PackageHeader.MappedVersion);
        reader.Manifest.Maps.Should().Equal("map/1-lead-map.yaml");
        reader.Manifest.Entries.Select(e => e.Path).Should().Contain([PackageMaps.SourcePath, "map/1-lead-map.yaml"]);
        using var text = new StreamReader(reader.OpenEntry("map/1-lead-map.yaml"));
        (await text.ReadToEndAsync()).Should().Be(LeadMap);
        (await reader.VerifyAsync(null, CancellationToken.None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_maps_the_package_is_format_1_with_no_catalog()
    {
        var folder = Path.Combine(_root, "pkg");
        var manifest = await Run(Environment(), folder);

        manifest.Maps.Should().BeEmpty();
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        reader.Manifest.FormatVersion.Should().Be(PackageHeader.CurrentVersion);
        reader.Manifest.Entries.Should().NotContain(e => e.Path.StartsWith("map/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mapped_and_custom_relationships_of_a_mapped_table_are_exported_and_others_warned()
    {
        var manifest = await Run(LeadEnvironment(), Path.Combine(_root, "pkg"), Mapped());

        manifest.Relationships.Select(r => r.SchemaName).Should().Contain(["leadcompetitors", "new_lead_new_tag"]).And.NotContain("leadaddresses_x");
        manifest.Warnings.Should().Contain(w => w.Code == "relationship-not-mapped" && w.Message.StartsWith("leadaddresses_x:"));
        manifest.Warnings.Should().NotContain(w => w.Code == "relationship-not-mapped" && w.Message.StartsWith("leadcompetitors:"));
    }

    [Fact]
    public async Task Custom_columns_and_relationships_of_a_mapped_table_go_to_the_delta_but_not_the_table()
    {
        var folder = Path.Combine(_root, "pkg");
        await Run(LeadEnvironment(), folder, Mapped());

        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        using var delta = await System.Text.Json.JsonDocument.ParseAsync(reader.OpenEntry(SchemaDelta.DeltaPath));
        delta.RootElement.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("table").GetString()).Should().NotContain("lead");
        delta.RootElement.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("table").GetString()}.{c.GetProperty("column").GetString()}")
            .Should().Contain("lead.new_score");
        delta.RootElement.GetProperty("relationships").EnumerateArray().Select(r => r.GetProperty("schemaName").GetString()).Should().Contain("new_lead_new_tag");
    }

    [Fact]
    public async Task Exclude_wins_over_the_map()
    {
        var env = LeadEnvironment();
        var manifest = await Run(env, Path.Combine(_root, "pkg"), Mapped(basis: Options with { Exclude = ["lead"] }));

        manifest.Tables.Should().NotContain(t => t.LogicalName == "lead");
        Fetches(env, "leads").Should().BeEmpty();
    }

    [Fact]
    public async Task First_party_columns_of_a_mapped_table_stay_out_unless_the_map_names_them()
    {
        static FakeEnvironment WithFirstParty() => LeadEnvironment().Also(e =>
        {
            var lead = e.Tables.Single(t => t.Name == "lead");
            e.Tables[e.Tables.IndexOf(lead)] = lead with { Columns = [.. lead.Columns, ("msdyn_score", "Integer", []), ("msdyn_other", "Integer", [])] };
        });
        var plain = await FirstPartyWarning(WithFirstParty(), Mapped());
        plain.Should().Contain("msdyn_score").And.Contain("msdyn_other");

        var named = await FirstPartyWarning(WithFirstParty(), Mapped("version: 1\ntables:\n  - source: lead\n    target: xv_lead\n    columns:\n      msdyn_score: xv_score\n"));
        named.Should().Contain("msdyn_other").And.NotContain("msdyn_score");
    }

    private async Task<string> FirstPartyWarning(FakeEnvironment env, ExportOptions options)
    {
        var manifest = await Run(env, Path.Combine(_root, Guid.NewGuid().ToString("N")), options);
        return manifest.Warnings.Single(w => w.Code == "first-party-columns" && w.Message.StartsWith("lead:")).Message;
    }

    [Fact]
    public void Combining_an_invalid_map_throws_before_the_exporter_exists_and_sends_nothing()
    {
        var env = LeadEnvironment();
        var twice = "version: 1\ntables:\n  - source: lead\n    target: xv_x\n  - source: contact\n    target: xv_x\n";
        FluentActions.Invoking(() => Mapped(twice)).Should().Throw<MapValidationException>();
        env.Fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Maps_on_a_format_1_writer_stop_before_any_call()
    {
        var env = LeadEnvironment();
        using var writer = PackageWriter.Create(Path.Combine(_root, "pkg"), SyntheticPackage.Passphrase);
        using var client = Client(env);
        await FluentActions.Awaiting(() => new Exporter(client, Mapped()).RunAsync(writer, CancellationToken.None))
            .Should().ThrowAsync<MigrationExportException>().WithMessage("*format*");
        env.Fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Resume_with_another_map_is_refused()
    {
        var folder = Path.Combine(_root, "pkg");
        var broken = LeadEnvironment();
        broken.FailFromRow["lead"] = 0;
        await FluentActions.Awaiting(() => Run(broken, folder, Mapped())).Should().ThrowAsync<DataverseException>();

        var other = Mapped("version: 1\ntables:\n  - source: lead\n    target: xv_lead\n");
        await FluentActions.Awaiting(() => Run(LeadEnvironment(), folder, other, resume: true))
            .Should().ThrowAsync<MigrationExportException>().WithMessage("*options differ*");
    }

    [Fact]
    public void Fingerprint_without_maps_keeps_the_previous_format()
    {
        var options = new ExportOptions(["Xv_Core"], true, new Dictionary<string, string>(), ["b", "A"], ["c"]);
        Exporter.Fingerprint(options).Should().Be("xv_core|all-unmanaged|a,b|c");
        Exporter.Fingerprint(Mapped(basis: options)).Should().StartWith("xv_core|all-unmanaged|a,b|c|").And.HaveLength("xv_core|all-unmanaged|a,b|c|".Length + 64);
    }

    [Fact]
    public async Task Options_of_a_same_name_mapped_table_are_read()
    {
        var env = LeadEnvironment();
        // A same-table entry (account stays account, its industry codes translated): the export reads the table's options for the map.
        await Run(env, Path.Combine(_root, "pkg"), Mapped("version: 1\ntables:\n  - source: account\n    target: account\n    columns:\n      industrycode: { target: industrycode, values: { 1: 2 } }\n"));
        env.Fake.Requests.Should().Contain(r => r.Url.Contains("LogicalName='account')/Attributes/Microsoft.Dynamics.CRM.PicklistAttributeMetadata", StringComparison.Ordinal));
    }
}
