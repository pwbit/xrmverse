using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd mailbox list|show|approve|link|test</c> and <c>xrmd mailbox profile create|update|list</c> over the Web API as the signed-in user
/// (plan 4.1b, task 7): the <c>mailboxes</c> and <c>emailserverprofiles</c> entity sets. A mailbox is named by its id or its email
/// address. A profile's secret (its SMTP password or its Microsoft Entra client secret) is read from standard input with
/// <c>--secret-stdin</c>, or asked for on a terminal without echo: never a command-line argument, and never printed (the server does
/// not return it; the CLI shows whether one is set). Every refusal is the server's.
/// </summary>
public static class MailboxCommands
{
    private const string MailboxSelect =
        "mailboxid,name,emailaddress,_emailserverprofile_value,outgoingemaildeliverymethod,enabledforoutgoingemail,outgoingemailstatus," +
        "emailrouteraccessapproval,testmailboxaccesscompletedon,statecode";
    private const string ProfileSelect =
        "emailserverprofileid,name,servertype,outgoingserverlocation,outgoingportnumber,outgoingusessl,outgoingusername,outgoingcredentialretrieval," +
        "exchangeonlinetenantid,oauthclientid,isoutgoingpasswordset,isoauthclientsecretset,statecode";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static Command Build(CliContext ctx) => new("mailbox", "List, approve and test mailboxes, and manage email server profiles (Web API).")
    {
        List(ctx),
        Show(ctx),
        Approve(ctx),
        Link(ctx),
        Test(ctx),
        new Command("profile", "Create, update and list email server profiles (SMTP or Microsoft Graph).") { ProfileCreate(ctx), ProfileUpdate(ctx), ProfileList(ctx) },
    };

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Status(int? value) => value switch { 1 => "Success", 2 => "Failure", _ => "Not Run" };

    private static string Approval(int? value) => value switch { 1 => "Approved", 2 => "Pending Approval", 3 => "Rejected", _ => "Empty" };

    private static string Delivery(int? value) => value switch { 2 => "Server-Side Synchronization", 0 or null => "None", { } n => n.ToString(CultureInfo.InvariantCulture) };

    /// <summary>A mailbox by id or by email address; exit 4 when there is none, 5 when the address is not unique.</summary>
    private static async Task<JsonElement> MailboxAsync(XrmdClient client, string mailbox, CancellationToken ct)
    {
        var filter = Guid.TryParse(mailbox, out var id) ? $"mailboxid eq {id}" : $"emailaddress eq {UserRoleLookup.Literal(mailbox)}";
        var rows = await client.GetAllPagesAsync($"mailboxes?$select={MailboxSelect},lastsyncerror,username,ispasswordset&$filter={filter}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Mailbox '{mailbox}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} mailboxes have the address '{mailbox}'. Use the mailbox id.");
        return rows[0];
    }

    private static Guid IdOf(JsonElement mailbox) => Guid.Parse(Str(mailbox, "mailboxid")!);

    private static object Row(JsonElement m) => new
    {
        mailboxid = Str(m, "mailboxid"),
        name = Str(m, "name"),
        emailaddress = Str(m, "emailaddress"),
        approval = Approval(Int(m, "emailrouteraccessapproval")),
        outgoing = Delivery(Int(m, "outgoingemaildeliverymethod")),
        enabled = Bool(m, "enabledforoutgoingemail"),
        outgoingemailstatus = Status(Int(m, "outgoingemailstatus")),
        testmailboxaccesscompletedon = Str(m, "testmailboxaccesscompletedon"),
        state = Int(m, "statecode") is 1 ? "Inactive" : "Active",
    };

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the mailboxes: address, approval, outgoing delivery and the last outgoing test.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync($"mailboxes?$select={MailboxSelect}&$orderby=name", ct);
            ctx.Write(rows.Select(Row).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Show(CliContext ctx)
    {
        var mailbox = new Argument<string>("mailbox") { Description = "Id or email address of the mailbox." };
        var command = new Command("show", "Show a mailbox, its profile, its last error and whether its password is set (never the password).") { mailbox };
        command.SetAction(async (parse, ct) =>
        {
            var m = await MailboxAsync(ctx.CreateClient(), parse.GetValue(mailbox)!, ct);
            ctx.Write(new
            {
                mailboxid = Str(m, "mailboxid"),
                name = Str(m, "name"),
                emailaddress = Str(m, "emailaddress"),
                approval = Approval(Int(m, "emailrouteraccessapproval")),
                emailserverprofile = Str(m, "_emailserverprofile_value"),
                outgoing = Delivery(Int(m, "outgoingemaildeliverymethod")),
                enabled = Bool(m, "enabledforoutgoingemail"),
                outgoingemailstatus = Status(Int(m, "outgoingemailstatus")),
                testmailboxaccesscompletedon = Str(m, "testmailboxaccesscompletedon"),
                lastsyncerror = Str(m, "lastsyncerror"),
                username = Str(m, "username"),
                password = Bool(m, "ispasswordset") ? "set" : "not set",
                state = Int(m, "statecode") is 1 ? "Inactive" : "Active",
            });
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Approve(CliContext ctx)
    {
        var mailbox = new Argument<string>("mailbox") { Description = "Id or email address of the mailbox." };
        var command = new Command("approve",
            "Approve the email address of a mailbox (System Administrator): the approval is its user's or queue's emailrouteraccessapproval.") { mailbox };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = IdOf(await MailboxAsync(client, parse.GetValue(mailbox)!, ct));
            // The approval follows the owner (the mailbox's copy is the server's): the user or the queue whose default mailbox it is.
            string? set = null, key = null;
            foreach (var (entitySet, primaryKey) in new[] { ("systemusers", "systemuserid"), ("queues", "queueid") })
            {
                var owners = await client.GetAllPagesAsync($"{entitySet}?$select={primaryKey}&$filter=_defaultmailbox_value eq {id}", ct);
                if (owners.Count == 0) continue;
                (set, key) = (entitySet, Str(owners[0], primaryKey));
                break;
            }
            if (set is null || key is null) throw new CliException(ExitCodes.NotFound, $"No user or queue has the mailbox {id} as its mailbox.");
            await client.SendJsonAsync(new HttpMethod("PATCH"), $"{set}({key})", new Dictionary<string, object?> { ["emailrouteraccessapproval"] = 1 },
                cancellationToken: ct);
            ctx.WriteResult(new { mailboxid = id, approval = "Approved" }, $"Approved the email address of mailbox {id}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// <c>mailbox link &lt;mailbox&gt; --profile &lt;profile&gt;</c> (plan 4.1b, task 8): the mailbox sends through the profile. One PATCH
    /// sets <c>emailserverprofile</c>, <c>outgoingemaildeliverymethod</c> 2 (Server-Side Synchronization) and
    /// <c>enabledforoutgoingemail</c> true; the server's rules apply (the profile is a System Administrator's column).
    /// </summary>
    private static Command Link(CliContext ctx)
    {
        var mailbox = new Argument<string>("mailbox") { Description = "Id or email address of the mailbox." };
        var profile = new Option<string>("--profile") { Description = "Id or name of the email server profile.", Required = true };
        var command = new Command("link",
            "Link a mailbox to an email server profile (System Administrator): sets the profile, Server-Side Synchronization for outgoing email, " +
            "and enables outgoing email. Test the mailbox afterwards.") { mailbox, profile };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = IdOf(await MailboxAsync(client, parse.GetValue(mailbox)!, ct));
            var row = await ProfileAsync(client, parse.GetValue(profile)!, ct);
            var profileId = Str(row, "emailserverprofileid")!;
            await client.SendJsonAsync(new HttpMethod("PATCH"), $"mailboxes({id})", new Dictionary<string, object?>
            {
                ["emailserverprofile@odata.bind"] = $"/emailserverprofiles({profileId})",
                ["outgoingemaildeliverymethod"] = 2,
                ["enabledforoutgoingemail"] = true,
            }, cancellationToken: ct);
            ctx.WriteResult(new { mailboxid = id, emailserverprofileid = profileId, outgoing = Delivery(2), enabled = true },
                $"Mailbox {id} sends through profile '{Str(row, "name")}'. Run 'xrmd mailbox test {id} --wait' to check it.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Test(CliContext ctx)
    {
        var mailbox = new Argument<string>("mailbox") { Description = "Id or email address of the mailbox." };
        var wait = new Option<bool>("--wait") { Description = "Wait for the test and print its result (exit 5 when it failed)." };
        var timeout = new Option<int>("--timeout") { Description = "With --wait: minutes to wait (default 5).", DefaultValueFactory = _ => 5 };
        var command = new Command("test",
            "Test the outgoing configuration of a mailbox (its owner or a System Administrator): sets testemailconfigurationscheduled.") { mailbox, wait, timeout };
        command.SetAction(async (parse, ct) =>
        {
            var minutes = parse.GetValue(timeout);
            if (minutes <= 0) throw new CliException(ExitCodes.Usage, "--timeout must be 1 or more minutes.");
            var client = ctx.CreateClient();
            var name = parse.GetValue(mailbox)!;
            var id = Guid.TryParse(name, out var given) ? given : IdOf(await MailboxAsync(client, name, ct));
            await client.SendJsonAsync(new HttpMethod("PATCH"), $"mailboxes({id})",
                new Dictionary<string, object?> { ["testemailconfigurationscheduled"] = true }, cancellationToken: ct);
            if (!parse.GetValue(wait))
            {
                ctx.WriteResult(new { mailboxid = id, scheduled = true }, $"The test of mailbox {id} is scheduled.");
                return ExitCodes.Ok;
            }

            var deadline = ctx.Env.Clock.GetUtcNow() + TimeSpan.FromMinutes(minutes);
            while (true)
            {
                var m = await client.GetJsonAsync(
                    $"mailboxes({id})?$select=testemailconfigurationscheduled,outgoingemailstatus,testmailboxaccesscompletedon,lastsyncerror", ct);
                if (!Bool(m, "testemailconfigurationscheduled"))
                {
                    var status = Int(m, "outgoingemailstatus");
                    var result = new
                    {
                        mailboxid = id,
                        outgoingemailstatus = Status(status),
                        testmailboxaccesscompletedon = Str(m, "testmailboxaccesscompletedon"),
                        lastsyncerror = status == 2 ? Str(m, "lastsyncerror") : null,
                    };
                    ctx.WriteResult(result, status == 2
                        ? $"The test of mailbox {id} failed: {Str(m, "lastsyncerror")}"
                        : $"The test of mailbox {id}: {Status(status)}.");
                    return status == 2 ? ExitCodes.Rejected : ExitCodes.Ok;
                }
                if (ctx.Env.Clock.GetUtcNow() >= deadline)
                    throw new CliException(ExitCodes.Unreachable,
                        $"The test of mailbox {id} has not run after {minutes} minute(s); stopped waiting. Is the server's job worker running?");
                await Task.Delay(PollInterval, ctx.Env.Clock, ct);
            }
        });
        return command;
    }

    // ---- profiles ----------------------------------------------------------------------------------------------------

    private static Option<bool> SecretStdin() => new("--secret-stdin")
    {
        Description = "Read the secret (the SMTP password, or the Microsoft Entra client secret) as one line from standard input. " +
                      "Without it the secret is asked for on a terminal; it is never a command-line argument.",
    };

    /// <summary>
    /// The secret: one line of standard input with <paramref name="fromStdin"/>, else asked for on a terminal (without echo on the real
    /// console). Exit 2 when neither is possible or the line is empty. The value is never echoed or logged.
    /// </summary>
    internal static async Task<string> ReadSecretAsync(CliContext ctx, bool fromStdin, string what, CancellationToken ct)
    {
        string? line;
        if (fromStdin) line = await ctx.Env.In.ReadLineAsync(ct);
        else if (ctx.Env.IsInputTty)
        {
            await ctx.Env.Err.WriteAsync($"{what}: ");
            await ctx.Env.Err.FlushAsync(ct);
            line = ReferenceEquals(ctx.Env.In, Console.In) && !Console.IsInputRedirected ? ReadHidden(ct) : await ctx.Env.In.ReadLineAsync(ct);
            await ctx.Env.Err.WriteLineAsync();
        }
        else throw new CliException(ExitCodes.Usage, $"Pass --secret-stdin to read the {what.ToLowerInvariant()} from standard input: a secret is never a command-line argument.");
        line = line?.TrimEnd('\r');
        if (string.IsNullOrEmpty(line)) throw new CliException(ExitCodes.Usage, $"No {what.ToLowerInvariant()} was given.");
        return line;
    }

    /// <summary>
    /// One line from the real console without echoing it. It waits for a key without blocking, so Ctrl+C (which cancels
    /// <paramref name="ct"/>) ends the prompt with <see cref="OperationCanceledException"/>; Escape cancels it too.
    /// </summary>
    private static string ReadHidden(CancellationToken ct)
    {
        var text = new StringBuilder();
        while (true)
        {
            while (!Console.KeyAvailable)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(25);
            }
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                throw new OperationCanceledException(ct);
            if (key.Key == ConsoleKey.Enter) return text.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
    }

    private sealed record ProfileOptions(
        Option<string?> Server, Option<int?> Port, Option<bool?> Ssl, Option<string?> UserName, Option<string?> Credentials,
        Option<string?> Tenant, Option<string?> ClientId, Option<bool> SecretStdin, Option<string?>[] SecretArguments);

    private static ProfileOptions NewProfileOptions()
    {
        var credentials = new Option<string?>("--credentials")
        {
            Description = "SMTP: where the user name and password are, 'profile' (default) or 'mailbox' (each mailbox's own).",
        };
        credentials.AcceptOnlyFromAmong("profile", "mailbox");
        return new ProfileOptions(
            new Option<string?>("--server") { Description = "SMTP: host name of the outgoing server." },
            new Option<int?>("--port") { Description = "SMTP: port of the outgoing server (default 465 with --ssl, else 587)." },
            new Option<bool?>("--ssl") { Description = "SMTP: TLS from the first byte (true); false requires STARTTLS." },
            new Option<string?>("--username") { Description = "SMTP: user name, with --credentials profile." },
            credentials,
            new Option<string?>("--tenant") { Description = "Graph: Microsoft Entra tenant id or domain." },
            new Option<string?>("--client-id") { Description = "Graph: application (client) id." },
            SecretStdin(),
            [.. SecretArgumentNames.Select(n => new Option<string?>(n) { Hidden = true, Arity = ArgumentArity.ZeroOrOne })]);
    }

    /// <summary>
    /// Options a secret could be mistaken for. They are hidden and refused: a value given with one is consumed by the parser (so its
    /// usage error never echoes it), never used and never printed.
    /// </summary>
    private static readonly string[] SecretArgumentNames = ["--password", "--client-secret", "--secret", "--oauthclientsecret", "--outgoingpassword"];

    private const string SecretNotAnArgument =
        "A secret is never a command-line argument: pass --secret-stdin to read it from standard input, or answer the prompt on a terminal.";

    /// <summary>Exit 2 when one of the <see cref="SecretArgumentNames"/> was given; the message names the option, never its value.</summary>
    private static void RefuseSecretArguments(ParseResult parse, ProfileOptions o)
    {
        if (o.SecretArguments.FirstOrDefault(a => parse.GetResult(a) is not null) is { } given)
            throw new CliException(ExitCodes.Usage, $"{given.Name} is not an option. {SecretNotAnArgument} The value given was not used.");
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Command, object> SecretCommands = [];

    /// <summary>
    /// The command takes a secret (<c>mailbox profile create|update</c>): its usage errors must not echo the tokens the parser did not
    /// match, which may be a secret typed as an argument (<see cref="CliApp"/>).
    /// </summary>
    internal static bool TakesSecret(Command command) => SecretCommands.TryGetValue(command, out _);

    /// <summary>A usage error of a command that takes a secret, without the unmatched tokens; it says how to pass a secret.</summary>
    internal static IEnumerable<string> RedactedErrors(ParseResult parse)
    {
        var unmatched = parse.UnmatchedTokens.Where(t => t.Length > 0).ToList();
        var redacted = false;
        foreach (var error in parse.Errors)
        {
            var message = error.Message;
            foreach (var token in unmatched)
                if (message.Contains(token, StringComparison.Ordinal))
                {
                    message = message.Replace(token, "(not shown)", StringComparison.Ordinal);
                    redacted = true;
                }
            yield return message;
        }
        if (redacted || unmatched.Count > 0) yield return SecretNotAnArgument;
    }

    private static void AddTo(Command command, ProfileOptions o)
    {
        command.Add(o.Server);
        command.Add(o.Port);
        command.Add(o.Ssl);
        command.Add(o.UserName);
        command.Add(o.Credentials);
        command.Add(o.Tenant);
        command.Add(o.ClientId);
        command.Add(o.SecretStdin);
        foreach (var secret in o.SecretArguments) command.Add(secret);
        SecretCommands.AddOrUpdate(command, true);
    }

    /// <summary>The columns the options given set (the server validates the profile as a whole).</summary>
    private static Dictionary<string, object?> Columns(ParseResult parse, ProfileOptions o)
    {
        var body = new Dictionary<string, object?>();
        if (parse.GetValue(o.Server) is { } server) body["outgoingserverlocation"] = server;
        if (parse.GetValue(o.Port) is { } port) body["outgoingportnumber"] = port;
        if (parse.GetValue(o.Ssl) is { } ssl) body["outgoingusessl"] = ssl;
        if (parse.GetValue(o.UserName) is { } user) body["outgoingusername"] = user;
        if (parse.GetValue(o.Credentials) is { } credentials) body["outgoingcredentialretrieval"] = credentials == "mailbox" ? 0 : 1;
        if (parse.GetValue(o.Tenant) is { } tenant) body["exchangeonlinetenantid"] = tenant;
        if (parse.GetValue(o.ClientId) is { } client) body["oauthclientid"] = client;
        return body;
    }

    private static Command ProfileCreate(CliContext ctx)
    {
        var name = new Option<string>("--name") { Description = "Name of the profile.", Required = true };
        var type = new Option<string>("--type") { Description = "smtp (IMAP/SMTP, Basic) or graph (Exchange Online, OAuth with Microsoft Entra ID).", Required = true };
        type.AcceptOnlyFromAmong("smtp", "graph");
        var options = NewProfileOptions();
        var command = new Command("create", "Create an email server profile; prints its id. The secret is read from stdin or a prompt.") { name, type };
        AddTo(command, options);
        command.SetAction(async (parse, ct) =>
        {
            RefuseSecretArguments(parse, options);
            var graph = parse.GetValue(type) == "graph";
            var body = new Dictionary<string, object?>
            {
                ["name"] = parse.GetValue(name),
                ["servertype"] = graph ? 0 : 4,
                ["outgoingauthenticationprotocol"] = graph ? 4 : 3,
                ["outgoingcredentialretrieval"] = graph ? 7 : 1,
            };
            if (graph) body["serverauthority"] = 0;
            foreach (var (column, value) in Columns(parse, options)) body[column] = value;
            if (graph && parse.GetResult(options.Credentials) is not null)
                throw new CliException(ExitCodes.Usage, "--credentials is for an SMTP profile; a Graph profile uses its Microsoft Entra application.");
            // A secret when the profile holds one: the Graph client secret, or the SMTP password with the credentials in the profile.
            if (graph) body["oauthclientsecret"] = await ReadSecretAsync(ctx, parse.GetValue(options.SecretStdin), "Client secret", ct);
            else if (Equals(body["outgoingcredentialretrieval"], 1)) body["outgoingpassword"] = await ReadSecretAsync(ctx, parse.GetValue(options.SecretStdin), "Password", ct);
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "emailserverprofiles", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no id for the new profile.");
            ctx.WriteResult(new { emailserverprofileid = id, name = parse.GetValue(name), type = parse.GetValue(type) },
                $"Created email server profile '{parse.GetValue(name)}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ProfileUpdate(CliContext ctx)
    {
        var profile = new Argument<string>("profile") { Description = "Id or name of the profile." };
        var secretPrompt = new Option<bool>("--secret-prompt") { Description = "Ask for a new secret on the terminal (without echo)." };
        var options = NewProfileOptions();
        var command = new Command("update", "Change an email server profile; a new secret only with --secret-stdin or --secret-prompt.") { profile, secretPrompt };
        AddTo(command, options);
        command.SetAction(async (parse, ct) =>
        {
            RefuseSecretArguments(parse, options);
            var client = ctx.CreateClient();
            var row = await ProfileAsync(client, parse.GetValue(profile)!, ct);
            var body = Columns(parse, options);
            var fromStdin = parse.GetValue(options.SecretStdin);
            if (fromStdin || parse.GetValue(secretPrompt))
            {
                if (fromStdin && parse.GetValue(secretPrompt)) throw new CliException(ExitCodes.Usage, "Give --secret-stdin or --secret-prompt, not both.");
                var graph = Int(row, "servertype") == 0;
                body[graph ? "oauthclientsecret" : "outgoingpassword"] = await ReadSecretAsync(ctx, fromStdin, graph ? "Client secret" : "Password", ct);
            }
            if (body.Count == 0) throw new CliException(ExitCodes.Usage, "Nothing to update: give an option or a new secret.");
            var id = Str(row, "emailserverprofileid");
            await client.SendJsonAsync(new HttpMethod("PATCH"), $"emailserverprofiles({id})", body, cancellationToken: ct);
            ctx.WriteResult(new { emailserverprofileid = id, name = Str(row, "name"), updated = true }, $"Updated email server profile '{Str(row, "name")}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>A profile by id or by name; exit 4 when there is none, 5 when the name is not unique.</summary>
    private static async Task<JsonElement> ProfileAsync(XrmdClient client, string profile, CancellationToken ct)
    {
        var filter = Guid.TryParse(profile, out var id) ? $"emailserverprofileid eq {id}" : $"name eq {UserRoleLookup.Literal(profile)}";
        var rows = await client.GetAllPagesAsync($"emailserverprofiles?$select={ProfileSelect}&$filter={filter}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Email server profile '{profile}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} email server profiles are named '{profile}'. Use the profile id.");
        return rows[0];
    }

    private static Command ProfileList(CliContext ctx)
    {
        var command = new Command("list", "List the email server profiles; a secret shows as set or not set, never its value.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync($"emailserverprofiles?$select={ProfileSelect}&$orderby=name", ct);
            ctx.Write(rows.Select(p =>
            {
                var graph = Int(p, "servertype") == 0;
                var inProfile = graph || Int(p, "outgoingcredentialretrieval") == 1;
                return new
                {
                    emailserverprofileid = Str(p, "emailserverprofileid"),
                    name = Str(p, "name"),
                    type = graph ? "graph" : Int(p, "servertype") == 4 ? "smtp" : $"servertype {Int(p, "servertype")}",
                    server = graph ? Str(p, "exchangeonlinetenantid") : Str(p, "outgoingserverlocation"),
                    port = graph ? null : Int(p, "outgoingportnumber"),
                    credentials = graph ? "application" : inProfile ? "profile" : "mailbox",
                    secret = !inProfile ? "-" : Bool(p, graph ? "isoauthclientsecretset" : "isoutgoingpasswordset") ? "set" : "not set",
                    state = Int(p, "statecode") is 1 ? "Inactive" : "Active",
                };
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }
}
