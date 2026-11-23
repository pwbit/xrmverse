using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1b, task 2: the write-only secrets of the mail tables (kept in <c>meta.mail_secret</c>, never returned by any read path) and
/// the profile rules on Create and Update of <c>emailserverprofile</c>.
/// </summary>
[Collection("xrmd")]
public sealed class MailProfileTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Password = "p4ss-w0rd-of-the-smtp";
    private const string ClientSecret = "cl1ent-s3cret-of-graph";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await PluginTestHelper.ClearAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.emailserverprofile");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM meta.mail_secret");
    }

    private static Entity Smtp(string name = "smtp", string? password = Password) => new("emailserverprofile")
    {
        ["name"] = name, ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
        ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(1),
        ["outgoingusername"] = "sender@example.com", ["outgoingpassword"] = password,
    };

    private static Entity Graph(string name = "graph") => new("emailserverprofile")
    {
        ["name"] = name, ["servertype"] = new OptionSetValue(0), ["outgoingauthenticationprotocol"] = new OptionSetValue(4),
        ["outgoingcredentialretrieval"] = new OptionSetValue(7), ["oauthclientid"] = Guid.NewGuid().ToString(),
        ["oauthclientsecret"] = ClientSecret,
    };

    private Task<object?> StoredAsync(Guid record, string name) =>
        TestSql.ScalarAsync(fx.ConnectionString, "SELECT value FROM meta.mail_secret WHERE recordid = @id AND name = @n", ("@id", record), ("@n", name));

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Secrets_AreNotReturned_ByAnyReadPath()
    {
        var smtp = await Admin.CreateAsync(Smtp());
        var graph = await Admin.CreateAsync(Graph());

        (await StoredAsync(smtp, "outgoingpassword")).Should().Be(Password);
        (await StoredAsync(graph, "oauthclientsecret")).Should().Be(ClientSecret);
        (await TestSql.CountAsync(fx.ConnectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = 'emailserverprofile' AND COLUMN_NAME IN ('outgoingpassword', 'oauthclientsecret')"))
            .Should().Be(0, "a secret lives only in the secret table");

        static void NoSecret(Entity e)
        {
            e.Attributes.Keys.Should().NotContain(["outgoingpassword", "oauthclientsecret", "incomingpassword"]);
            e.Attributes.Values.OfType<string>().Should().NotContain(v => v.Contains(Password) || v.Contains(ClientSecret));
        }

        var retrieved = await Admin.RetrieveAsync("emailserverprofile", smtp, new ColumnSet(true));
        NoSecret(retrieved);
        retrieved.GetAttributeValue<bool>("isoutgoingpasswordset").Should().BeTrue();
        var retrievedGraph = await Admin.RetrieveAsync("emailserverprofile", graph, new ColumnSet(true));
        NoSecret(retrievedGraph);
        retrievedGraph.GetAttributeValue<bool>("isoauthclientsecretset").Should().BeTrue();
        retrievedGraph.GetAttributeValue<bool>("isoutgoingpasswordset").Should().BeFalse();

        var multiple = await Admin.RetrieveMultipleAsync(new QueryExpression("emailserverprofile") { ColumnSet = new ColumnSet(true) });
        multiple.Entities.Should().HaveCount(2).And.AllSatisfy(NoSecret);

        var fetched = await Admin.RetrieveMultipleAsync(new FetchExpression("<fetch><entity name='emailserverprofile'><all-attributes /></entity></fetch>"));
        fetched.Entities.Should().HaveCount(2).And.AllSatisfy(NoSecret);

        var (status, body) = await GetAsync($"emailserverprofiles({smtp})");
        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(Password).And.NotContain("outgoingpassword\"").And.Contain("\"isoutgoingpasswordset\":true");
        var (listStatus, listBody) = await GetAsync("emailserverprofiles");
        listStatus.Should().Be(HttpStatusCode.OK, listBody);
        listBody.Should().NotContain(Password).And.NotContain(ClientSecret);
        var (selectStatus, selectBody) = await GetAsync($"emailserverprofiles({smtp})?$select=outgoingpassword");
        selectStatus.Should().Be(HttpStatusCode.BadRequest, selectBody);
        selectBody.Should().NotContain(Password);

        // Asked for by name, the usual 400 of an unreadable column.
        (await BadRequestAsync(() => Admin.RetrieveAsync("emailserverprofile", smtp, new ColumnSet("outgoingpassword"))))
            .Should().Be("'emailserverprofile' entity doesn't contain attribute with Name = 'outgoingpassword'.");

        // Audit: the table's setting is fixed off, so a write leaves no audit row; whatever is audited never holds a secret.
        await Admin.UpdateAsync(new Entity("emailserverprofile", smtp) { ["outgoingpassword"] = "an0ther-p4ssword" });
        (await TestSql.CountAsync(fx.ConnectionString,
            $"SELECT COUNT(*) FROM data.audit WHERE changedata LIKE '%{Password}%' OR changedata LIKE '%an0ther-p4ssword%'"))
            .Should().Be(0);

        // A registered PreImage carries no secret (it is the stored row).
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "emailserverprofile", 20, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 0, "pre");
        await Admin.UpdateAsync(new Entity("emailserverprofile", smtp) { ["outgoingpassword"] = "y3t-an0ther", ["description"] = "recorded" });
        var recorded = (string?)await TestSql.ScalarAsync(fx.ConnectionString,
            $"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE @n", ("@n", "Update|20|%"));
        recorded.Should().NotBeNull();
        // The plug-in's Target is what the caller sent (as in Dataverse); the PreImage is the stored row, which has no secret.
        recorded.Should().NotContain(Password).And.NotContain("an0ther-p4ssword");
        recorded!.Split('\n').Single(line => line.StartsWith("pre=", StringComparison.Ordinal)).Split(':')[1].Split(',')
            .Should().NotContain(["outgoingpassword", "incomingpassword", "oauthclientsecret"]).And.Contain("isoutgoingpasswordset");
    }

    [Fact]
    public async Task Secrets_NeverReach_PostOperation_AsyncOrWebhookContexts()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "emailserverprofile", 40, mode: 0, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "emailserverprofile", 40, mode: 0, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "emailserverprofile", 40, mode: 1, rank: 2, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "emailserverprofile", 40, mode: 1, rank: 2, config: TestEntities.PipeLog);
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.asyncoperation");

        var id = await Admin.CreateAsync(Smtp(password: "cr3ate-secret"));
        await Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["outgoingpassword"] = "upd4te-secret" });

        (await StoredAsync(id, "outgoingpassword")).Should().Be("upd4te-secret");
        var recorded = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{TestEntities.PipeLog}");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) recorded.Add(rd.GetString(0));
        }
        recorded.Should().HaveCount(2, "one synchronous step per message");
        recorded.Should().AllSatisfy(r => r.Should().NotContain("cr3ate-secret").And.NotContain("upd4te-secret").And.NotContain("outgoingpassword"));

        var jobs = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT data FROM data.asyncoperation");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) jobs.Add(rd.IsDBNull(0) ? "" : rd.GetString(0));
        }
        jobs.Should().HaveCount(2, "one asynchronous step per message");
        jobs.Should().AllSatisfy(j => j.Should().Contain("RemoteExecutionContext").And.NotContain("cr3ate-secret").And.NotContain("upd4te-secret")
            .And.NotContain("outgoingpassword"));
    }

    [Fact]
    public async Task Secrets_NeverReach_UpsertContexts()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        foreach (var message in new[] { "Create", "Update" })
        {
            await PluginTestHelper.RegisterStepAsync(Admin, type, message, "emailserverprofile", 40, mode: 0, config: TestEntities.PipeLog);
            await PluginTestHelper.RegisterStepAsync(Admin, type, message, "emailserverprofile", 40, mode: 1, rank: 2, config: TestEntities.PipeLog);
        }
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.asyncoperation");

        // UpsertRequest, insert branch then update branch, and UpsertMultiple (one insert, one update).
        var id = Guid.NewGuid();
        var insert = Smtp("upsert", "1ns3rt-secret");
        insert.Id = id;
        await Admin.ExecuteAsync(new UpsertRequest { Target = insert });
        (await StoredAsync(id, "outgoingpassword")).Should().Be("1ns3rt-secret");
        var update = Smtp("upsert", "upd4ted-secret");
        update.Id = id;
        await Admin.ExecuteAsync(new UpsertRequest { Target = update });
        (await StoredAsync(id, "outgoingpassword")).Should().Be("upd4ted-secret");

        var multiNew = Smtp("multi new", "mult1-new-secret");
        multiNew.Id = Guid.NewGuid();
        var multiOld = Smtp("upsert", "mult1-upd-secret");
        multiOld.Id = id;
        await Admin.ExecuteAsync(new UpsertMultipleRequest { Targets = new EntityCollection([multiNew, multiOld]) { EntityName = "emailserverprofile" } });
        (await StoredAsync(multiNew.Id, "outgoingpassword")).Should().Be("mult1-new-secret");
        (await StoredAsync(id, "outgoingpassword")).Should().Be("mult1-upd-secret");

        string[] secrets = ["1ns3rt-secret", "upd4ted-secret", "mult1-new-secret", "mult1-upd-secret"];
        var recorded = new List<string>();
        var jobs = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using (var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{TestEntities.PipeLog}"))
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync()) recorded.Add(rd.GetString(0));
            await using (var cmd = TestSql.Command(conn, "SELECT data FROM data.asyncoperation"))
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync()) jobs.Add(rd.IsDBNull(0) ? "" : rd.GetString(0));
        }
        recorded.Should().HaveCount(4, "one synchronous inner step per upserted record");
        jobs.Should().HaveCount(4, "one asynchronous inner step per upserted record");
        recorded.Should().AllSatisfy(r => r.Should().Contain("parent=Upsert").And.Contain("parenttarget=").And.NotContain("outgoingpassword"));
        jobs.Should().AllSatisfy(j => j.Should().Contain("RemoteExecutionContext").And.NotContain("outgoingpassword"));
        foreach (var secret in secrets)
        {
            recorded.Should().AllSatisfy(r => r.Should().NotContain(secret));
            jobs.Should().AllSatisfy(j => j.Should().NotContain(secret));
        }
    }

    [Fact]
    public async Task Secret_TooLong_Is400_NamingTheColumn()
    {
        // The seeded MaxLength of outgoingpassword is 250; nothing is stored for a refused write.
        var message = await BadRequestAsync(() => Admin.CreateAsync(Smtp(password: new string('x', 251))));
        message.Should().Contain("outgoingpassword").And.Contain("250");
        var id = await Admin.CreateAsync(Smtp(password: new string('y', 250)));
        (await StoredAsync(id, "outgoingpassword")).Should().Be(new string('y', 250));

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["outgoingpassword"] = new string('z', 3000) })))
            .Should().Contain("outgoingpassword");
        (await StoredAsync(id, "outgoingpassword")).Should().Be(new string('y', 250));

        var graph = Graph();
        graph["oauthclientsecret"] = new string('s', 5000);
        (await BadRequestAsync(() => Admin.CreateAsync(graph))).Should().Contain("oauthclientsecret");
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret")).Should().Be(1);
    }

    [Fact]
    public async Task Secret_SetToNull_ClearsFlag_AndRow()
    {
        var id = await Admin.CreateAsync(Smtp());
        (await StoredAsync(id, "outgoingpassword")).Should().Be(Password);

        await Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["description"] = "no new password" });
        (await StoredAsync(id, "outgoingpassword")).Should().Be(Password, "an Update without the column keeps the stored value");

        await Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["outgoingpassword"] = "s3cond" });
        (await StoredAsync(id, "outgoingpassword")).Should().Be("s3cond");
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret")).Should().Be(1);

        await Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["outgoingpassword"] = null });
        (await StoredAsync(id, "outgoingpassword")).Should().BeNull();
        (await Admin.RetrieveAsync("emailserverprofile", id, new ColumnSet("isoutgoingpasswordset")))
            .GetAttributeValue<bool>("isoutgoingpasswordset").Should().BeFalse();

        // A Create with no secret (or a null one) stores nothing and leaves the flag false.
        var bare = await Admin.CreateAsync(Smtp("bare", password: null));
        (await StoredAsync(bare, "outgoingpassword")).Should().BeNull();
        (await Admin.RetrieveAsync("emailserverprofile", bare, new ColumnSet("isoutgoingpasswordset")))
            .GetAttributeValue<bool>("isoutgoingpasswordset").Should().BeFalse();
    }

    [Fact]
    public async Task Mailbox_Password_UsesTheSameStore()
    {
        var queue = await Admin.CreateAsync(new Entity("queue") { ["name"] = "Support " + Guid.NewGuid().ToString("N")[..6] });
        var mailbox = await Admin.CreateAsync(new Entity("mailbox")
        {
            ["name"] = "support mailbox", ["emailaddress"] = "support@example.com", ["username"] = "support@example.com", ["password"] = "mb0x-p4ss",
            ["regardingobjectid"] = new EntityReference("queue", queue),
        });
        try
        {
            (await StoredAsync(mailbox, "password")).Should().Be("mb0x-p4ss");
            var read = await Admin.RetrieveAsync("mailbox", mailbox, new ColumnSet(true));
            read.Attributes.Keys.Should().NotContain("password");
            read.GetAttributeValue<bool>("ispasswordset").Should().BeTrue();
        }
        finally
        {
            await Admin.DeleteAsync("mailbox", mailbox);
            await Admin.DeleteAsync("queue", queue);
        }
        (await StoredAsync(mailbox, "password")).Should().BeNull("the secret goes with its mailbox");
    }

    [Theory]
    [InlineData(1, "1 (Other (POP3/SMTP))")]
    [InlineData(2, "2 (Exchange Server (Hybrid))")]
    [InlineData(3, "3 (Exchange Online (Hybrid))")]
    public async Task Profile_UnsupportedServerType_Is400(int serverType, string named)
    {
        var profile = Smtp();
        profile["servertype"] = new OptionSetValue(serverType);
        var message = await BadRequestAsync(() => Admin.CreateAsync(profile));
        message.Should().Contain("Email server profile 'smtp'").And.Contain($"server type {named} is not supported");
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret")).Should().Be(0, "a refused profile stores no secret");
    }

    [Fact]
    public async Task Profile_UnsupportedProtocol_Is400()
    {
        var ntlm = Smtp();
        ntlm["outgoingauthenticationprotocol"] = new OptionSetValue(2);
        (await BadRequestAsync(() => Admin.CreateAsync(ntlm))).Should().Contain("outgoing authentication protocol 2 (NTLM) is not supported for SMTP");

        var basic = Graph();
        basic["outgoingauthenticationprotocol"] = new OptionSetValue(3);
        (await BadRequestAsync(() => Admin.CreateAsync(basic))).Should().Contain("outgoing authentication protocol 3 (Basic) is not supported for Microsoft Graph");

        var authority = Graph();
        authority["serverauthority"] = new OptionSetValue(2);
        (await BadRequestAsync(() => Admin.CreateAsync(authority))).Should().Contain("server authority 2 (China) is not supported");

        // An Update is checked as the row will be: moving a stored SMTP profile to NTLM is refused, the profile stays.
        var id = await Admin.CreateAsync(Smtp());
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["outgoingauthenticationprotocol"] = new OptionSetValue(2) })))
            .Should().Contain("NTLM");
        (await Admin.RetrieveAsync("emailserverprofile", id, new ColumnSet("outgoingauthenticationprotocol")))
            .GetAttributeValue<OptionSetValue>("outgoingauthenticationprotocol").Value.Should().Be(3);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["servertype"] = new OptionSetValue(0) })))
            .Should().Contain("not supported for Microsoft Graph");
    }

    [Theory]
    [InlineData(2, "2 (server to server)")]
    [InlineData(3, "3 (Windows)")]
    [InlineData(4, "4 (anonymous)")]
    [InlineData(5, "5 (Gmail OAuth)")]
    [InlineData(6, "6 (HMA)")]
    [InlineData(8, "8 (PPMI)")]
    public async Task Profile_UnsupportedCredentialRetrieval_Is400(int retrieval, string named)
    {
        var profile = Smtp();
        profile["outgoingcredentialretrieval"] = new OptionSetValue(retrieval);
        (await BadRequestAsync(() => Admin.CreateAsync(profile))).Should().Contain($"outgoing credential retrieval {named} is not supported");

        var graph = Graph();
        graph["outgoingcredentialretrieval"] = new OptionSetValue(retrieval);
        (await BadRequestAsync(() => Admin.CreateAsync(graph))).Should().Contain($"outgoing credential retrieval {named} is not supported");
    }

    [Fact]
    public async Task Profile_AutoDetectOnSmtp_IsStoredAsBasic_AndGraphIsAccepted()
    {
        var auto = Smtp();
        auto["outgoingauthenticationprotocol"] = new OptionSetValue(0);
        var id = await Admin.CreateAsync(auto);
        (await Admin.RetrieveAsync("emailserverprofile", id, new ColumnSet("outgoingauthenticationprotocol")))
            .GetAttributeValue<OptionSetValue>("outgoingauthenticationprotocol").Value.Should().Be(3);

        var byUser = Smtp("by user");
        byUser["outgoingcredentialretrieval"] = new OptionSetValue(0);
        await Admin.CreateAsync(byUser);

        var graph = await Admin.CreateAsync(Graph());
        (await Admin.RetrieveAsync("emailserverprofile", graph, new ColumnSet("serverauthority", "servertype")))
            .GetAttributeValue<OptionSetValue>("servertype").Value.Should().Be(0);
        var automatic = Graph("automatic");
        automatic["serverauthority"] = new OptionSetValue(3);
        await Admin.CreateAsync(automatic);

        // An Update that touches none of the rules is not checked again.
        await Admin.UpdateAsync(new Entity("emailserverprofile", id) { ["name"] = "renamed" });
    }

    [Fact]
    public async Task Profile_Delete_RemovesSecrets()
    {
        var smtp = await Admin.CreateAsync(Smtp());
        var graph = await Admin.CreateAsync(Graph());
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret")).Should().Be(2);

        await Admin.DeleteAsync("emailserverprofile", smtp);

        (await StoredAsync(smtp, "outgoingpassword")).Should().BeNull();
        (await StoredAsync(graph, "oauthclientsecret")).Should().Be(ClientSecret);
    }

    [Fact]
    public async Task Profile_Create_ByBasicUser_Is403()
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);

        (await FaultAsync(() => ServiceFor(fx, oid).CreateAsync(Smtp()))).HttpStatus.Should().Be(403);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret")).Should().Be(0);
    }
}
