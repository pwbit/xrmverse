using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Xrmd.Cli.Http;
using Xrmd.Cli.Output;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd currency list|create|rate|activate|deactivate|pricing-precision</c> over the Web API as the signed-in user (plan 2.10):
/// the <c>transactioncurrencies</c> entity set and <c>organizations.pricingdecimalprecision</c>. Every rule is the server's (base
/// currency immutable, unique ISO code, rate range, precision 0-4): a refusal is exit 5, a missing privilege exit 3, a currency
/// code that does not exist exit 4, a usage error exit 2.
/// </summary>
public static class CurrencyCommands
{
    private const string Select = "transactioncurrencyid,isocurrencycode,currencyname,currencysymbol,currencyprecision,exchangerate,currencytype,statecode";

    public static Command Build(CliContext ctx) => new("currency", "List and manage currencies, exchange rates and the pricing precision (Web API).")
    {
        List(ctx), Create(ctx), Rate(ctx), State(ctx, activate: true), State(ctx, activate: false), PricingPrecision(ctx),
    };

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static Argument<string> CodeArgument() => new("code") { Description = "ISO currency code, e.g. USD." };

    private static decimal ParseRate(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            throw new CliException(ExitCodes.Usage, $"'{text}' is not a number.");
        return rate;
    }

    private static async Task<Guid?> BaseIdAsync(XrmdClient client, CancellationToken ct)
    {
        var org = (await client.GetAllPagesAsync("organizations?$select=_basecurrencyid_value", ct)).FirstOrDefault();
        return org.ValueKind == JsonValueKind.Object && Str(org, "_basecurrencyid_value") is { } id ? Guid.Parse(id) : null;
    }

    private static async Task<JsonElement> FindAsync(XrmdClient client, string code, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync($"transactioncurrencies?$select={Select}&$filter=isocurrencycode eq {Literal(code.Trim().ToUpperInvariant())}", ct);
        return rows.Count == 0 ? throw new CliException(ExitCodes.NotFound, $"Currency '{code}' not found.") : rows[0];
    }

    // The Web API leaves a null column out of a $select response, so every read tolerates a missing property.
    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static decimal? Dec(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static bool IsBase(JsonElement row, Guid? baseId) =>
        Str(row, "transactioncurrencyid") is { } id && baseId is { } b && Guid.Parse(id) == b;

    private static object Shape(JsonElement row, Guid? baseId) => new
    {
        code = Str(row, "isocurrencycode"),
        name = Str(row, "currencyname"),
        symbol = Str(row, "currencysymbol"),
        precision = Int(row, "currencyprecision"),
        rate = Dec(row, "exchangerate"),
        state = Int(row, "statecode") == 1 ? "Inactive" : "Active",
        type = Int(row, "currencytype") == 1 ? "Custom" : "System",
        @base = IsBase(row, baseId),
        transactioncurrencyid = Str(row, "transactioncurrencyid"),
    };

    private static void WriteCurrency(CliContext ctx, JsonElement row, Guid? baseId, string message)
    {
        var shaped = Shape(row, baseId);
        ctx.WriteResult(shaped, message);
    }

    private static Command List(CliContext ctx)
    {
        var list = new Command("list", "List the currencies: code, name, symbol, precision, rate, state and the base currency.");
        list.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var baseId = await BaseIdAsync(client, ct);
            var rows = await client.GetAllPagesAsync($"transactioncurrencies?$select={Select}&$orderby=isocurrencycode", ct);
            if (ctx.Format == OutputFormat.Json)
                ctx.Write(rows.Select(r => Shape(r, baseId)).ToList());
            else
                ctx.Write(rows.Select(r => new
                {
                    code = Str(r, "isocurrencycode"),
                    name = Str(r, "currencyname"),
                    symbol = Str(r, "currencysymbol"),
                    precision = Int(r, "currencyprecision"),
                    rate = Dec(r, "exchangerate"),
                    state = Int(r, "statecode") == 1 ? "Inactive" : "Active",
                    @base = IsBase(r, baseId) ? "Base" : "",
                }).ToList());
            return ExitCodes.Ok;
        });
        return list;
    }

    private static Command Create(CliContext ctx)
    {
        var code = CodeArgument();
        var name = new Option<string>("--name") { Description = "Currency name.", Required = true };
        var symbol = new Option<string>("--symbol") { Description = "Currency symbol.", Required = true };
        var rate = new Option<string>("--rate") { Description = "Exchange rate to the base currency (1 base = rate of this currency).", Required = true };
        var precision = new Option<int>("--precision") { Description = "Decimal places, 0 to 4.", DefaultValueFactory = _ => 2 };
        var custom = new Option<bool>("--custom") { Description = "A custom currency: any three capital letters (default: an ISO 4217 code)." };
        var create = new Command("create", "Create a currency.") { code, name, symbol, rate, precision, custom };
        create.SetAction(async (parse, ct) =>
        {
            var isoCode = parse.GetValue(code)!.Trim().ToUpperInvariant();
            var body = new Dictionary<string, object?>
            {
                ["isocurrencycode"] = isoCode,
                ["currencyname"] = parse.GetValue(name),
                ["currencysymbol"] = parse.GetValue(symbol),
                ["currencyprecision"] = parse.GetValue(precision),
                ["exchangerate"] = ParseRate(parse.GetValue(rate)!),
                ["currencytype"] = parse.GetValue(custom) ? 1 : 0,
            };
            var client = ctx.CreateClient();
            await client.SendJsonAsync(HttpMethod.Post, "transactioncurrencies", body, cancellationToken: ct);
            var row = await FindAsync(client, isoCode, ct);
            WriteCurrency(ctx, row, await BaseIdAsync(client, ct), $"Created currency {isoCode}.");
            return ExitCodes.Ok;
        });
        return create;
    }

    private static Command Rate(CliContext ctx)
    {
        var code = CodeArgument();
        var rate = new Argument<string>("rate") { Description = "Exchange rate to the base currency." };
        var command = new Command("rate", "Set the exchange rate of a currency (records keep their rate until a save changes their money or currency).") { code, rate };
        command.SetAction(async (parse, ct) =>
        {
            var value = ParseRate(parse.GetValue(rate)!);
            var client = ctx.CreateClient();
            var row = await FindAsync(client, parse.GetValue(code)!, ct);
            await client.SendJsonAsync(HttpMethod.Patch, $"transactioncurrencies({Str(row, "transactioncurrencyid")})",
                new Dictionary<string, object?> { ["exchangerate"] = value }, cancellationToken: ct);
            var updated = await FindAsync(client, parse.GetValue(code)!, ct);
            WriteCurrency(ctx, updated, await BaseIdAsync(client, ct), $"Rate of {Str(updated, "isocurrencycode")} is now {value.ToString(CultureInfo.InvariantCulture)}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command State(CliContext ctx, bool activate)
    {
        var code = CodeArgument();
        var command = activate
            ? new Command("activate", "Activate a currency.") { code }
            : new Command("deactivate", "Deactivate a currency (the base currency cannot be deactivated).") { code, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var row = await FindAsync(client, parse.GetValue(code)!, ct);
            var isoCode = Str(row, "isocurrencycode");
            if (!activate)
                await ctx.ConfirmDestructiveAsync($"This deactivates currency {isoCode}: records can no longer be moved to it. Continue?");
            await client.SendJsonAsync(HttpMethod.Patch, $"transactioncurrencies({Str(row, "transactioncurrencyid")})",
                new Dictionary<string, object?> { ["statecode"] = activate ? 0 : 1, ["statuscode"] = activate ? 1 : 2 }, cancellationToken: ct);
            var updated = await FindAsync(client, isoCode!, ct);
            WriteCurrency(ctx, updated, await BaseIdAsync(client, ct), $"Currency {isoCode} is {(activate ? "active" : "inactive")}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command PricingPrecision(CliContext ctx)
    {
        var value = new Argument<string>("precision") { Description = "Decimal places of money columns that use the pricing precision, 0 to 4." };
        var command = new Command("pricing-precision", "Set the organization's pricing decimal precision.") { value };
        command.SetAction(async (parse, ct) =>
        {
            var text = parse.GetValue(value)!;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var precision))
                throw new CliException(ExitCodes.Usage, $"'{text}' is not a whole number.");
            var client = ctx.CreateClient();
            var org = (await client.GetAllPagesAsync("organizations?$select=organizationid", ct)).FirstOrDefault();
            var id = org.ValueKind == JsonValueKind.Object ? Str(org, "organizationid") : null;
            if (id is null) throw new CliException(ExitCodes.Unexpected, "The server returned no organization.");
            await client.SendJsonAsync(HttpMethod.Patch, $"organizations({id})",
                new Dictionary<string, object?> { ["pricingdecimalprecision"] = precision }, cancellationToken: ct);
            ctx.WriteResult(new { pricingPrecision = precision }, $"Pricing precision is now {precision}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}
