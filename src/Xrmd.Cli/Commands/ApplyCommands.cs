using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xrmd.Schema;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd apply &lt;schema.yaml&gt; [--diff]</c>: metadata as code. The file is validated locally with
/// <see cref="SchemaYaml.Read"/> (exit 2 with line and column, before any request), then sent to
/// <c>POST /admin/schema/apply</c>. The query option <c>dryRun</c> is always sent explicitly (the server's default is true).
/// </summary>
public static class ApplyCommands
{
    public static Command Build(CliContext ctx)
    {
        var file = new Argument<string>("schema.yaml") { Description = "The schema file (YAML)." };
        var diff = new Option<bool>("--diff") { Description = "Dry run: print what would change (+ change, = extra, ! rejection) and change nothing." };
        var command = new Command("apply", "Apply a schema YAML file (tables, columns, choices, relationships) to the organization.") { file, diff };
        command.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(file)!;
            var dryRun = parse.GetValue(diff);

            string yaml;
            try { yaml = await File.ReadAllTextAsync(path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException(ExitCodes.Usage, $"Cannot read '{path}': {ex.Message}");
            }
            try { SchemaYaml.Read(yaml); }
            catch (SchemaFormatException ex)
            {
                throw new CliException(ExitCodes.Usage, $"{path} {ex.Message}", ex);
            }

            var content = new StringContent(yaml, new UTF8Encoding(false), new MediaTypeHeaderValue("text/yaml"));
            JsonElement result;
            try
            {
                var response = await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"schema/apply?dryRun={(dryRun ? "true" : "false")}", content, ct);
                result = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the schema apply.");
            }
            catch (CliException ex) when (ex.HttpStatus == 400 && ParsePlan(ex.ResponseBody) is { } plan)
            {
                // Rejections: the server answers 400 with the plan. Print it, and never report success.
                Print(ctx, plan, dryRun);
                throw new CliException(ExitCodes.Rejected, $"The schema was rejected ({Count(plan, "rejections")} rejection(s)); nothing was applied.", ex);
            }

            Print(ctx, result, dryRun);
            if (Count(result, "rejections") > 0)
                throw new CliException(ExitCodes.Rejected, "The schema was rejected; nothing was applied.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static JsonElement? ParsePlan(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var root = JsonDocument.Parse(body).RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rejections", out _) ? root : null;
        }
        catch (JsonException) { return null; }
    }

    private static int Count(JsonElement plan, string name) =>
        plan.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;

    private static IEnumerable<string> Lines(JsonElement plan, string name) =>
        plan.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString() ?? "")
            : [];

    private static void Print(CliContext ctx, JsonElement plan, bool dryRun)
    {
        if (ctx.Format == Output.OutputFormat.Json)
        {
            ctx.Write(plan);
            return;
        }
        var lines = Lines(plan, "changes").Select(c => "+ " + c)
            .Concat(Lines(plan, "extras").Select(e => "= " + e))
            .Concat(Lines(plan, "rejections").Select(r => "! " + r)).ToList();
        foreach (var line in lines) ctx.Env.Out.WriteLine(line);
        if (Count(plan, "rejections") == 0)
            ctx.Env.Out.WriteLine(lines.Count == 0 || Count(plan, "changes") == 0
                ? "No changes."
                : dryRun ? $"{Count(plan, "changes")} change(s) would be applied." : $"Applied {Count(plan, "changes")} change(s).");
    }
}
