using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Xrmd.Cli.Http;
using Xrmd.Cli.Plugins;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd plugin</c>: registers plug-in assemblies, steps and images through the Web API (<c>pluginassemblies</c>, <c>plugintypes</c>,
/// <c>sdkmessageprocessingsteps</c>, <c>sdkmessageprocessingstepimages</c>) as the signed-in user, and reads <c>plugintracelogs</c>.
/// The assembly is read locally with <see cref="AssemblyInspector"/> (never loaded); the server validates the registration.
/// </summary>
public static class PluginCommands
{
    private const string SolutionHeader = "MSCRM.SolutionUniqueName";
    private const int SandboxIsolation = 2;
    private const int DatabaseSource = 0;

    public static Command Build(CliContext ctx) => new("plugin", "Register plug-in assemblies, steps and images; read plug-in traces.")
    {
        Register(ctx),
        List(ctx),
        Step(ctx),
        Image(ctx),
        Trace(ctx),
    };

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static string Bind(string set, Guid id) => $"/{set}({id})";

    private static IReadOnlyDictionary<string, string>? SolutionHeaders(string? solution) =>
        solution is null ? null : new Dictionary<string, string> { [SolutionHeader] = solution };

    private static string Eq(string attribute, string value) => $"{attribute} eq {UserRoleLookup.Literal(value)}";

    // ---- register -----------------------------------------------------------------------------------------------------

    private static Command Register(CliContext ctx)
    {
        var dll = new Argument<FileInfo>("dll") { Description = "The plug-in assembly (.dll)." };
        var solution = new Option<string?>("--solution") { Description = "Unique name of the solution that receives the assembly and its types (MSCRM.SolutionUniqueName header)." };
        var command = new Command("register", "Register a plug-in assembly (or update it: new content and version, new types only).") { dll, solution };
        command.SetAction(async (parse, ct) =>
        {
            var info = parse.GetValue(dll)!;
            if (!info.Exists) throw new CliException(ExitCodes.Usage, $"File '{info.FullName}' not found.");
            var inspected = AssemblyInspector.Inspect(info.FullName);
            if (inspected.PluginTypes.Count == 0)
                throw new CliException(ExitCodes.Usage, $"'{info.Name}' contains no public, non-abstract class that implements Microsoft.Xrm.Sdk.IPlugin.");

            var headers = SolutionHeaders(parse.GetValue(solution));
            var client = ctx.CreateClient();
            var content = Convert.ToBase64String(await File.ReadAllBytesAsync(info.FullName, ct));
            var version = inspected.Version.ToString();

            var existing = await client.GetAllPagesAsync($"pluginassemblies?$select=pluginassemblyid,culture,publickeytoken&$filter={Eq("name", inspected.Name)}", ct);
            if (existing.Count > 1)
                throw new CliException(ExitCodes.Rejected, $"{existing.Count} plug-in assemblies are named '{inspected.Name}'; remove the duplicates first.");
            if (existing.Count == 1)
            {
                var storedCulture = Str(existing[0], "culture");
                var storedToken = Str(existing[0], "publickeytoken");
                if (!string.Equals(storedCulture, inspected.Culture, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(storedToken ?? "", inspected.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
                    throw new CliException(ExitCodes.Rejected,
                        $"Assembly '{inspected.Name}' is already registered with culture '{storedCulture}' and public key token '{storedToken}', "
                        + $"but this file has culture '{inspected.Culture}' and public key token '{inspected.PublicKeyToken}': a different assembly cannot replace it.");
            }

            Guid assemblyId;
            var created = existing.Count == 0;
            if (created)
            {
                var response = await client.SendJsonAsync(HttpMethod.Post, "pluginassemblies", new Dictionary<string, object?>
                {
                    ["name"] = inspected.Name,
                    ["version"] = version,
                    ["culture"] = inspected.Culture,
                    ["publickeytoken"] = inspected.PublicKeyToken,
                    ["isolationmode"] = SandboxIsolation,
                    ["sourcetype"] = DatabaseSource,
                    ["content"] = content,
                }, headers, ct);
                assemblyId = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no id for the new plug-in assembly.");
            }
            else
            {
                assemblyId = Guid.Parse(Str(existing[0], "pluginassemblyid")!);
                await client.SendJsonAsync(new HttpMethod("PATCH"), $"pluginassemblies({assemblyId})",
                    new Dictionary<string, object?> { ["content"] = content, ["version"] = version }, headers, ct);
            }

            var known = (await client.GetAllPagesAsync($"plugintypes?$select=typename&$filter=_pluginassemblyid_value eq {assemblyId}", ct))
                .Select(t => Str(t, "typename")).ToHashSet(StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var typeName in inspected.PluginTypes.Where(t => !known.Contains(t)))
            {
                await client.SendJsonAsync(HttpMethod.Post, "plugintypes", new Dictionary<string, object?>
                {
                    ["typename"] = typeName,
                    ["name"] = typeName,
                    ["friendlyname"] = typeName,
                    ["pluginassemblyid@odata.bind"] = Bind("pluginassemblies", assemblyId),
                }, headers, ct);
                added.Add(typeName);
            }

            ctx.WriteResult(
                new { assembly = inspected.Name, assemblyId, version, created, typesAdded = added.Count, types = inspected.PluginTypes },
                $"{(created ? "Registered" : "Updated")} assembly '{inspected.Name}' {version} ({assemblyId}); {added.Count} new plug-in type(s), {inspected.PluginTypes.Count - added.Count} already registered.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- list ---------------------------------------------------------------------------------------------------------

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the registered assemblies, their plug-in types and steps.");
        command.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var assemblies = await client.GetAllPagesAsync("pluginassemblies?$select=pluginassemblyid,name,version", ct);
            var types = await client.GetAllPagesAsync("plugintypes?$select=plugintypeid,typename,_pluginassemblyid_value", ct);
            const string stepsPath =
                "sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,name,stage,mode,rank,filteringattributes,_plugintypeid_value,_sdkmessageid_value,_sdkmessagefilterid_value";
            IReadOnlyList<JsonElement> steps;
            try
            {
                steps = await client.GetAllPagesAsync(stepsPath + "&$expand=eventhandler_serviceendpoint($select=serviceendpointid,name)", ct);
            }
            catch (CliException ex) when (ex.HttpStatus == 400)
            {
                // A server without webhooks (before plan 3.2) has no eventhandler_serviceendpoint: list without the Webhooks section.
                steps = await client.GetAllPagesAsync(stepsPath, ct);
            }
            var messages = (await client.GetAllPagesAsync("sdkmessages?$select=sdkmessageid,name", ct))
                .ToDictionary(m => Str(m, "sdkmessageid")!, m => Str(m, "name"), StringComparer.OrdinalIgnoreCase);
            var filters = (await client.GetAllPagesAsync("sdkmessagefilters?$select=sdkmessagefilterid,primaryobjecttypecode", ct))
                .ToDictionary(f => Str(f, "sdkmessagefilterid")!, f => Str(f, "primaryobjecttypecode"), StringComparer.OrdinalIgnoreCase);

            var rows = new List<object>();
            foreach (var assembly in assemblies.OrderBy(a => Str(a, "name"), StringComparer.Ordinal))
            {
                var assemblyId = Str(assembly, "pluginassemblyid")!;
                foreach (var type in types.Where(t => string.Equals(Str(t, "_pluginassemblyid_value"), assemblyId, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(t => Str(t, "typename"), StringComparer.Ordinal))
                {
                    var typeId = Str(type, "plugintypeid")!;
                    var typeSteps = steps.Where(s => string.Equals(Str(s, "_plugintypeid_value"), typeId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (typeSteps.Count == 0)
                    {
                        rows.Add(new { assembly = Str(assembly, "name"), version = Str(assembly, "version"), type = Str(type, "typename"), stepId = (string?)null,
                            message = (string?)null, entity = (string?)null, stage = (int?)null, mode = (string?)null, rank = (int?)null });
                        continue;
                    }

                    foreach (var step in typeSteps)
                        rows.Add(new
                        {
                            assembly = Str(assembly, "name"),
                            version = Str(assembly, "version"),
                            type = Str(type, "typename"),
                            stepId = Str(step, "sdkmessageprocessingstepid"),
                            message = Str(step, "_sdkmessageid_value") is { } m && messages.TryGetValue(m, out var mn) ? mn : null,
                            entity = Str(step, "_sdkmessagefilterid_value") is { } f && filters.TryGetValue(f, out var fn) ? fn : null,
                            stage = (int?)Int(step, "stage"),
                            mode = Int(step, "mode") == 1 ? "async" : "sync",
                            rank = (int?)Int(step, "rank"),
                        });
                }
            }

            // Webhook steps have no plug-in type: they are listed apart, under their own heading in a table; in json they follow the plug-in rows.
            var webhooks = steps
                .Where(s => s.TryGetProperty("eventhandler_serviceendpoint", out var e) && e.ValueKind == JsonValueKind.Object)
                .OrderBy(s => Str(s.GetProperty("eventhandler_serviceendpoint"), "name"), StringComparer.Ordinal)
                .ThenBy(s => Str(s, "name"), StringComparer.Ordinal)
                .Select(step => (object)new
                {
                    webhook = Str(step.GetProperty("eventhandler_serviceendpoint"), "name"),
                    stepId = Str(step, "sdkmessageprocessingstepid"),
                    message = Str(step, "_sdkmessageid_value") is { } m && messages.TryGetValue(m, out var mn) ? mn : null,
                    entity = Str(step, "_sdkmessagefilterid_value") is { } f && filters.TryGetValue(f, out var fn) ? fn : null,
                    stage = (int?)Int(step, "stage"),
                    mode = Int(step, "mode") == 1 ? "async" : "sync",
                    rank = (int?)Int(step, "rank"),
                }).ToList();

            if (ctx.Format == Output.OutputFormat.Json)
            {
                ctx.Write(rows.Concat(webhooks).ToList());
                return ExitCodes.Ok;
            }

            ctx.Write(rows);
            if (webhooks.Count > 0)
            {
                ctx.Env.Out.WriteLine();
                ctx.Env.Out.WriteLine("Webhooks");
                ctx.Write(webhooks);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    // ---- step ---------------------------------------------------------------------------------------------------------

    private static Command Step(CliContext ctx) => new("step", "Add or remove a processing step.") { StepAdd(ctx), StepRemove(ctx) };

    private static Command StepAdd(CliContext ctx)
    {
        var type = new Option<string?>("--type") { Description = "Full name of the registered plug-in type (or --webhook)." };
        var webhook = new Option<string?>("--webhook") { Description = "Name of a webhook (xrmd webhook create) instead of --type." };
        var message = new Option<string>("--message") { Description = "SDK message: Create, Update, Delete, ...", Required = true };
        var entity = new Option<string?>("--entity") { Description = "Logical name of the table the step filters on." };
        var stage = new Option<int>("--stage") { Description = "Pipeline stage: 10 (pre-validation), 20 (pre-operation) or 40 (post-operation).", Required = true };
        stage.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is not (10 or 20 or 40)) r.AddError("--stage must be 10, 20 or 40.");
        });
        var mode = new Option<string>("--mode") { Description = "sync or async (async needs stage 40).", DefaultValueFactory = _ => "sync" };
        mode.AcceptOnlyFromAmong("sync", "async");
        var filtering = new Option<string?>("--filtering") { Description = "Comma-separated filtering attributes (Update only)." };
        var rank = new Option<int>("--rank") { Description = "Execution order within the stage.", DefaultValueFactory = _ => 1 };
        var name = new Option<string?>("--name") { Description = "Step name; default: '<type or webhook>: <message> of <entity>'." };
        var config = new Option<string?>("--config") { Description = "Unsecure configuration passed to the plug-in constructor." };
        var solution = new Option<string?>("--solution") { Description = "Unique name of the solution that receives the step (MSCRM.SolutionUniqueName header)." };
        var command = new Command("add", "Register a processing step for a plug-in type or a webhook.") { type, webhook, message, entity, stage, mode, filtering, rank, name, config, solution };
        command.SetAction(async (parse, ct) =>
        {
            var messageName = parse.GetValue(message)!;
            var entityName = parse.GetValue(entity);
            var hookName = parse.GetValue(webhook);
            var typeOption = parse.GetValue(type);
            if (typeOption is not null && hookName is not null) throw new CliException(ExitCodes.Usage, "Give either --type or --webhook, not both.");
            if (typeOption is null && hookName is null) throw new CliException(ExitCodes.Usage, "Give --type (a plug-in type) or --webhook (a webhook).");
            if (hookName is not null && parse.GetValue(config) is not null) throw new CliException(ExitCodes.Usage, "--config does not apply to a webhook step.");
            var typeName = typeOption ?? hookName!;
            var client = ctx.CreateClient();

            string handlerBinding, handlerProperty;
            if (hookName is not null)
            {
                var hook = await WebhookCommands.FindAsync(client, hookName, ct);
                handlerProperty = "eventhandler_serviceendpoint@odata.bind";
                handlerBinding = Bind("serviceendpoints", Guid.Parse(Str(hook, "serviceendpointid")!));
            }
            else
            {
                var types = await client.GetAllPagesAsync($"plugintypes?$select=plugintypeid&$filter={Eq("typename", typeName)}", ct);
                if (types.Count == 0) throw new CliException(ExitCodes.NotFound, $"Plug-in type '{typeName}' is not registered (run 'xrmd plugin register' first).");
                if (types.Count > 1) throw new CliException(ExitCodes.Rejected, $"{types.Count} registered plug-in types are named '{typeName}'.");
                handlerProperty = "plugintypeid@odata.bind";
                handlerBinding = Bind("plugintypes", Guid.Parse(Str(types[0], "plugintypeid")!));
            }

            var messages = await client.GetAllPagesAsync($"sdkmessages?$select=sdkmessageid&$filter={Eq("name", messageName)}", ct);
            if (messages.Count == 0) throw new CliException(ExitCodes.Rejected, $"Unknown SDK message '{messageName}'.");
            var messageId = Guid.Parse(Str(messages[0], "sdkmessageid")!);

            var body = new Dictionary<string, object?>
            {
                ["name"] = parse.GetValue(name) ?? $"{typeName}: {messageName}{(entityName is null ? "" : " of " + entityName)}",
                ["stage"] = parse.GetValue(stage),
                ["mode"] = parse.GetValue(mode) == "async" ? 1 : 0,
                ["rank"] = parse.GetValue(rank),
                ["sdkmessageid@odata.bind"] = Bind("sdkmessages", messageId),
                [handlerProperty] = handlerBinding,
            };
            if (entityName is not null)
            {
                var filters = await client.GetAllPagesAsync(
                    $"sdkmessagefilters?$select=sdkmessagefilterid&$filter=_sdkmessageid_value eq {messageId} and {Eq("primaryobjecttypecode", entityName)}", ct);
                if (filters.Count == 0)
                    throw new CliException(ExitCodes.Rejected, $"SDK message '{messageName}' is not available on table '{entityName}'.");
                body["sdkmessagefilterid@odata.bind"] = Bind("sdkmessagefilters", Guid.Parse(Str(filters[0], "sdkmessagefilterid")!));
            }

            if (parse.GetValue(filtering) is { } attributes) body["filteringattributes"] = attributes;
            if (parse.GetValue(config) is { } unsecure) body["configuration"] = unsecure;

            var response = await client.SendJsonAsync(HttpMethod.Post, "sdkmessageprocessingsteps", body, SolutionHeaders(parse.GetValue(solution)), ct);
            var stepId = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no id for the new step.");
            ctx.WriteResult(
                new { stepId, type = typeOption, webhook = hookName, message = messageName, entity = entityName, stage = parse.GetValue(stage), mode = parse.GetValue(mode), rank = parse.GetValue(rank) },
                $"Registered step {stepId}: {(hookName is null ? "" : "webhook ")}{typeName} on {messageName}{(entityName is null ? "" : " of " + entityName)}, stage {parse.GetValue(stage)}, {parse.GetValue(mode)}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command StepRemove(CliContext ctx)
    {
        var stepId = new Argument<Guid>("stepid") { Description = "Id of the step." };
        var command = new Command("remove", "Remove a processing step (and its images).") { stepId, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var id = parse.GetValue(stepId);
            await ctx.ConfirmDestructiveAsync($"This removes the step {id} and its images. Continue?");
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Delete, $"sdkmessageprocessingsteps({id})", cancellationToken: ct);
            ctx.WriteResult(new { removed = id }, $"Removed step {id}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- image --------------------------------------------------------------------------------------------------------

    /// <summary>The message property an image reads (Dataverse): Create exposes the new id as <c>Id</c>, SetState the <c>EntityMoniker</c>, the rest the <c>Target</c>.</summary>
    private static readonly Dictionary<string, string> MessageProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Create"] = "Id",
        ["Update"] = "Target",
        ["Delete"] = "Target",
        ["Assign"] = "Target",
        ["Merge"] = "Target",
        ["Route"] = "Target",
        ["SetState"] = "EntityMoniker",
        ["SetStateDynamicEntity"] = "EntityMoniker",
    };

    private static Command Image(CliContext ctx)
    {
        var step = new Option<Guid>("--step") { Description = "Id of the step.", Required = true };
        var type = new Option<string>("--type") { Description = "Image type: pre, post or both.", Required = true };
        type.AcceptOnlyFromAmong("pre", "post", "both");
        var alias = new Option<string>("--alias") { Description = "Entity alias the plug-in reads the image under.", Required = true };
        var attributes = new Option<string?>("--attributes") { Description = "Comma-separated attributes of the image; default: all." };
        var add = new Command("add", "Add a pre/post image to a step.") { step, type, alias, attributes };
        add.SetAction(async (parse, ct) =>
        {
            var stepId = parse.GetValue(step);
            var client = ctx.CreateClient();
            var stepRow = await client.GetJsonAsync($"sdkmessageprocessingsteps({stepId})?$select=_sdkmessageid_value", ct);
            var messageRow = Str(stepRow, "_sdkmessageid_value") is { } messageId
                ? await client.GetJsonAsync($"sdkmessages({messageId})?$select=name", ct)
                : (JsonElement?)null;
            var messageName = messageRow is { } row ? Str(row, "name") : null;
            var propertyName = messageName is not null && MessageProperties.TryGetValue(messageName, out var property)
                ? property
                : throw new CliException(ExitCodes.Usage,
                    $"Images are not supported for message '{messageName}': the image message property is known only for {string.Join(", ", MessageProperties.Keys)}.");
            var aliasName = parse.GetValue(alias)!;
            var imageType = parse.GetValue(type) switch { "pre" => 0, "post" => 1, _ => 2 };
            var body = new Dictionary<string, object?>
            {
                ["name"] = aliasName,
                ["entityalias"] = aliasName,
                ["imagetype"] = imageType,
                ["messagepropertyname"] = propertyName,
                ["sdkmessageprocessingstepid@odata.bind"] = Bind("sdkmessageprocessingsteps", stepId),
            };
            if (parse.GetValue(attributes) is { } list) body["attributes"] = list;
            var response = await client.SendJsonAsync(HttpMethod.Post, "sdkmessageprocessingstepimages", body, cancellationToken: ct);
            var imageId = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no id for the new image.");
            ctx.WriteResult(new { imageId, stepId, type = parse.GetValue(type), alias = aliasName },
                $"Added {parse.GetValue(type)} image '{aliasName}' ({imageId}) to step {stepId}.");
            return ExitCodes.Ok;
        });
        return new Command("image", "Add a pre/post image to a step.") { add };
    }

    // ---- trace --------------------------------------------------------------------------------------------------------

    private static Command Trace(CliContext ctx)
    {
        var type = new Option<string?>("--type") { Description = "Only the traces of this plug-in type (full name)." };
        var take = new Option<int>("--take") { Description = "How many traces, newest first.", DefaultValueFactory = _ => 50 };
        take.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 1 or > XrmdClient.MaxPageSize) r.AddError($"--take must be between 1 and {XrmdClient.MaxPageSize}.");
        });
        var command = new Command("trace", "Show the newest plug-in trace logs.") { type, take };
        command.SetAction(async (parse, ct) =>
        {
            var path = "plugintracelogs?$select=createdon,typename,messagename,primaryentity,depth,mode,messageblock,exceptiondetails"
                       + "&$orderby=createdon desc,plugintracelogid"
                       + $"&$top={parse.GetValue(take).ToString(CultureInfo.InvariantCulture)}";
            if (parse.GetValue(type) is { } typeName) path += "&$filter=" + Eq("typename", typeName);
            var rows = await ctx.CreateClient().GetAllPagesAsync(path, ct);
            ctx.Write(rows.Select(r => new
            {
                createdon = Str(r, "createdon"),
                typename = Str(r, "typename"),
                messagename = Str(r, "messagename"),
                primaryentity = Str(r, "primaryentity"),
                depth = Int(r, "depth"),
                mode = Int(r, "mode") == 1 ? "async" : "sync",
                messageblock = Str(r, "messageblock"),
                exceptiondetails = Str(r, "exceptiondetails"),
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }
}
