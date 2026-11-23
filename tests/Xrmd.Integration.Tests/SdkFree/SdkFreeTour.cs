using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions.Sdk;
using Xrmd.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests.SdkFree;

/// <summary>One step of <see cref="SdkFreeTour"/>: its fixed name and what it does against the host of a <see cref="TourContext"/>.</summary>
public sealed record TourStep(string Name, Func<TourContext, Task> Run);

/// <summary>
/// What every tour step works with: the published host (its process, base URL and SQL endpoint port), an admin <see cref="HttpClient"/>
/// with a dev token, a way to mint a dev token for another user, the published single-file CLI and the SDK folder it runs with.
/// </summary>
public sealed class TourContext(RunningProcess host, string baseUrl, Guid adminObjectId, int tdsPort, string cli, string cliSdkFolder, string workFolder)
    : IDisposable
{
    public const string Api = "api/data/v9.2/";

    public RunningProcess Host { get; } = host;

    /// <summary>The host's base URL, with a trailing slash; also the dev tokens' audience.</summary>
    public string BaseUrl { get; } = baseUrl;

    public Guid AdminObjectId { get; } = adminObjectId;

    /// <summary>The port of the host's SQL (TDS) endpoint.</summary>
    public int TdsPort { get; } = tdsPort;

    /// <summary>The published single-file <c>xrmd</c>.</summary>
    public string Cli { get; } = cli;

    /// <summary>The CLI's <c>XRMD_SDK_DIR</c>: an empty folder (the CLI runs without the SDK).</summary>
    public string CliSdkFolder { get; } = cliSdkFolder;

    /// <summary>A folder of this tour (the CLI's home and config folder, files the steps write).</summary>
    public string WorkFolder { get; } = workFolder;

    /// <summary>A short id of this tour run, used in the names of the records it creates.</summary>
    public string Run { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>An <see cref="HttpClient"/> on <see cref="BaseUrl"/> whose requests carry the administrator's dev token.</summary>
    public HttpClient Admin { get; } = Client(baseUrl, adminObjectId);

    /// <summary>A dev token of the user with Entra object id <paramref name="objectId"/>, for this host.</summary>
    public string TokenFor(Guid objectId) => DevTokenFactory.Create(objectId, BaseUrl);

    /// <summary>A new <see cref="HttpClient"/> on <paramref name="baseUrl"/> with the dev token of <paramref name="objectId"/>.</summary>
    public static HttpClient Client(string baseUrl, Guid objectId)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, baseUrl));
        return client;
    }

    /// <summary>Sends a request as the administrator (or the client given); the status, the body text and the response.</summary>
    public async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path,
        string? json = null, Action<HttpRequestMessage>? configure = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        configure?.Invoke(request);
        var response = await (client ?? Admin).SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    /// <summary>A request whose answer must be <paramref name="expected"/>; its body as JSON (null when empty).</summary>
    public async Task<JsonNode?> ExpectAsync(HttpStatusCode expected, HttpMethod method, string path, string? json = null,
        Action<HttpRequestMessage>? configure = null, HttpClient? client = null)
    {
        var (status, text, _) = await SendAsync(method, path, json, configure, client);
        status.Should().Be(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>A Web API POST that creates a record (204); the id of the new record, from <c>OData-EntityId</c>.</summary>
    public async Task<Guid> CreateAsync(string set, JsonObject body, Action<HttpRequestMessage>? configure = null, HttpClient? client = null)
    {
        var (status, text, response) = await SendAsync(HttpMethod.Post, Api + set, body.ToJsonString(), configure, client);
        status.Should().Be(HttpStatusCode.NoContent, $"POST {set}: {text}");
        var url = response.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    /// <summary>The answer of <c>WhoAmI</c> for the administrator.</summary>
    public async Task<(Guid UserId, Guid BusinessUnitId, Guid OrganizationId)> WhoAmIAsync()
    {
        var json = (await ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + "WhoAmI"))!;
        return (json["UserId"]!.GetValue<Guid>(), json["BusinessUnitId"]!.GetValue<Guid>(), json["OrganizationId"]!.GetValue<Guid>());
    }

    /// <summary>Creates a user-owned table <paramref name="name"/> with the primary name column <c>new_name</c>; its entity set name.</summary>
    public async Task<string> CreateTableAsync(string name, string? solution = null)
    {
        await ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Post, Api + "EntityDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{name}}", "LogicalName": "{{name}}",
              "OwnershipType": "UserOwned",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Probe {{Run}}", "LanguageCode": 1033 } ] },
              "DisplayCollectionName": { "LocalizedLabels": [ { "Label": "Probes {{Run}}", "LanguageCode": 1033 } ] },
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true,
                                "DisplayName": { "LocalizedLabels": [ { "Label": "Name", "LanguageCode": 1033 } ] } } ]
            }
            """, r => { if (solution is not null) r.Headers.Add("MSCRM.SolutionUniqueName", solution); });
        var entity = (await ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + $"EntityDefinitions(LogicalName='{name}')?$select=EntitySetName"))!;
        return entity["EntitySetName"]!.GetValue<string>();
    }

    /// <summary>Runs the published CLI with <see cref="CliSdkFolder"/> as its SDK folder, its own home and config folder, no terminal.</summary>
    public async Task<(int Code, string Output, string Error)> RunCliAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(Cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = WorkFolder,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["HOME"] = WorkFolder;
        psi.Environment["USERPROFILE"] = WorkFolder;
        psi.Environment["XRMD_CONFIG_DIR"] = Path.Combine(WorkFolder, "config");
        psi.Environment[SdkFolder.DirectoryVariable] = CliSdkFolder;
        psi.Environment["XRMD_DEV_SIGNING_KEY"] = DevTokenFactory.SigningKey;

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"xrmd {string.Join(' ', args)} did not exit within 2 minutes.");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose() => Admin.Dispose();
}

/// <summary>
/// Plan S.2a, Task 6: the fixed tour of the SDK-free probe. Each step does the smallest real thing of its area over the public
/// surfaces of a published host (Web API, Admin API, authoring API, MCP, SQL endpoint, web shell, CLI), reusing the request shapes
/// of that area's tests. The names and their order are fixed: a later plan may only remove names from the baseline, never add a step
/// without the owner's OK. None of the steps uses a feature an SDK-free server refuses by design (SOAP, SDK plug-ins, migration).
/// </summary>
public static class SdkFreeTour
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("host-start", HostStartAsync),
        new("healthz", HealthzAsync),
        new("admin-health", AdminHealthAsync),
        new("webapi-whoami", WhoAmIAsync),
        new("webapi-crud", CrudAsync),
        new("webapi-metadata", MetadataAsync),
        new("query-odata", ODataAsync),
        new("query-fetchxml", FetchXmlAsync),
        new("security-privilege", SecurityPrivilegeAsync),
        new("webhook", WebhookAsync),
        new("workflow", WorkflowAsync),
        new("custom-api", CustomApiAsync),
        new("solution-export-import", SolutionExportImportAsync),
        new("audit", AuditAsync),
        new("files", FilesAsync),
        new("tds", TdsAsync),
        new("mcp", McpAsync),
        new("web-admin", WebAdminAsync),
        new("apps", AppsAsync),
        new("workflow-studio", WorkflowStudioAsync),
        new("cli", CliAsync),
    ];

    /// <summary>The name of the step that starts the host: when it fails, no later step runs.</summary>
    public const string HostStart = "host-start";

    private const string Api = TourContext.Api;

    // ---- host -------------------------------------------------------------------------------------------------------------

    /// <summary>The host answers <c>/healthz</c> within 4 minutes; a process that exits first fails the step with its exit code.</summary>
    private static async Task HostStartAsync(TourContext ctx)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        while (true)
        {
            if (ctx.Host.Process.HasExited)
            {
                await ctx.Host.WaitForExitAsync(TimeSpan.FromSeconds(10)); // its output is complete
                throw new InvalidOperationException($"The host exited with code {ctx.Host.Process.ExitCode} before it answered /healthz.");
            }
            try
            {
                if ((await http.GetAsync(ctx.BaseUrl + "healthz")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The host did not answer /healthz within 4 minutes.");
            await Task.Delay(500);
        }
    }

    private static async Task HealthzAsync(TourContext ctx)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ctx.BaseUrl) };
        var response = await http.GetAsync("healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    private static async Task AdminHealthAsync(TourContext ctx)
    {
        var health = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, "admin/health"))!;
        health["status"]!.GetValue<string>().Should().Be("Healthy", health.ToJsonString());
    }

    // ---- Web API ----------------------------------------------------------------------------------------------------------

    private static async Task WhoAmIAsync(TourContext ctx)
    {
        var (user, businessUnit, organization) = await ctx.WhoAmIAsync();
        user.Should().NotBeEmpty();
        businessUnit.Should().NotBeEmpty();
        organization.Should().NotBeEmpty();
    }

    /// <summary>Creates, reads, updates and deletes an <c>account</c>.</summary>
    private static async Task CrudAsync(TourContext ctx)
    {
        var id = await ctx.CreateAsync("accounts", new JsonObject { ["name"] = "Probe " + ctx.Run });
        var read = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + $"accounts({id})?$select=name"))!;
        read["name"]!.GetValue<string>().Should().Be("Probe " + ctx.Run);

        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Patch, Api + $"accounts({id})", """{ "name": "Probe updated" }""");
        (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + $"accounts({id})?$select=name"))!["name"]!.GetValue<string>()
            .Should().Be("Probe updated");

        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Delete, Api + $"accounts({id})");
        await ctx.ExpectAsync(HttpStatusCode.NotFound, HttpMethod.Get, Api + $"accounts({id})?$select=name");
    }

    /// <summary>A table's and a column's definitions and the CSDL document.</summary>
    private static async Task MetadataAsync(TourContext ctx)
    {
        var entity = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + "EntityDefinitions(LogicalName='account')?$select=LogicalName,EntitySetName,PrimaryIdAttribute"))!;
        entity["EntitySetName"]!.GetValue<string>().Should().Be("accounts");
        entity["PrimaryIdAttribute"]!.GetValue<string>().Should().Be("accountid");

        var column = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + "EntityDefinitions(LogicalName='account')/Attributes(LogicalName='name')?$select=LogicalName,AttributeType"))!;
        column["AttributeType"]!.GetValue<string>().Should().Be("String");

        var (status, csdl, _) = await ctx.SendAsync(HttpMethod.Get, Api + "$metadata");
        status.Should().Be(HttpStatusCode.OK, csdl);
        csdl.Should().Contain("<EntityType Name=\"account\"");
    }

    private static async Task<string> TwoAccountsAsync(TourContext ctx, string what)
    {
        var prefix = $"Probe {what} {ctx.Run} ";
        await ctx.CreateAsync("accounts", new JsonObject { ["name"] = prefix + "b" });
        await ctx.CreateAsync("accounts", new JsonObject { ["name"] = prefix + "a" });
        return prefix;
    }

    private static async Task ODataAsync(TourContext ctx)
    {
        var prefix = await TwoAccountsAsync(ctx, "odata");
        var json = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + $"accounts?$select=name&$filter=startswith(name,'{prefix}')&$orderby=name asc&$count=true"))!;
        json["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Equal(prefix + "a", prefix + "b");
        json["@odata.count"]!.GetValue<int>().Should().Be(2);
    }

    private static async Task FetchXmlAsync(TourContext ctx)
    {
        var prefix = await TwoAccountsAsync(ctx, "fetch");
        var fetch = $"""
            <fetch><entity name="account"><attribute name="name" /><order attribute="name" descending="true" />
            <filter><condition attribute="name" operator="like" value="{prefix}%" /></filter></entity></fetch>
            """;
        var json = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + "accounts?fetchXml=" + Uri.EscapeDataString(fetch)))!;
        json["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Equal(prefix + "b", prefix + "a");
    }

    // ---- security ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A user of the organization with no security role gets 403 on <c>POST accounts</c> (the caller check's "no security role",
    /// 0x80042f09); given a role with no privileges, the same POST is the privilege check's 403 (PrivilegeDenied, 0x80040220). Each
    /// refusal is asserted by its error code, so a crash mapped to 403 cannot pass, and no account is created.
    /// </summary>
    private static async Task SecurityPrivilegeAsync(TourContext ctx)
    {
        var (_, businessUnit, _) = await ctx.WhoAmIAsync();
        var oid = Guid.NewGuid();
        var user = await ctx.CreateAsync("systemusers", new JsonObject
        {
            ["azureactivedirectoryobjectid"] = oid.ToString(),
            ["domainname"] = $"probe-{ctx.Run}@example.com",
            ["fullname"] = "Probe no role " + ctx.Run,
            ["businessunitid@odata.bind"] = $"/businessunits({businessUnit})",
        });

        using var client = TourContext.Client(ctx.BaseUrl, oid);
        var name = "Probe forbidden " + ctx.Run;
        async Task RefusedAsync(string code)
        {
            var (status, text, _) = await ctx.SendAsync(HttpMethod.Post, Api + "accounts", new JsonObject { ["name"] = name }.ToJsonString(), client: client);
            status.Should().Be(HttpStatusCode.Forbidden, text);
            JsonNode.Parse(text)!["error"]!["code"]!.GetValue<string>().Should().Be(code, text);
        }

        await RefusedAsync("0x80042f09");

        var role = await ctx.CreateAsync("roles", new JsonObject
        {
            ["name"] = "Probe empty role " + ctx.Run, ["businessunitid@odata.bind"] = $"/businessunits({businessUnit})",
        });
        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Post, Api + $"systemusers({user})/systemuserroles_association/$ref",
            new JsonObject { ["@odata.id"] = $"{ctx.BaseUrl}{Api}roles({role})" }.ToJsonString());
        await RefusedAsync("0x80040220");

        var rows = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + $"accounts?$select=name&$filter=name eq '{name}'"))!;
        rows["value"]!.AsArray().Should().BeEmpty("the refused creates wrote nothing");
    }

    // ---- extensibility ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A webhook (contract 8, HttpHeader auth) and a synchronous post-operation step on Create of <c>account</c>, authored over the
    /// Web API as <c>WebhookWebApiTests</c> does; a Create reaches a local receiver with the configured header. The step and the
    /// endpoint are deleted again, so later creates do not call a receiver that is gone.
    /// </summary>
    private static async Task WebhookAsync(TourContext ctx)
    {
        const string header = "x-probe-token";
        using var receiver = new WebhookReceiver();
        var endpoint = await ctx.CreateAsync("serviceendpoints", new JsonObject
        {
            ["name"] = "probe hook " + ctx.Run, ["contract"] = 8, ["authtype"] = 5, ["url"] = receiver.Url,
            ["authvalue"] = $"<settings><setting name=\"{header}\" value=\"{ctx.Run}\" /></settings>",
        });
        Guid? step = null;
        try
        {
            step = await ctx.CreateAsync("sdkmessageprocessingsteps", new JsonObject
            {
                ["name"] = "probe hook step " + ctx.Run, ["stage"] = 40, ["mode"] = 0, ["rank"] = 1,
                ["eventhandler_serviceendpoint@odata.bind"] = $"/serviceendpoints({endpoint})",
                ["sdkmessageid@odata.bind"] = $"/sdkmessages({SdkMessageCatalog.MessageId("Create")})",
                ["sdkmessagefilterid@odata.bind"] = $"/sdkmessagefilters({SdkMessageCatalog.FilterId("Create", "account")})",
            });

            await ctx.CreateAsync("accounts", new JsonObject { ["name"] = "Probe webhook " + ctx.Run });

            var request = receiver.Requests.Should().ContainSingle("one Create was delivered").Which;
            request[header].Should().Be(ctx.Run);
            request["x-ms-dynamics-request-name"].Should().Be("Create");
        }
        finally
        {
            if (step is { } id) await ctx.SendAsync(HttpMethod.Delete, Api + $"sdkmessageprocessingsteps({id})");
            await ctx.SendAsync(HttpMethod.Delete, Api + $"serviceendpoints({endpoint})");
        }
    }

    private static string RealtimeWorkflow(string name, string subject) => $$"""
        kind: workflow
        name: {{name}}
        entity: account
        mode: realtime
        trigger:
          create: true
        steps:
          - create: { entity: task, set: { subject: {{subject}}, regardingobjectid: { record: primary } } }

        """;

    private static Action<HttpRequestMessage> Yaml(string yaml) =>
        r => r.Content = new StringContent(yaml, Encoding.UTF8, "text/yaml");

    /// <summary>
    /// A real-time workflow on Create of <c>account</c>, applied and activated through the authoring API (the YAML of
    /// <c>CliWorkflowMcpTests</c>); a new account gets its task. Deactivated again at the end.
    /// </summary>
    private static async Task WorkflowAsync(TourContext ctx)
    {
        var name = "Probe workflow " + ctx.Run;
        var subject = "Probe task " + ctx.Run;
        var applied = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Post, "authoring/workflows/apply?dryRun=false&activate=true",
            configure: Yaml(RealtimeWorkflow(name, subject))))!;
        applied["action"]!.GetValue<string>().Should().Be("created", applied.ToJsonString());
        try
        {
            var account = await ctx.CreateAsync("accounts", new JsonObject { ["name"] = "Probe workflow " + ctx.Run });
            var tasks = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
                Api + $"tasks?$select=subject&$filter=_regardingobjectid_value eq {account}"))!;
            tasks["value"]!.AsArray().Select(t => t!["subject"]!.GetValue<string>()).Should().Equal(subject);
        }
        finally
        {
            await ctx.SendAsync(HttpMethod.Post, $"authoring/workflows/{Uri.EscapeDataString(name)}/deactivate");
        }
    }

    /// <summary>
    /// A custom API without a plug-in (an unbound action with one optional input), created over the Web API as
    /// <c>CustomApiAuthoringWebApiTests</c> does: listed in <c>$metadata</c> and callable in the next request.
    /// </summary>
    private static async Task CustomApiAsync(TourContext ctx)
    {
        var name = "new_Probe" + ctx.Run;
        var api = await ctx.CreateAsync("customapis", new JsonObject
        {
            ["uniquename"] = name, ["name"] = name, ["displayname"] = name, ["bindingtype"] = 0, ["isfunction"] = false,
            ["allowedcustomprocessingsteptype"] = 0,
        });
        await ctx.CreateAsync("customapirequestparameters", new JsonObject
        {
            ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "Text", ["type"] = 10, ["isoptional"] = true,
            ["CustomAPIId@odata.bind"] = $"/customapis({api})",
        });

        var (status, csdl, _) = await ctx.SendAsync(HttpMethod.Get, Api + "$metadata");
        status.Should().Be(HttpStatusCode.OK, csdl);
        csdl.Should().Contain($"<Action Name=\"{name}\"");

        var (called, text, _) = await ctx.SendAsync(HttpMethod.Post, Api + name, """{ "Text": "hi" }""");
        ((int)called).Should().BeInRange(200, 299, $"POST {name}: {text}");
    }

    // ---- solutions --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An unmanaged solution of the default publisher holding one new table (created with <c>MSCRM.SolutionUniqueName</c>) is exported
    /// with <c>ExportSolution</c> and its zip imported back with <c>ImportSolution</c> (the request shapes of <c>SolutionWebApiTests</c>).
    /// </summary>
    private static async Task SolutionExportImportAsync(TourContext ctx)
    {
        var publishers = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + "publishers?$select=publisherid&$filter=customizationprefix eq 'new'"))!;
        var publisher = publishers["value"]!.AsArray().First()!["publisherid"]!.GetValue<Guid>();
        var solution = "probe" + ctx.Run;
        await ctx.CreateAsync("solutions", new JsonObject
        {
            ["uniquename"] = solution, ["friendlyname"] = "Probe " + ctx.Run, ["version"] = "1.0.0.0",
            ["publisherid@odata.bind"] = $"/publishers({publisher})",
        });
        var table = "new_ps" + ctx.Run;
        await ctx.CreateTableAsync(table, solution);

        var exported = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Post, Api + "ExportSolution",
            $$"""{ "SolutionName": "{{solution}}", "Managed": false }"""))!;
        var file = exported["ExportSolutionFile"]!.GetValue<string>();
        using (var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(file)), ZipArchiveMode.Read))
        {
            var customizations = zip.GetEntry("customizations.xml") ?? throw new InvalidOperationException("The export has no customizations.xml.");
            using var reader = new StreamReader(customizations.Open());
            (await reader.ReadToEndAsync()).Should().Contain(table, "the export holds the new table");
        }

        var job = Guid.NewGuid();
        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Post, Api + "ImportSolution",
            $$"""{ "CustomizationFile": "{{file}}", "ImportJobId": "{{job}}", "OverwriteUnmanagedCustomizations": true, "PublishWorkflows": false }""");
        await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, Api + $"EntityDefinitions(LogicalName='{table}')?$select=LogicalName");
    }

    // ---- audit and files --------------------------------------------------------------------------------------------------

    /// <summary>Auditing on for the organization; a created then updated account has its history in <c>RetrieveRecordChangeHistory</c>.</summary>
    private static async Task AuditAsync(TourContext ctx)
    {
        var (_, _, organization) = await ctx.WhoAmIAsync();
        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Patch, Api + $"organizations({organization})", """{ "isauditenabled": true }""");

        var id = await ctx.CreateAsync("accounts", new JsonObject { ["name"] = "Probe audit " + ctx.Run });
        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Patch, Api + $"accounts({id})", """{ "name": "Probe audit updated" }""");

        var json = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + "RetrieveRecordChangeHistory(Target=@target)?@target=" + Uri.EscapeDataString($"{{'@odata.id':'accounts({id})'}}")))!;
        var details = json["AuditDetailCollection"]!["AuditDetails"]!.AsArray();
        details.Should().NotBeEmpty("the update was audited: " + json.ToJsonString());
        details.Should().Contain(d => d!["NewValue"]!["name"] != null && d["NewValue"]!["name"]!.GetValue<string>() == "Probe audit updated",
            json.ToJsonString());
    }

    /// <summary>A file column on a new table: a single-request upload (PATCH) and the download (<c>GET …/$value</c>) of the same bytes.</summary>
    private static async Task FilesAsync(TourContext ctx)
    {
        var table = "new_pf" + ctx.Run;
        var set = await ctx.CreateTableAsync(table);
        await ctx.ExpectAsync(HttpStatusCode.NoContent, HttpMethod.Post, Api + $"EntityDefinitions(LogicalName='{table}')/Attributes", """
            {
              "@odata.type": "Microsoft.Dynamics.CRM.FileAttributeMetadata", "LogicalName": "new_doc", "SchemaName": "new_Doc",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Doc", "LanguageCode": 1033 } ] }
            }
            """);
        var id = await ctx.CreateAsync(set, new JsonObject { ["new_name"] = "probe" });

        var bytes = RandomNumberGenerator.GetBytes(10_000);
        var upload = new HttpRequestMessage(HttpMethod.Patch, Api + $"{set}({id})/new_doc") { Content = new ByteArrayContent(bytes) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Headers.Add("x-ms-file-name", "probe.bin");
        var uploaded = await ctx.Admin.SendAsync(upload);
        uploaded.StatusCode.Should().Be(HttpStatusCode.NoContent, await uploaded.Content.ReadAsStringAsync());

        var download = await ctx.Admin.GetAsync(Api + $"{set}({id})/new_doc/$value");
        download.StatusCode.Should().Be(HttpStatusCode.OK, await download.Content.ReadAsStringAsync());
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    // ---- SQL endpoint, MCP ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The SQL (TDS) endpoint: PRELOGIN, TLS with the host's certificate and LOGIN7 reach the server's authenticator. The tour host
    /// runs in Development mode (dev tokens for every other step), where the endpoint has no local credentials, so the smallest real
    /// check is the server's own refusal of a SQL login (<c>TdsAuthenticationTests</c>), not a query: query execution (translation,
    /// the query handler) is not covered by this step. The listener maps any exception of the authenticator to the same 18456, so the
    /// step also requires the authenticator's own refusal line in the host output and no "failed with &lt;exception&gt;" line.
    /// </summary>
    private static async Task TdsAsync(TourContext ctx)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{ctx.TdsPort}",
            InitialCatalog = "acme",
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            Pooling = false,
            UserID = "probe-admin",
            Password = "probe-password",
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        var error = (await FluentActions.Awaiting(() => connection.OpenAsync()).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(18456, error.Message);
        error.Message.Should().Be("Login failed for the SQL endpoint.");

        // The authenticator itself refused (TdsAuthenticator.Refused), not the listener's catch-all for an exception in it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!ctx.Host.Output.Contains("SQL endpoint login of probe-admin from", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        var log = ctx.Host.Output;
        log.Should().MatchRegex(@"SQL endpoint login of probe-admin from \S+ refused: ", "the authenticator refused the login itself");
        log.Should().NotContain("A SQL endpoint login from", "no exception was thrown while authenticating");
    }

    /// <summary>The MCP server on <c>/mcp</c> (streamable HTTP) with the admin token: its tools are listed and <c>query</c> runs.</summary>
    private static async Task McpAsync(TourContext ctx)
    {
        var http = TourContext.Client(ctx.BaseUrl, ctx.AdminObjectId);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(ctx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().Contain(["list_tables", "query", "create_record"]);

        var result = await client.CallToolAsync("query", new Dictionary<string, object?> { ["odata"] = "accounts?$select=name&$top=1" });
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        result.IsError.Should().NotBe(true, text);
        JsonNode.Parse(text).Should().NotBeNull();
    }

    // ---- web shells -------------------------------------------------------------------------------------------------------

    private static async Task ShellAsync(TourContext ctx, string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ctx.BaseUrl) };
        var response = await http.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html", path);
        (await response.Content.ReadAsStringAsync()).Should().Contain("<html", path);
    }

    /// <summary>The web admin shell and the anonymous <c>/ui/config.json</c> it boots from.</summary>
    private static async Task WebAdminAsync(TourContext ctx)
    {
        await ShellAsync(ctx, "");
        using var http = new HttpClient { BaseAddress = new Uri(ctx.BaseUrl) };
        var response = await http.GetAsync("ui/config.json");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var config = JsonNode.Parse(text)!;
        config["authMode"]!.GetValue<string>().Should().Be("Development", text);
        config["apiVersion"]!.GetValue<string>().Should().Be("v9.2", text);
    }

    /// <summary>The apps page, a new app, and the query its picker reads (<c>ui/src/apps/loadApps.ts</c>).</summary>
    private static async Task AppsAsync(TourContext ctx)
    {
        await ShellAsync(ctx, "apps");
        // A new organization has no app: the step adds one, as AppModuleTablesTests does, and the picker's query must list it.
        var unique = "new_probeapp" + ctx.Run;
        await ctx.CreateAsync("appmodules", new JsonObject { ["name"] = "Probe app " + ctx.Run, ["uniquename"] = unique });
        var json = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get,
            Api + "appmodules?$select=appmoduleid,uniquename,name,description,clienttype&$filter=statecode eq 0"))!;
        json["value"]!.AsArray().Select(a => a!["uniquename"]!.GetValue<string>()).Should().Contain(unique, json.ToJsonString());
    }

    /// <summary>The workflow studio's page and the authoring API calls it makes: the list and a dry-run check of a draft.</summary>
    private static async Task WorkflowStudioAsync(TourContext ctx)
    {
        await ShellAsync(ctx, "studio/workflows");
        var name = "Probe studio " + ctx.Run;
        var dryRun = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Post, "authoring/workflows/apply?dryRun=true",
            configure: Yaml(RealtimeWorkflow(name, "Studio"))))!;
        dryRun["errors"]!.AsArray().Should().BeEmpty(dryRun.ToJsonString());

        var list = (await ctx.ExpectAsync(HttpStatusCode.OK, HttpMethod.Get, "authoring/workflows?kind=workflow"))!;
        list.Should().BeOfType<JsonArray>();
        list.AsArray().Should().NotContain(w => w!["name"]!.GetValue<string>() == name, "a dry run writes nothing");
    }

    // ---- CLI --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The published single-file CLI, its SDK folder empty: <c>auth login --mode dev</c>, <c>table list -o json</c> and
    /// <c>data query --odata accounts… -o json</c> (the CLI has no <c>data list</c>; <c>data query</c> is its record listing).
    /// </summary>
    private static async Task CliAsync(TourContext ctx)
    {
        var login = await ctx.RunCliAsync("auth", "login", "--url", ctx.BaseUrl, "--mode", "dev", "--object-id", ctx.AdminObjectId.ToString());
        login.Code.Should().Be(0, login.Output + login.Error);

        var tables = await ctx.RunCliAsync("table", "list", "-o", "json");
        tables.Code.Should().Be(0, tables.Output + tables.Error);
        tables.Output.Should().Contain("\"account\"");
        JsonNode.Parse(tables.Output).Should().BeOfType<JsonArray>();

        var name = "Probe cli " + ctx.Run;
        await ctx.CreateAsync("accounts", new JsonObject { ["name"] = name });
        var rows = await ctx.RunCliAsync("data", "query", "--odata", $"accounts?$select=name&$filter=name eq '{name}'", "-o", "json");
        rows.Code.Should().Be(0, rows.Output + rows.Error);
        JsonNode.Parse(rows.Output)!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Equal(name);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    /// <summary>Writes a self-signed PEM certificate and key for 127.0.0.1 and localhost (the SQL endpoint's TLS).</summary>
    public static void WriteCertificate(string certificatePath, string keyPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>A free TCP port on the loopback interface.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An <see cref="HttpListener"/> on 127.0.0.1 and a free port that answers 200 and keeps each request's headers.</summary>
    private sealed class WebhookReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public WebhookReceiver()
        {
            Url = $"http://127.0.0.1:{FreePort()}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public ConcurrentQueue<IReadOnlyDictionary<string, string>> Requests { get; } = new();

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                using (var reader = new StreamReader(context.Request.InputStream)) await reader.ReadToEndAsync();
                Requests.Enqueue(context.Request.Headers.AllKeys.Where(k => k is not null)
                    .ToDictionary(k => k!, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase));
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }
}
