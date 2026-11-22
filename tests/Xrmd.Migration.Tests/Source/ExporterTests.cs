using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class ExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Guid G(int table, int i) => new(i, (short)table, 0, new byte[8]);

    private static readonly ExportOptions Options = new(["xv_core"], false, new Dictionary<string, string>(), [], [], Parallel: 1);

    private static FakeEnvironment Environment(int projects = 5)
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
        return env.Build();
    }

    private static DataverseClient Client(FakeEnvironment env) => new(env.Fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    private static IEnumerable<string> Fetches(FakeEnvironment env, string set) =>
        env.Fake.Requests.Where(r => r.Url.StartsWith($"/api/data/v9.2/{set}?fetchXml=", StringComparison.Ordinal)).Select(r => r.Url);

    private async Task<PackageManifest> Run(FakeEnvironment env, string folder, ExportOptions? options = null, bool resume = false)
    {
        using var writer = resume ? PackageWriter.OpenForResume(folder, SyntheticPackage.Passphrase) : PackageWriter.Create(folder, SyntheticPackage.Passphrase);
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
        var manifest = await Run(env, Path.Combine(_root, "pkg"));

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
}
