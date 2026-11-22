using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd publisher create|list</c>: the publishers of the organization (Web API <c>publishers</c>), whose customization
/// prefixes are the ones a custom table, column, relationship or choice name may start with.
/// </summary>
public static partial class PublisherCommands
{
    /// <summary>Exit code of a duplicate prefix or unique name (the plan's ruling C7: exit 1).</summary>
    private const int Duplicate = ExitCodes.Unexpected;

    private const int MinOptionValuePrefix = 10000;
    private const int MaxOptionValuePrefix = 99999;
    private const string Select = "publisherid,uniquename,friendlyname,customizationprefix,customizationoptionvalueprefix";

    public static Command Build(CliContext ctx) => new("publisher", "Create and list publishers (Web API publishers).")
    {
        Create(ctx),
        List(ctx),
    };

    private static Command Create(CliContext ctx)
    {
        var uniqueName = new Option<string>("--unique-name") { Description = "Unique name of the publisher (letters, digits and '_', starting with a letter).", Required = true };
        var friendlyName = new Option<string?>("--friendly-name") { Description = "Display name; default: the unique name." };
        var prefix = new Option<string>("--prefix")
        {
            Description = "Customization prefix: 2 to 8 lowercase letters or digits, starting with a letter, not starting with 'mscrm'.",
            Required = true,
        };
        var optionValuePrefix = new Option<int>("--option-value-prefix")
        {
            Description = $"Option value prefix, from {MinOptionValuePrefix} to {MaxOptionValuePrefix}.",
            Required = true,
        };
        var command = new Command("create", "Create a publisher: the prefix its custom names start with.")
        {
            uniqueName, friendlyName, prefix, optionValuePrefix,
        };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(uniqueName)!;
            var customizationPrefix = parse.GetValue(prefix)!;
            var valuePrefix = parse.GetValue(optionValuePrefix);
            if (!UniqueNamePattern().IsMatch(name))
                throw new CliException(ExitCodes.Usage,
                    $"--unique-name '{name}' is not valid: use letters, digits and '_', starting with a letter (at most 256 characters).");
            if (!PrefixPattern().IsMatch(customizationPrefix))
                throw new CliException(ExitCodes.Usage,
                    $"--prefix '{customizationPrefix}' is not valid: use 2 to 8 lowercase letters or digits, starting with a letter.");
            if (customizationPrefix.StartsWith("mscrm", StringComparison.Ordinal))
                throw new CliException(ExitCodes.Usage, $"--prefix '{customizationPrefix}' is not valid: it cannot start with 'mscrm'.");
            if (valuePrefix is < MinOptionValuePrefix or > MaxOptionValuePrefix)
                throw new CliException(ExitCodes.Usage,
                    $"--option-value-prefix {valuePrefix} is not valid: use a value from {MinOptionValuePrefix} to {MaxOptionValuePrefix}.");

            var client = ctx.CreateClient();
            foreach (var existing in await client.GetAllPagesAsync($"publishers?$select={Select}", ct))
            {
                if (string.Equals(UserRoleLookup.Str(existing, "uniquename"), name, StringComparison.OrdinalIgnoreCase))
                    throw new CliException(Duplicate, $"A publisher with the unique name '{name}' already exists.");
                if (string.Equals(UserRoleLookup.Str(existing, "customizationprefix"), customizationPrefix, StringComparison.Ordinal))
                    throw new CliException(Duplicate,
                        $"The prefix '{customizationPrefix}' already belongs to the publisher '{UserRoleLookup.Str(existing, "uniquename")}'.");
            }

            var body = new Dictionary<string, object?>
            {
                ["uniquename"] = name,
                ["friendlyname"] = parse.GetValue(friendlyName) ?? name,
                ["customizationprefix"] = customizationPrefix,
                ["customizationoptionvalueprefix"] = valuePrefix,
            };
            var response = await client.SendJsonAsync(HttpMethod.Post, "publishers", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no OData-EntityId for the new publisher.");
            ctx.WriteResult(
                new { publisherid = id, uniquename = name, customizationprefix = customizationPrefix, customizationoptionvalueprefix = valuePrefix },
                $"Created publisher {name} (prefix {customizationPrefix}, option value prefix {valuePrefix}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the publishers.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync($"publishers?$select={Select}&$orderby=uniquename", ct);
            ctx.Write(rows.Select(r => new
            {
                publisherid = UserRoleLookup.Str(r, "publisherid"),
                uniquename = UserRoleLookup.Str(r, "uniquename"),
                friendlyname = UserRoleLookup.Str(r, "friendlyname"),
                customizationprefix = UserRoleLookup.Str(r, "customizationprefix"),
                customizationoptionvalueprefix = r.TryGetProperty("customizationoptionvalueprefix", out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : (int?)null,
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]{1,7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UniqueNamePattern();
}
