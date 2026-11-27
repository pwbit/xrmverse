using System.Globalization;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Workflows;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c2b, Task 1: golden dumps of the internal checks of stage 20 (custom API authoring, registration validation, process writes,
/// service endpoints, mail secrets, and the owner a real-time workflow is matched on), captured on the code before they move onto the
/// model request (plan S.2e3c2b, Review Focus 1, 2 and 4). Each scenario arranges its rows as the administrator, then runs one request
/// through <see cref="PipelineExecutor.ExecuteAsync"/> as the System Administrator, or the caller it names, and writes the request, the
/// fault (or <c>none</c>), the request as the caller holds it afterwards, the rows of the table written, the secrets of the record and,
/// where named, what a PostOperation step and its asynchronous payload saw of the Target. The conventions are in
/// <c>tests/fixtures/stage20-golden/README.md</c>; a missing file is written once and the test fails, an existing file is never rewritten.
/// </summary>
[Collection("xrmd")]
public sealed class Stage20GoldenTests(XrmdFixture fx)
{
    public static TheoryData<string> Names => [.. Stage20Scenarios.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Stage_20_checks_are_what_the_golden_file_says(string name)
    {
        var scenario = Stage20Scenarios.All.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var s20 = new Stage20Kit(kit);
        var call = await scenario.Arrange(s20);

        var jobsBefore = call.Stage40 ? await s20.AsyncOperationIdsAsync() : [];
        if (call.Stage40) await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");

        var file = new StringBuilder();
        file.Append("=== PipelineExecutor.ExecuteAsync as ").Append(call.CallerText).Append(" ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        var fault = await new GuardKit(kit).RunAsync(call.Request, call.Caller);
        file.Append("--- fault ---\n").Append(fault is null ? "none\n" : CoreDump.Response(null, fault));
        file.Append("--- request after ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        file.Append("--- rows ---\n");
        foreach (var (label, sql) in call.Rows) file.Append(await s20.RowsTextAsync(label, sql));
        if (call.Secrets is { } secrets)
        {
            file.Append("--- secrets ---\n");
            foreach (var (label, sql) in secrets) file.Append(await s20.RowsTextAsync(label, sql));
        }
        if (call.Stage40)
        {
            file.Append("--- stage 40 ---\n").Append(await s20.Stage40TextAsync(jobsBefore));
        }

        CoreDump.AssertGolden(name + ".txt", s20.Normalize(file.ToString()), Stage20Kit.Directory);
    }
}

/// <summary>One scenario: its arrangement, which returns the call to run.</summary>
internal sealed record Stage20Scenario(string Name, Func<Stage20Kit, Task<Stage20Call>> Arrange);

/// <summary>
/// The call of a scenario: the request, the caller (and how the golden names it), the queries of the rows of the table written
/// (<paramref name="Rows"/>: label and SQL, every row of the answer written), the queries of the record's secrets (<paramref name="Secrets"/>,
/// for a table with secrets) and whether the Target a registered PostOperation step and its asynchronous payload saw is written
/// (<paramref name="Stage40"/>).
/// </summary>
internal sealed record Stage20Call(
    OrganizationRequest Request, Caller Caller, string CallerText, IReadOnlyList<(string Label, string Sql)> Rows,
    IReadOnlyList<(string Label, string Sql)>? Secrets = null, bool Stage40 = false);

/// <summary>The scenarios of the stage-20 goldens (plan S.2e3c2b, Task 1; pre-flight ruling P1).</summary>
internal static class Stage20Scenarios
{
    private static readonly Guid Unknown = CoreScenarioKit.UnknownId;

    public static IReadOnlyList<Stage20Scenario> All { get; } =
    [
        // ---- custom API authoring ----
        new("CustomApiCreate", g => Task.FromResult(g.Admin(
            new CreateRequest { Target = new Entity(CustomApiTables.Api) { ["uniquename"] = "new_S20Ping", ["name"] = "new_S20Ping" } },
            Stage20Kit.ApiRows("new_S20Ping")))),
        new("CustomApiCreateWithRelatedRecord", g =>
        {
            var api = Stage20Kit.Api("new_S20Related");
            api.RelatedEntities[new Relationship(CustomApiTables.RequestParametersRelationship)] =
                new EntityCollection([new Entity(CustomApiTables.RequestParameter) { ["uniquename"] = "P", ["name"] = "P" }]);
            return Task.FromResult(g.Admin(new CreateRequest { Target = api }, Stage20Kit.ApiRows("new_S20Related")));
        }),
        new("CustomApiUpdateImmutableColumn", async g =>
        {
            var api = await g.CreateAsync(Stage20Kit.Api("new_S20Fixed"), "api");
            return g.Admin(new UpdateRequest { Target = new Entity(CustomApiTables.Api, api) { ["bindingtype"] = new OptionSetValue(1), ["displayname"] = "s20" } },
                Stage20Kit.ApiRows("new_S20Fixed"));
        }),
        new("RequestParameterCreate", async g =>
        {
            var api = await g.CreateAsync(Stage20Kit.Api("new_S20Params"), "api");
            return g.Admin(new CreateRequest
            {
                Target = new Entity(CustomApiTables.RequestParameter)
                {
                    ["uniquename"] = "Text", ["name"] = "Text", ["type"] = new OptionSetValue((int)CustomApiType.String),
                    ["customapiid"] = new EntityReference(CustomApiTables.Api, api),
                },
            }, [("request parameters of the API",
                $"SELECT uniquename, type, isoptional, customapiid FROM data.customapirequestparameter WHERE customapiid = '{api}' ORDER BY uniquename")]);
        }),
        new("CustomApiDeleteWithStep", async g =>
        {
            var api = await g.CreateAsync(Stage20Kit.Api("new_S20Stepped", stepType: 2), "api");
            var step = await PluginTestHelper.RegisterStepAsync(g.Kit.Admin, await g.PluginTypeAsync(), "new_S20Stepped", null, 40);
            g.Kit.Name(step, "step");
            return g.Admin(new DeleteRequest { Target = new EntityReference(CustomApiTables.Api, api) }, Stage20Kit.ApiRows("new_S20Stepped"));
        }),
        new("CustomApiDelete", async g =>
        {
            var api = await g.CreateAsync(Stage20Kit.Api("new_S20Gone"), "api");
            return g.Admin(new DeleteRequest { Target = new EntityReference(CustomApiTables.Api, api) }, Stage20Kit.ApiRows("new_S20Gone"));
        }),
        new("CustomApiCreateIntegerUniquename", g => Task.FromResult(g.Admin(
            new CreateRequest { Target = new Entity(CustomApiTables.Api) { ["uniquename"] = 42, ["name"] = "new_S20Int" } },
            [("custom APIs named 'new_S20Int'", "SELECT COUNT(*) AS apis FROM data.customapi WHERE name = 'new_S20Int'")]))),

        // ---- registration validation ----
        new("PluginAssemblyCreate", g => Task.FromResult(g.Admin(new CreateRequest
        {
            Target = new Entity("pluginassembly")
            {
                ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes), ["sourcetype"] = new OptionSetValue(0),
                ["isolationmode"] = new OptionSetValue(2),
            },
        }, [("plug-in assemblies", "SELECT name, version, culture, publickeytoken, sourcetype, isolationmode FROM data.pluginassembly ORDER BY name")]))),
        new("StepCreateInvalidStage", async g =>
        {
            var type = await g.PluginTypeAsync();
            return g.Admin(new CreateRequest
            {
                Target = new Entity("sdkmessageprocessingstep")
                {
                    ["name"] = "s20 step", ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create")),
                    ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId("Create", "account")),
                    ["plugintypeid"] = new EntityReference("plugintype", type), ["eventhandler"] = new EntityReference("plugintype", type),
                    ["stage"] = new OptionSetValue(30), ["mode"] = new OptionSetValue(0), ["rank"] = 1,
                },
            }, [("steps named 's20 step'", "SELECT COUNT(*) AS steps FROM data.sdkmessageprocessingstep WHERE name = 's20 step'")]);
        }),
        new("StepUpdateEventhandlerMissingEndpoint", async g =>
        {
            var step = await g.PluginStepAsync();
            return g.Admin(new UpdateRequest
            {
                Target = new Entity("sdkmessageprocessingstep", step)
                {
                    ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, Unknown), ["plugintypeid"] = null,
                },
            }, [Stage20Kit.StepRow(step)]);
        }),
        // As the System Administrator, the guard reads the missing lookup first (404); the internal caller skips the guard, so the
        // registration validator's own check is reached.
        new("StepUpdateEventhandlerMissingEndpointInternal", async g =>
        {
            var step = await g.PluginStepAsync();
            return new Stage20Call(new UpdateRequest
            {
                Target = new Entity("sdkmessageprocessingstep", step)
                {
                    ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, Unknown), ["plugintypeid"] = null,
                },
            }, Caller.Internal, "the internal caller (Caller.Internal)", [Stage20Kit.StepRow(step)]);
        }),
        new("AssociateStepEventhandler", async g =>
        {
            var step = await g.PluginStepAsync();
            var type = g.Kit.Last["type"];
            return g.Admin(new AssociateRequest
            {
                Target = new EntityReference("plugintype", type), Relationship = new Relationship("plugintype_sdkmessageprocessingstep"),
                RelatedEntities = [new EntityReference("sdkmessageprocessingstep", step)],
            }, [Stage20Kit.StepRow(step)]);
        }),
        new("PluginTypeUpdateStringIsWorkflowActivity", async g =>
        {
            var type = await g.PluginTypeAsync();
            return g.Admin(new UpdateRequest { Target = new Entity("plugintype", type) { ["isworkflowactivity"] = "true" } },
                [("the plug-in type", $"SELECT typename, isworkflowactivity FROM data.plugintype WHERE plugintypeid = '{type}'")]);
        }),

        // ---- process writes ----
        new("WorkflowCreateWithoutType", async g =>
        {
            await g.WorkflowsAsync();
            await g.RecordStage40Async(S.Table, "Create");
            var definition = Stage20Kit.Realtime("s20 untyped");
            definition.Attributes.Remove(S.TypeColumn);
            return g.Admin(new CreateRequest { Target = definition }, [Stage20Kit.WorkflowRows("s20 untyped")], stage40: true);
        }),
        new("WorkflowCreateType2", async g =>
        {
            await g.WorkflowsAsync();
            var definition = Stage20Kit.Realtime("s20 type 2");
            definition[S.TypeColumn] = new OptionSetValue(2);
            return g.Admin(new CreateRequest { Target = definition }, [Stage20Kit.WorkflowRows("s20 type 2")]);
        }),
        new("WorkflowCreateXamlRefused", async g =>
        {
            await g.WorkflowsAsync();
            var definition = Stage20Kit.Realtime("s20 malformed");
            definition.Attributes.Remove(S.TypeColumn);
            definition[S.Xaml] = "<Activity>\n  <Sequence>\n</Activity>";
            return g.Admin(new CreateRequest { Target = definition }, [Stage20Kit.WorkflowRows("s20 malformed")]);
        }),
        new("WorkflowUpdateActivated", async g =>
        {
            var id = await g.DefinitionAsync("s20 activated", activate: true);
            return g.Admin(new UpdateRequest { Target = new Entity(S.Table, id) { [S.Name] = "s20 renamed" } },
                [Stage20Kit.WorkflowRows("s20 activated"), Stage20Kit.WorkflowRows("s20 renamed")]);
        }),
        new("SetStateActivatesDefinition", async g =>
        {
            var id = await g.DefinitionAsync("s20 activate", activate: false);
            return g.Admin(new SetStateRequest
            {
                EntityMoniker = new EntityReference(S.Table, id), State = new OptionSetValue(S.ActivatedState), Status = new OptionSetValue(S.ActivatedStatus),
            }, [Stage20Kit.WorkflowRows("s20 activate")]);
        }),
        new("SetStateOfActivation", async g =>
        {
            var id = await g.DefinitionAsync("s20 activation", activate: true);
            var activation = await g.Kit.NameAsync("activation",
                $"SELECT workflowid FROM data.workflow WHERE parentworkflowid = '{id}' AND type = {(int)S.Type.Activation}");
            return g.Admin(new SetStateRequest
            {
                EntityMoniker = new EntityReference(S.Table, activation), State = new OptionSetValue(S.DraftState), Status = new OptionSetValue(S.DraftStatus),
            }, [Stage20Kit.WorkflowRows("s20 activation")]);
        }),
        new("ProcessTriggerCreate", async g =>
        {
            var id = await g.DefinitionAsync("s20 triggered", activate: false);
            return g.Admin(new CreateRequest
            {
                Target = new Entity(S.TriggerTable) { [S.Event] = "change", [S.ProcessId] = new EntityReference(S.Table, id) },
            }, [("process triggers of the definition", $"SELECT COUNT(*) AS triggers FROM data.processtrigger WHERE processid = '{id}'")]);
        }),

        // ---- service endpoints ----
        new("EndpointCreate", async g =>
        {
            await g.EndpointsAsync();
            return g.Admin(new CreateRequest { Target = Stage20Kit.Webhook("s20 hook", "s20-k3y") }, [Stage20Kit.EndpointRows],
                [Stage20Kit.EndpointSecrets]);
        }),
        new("EndpointCreateBlankAuthvalue", async g =>
        {
            await g.EndpointsAsync();
            return g.Admin(new CreateRequest { Target = Stage20Kit.Webhook("s20 blank", "") }, [Stage20Kit.EndpointRows], [Stage20Kit.EndpointSecrets]);
        }),
        new("EndpointCreateOptionSetUrl", async g =>
        {
            await g.EndpointsAsync();
            var hook = Stage20Kit.Webhook("s20 choice url", "s20-k3y");
            hook["url"] = new OptionSetValue(1);
            return g.Admin(new CreateRequest { Target = hook }, [Stage20Kit.EndpointRows], [Stage20Kit.EndpointSecrets]);
        }),
        new("EndpointUpdateAuthvalue", async g =>
        {
            await g.EndpointsAsync();
            var id = await g.CreateAsync(Stage20Kit.Webhook("s20 hook", "s20-f1rst"), "endpoint");
            return g.Admin(new UpdateRequest { Target = new Entity(ServiceEndpointSchema.Table, id) { [ServiceEndpointSchema.AuthValue] = "s20-s3cond" } },
                [Stage20Kit.EndpointRows], [Stage20Kit.EndpointSecrets]);
        }),
        new("EndpointDelete", async g =>
        {
            await g.EndpointsAsync();
            var id = await g.CreateAsync(Stage20Kit.Webhook("s20 hook", "s20-k3y"), "endpoint");
            return g.Admin(new DeleteRequest { Target = new EntityReference(ServiceEndpointSchema.Table, id) }, [Stage20Kit.EndpointRows],
                [Stage20Kit.EndpointSecrets]);
        }),

        // ---- mail secrets ----
        new("MailboxCreate", async g =>
        {
            var queue = await g.QueueAsync();
            await g.RecordStage40Async("mailbox", "Create");
            return g.Admin(new CreateRequest { Target = Stage20Kit.Mailbox("s20 mailbox", queue, "s20-mb0x") }, [Stage20Kit.MailboxRows],
                [Stage20Kit.MailboxSecrets], stage40: true);
        }),
        new("ProfileUpdateNonStringPassword", async g =>
        {
            var profile = await g.ProfileAsync();
            return g.Admin(new UpdateRequest
            {
                Target = new Entity("emailserverprofile", profile) { ["incomingpassword"] = "s20-1ncoming", ["outgoingpassword"] = 42 },
            }, [("the profile", $"SELECT name, isincomingpasswordset, isoutgoingpasswordset FROM data.emailserverprofile WHERE emailserverprofileid = '{profile}'")],
                [("meta.mail_secret of the profile", $"SELECT name, value FROM meta.mail_secret WHERE recordid = '{profile}' ORDER BY name")]);
        }),
        new("MailboxUpsertCreate", async g =>
        {
            var queue = await g.QueueAsync();
            await g.RecordStage40Async("mailbox", "Create", "Update");
            var id = Guid.NewGuid();
            g.Kit.Name(id, "mailbox");
            var target = Stage20Kit.Mailbox("s20 mailbox upserted", queue, "s20-ups3rt");
            target.Id = id;
            return g.Admin(new UpsertRequest { Target = target }, [Stage20Kit.MailboxRows], [Stage20Kit.MailboxSecrets], stage40: true);
        }),
        new("MailboxUpsertUpdate", async g =>
        {
            var queue = await g.QueueAsync();
            var id = await g.CreateAsync(Stage20Kit.Mailbox("s20 mailbox upserted", queue, "s20-f1rst"), "mailbox");
            await g.RecordStage40Async("mailbox", "Create", "Update");
            var target = Stage20Kit.Mailbox("s20 mailbox upserted", queue, "s20-upd4te");
            target.Id = id;
            return g.Admin(new UpsertRequest { Target = target }, [Stage20Kit.MailboxRows], [Stage20Kit.MailboxSecrets], stage40: true);
        }),
        new("MailboxCreateMultiple", async g =>
        {
            var queue = await g.QueueAsync();
            await g.RecordStage40Async("mailbox", "Create");
            return g.Admin(new CreateMultipleRequest
            {
                Targets = new EntityCollection([Stage20Kit.Mailbox("s20 mailbox 1", queue, "s20-mult1-one"), Stage20Kit.Mailbox("s20 mailbox 2", queue, "s20-mult1-two")])
                {
                    EntityName = "mailbox",
                },
            }, [Stage20Kit.MailboxRows], [Stage20Kit.MailboxSecrets], stage40: true);
        }),

        // ---- the owner of a real-time workflow's table (ruling P1) ----
        new("RealtimeOwnerUpdateString", async g =>
        {
            await g.RealtimeOnOrderAsync();
            var order = await g.CreateAsync(new Entity(TestEntities.Order) { ["xrmv_name"] = "s20 order" }, "order");
            return g.Admin(new UpdateRequest { Target = new Entity(TestEntities.Order, order) { ["ownerid"] = "s20 owner" } },
                [("the order", $"SELECT xrmv_name, ownerid FROM data.xrmv_order WHERE xrmv_orderid = '{order}'")]);
        }),
        new("RealtimeOwnerCreateString", async g =>
        {
            await g.RealtimeOnOrderAsync();
            return g.Admin(new CreateRequest { Target = new Entity(TestEntities.Order) { ["xrmv_name"] = "s20 order created", ["ownerid"] = "s20 owner" } },
                [("orders named 's20 order created'", "SELECT COUNT(*) AS orders FROM data.xrmv_order WHERE xrmv_name = 's20 order created'")]);
        }),
        // Ruling P1: as the System Administrator the guard casts ownerid first; the internal caller skips the guard, so the owner read
        // of the real-time workflow match (RecordOwnerAsync) is reached.
        new("RealtimeOwnerCreateStringInternal", async g =>
        {
            await g.RealtimeOnOrderAsync();
            return new Stage20Call(
                new CreateRequest { Target = new Entity(TestEntities.Order) { ["xrmv_name"] = "s20 order created", ["ownerid"] = "s20 owner" } },
                Caller.Internal, "the internal caller (Caller.Internal)",
                [("orders named 's20 order created'", "SELECT COUNT(*) AS orders FROM data.xrmv_order WHERE xrmv_name = 's20 order created'")]);
        }),
    ];
}

/// <summary>The arrangements, the row and secret writers and the stage-40 recorder of the stage-20 goldens.</summary>
internal sealed class Stage20Kit(CoreScenarioKit kit)
{
    public const string Directory = "stage20-golden";
    private const string OnCreateXaml = "OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66";

    public CoreScenarioKit Kit { get; } = kit;

    /// <summary>
    /// The kit's normalisation with fixed dates kept; the base64 content of the test plug-in assembly and the quoted XAML of the parity
    /// export's OnCreate are written as placeholders (they are fixtures, not what a check changes).
    /// </summary>
    public string Normalize(string text) =>
        GuardKit.Normalize(Kit, text
            .Replace(Convert.ToBase64String(PluginTestHelper.TestPluginBytes), "{content of Xrmd.TestPlugins.dll}", StringComparison.Ordinal)
            .Replace(CoreDump.Quote(WorkflowTestHelper.Real(OnCreateXaml)), "{xaml of the parity export's OnCreate}", StringComparison.Ordinal));

    /// <summary>A call as the System Administrator (the kit's caller).</summary>
    public Stage20Call Admin(OrganizationRequest request, IReadOnlyList<(string, string)> rows, IReadOnlyList<(string, string)>? secrets = null,
        bool stage40 = false) => new(request, Kit.Caller, "the System Administrator", rows, secrets, stage40);

    /// <summary>Creates <paramref name="entity"/> as the administrator; its id is named <c>{a:label}</c> and kept in <see cref="CoreScenarioKit.Last"/>.</summary>
    public async Task<Guid> CreateAsync(Entity entity, string label)
    {
        var id = await Kit.Admin.CreateAsync(entity);
        Kit.Name(id, label);
        Kit.Last[label] = id;
        return id;
    }

    // ---- rows ----

    /// <summary>
    /// The rows <paramref name="sql"/> reads, one line each as <c>{label}: column=value, ...</c> (strings quoted, Booleans as
    /// <c>true</c>/<c>false</c>), or <c>{label}: none</c>.
    /// </summary>
    public async Task<string> RowsTextAsync(string label, string sql)
    {
        var lines = new StringBuilder();
        await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, sql))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync())
            {
                lines.Append(label).Append(": ");
                for (var i = 0; i < rd.FieldCount; i++)
                {
                    if (i > 0) lines.Append(", ");
                    lines.Append(rd.GetName(i)).Append('=').Append(Value(rd.IsDBNull(i) ? null : rd.GetValue(i)));
                }
                lines.Append('\n');
            }
        return lines.Length == 0 ? label + ": none\n" : lines.ToString();
    }

    private static string Value(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        Guid g => g.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // ---- stage 40 ----

    /// <summary>
    /// Registers the <c>ContextRecorderPlugin</c> on PostOperation of each of <paramref name="messages"/> on <paramref name="table"/>,
    /// synchronous (rank 1) and asynchronous (rank 2), recording into <c>pb_pipelog</c>.
    /// </summary>
    public async Task RecordStage40Async(string table, params string[] messages)
    {
        await Kit.EnsurePipeAsync();
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Kit.Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Kit.Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        foreach (var message in messages)
        {
            await PluginTestHelper.RegisterStepAsync(Kit.Admin, type, message, table, 40, mode: 0, config: TestEntities.PipeLog);
            await PluginTestHelper.RegisterStepAsync(Kit.Admin, type, message, table, 40, mode: 1, rank: 2, config: TestEntities.PipeLog);
        }
    }

    public async Task<HashSet<Guid>> AsyncOperationIdsAsync()
    {
        var ids = new HashSet<Guid>();
        await using var conn = await TestProvider.OpenAsync(Kit.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT asyncoperationid FROM data.asyncoperation");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// What the recorder saw: per synchronous step (<c>Message|Stage|Mode|Depth</c>) the Target's column names, and those of the parent
    /// context's Target; per asynchronous job created by the call, the column names of the Target of each level of its payload. The jobs
    /// are deleted afterwards.
    /// </summary>
    public async Task<string> Stage40TextAsync(HashSet<Guid> jobsBefore)
    {
        var steps = new List<string>();
        var jobs = new List<(Guid Id, string Text)>();
        await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
        {
            await using (var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}"))
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync())
                {
                    var data = rd.GetString(1).Split('\n');
                    string Line(string key) => data.Single(l => l.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];
                    var parentKeys = string.Join(",", Line("parenttarget").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0]));
                    steps.Add($"step {rd.GetString(0)}: target={Line("target")}; targets={Line("targets")}; parent={Line("parent")}; parenttarget={parentKeys}\n");
                }
            await using (var cmd = TestSql.Command(conn, "SELECT asyncoperationid, data FROM data.asyncoperation"))
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync())
                {
                    var id = rd.GetGuid(0);
                    if (jobsBefore.Contains(id)) continue;
                    var text = new StringBuilder("job:");
                    for (var level = rd.IsDBNull(1) ? null : AsyncContextSerializer.Deserialize(rd.GetString(1)); level is not null;
                         level = level.ParentContext as RemoteExecutionContext)
                    {
                        text.Append(' ').Append(level.MessageName).Append('|').Append(level.Stage.ToString(CultureInfo.InvariantCulture)).Append(" target=");
                        if (level.InputParameters.TryGetValue("Target", out var t) && t is Entity e)
                            text.Append(string.Join(",", e.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal)));
                        if (level.InputParameters.TryGetValue("Targets", out var ts) && ts is EntityCollection c)
                            text.Append(" targets=").Append(string.Join(";", c.Entities.Select(r => string.Join(",", r.Attributes.Keys.OrderBy(k => k, StringComparer.Ordinal)))));
                        text.Append(';');
                    }
                    jobs.Add((id, text.Append('\n').ToString()));
                }
        }
        foreach (var (id, _) in jobs) await Kit.ExecAsync($"DELETE FROM data.asyncoperation WHERE asyncoperationid = '{id}'");
        steps.Sort(StringComparer.Ordinal);
        var lines = steps.Concat(jobs.Select(j => j.Text).Order(StringComparer.Ordinal)).ToList();
        return lines.Count == 0 ? "none\n" : string.Concat(lines);
    }

    // ---- custom APIs and registrations ----

    public static Entity Api(string uniqueName, int stepType = 0) => new(CustomApiTables.Api)
    {
        ["uniquename"] = uniqueName, ["name"] = uniqueName, ["bindingtype"] = new OptionSetValue(0), ["isfunction"] = false,
        ["allowedcustomprocessingsteptype"] = new OptionSetValue(stepType),
    };

    /// <summary>The API named <paramref name="uniqueName"/>, its message and the filters of that message.</summary>
    public static (string, string)[] ApiRows(string uniqueName) =>
    [
        ("custom API", $"SELECT uniquename, name, isprivate, isfunction, bindingtype, allowedcustomprocessingsteptype, workflowsdkstepenabled, sdkmessageid FROM data.customapi WHERE uniquename = '{uniqueName}'"),
        ("message", $"SELECT name, categoryname, isprivate FROM data.sdkmessage WHERE name = '{uniqueName}'"),
        ("filters of the message", $"SELECT COUNT(*) AS filters FROM data.sdkmessagefilter WHERE sdkmessageid = '{SdkMessageCatalog.MessageId(uniqueName)}'"),
    ];

    /// <summary>A plug-in type <c>SetAttributePlugin</c> of the test assembly, named <c>{a:type}</c>.</summary>
    public async Task<Guid> PluginTypeAsync()
    {
        if (Kit.Last.TryGetValue("type", out var known)) return known;
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Kit.Admin, PluginTestHelper.TestPluginBytes);
        Kit.Name(assembly, "assembly");
        var type = await PluginTestHelper.RegisterTypeAsync(Kit.Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        Kit.Name(type, "type");
        Kit.Last["type"] = type;
        return type;
    }

    /// <summary>An asynchronous PostOperation step of <see cref="PluginTypeAsync"/> on Create of account, named <c>{a:step}</c>.</summary>
    public async Task<Guid> PluginStepAsync()
    {
        var step = await PluginTestHelper.RegisterStepAsync(Kit.Admin, await PluginTypeAsync(), "Create", "account", 40, mode: 1);
        Kit.Name(step, "step");
        return step;
    }

    public static (string, string) StepRow(Guid step) =>
        ("the step", $"SELECT name, stage, eventhandler, eventhandlertype, plugintypeid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = '{step}'");

    // ---- processes ----

    /// <summary><c>xrmv_order</c> exists and no definition named <c>s20 ...</c> is left (deactivated, then deleted, now and on dispose).</summary>
    public async Task WorkflowsAsync()
    {
        await TestEntities.EnsureOrderAsync(Kit.Store);
        await ClearDefinitionsAsync();
        Kit.OnCleanup(ClearDefinitionsAsync);
    }

    private async Task ClearDefinitionsAsync()
    {
        var activated = new List<Guid>();
        var definitions = new List<Guid>();
        await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, $"SELECT workflowid, statecode FROM data.workflow WHERE name LIKE 's20 %' AND type = {(int)S.Type.Definition}"))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync())
            {
                definitions.Add(rd.GetGuid(0));
                if (Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture) == S.ActivatedState) activated.Add(rd.GetGuid(0));
            }
        foreach (var id in activated) await WorkflowTestHelper.DeactivateAsync(Kit.Admin, id);
        foreach (var id in definitions) await Kit.Admin.DeleteAsync(S.Table, id);
        Kit.Services.GetRequiredService<WorkflowRegistry>().Invalidate();
    }

    /// <summary>The parity export's OnCreate: a real-time workflow on create of an order (post-operation), as WorkflowWriteTests writes it.</summary>
    public static Entity Realtime(string name) => new(S.Table)
    {
        [S.Name] = name, [S.TypeColumn] = new OptionSetValue(1), [S.CategoryColumn] = new OptionSetValue(0),
        [S.ModeColumn] = new OptionSetValue(1), [S.ScopeColumn] = new OptionSetValue(4), [S.PrimaryEntity] = TestEntities.Order,
        [S.TriggerOnCreate] = true, [S.CreateStage] = new OptionSetValue(40), [S.Xaml] = WorkflowTestHelper.Real(OnCreateXaml),
    };

    /// <summary>A definition <see cref="Realtime"/> named <paramref name="name"/>, named <c>{a:definition}</c>, activated when asked.</summary>
    public async Task<Guid> DefinitionAsync(string name, bool activate)
    {
        await WorkflowsAsync();
        var id = await CreateAsync(Realtime(name), "definition");
        if (activate) await WorkflowTestHelper.SetStateAsync(Kit.Admin, id, S.ActivatedState, S.ActivatedStatus);
        return id;
    }

    /// <summary>
    /// An activated real-time workflow on <c>xrmv_order</c> (named <c>s20 realtime</c>, named <c>{a:definition}</c>) while the kit lives,
    /// triggered by an Update of <c>xrmv_amount</c> only: the owner scenarios neither write that column nor create through it (the
    /// OnCreate XAML calls a child workflow that is not activated here), so the workflow is matched on, never run.
    /// </summary>
    public async Task RealtimeOnOrderAsync()
    {
        await WorkflowsAsync();
        var definition = Realtime("s20 realtime");
        definition[S.TriggerOnCreate] = false;
        definition[S.TriggerOnUpdateAttributeList] = "xrmv_amount";
        definition[S.UpdateStage] = new OptionSetValue(40);
        var id = await CreateAsync(definition, "definition");
        await WorkflowTestHelper.SetStateAsync(Kit.Admin, id, S.ActivatedState, S.ActivatedStatus);
    }

    /// <summary>The definitions and activations named <paramref name="name"/>.</summary>
    public static (string, string) WorkflowRows(string name) =>
        ($"processes named '{name}'", $"SELECT name, type, statecode, statuscode, category, parentworkflowid FROM data.workflow WHERE name = '{name}' ORDER BY type");

    // ---- service endpoints ----

    /// <summary>No service endpoint or endpoint secret is left, now and on dispose (as ServiceEndpointAuthoringTests leaves them).</summary>
    public async Task EndpointsAsync()
    {
        const string clear = "DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;";
        await Kit.ExecAsync(clear);
        Kit.OnCleanup(() => Kit.ExecAsync(clear));
    }

    public static Entity Webhook(string name, string authValue) => new(ServiceEndpointSchema.Table)
    {
        ["name"] = name, ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
        ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = "https://hooks.example.invalid/receive",
        [ServiceEndpointSchema.AuthValue] = authValue,
    };

    public static readonly (string, string) EndpointRows =
        ("service endpoints", "SELECT name, contract, authtype, url, isauthvalueset FROM data.serviceendpoint ORDER BY name");

    public static readonly (string, string) EndpointSecrets =
        ("meta.serviceendpoint_secret", "SELECT serviceendpointid, authvalue FROM meta.serviceendpoint_secret ORDER BY authvalue");

    // ---- mail ----

    /// <summary>A queue named <c>s20q queue</c>, named <c>{a:queue}</c>; the <c>s20 ...</c> mailboxes and the queue are deleted on dispose.</summary>
    public async Task<Guid> QueueAsync()
    {
        Kit.OnCleanup(async () =>
        {
            var mailboxes = new List<Guid>();
            await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
            await using (var cmd = TestSql.Command(conn, "SELECT mailboxid FROM data.mailbox WHERE name LIKE 's20 %'"))
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync()) mailboxes.Add(rd.GetGuid(0));
            foreach (var mailbox in mailboxes) await Kit.Admin.DeleteAsync("mailbox", mailbox);
        });
        var queue = await CreateAsync(new Entity("queue")
        {
            ["name"] = "s20q queue", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
        }, "queue");
        Kit.OnCleanup(() => Kit.Admin.DeleteAsync("queue", queue));
        return queue;
    }

    public static Entity Mailbox(string name, Guid queue, string password) => new("mailbox")
    {
        ["name"] = name, ["emailaddress"] = "s20@example.com", ["username"] = "s20@example.com", ["password"] = password,
        ["regardingobjectid"] = new EntityReference("queue", queue),
    };

    public static readonly (string, string) MailboxRows =
        ("mailboxes named 's20 ...'", "SELECT name, username, ispasswordset FROM data.mailbox WHERE name LIKE 's20 %' ORDER BY name");

    public static readonly (string, string) MailboxSecrets =
        ("meta.mail_secret of the mailboxes", """
            SELECT m.name AS mailbox, s.name, s.value FROM meta.mail_secret s JOIN data.mailbox m ON m.mailboxid = s.recordid
            WHERE m.name LIKE 's20 %' ORDER BY m.name, s.name
            """);

    /// <summary>An SMTP profile named <c>s20 smtp</c> with an outgoing password, named <c>{a:profile}</c>; deleted on dispose.</summary>
    public async Task<Guid> ProfileAsync()
    {
        var profile = await CreateAsync(new Entity("emailserverprofile")
        {
            ["name"] = "s20 smtp", ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
            ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(1),
            ["outgoingusername"] = "sender@example.com", ["outgoingpassword"] = "s20-0utgoing",
        }, "profile");
        Kit.OnCleanup(() => Kit.Admin.DeleteAsync("emailserverprofile", profile));
        return profile;
    }
}
