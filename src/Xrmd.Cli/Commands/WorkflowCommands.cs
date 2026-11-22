using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.CommandLine;
using Xrmd.Cli.Http;
using Xrmd.Cli.Output;
using Xrmd.Schema;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd workflow apply|export|list|activate|deactivate</c>: workflow authoring as code over the authoring API
/// (<c>/authoring/workflows</c>, plan 2.21, A18) as the signed-in user, whose Dataverse privileges decide. <c>apply</c> reads the YAML and
/// checks it locally first with <see cref="WorkflowYaml.Read"/> (exit 2 with <c>path:line:column</c>, nothing sent), then posts it with
/// <c>dryRun</c> always explicit; a rejected document (400 with the result body) prints every error and exits 5. <c>export</c> writes the
/// YAML (or the XAML) to stdout or a file. A name that contains <c>/</c> cannot be addressed by name: give its id.
/// </summary>
public static class WorkflowCommands
{
    private const string Root = "workflows";

    public static Command Build(CliContext ctx) => new("workflow", "Apply, export, list, activate and deactivate workflows, business rules and actions as YAML.")
    {
        Apply(ctx),
        Export(ctx),
        List(ctx),
        State(ctx, "activate", "Activate a workflow, business rule or action by name or id.", activate: true),
        State(ctx, "deactivate", "Deactivate (draft) a workflow, business rule or action by name or id.", activate: false),
    };

    private static string Segment(string nameOrId) => Uri.EscapeDataString(nameOrId);

    /// <summary>A name with a slash cannot be addressed in the route: refused before any request (exit 2).</summary>
    private static void RequireRoutable(string nameOrId)
    {
        if (nameOrId.Contains('/'))
            throw new CliException(ExitCodes.Usage, $"'{nameOrId}' contains '/': give the process by its id instead (xrmd workflow list shows the ids).");
    }

    private static Task<XrmdResponse> SendAsync(XrmdClient client, string nameOrId, HttpMethod method, string path, CancellationToken ct)
    {
        RequireRoutable(nameOrId);
        return client.AuthoringAsync(method, path, null, ct);
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static Command Apply(CliContext ctx)
    {
        var file = new Argument<string>("file.yaml") { Description = "The workflow file (YAML)." };
        var solution = new Option<string?>("--solution") { Description = "Unique name of the unmanaged solution that receives the process." };
        var activate = new Option<bool>("--activate") { Description = "Activate the process after writing it." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Compile and compare only: print what would change and write nothing." };
        var command = new Command("apply", "Apply a workflow, business rule or action YAML file: create it, or update it by its identity.") { file, solution, activate, dryRun };
        command.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(file)!;
            var dry = parse.GetValue(dryRun);
            string yaml;
            try { yaml = await File.ReadAllTextAsync(path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException(ExitCodes.Usage, $"Cannot read '{path}': {ex.Message}");
            }
            try { WorkflowYaml.Read(yaml); }
            catch (SchemaFormatException ex)
            {
                var text = ex.Message;
                var cut = text.IndexOf("): ", StringComparison.Ordinal);
                throw new CliException(ExitCodes.Usage, $"{path}:{ex.Line}:{ex.Column}: {(cut >= 0 ? text[(cut + 3)..] : text)}", ex);
            }

            var query = $"dryRun={Flag(dry)}&activate={Flag(parse.GetValue(activate))}";
            if (parse.GetValue(solution) is { Length: > 0 } name) query += "&solution=" + Uri.EscapeDataString(name);
            var content = new StringContent(yaml, new UTF8Encoding(false), new MediaTypeHeaderValue("text/yaml"));
            JsonElement result;
            try
            {
                var response = await ctx.CreateClient().AuthoringAsync(HttpMethod.Post, $"{Root}/apply?{query}", content, ct);
                result = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the workflow apply.");
            }
            catch (CliException ex) when (ex.HttpStatus == 400 && ParseResult(ex.ResponseBody) is { } refused)
            {
                // The document was refused: the server answers 400 with the result. Print every error and never report success.
                if (ctx.Format == OutputFormat.Json) ctx.Write(refused);
                var errors = refused.GetProperty("errors").EnumerateArray().ToList();
                foreach (var error in errors) ctx.Env.Err.WriteLine(ErrorLine(path, error));
                throw new CliException(ExitCodes.Rejected,
                    $"The workflow was rejected ({errors.Count} error(s)); nothing was written.", ex);
            }

            foreach (var shape in Strings(result, "assumedShapes")) ctx.Env.Err.WriteLine($"warning: {shape}");
            var action = Str(result, "action");
            var state = Str(result, "state");
            ctx.WriteResult(result, dry
                ? $"Dry run: '{Str(result, "name")}' ({Str(result, "id")}) would be {action}; state {state}."
                : $"'{Str(result, "name")}' ({Str(result, "id")}) {action}; state {state}{(Bool(result, "stateChanged") ? " (changed)" : "")}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Export(CliContext ctx)
    {
        var name = new Argument<string>("nameOrId") { Description = "Name or id of the process (a name with '/' must be given by its id)." };
        var format = new Option<string>("--format") { Description = "yaml (default) or xaml.", DefaultValueFactory = _ => "yaml", HelpName = "yaml|xaml" };
        var file = new Option<string?>("--file") { Description = "Write to this file instead of stdout." };
        var command = new Command("export", "Export a process as YAML (default) or as its XAML, to stdout or a file.") { name, format, file };
        command.SetAction(async (parse, ct) =>
        {
            var nameOrId = parse.GetValue(name)!;
            RequireRoutable(nameOrId);
            var chosen = parse.GetValue(format)!.Trim().ToLowerInvariant();
            if (chosen is not ("yaml" or "xaml")) throw new CliException(ExitCodes.Usage, $"--format must be yaml or xaml; got '{chosen}'.");
            var response = await SendAsync(ctx.CreateClient(), nameOrId, HttpMethod.Get, $"{Root}/{Segment(nameOrId)}?format={chosen}", ct);
            var text = response.Text;
            if (parse.GetValue(file) is { Length: > 0 } target)
            {
                try { await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CliException(ExitCodes.Usage, $"Cannot write '{target}': {ex.Message}");
                }
                ctx.WriteResult(new { file = target, format = chosen, bytes = Encoding.UTF8.GetByteCount(text) }, $"Wrote {chosen} to '{target}'.");
            }
            else
            {
                ctx.Env.Out.Write(text);
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command List(CliContext ctx)
    {
        var kind = new Option<string?>("--kind") { Description = "workflow, businessRule or action.", HelpName = "kind" };
        var entity = new Option<string?>("--entity") { Description = "Logical name of the table.", HelpName = "table" };
        var command = new Command("list", "List the workflows, business rules and actions: id, name, kind, table, mode, state and unsupported steps.") { kind, entity };
        command.SetAction(async (parse, ct) =>
        {
            var parts = new List<string>();
            if (parse.GetValue(kind) is { Length: > 0 } k) parts.Add("kind=" + Uri.EscapeDataString(k));
            if (parse.GetValue(entity) is { Length: > 0 } e) parts.Add("entity=" + Uri.EscapeDataString(e));
            var response = await ctx.CreateClient().AuthoringAsync(HttpMethod.Get, parts.Count == 0 ? Root : $"{Root}?{string.Join('&', parts)}", null, ct);
            ctx.Write(response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the workflow list."));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command State(CliContext ctx, string verb, string description, bool activate)
    {
        var name = new Argument<string>("nameOrId") { Description = "Name or id of the process (a name with '/' must be given by its id)." };
        var command = new Command(verb, description) { name };
        command.SetAction(async (parse, ct) =>
        {
            var nameOrId = parse.GetValue(name)!;
            var response = await SendAsync(ctx.CreateClient(), nameOrId, HttpMethod.Post, $"{Root}/{Segment(nameOrId)}/{verb}", ct);
            var summary = response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for the workflow {verb}.");
            ctx.WriteResult(summary, $"'{Str(summary, "name")}' ({Str(summary, "id")}) is now {Str(summary, "state")}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The 400 body of a refused apply: the result with a non-empty <c>errors</c> array; null for an error envelope.</summary>
    private static JsonElement? ParseResult(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var root = JsonDocument.Parse(body).RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? root : null;
        }
        catch (JsonException) { return null; }
    }

    private static string ErrorLine(string path, JsonElement error)
    {
        var message = Str(error, "message");
        return error.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Object
               && at.TryGetProperty("line", out var line) && at.TryGetProperty("column", out var column)
            ? $"{path}:{line}:{column}: {message}"
            : message;
    }

    private static string Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> Strings(JsonElement row, string name) =>
        row.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => e.GetString() ?? "") : [];
}
