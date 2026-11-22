using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Http;
using Xrmd.Metadata;
using Xrmd.Schema;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd table</c>: tables, columns, relationships and global choices through the Web API metadata endpoints
/// (<c>EntityDefinitions</c>, <c>RelationshipDefinitions</c>, <c>GlobalOptionSetDefinitions</c>) as the signed-in user.
/// The column types and the bodies come from <see cref="SchemaColumnTypes"/> and <see cref="SchemaWebApiBodies"/>.
/// </summary>
public static class TableCommands
{
    private const string SolutionHeader = "MSCRM.SolutionUniqueName";

    public static Command Build(CliContext ctx) => new("table", "Create, list, describe and delete tables, columns, relationships and choices (Web API metadata).")
    {
        Create(ctx),
        List(ctx),
        Describe(ctx),
        Set(ctx),
        Delete(ctx),
        Column(ctx),
        Relationship(ctx),
        Choice(ctx),
    };

    private static Argument<string> TableArgument(string description = "Logical name of the table.") => new("table") { Description = description };

    private static Option<string?> SolutionOption() =>
        new("--solution") { Description = "Unique name of the solution that receives the new component (MSCRM.SolutionUniqueName header)." };

    private static IReadOnlyDictionary<string, string>? SolutionHeaders(string? solution) =>
        solution is null ? null : new Dictionary<string, string> { [SolutionHeader] = solution };

    /// <summary>An OData metadata key: <c>EntityDefinitions(LogicalName='x')</c>, with the name quoted and escaped.</summary>
    private static string EntityPath(string table) => $"EntityDefinitions(LogicalName={UserRoleLookup.Literal(table)})";

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    /// <summary>The text of a Web API label property (<c>UserLocalizedLabel</c>, else the first localized label).</summary>
    private static string? LabelText(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var label) || label.ValueKind != JsonValueKind.Object) return null;
        if (label.TryGetProperty("UserLocalizedLabel", out var user) && user.ValueKind == JsonValueKind.Object && Str(user, "Label") is { } text) return text;
        return label.TryGetProperty("LocalizedLabels", out var all) && all.ValueKind == JsonValueKind.Array && all.GetArrayLength() > 0
            ? Str(all[0], "Label")
            : null;
    }

    // ---- table create / list / describe / delete ----------------------------------------------------------------------

    private static Command Create(CliContext ctx)
    {
        var name = TableArgument("Logical name of the new table, with the publisher prefix (e.g. xv_project).");
        var label = new Option<string>("--label") { Description = "Display name.", Required = true };
        var plural = new Option<string>("--plural-label") { Description = "Plural display name.", Required = true };
        var ownership = new Option<string>("--ownership") { Description = "user (default) or organization.", DefaultValueFactory = _ => "user" };
        ownership.AcceptOnlyFromAmong("user", "organization");
        var primaryName = new Option<string?>("--primary-name") { Description = "Logical name of the primary name column; default: <prefix>_name (the table's prefix)." };
        var activity = new Option<bool>("--activity")
        {
            Description = "Create a custom activity table: primary name column 'subject' (\"Subject\"), notes on, user owned. Set at creation only.",
        };
        var notes = new Option<bool>("--notes") { Description = "Allow notes on the table's records (HasNotes). Cannot be switched off later." };
        var activities = new Option<bool>("--activities") { Description = "Allow activities regarding the table's records (HasActivities). Cannot be switched off later." };
        var queues = new Option<bool>("--queues")
        {
            Description = "Allow the table's records in queues (IsValidForQueue; on by default for an activity). Cannot be switched off later.",
        };
        var solution = SolutionOption();
        var command = new Command("create", "Create a table with its primary name column.")
        {
            name, label, plural, ownership, primaryName, activity, notes, activities, queues, solution,
        };
        command.SetAction(async (parse, ct) =>
        {
            var table = parse.GetValue(name)!;
            var isActivity = parse.GetValue(activity);
            if (isActivity && parse.GetValue(primaryName) is not null)
                throw new CliException(ExitCodes.Usage,
                    $"--primary-name does not apply with --activity: the primary name column of an activity is '{SchemaTable.ActivityPrimaryName}'.");
            var primary = isActivity ? SchemaTable.ActivityPrimaryName : parse.GetValue(primaryName) ?? DefaultPrimaryName(table);
            var schemaTable = new SchemaTable
            {
                Name = table,
                Label = parse.GetValue(label)!,
                PluralLabel = parse.GetValue(plural),
                Ownership = parse.GetValue(ownership) == "organization" ? SchemaOwnership.Organization : SchemaOwnership.User,
                PrimaryName = new SchemaPrimaryName { Name = primary, Label = isActivity ? "Subject" : "Name" },
                IsActivity = isActivity,
                HasNotes = parse.GetValue(notes) ? true : null,
                HasActivities = parse.GetValue(activities) ? true : null,
                IsValidForQueue = parse.GetValue(queues) ? true : null,
            };
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "EntityDefinitions", SchemaWebApiBodies.Entity(schemaTable),
                SolutionHeaders(parse.GetValue(solution)), ct);
            ctx.WriteResult(new { table, primaryName = primary, metadataId = MetadataId(response) }, $"Created table '{table}' (primary name column '{primary}').");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static string DefaultPrimaryName(string table) =>
        table.IndexOf('_') is var underscore and > 0
            ? table[..underscore] + "_name"
            : throw new CliException(ExitCodes.Usage, $"The table '{table}' has no publisher prefix (prefix_name): pass --primary-name to name its primary name column.");

    private static Guid? MetadataId(XrmdResponse response) => response.EntityId;

    private static Command List(CliContext ctx)
    {
        var custom = new Option<bool>("--custom") { Description = "Only custom tables." };
        var command = new Command("list", "List the tables.") { custom };
        command.SetAction(async (parse, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync(
                "EntityDefinitions?$select=LogicalName,EntitySetName,DisplayName,OwnershipType,IsCustomEntity", ct);
            var onlyCustom = parse.GetValue(custom);
            ctx.Write(rows
                .Where(r => !onlyCustom || IsCustom(r))
                .OrderBy(r => Str(r, "LogicalName"), StringComparer.Ordinal)
                .Select(r => new
                {
                    name = Str(r, "LogicalName"),
                    entitySet = Str(r, "EntitySetName"),
                    label = LabelText(r, "DisplayName"),
                    ownership = Str(r, "OwnershipType"),
                    custom = IsCustom(r),
                }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>A custom table: the server's <c>IsCustomEntity</c> (true for every table created through the API or an import).</summary>
    private static bool IsCustom(JsonElement entity) =>
        entity.TryGetProperty("IsCustomEntity", out var flag) && flag.ValueKind == JsonValueKind.True;

    private static Command Describe(CliContext ctx)
    {
        var table = TableArgument();
        var command = new Command("describe", "Show a table: its settings, columns and relationships.") { table };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(table)!;
            var client = ctx.CreateClient();
            var entity = await client.GetJsonAsync(EntityPath(name), ct);
            var attributes = (await client.GetAllPagesAsync($"{EntityPath(name)}/Attributes", ct))
                .Select(a => new
                {
                    name = Str(a, "LogicalName"),
                    type = ColumnType(a),
                    label = LabelText(a, "DisplayName"),
                    primaryName = a.TryGetProperty("IsPrimaryName", out var p) && p.ValueKind == JsonValueKind.True,
                    maxLength = a.TryGetProperty("MaxLength", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : (int?)null,
                    target = a.TryGetProperty("Targets", out var t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0 ? string.Join(", ", t.EnumerateArray().Select(x => x.GetString())) : null,
                }).ToList();
            var relationships = (await client.GetAllPagesAsync("RelationshipDefinitions", ct))
                .Where(r => Str(r, "ReferencingEntity") == name || Str(r, "ReferencedEntity") == name
                            || Str(r, "Entity1LogicalName") == name || Str(r, "Entity2LogicalName") == name)
                .Select(r => Str(r, "ReferencingEntity") is { } referencing
                    ? new { name = Str(r, "SchemaName"), kind = "one-to-many", referencing = (string?)referencing, referenced = (string?)Str(r, "ReferencedEntity"), column = Str(r, "ReferencingAttribute") }
                    : new { name = Str(r, "SchemaName"), kind = "many-to-many", referencing = (string?)Str(r, "Entity1LogicalName"), referenced = (string?)Str(r, "Entity2LogicalName"), column = (string?)null })
                .ToList();
            var summary = new
            {
                name = Str(entity, "LogicalName"),
                schemaName = Str(entity, "SchemaName"),
                entitySet = Str(entity, "EntitySetName"),
                label = LabelText(entity, "DisplayName"),
                pluralLabel = LabelText(entity, "DisplayCollectionName"),
                ownership = Str(entity, "OwnershipType"),
                custom = IsCustom(entity),
                primaryIdAttribute = Str(entity, "PrimaryIdAttribute"),
                primaryNameAttribute = Str(entity, "PrimaryNameAttribute"),
                isActivity = Flag(entity, "IsActivity"),
                hasNotes = Flag(entity, "HasNotes"),
                hasActivities = Flag(entity, "HasActivities"),
                isValidForQueue = Flag(entity, "IsValidForQueue"),
            };
            if (ctx.Format == Output.OutputFormat.Json)
            {
                ctx.Write(new { summary.name, summary.schemaName, summary.entitySet, summary.label, summary.pluralLabel, summary.ownership,
                    summary.custom, summary.primaryIdAttribute, summary.primaryNameAttribute, summary.isActivity, summary.hasNotes, summary.hasActivities, summary.isValidForQueue,
                    columns = attributes, relationships });
            }
            else
            {
                ctx.Write(summary);
                ctx.Env.Out.WriteLine();
                ctx.Env.Out.WriteLine("Columns");
                ctx.Write(attributes);
                ctx.Env.Out.WriteLine();
                ctx.Env.Out.WriteLine("Relationships");
                ctx.Write(relationships);
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// A boolean property of a Web API definition: true only when the server returns true, directly or as the <c>Value</c> of a
    /// managed property (<c>IsValidForQueue</c>).
    /// </summary>
    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out var flag)
        && (flag.ValueKind == JsonValueKind.True
            || (flag.ValueKind == JsonValueKind.Object && flag.TryGetProperty("Value", out var value) && value.ValueKind == JsonValueKind.True));

    /// <summary>
    /// <c>table set &lt;table&gt; --notes|--activities|--queues</c> (plan 2.11, A5; plan 4.1a): switches <c>HasNotes</c>, <c>HasActivities</c>
    /// and <c>IsValidForQueue</c> on with a
    /// PUT of the table's definition (as <c>change-tracking enable</c>). One-way, as on the server: there is no option to switch
    /// one off (the flags take no value), and a confirmation is asked (<c>--yes</c> without a terminal). A flag already on is
    /// reported and not sent.
    /// </summary>
    private static Command Set(CliContext ctx)
    {
        var table = TableArgument();
        var notes = new Option<bool>("--notes") { Description = "Switch notes on (HasNotes). Cannot be undone.", Arity = ArgumentArity.Zero };
        var activities = new Option<bool>("--activities") { Description = "Switch activities on (HasActivities). Cannot be undone.", Arity = ArgumentArity.Zero };
        var queues = new Option<bool>("--queues") { Description = "Switch queues on (IsValidForQueue). Cannot be undone.", Arity = ArgumentArity.Zero };
        var command = new Command("set", "Switch a table's notes, activities or queues on (one-way: they cannot be switched off).")
        {
            table, notes, activities, queues, ctx.YesOption,
        };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(table)!;
            var wanted = new List<string>();
            if (parse.GetValue(notes)) wanted.Add("HasNotes");
            if (parse.GetValue(activities)) wanted.Add("HasActivities");
            if (parse.GetValue(queues)) wanted.Add("IsValidForQueue");
            if (wanted.Count == 0)
                throw new CliException(ExitCodes.Usage, "Nothing to switch on: give --notes, --activities, --queues or several (none can be switched off).");

            var client = ctx.CreateClient();
            var path = EntityPath(name);
            var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
            var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.");
            var alreadyOn = wanted.Where(f => Flag(json, f)).Select(Camel).ToList();
            var switchOn = wanted.Where(f => !Flag(json, f)).ToList();
            if (switchOn.Count > 0)
            {
                await ctx.ConfirmDestructiveAsync(
                    $"{string.Join(" and ", switchOn)} cannot be switched off again once on for the table '{name}'. Continue?");
                var body = JsonNode.Parse(json.GetRawText())!.AsObject();
                body.Remove("@odata.context");
                // Computed by the server, never written (as change-tracking enable).
                body.Remove("CanChangeTrackingBeEnabled");
                foreach (var flag in switchOn)
                    // IsValidForQueue is a managed property: its Value is switched, the rest of what the server returned stays.
                    if (body[flag] is JsonObject managed) managed["Value"] = true;
                    else body[flag] = flag == "IsValidForQueue" ? new JsonObject { ["Value"] = true } : true;
                await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
            }
            ctx.WriteResult(new { table = name, switchedOn = switchOn.Select(Camel).ToList(), alreadyOn },
                switchOn.Count == 0
                    ? $"Table '{name}': {string.Join(" and ", alreadyOn)} already on."
                    : $"Table '{name}': {string.Join(" and ", switchOn.Select(Camel))} on." + (alreadyOn.Count > 0 ? $" ({string.Join(" and ", alreadyOn)} already on.)" : ""));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static string Camel(string property) => char.ToLowerInvariant(property[0]) + property[1..];

    /// <summary>The schema type name of a column (<see cref="SchemaColumnTypes.TypeNameOf"/>), else the server's AttributeType.</summary>
    private static string? ColumnType(JsonElement attribute)
    {
        try
        {
            var node = JsonNode.Parse(attribute.GetRawText())!.AsObject();
            // The deserializer needs @odata.type first and without the Web API's leading '#'.
            var typed = new JsonObject { ["@odata.type"] = node["@odata.type"]!.GetValue<string>().TrimStart('#') };
            foreach (var (key, value) in node.ToList())
            {
                if (key == "@odata.type") continue;
                node.Remove(key);
                typed[key] = value;
            }
            if (SchemaColumnTypes.TypeNameOf(MetadataSerializer.DeserializeAttribute(typed.ToJsonString())) is { } known) return known;
        }
        // A missing Microsoft SDK is not "an unreadable column": it reaches CliApp, which prints the missing-SDK message (exit 1).
        catch (Exception ex) when (SdkAssemblyResolver.MissingAssembly(ex) is null)
        {
            // Not a column xrmverse can read as a schema type: the server's own AttributeType below.
        }
        return Str(attribute, "AttributeType");
    }

    private static Command Delete(CliContext ctx)
    {
        var table = TableArgument();
        var command = new Command("delete", "Delete a table (refused while another component refers to it).") { table, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(table)!;
            await ctx.ConfirmDestructiveAsync($"This permanently deletes the table '{name}' and all its rows. Continue?");
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Delete, EntityPath(name), cancellationToken: ct);
            ctx.WriteResult(new { deleted = name }, $"Deleted table '{name}'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- table column add / remove ------------------------------------------------------------------------------------

    private static Command Column(CliContext ctx) => new("column", "Add and remove columns.") { ColumnAdd(ctx), ColumnRemove(ctx) };

    private static Command ColumnAdd(CliContext ctx)
    {
        var table = TableArgument();
        var name = new Argument<string>("name") { Description = "Logical name of the new column, with the publisher prefix." };
        var type = new Option<string>("--type") { Description = "Column type: " + string.Join(", ", SchemaColumnTypes.Names) + ".", Required = true };
        var label = new Option<string?>("--label") { Description = "Display name; default: the column name." };
        var maxLength = new Option<int?>("--max-length") { Description = "Maximum length (string, memo)." };
        var target = new Option<string?>("--target") { Description = "Table a lookup refers to (lookup); a customer column takes none (it refers to account and contact)." };
        var choice = new Option<string?>("--choice") { Description = "Global choice the column uses (choice, choices); see `table choice add`." };
        var maxSize = new Option<int?>("--max-size-in-kb") { Description = "Largest file or image, in KB (file, image); default: the server's (32768, 10240)." };
        var fullImage = new Option<bool>("--can-store-full-image") { Description = "Keep the full-size image besides the thumbnail (image)." };
        var primary = new Option<bool>("--primary") { Description = "Make the column the primary image of its table (image)." };
        var solution = SolutionOption();
        var command = new Command("add", "Add a column to a table (a lookup is created with its one-to-many relationship, a customer column with two: to account and to contact).")
        {
            table, name, type, label, maxLength, target, choice, maxSize, fullImage, primary, solution,
        };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var typeName = parse.GetValue(type)!;
            if (!SchemaColumnTypes.IsKnown(typeName))
                throw new CliException(ExitCodes.Usage, $"Unknown column type '{typeName}'. Allowed types: {string.Join(", ", SchemaColumnTypes.Names)}.");
            var column = new SchemaColumn
            {
                Name = parse.GetValue(name)!,
                Type = typeName,
                Label = parse.GetValue(label) ?? parse.GetValue(name),
                MaxLength = parse.GetValue(maxLength),
                Target = parse.GetValue(target),
                Choice = parse.GetValue(choice),
                MaxSizeInKB = parse.GetValue(maxSize),
                CanStoreFullImage = parse.GetValue(fullImage) ? true : null,
                Primary = parse.GetValue(primary) ? true : null,
            };
            if (typeName is SchemaColumnTypes.Choice or SchemaColumnTypes.Choices && column.Choice is null)
                throw new CliException(ExitCodes.Usage, $"A {typeName} column needs --choice <global choice> (create one with `xrmd table choice add`).");
            if (typeName == SchemaColumnTypes.Lookup && column.Target is null)
                throw new CliException(ExitCodes.Usage, "A lookup column needs --target <table>.");
            if (SchemaColumnTypes.Validate(column) is { } problem) throw new CliException(ExitCodes.Usage, problem);

            var client = ctx.CreateClient();
            var headers = SolutionHeaders(parse.GetValue(solution));
            if (typeName == SchemaColumnTypes.Customer)
            {
                var relationships = await AddCustomerAsync(client, tableName, column, headers, ct);
                ctx.WriteResult(new { table = tableName, column = column.Name, type = typeName, relationships },
                    $"Added customer column '{tableName}.{column.Name}' to account and contact (relationships {string.Join(", ", relationships.Select(r => $"'{r}'"))}).");
            }
            else if (typeName == SchemaColumnTypes.Lookup)
            {
                var relationship = await AddLookupAsync(client, tableName, column, null, headers, ct);
                ctx.WriteResult(new { table = tableName, column = column.Name, type = typeName, relationship },
                    $"Added lookup column '{tableName}.{column.Name}' to '{column.Target}' (relationship '{relationship}').");
            }
            else
            {
                await client.SendJsonAsync(HttpMethod.Post, $"{EntityPath(tableName)}/Attributes", SchemaWebApiBodies.Attribute(column), headers, ct);
                // A create ignores IsPrimaryImage = true when the table has an image column: an update moves the flag.
                if (column.Primary == true)
                {
                    try
                    {
                        await client.SendJsonAsync(HttpMethod.Put, $"{EntityPath(tableName)}/Attributes(LogicalName={UserRoleLookup.Literal(column.Name)})",
                            SchemaWebApiBodies.PrimaryImage(column.Name), headers, ct);
                    }
                    catch (CliException ex)
                    {
                        throw new CliException(ex.ExitCode,
                            $"The column '{tableName}.{column.Name}' was created but was not made the primary image: {ex.Message}", ex)
                        {
                            HttpStatus = ex.HttpStatus, ResponseBody = ex.ResponseBody,
                        };
                    }
                }
                ctx.WriteResult(new { table = tableName, column = column.Name, type = typeName }, $"Added {typeName} column '{tableName}.{column.Name}'.");
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>Creates the lookup and its one-to-many relationship (default name <c>{table}_{column}_{target}</c>, as the schema YAML); returns the relationship's name.</summary>
    private static async Task<string> AddLookupAsync(XrmdClient client, string table, SchemaColumn lookup, string? relationshipName,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var target = lookup.Target!;
        var targetEntity = await client.GetJsonAsync($"{EntityPath(target)}?$select=PrimaryIdAttribute", ct);
        var name = relationshipName ?? $"{table}_{lookup.Name}_{target}";
        var referenced = Str(targetEntity, "PrimaryIdAttribute")
                         ?? throw new CliException(ExitCodes.Unexpected, $"The server reports no PrimaryIdAttribute for the table '{target}': cannot create a lookup to it.");
        await client.SendJsonAsync(HttpMethod.Post, "RelationshipDefinitions", SchemaWebApiBodies.Lookup(lookup, table, name, referenced), headers, ct);
        return name;
    }

    /// <summary>
    /// Creates a customer column with its two one-to-many relationships in one call (<c>CreateCustomerRelationships</c>, the
    /// only way the server creates one); returns the relationships' names (<c>{table}_{column}_account</c>, <c>_contact</c>).
    /// </summary>
    private static async Task<IReadOnlyList<string>> AddCustomerAsync(XrmdClient client, string table, SchemaColumn customer,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        await client.SendJsonAsync(HttpMethod.Post, "CreateCustomerRelationships", SchemaWebApiBodies.Customer(customer, table), headers, ct);
        return CustomerColumns.Targets.Select(t => SchemaWebApiBodies.CustomerRelationshipName(table, customer.Name, t)).ToList();
    }

    private static Command ColumnRemove(CliContext ctx)
    {
        var table = TableArgument();
        var name = new Argument<string>("name") { Description = "Logical name of the column." };
        var command = new Command("remove", "Remove a column (and its data).") { table, name, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var column = parse.GetValue(name)!;
            await ctx.ConfirmDestructiveAsync($"This permanently deletes the column '{tableName}.{column}' and its data. Continue?");
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Delete, $"{EntityPath(tableName)}/Attributes(LogicalName={UserRoleLookup.Literal(column)})", cancellationToken: ct);
            ctx.WriteResult(new { table = tableName, removed = column }, $"Removed column '{tableName}.{column}'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- table relationship add ---------------------------------------------------------------------------------------

    private static Command Relationship(CliContext ctx)
    {
        var add = RelationshipAdd(ctx);
        return new Command("relationship", "Add relationships.") { add };
    }

    private static Command RelationshipAdd(CliContext ctx)
    {
        var table = TableArgument();
        var to = new Option<string>("--to") { Description = "The other table.", Required = true };
        var manyToMany = new Option<bool>("--many-to-many") { Description = "Create a many-to-many relationship; without it, a one-to-many where <table> holds a lookup to --to." };
        var name = new Option<string?>("--name") { Description = "Relationship SchemaName; default: <table>_<to> (many-to-many) or <table>_<lookup column>_<to> (one-to-many)." };
        var solution = SolutionOption();
        var command = new Command("add", "Add a relationship between two tables.") { table, to, manyToMany, name, solution };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var other = parse.GetValue(to)!;
            var client = ctx.CreateClient();
            var headers = SolutionHeaders(parse.GetValue(solution));
            if (parse.GetValue(manyToMany))
            {
                var relationship = parse.GetValue(name) ?? $"{tableName}_{other}";
                await client.SendJsonAsync(HttpMethod.Post, "RelationshipDefinitions", SchemaWebApiBodies.ManyToMany(relationship, tableName, other), headers, ct);
                ctx.WriteResult(new { relationship, kind = "many-to-many", referencing = tableName, referenced = other },
                    $"Added many-to-many relationship '{relationship}' between '{tableName}' and '{other}'.");
            }
            else
            {
                var column = new SchemaColumn { Name = LookupColumnName(tableName, other), Type = SchemaColumnTypes.Lookup, Target = other };
                column.Label = other;
                var relationship = await AddLookupAsync(client, tableName, column, parse.GetValue(name), headers, ct);
                ctx.WriteResult(new { relationship, kind = "one-to-many", referencing = tableName, referenced = other, column = column.Name },
                    $"Added one-to-many relationship '{relationship}': '{tableName}.{column.Name}' refers to '{other}'.");
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The lookup column of a one-to-many: the table's prefix, then the target without its prefix, plus <c>id</c>.</summary>
    private static string LookupColumnName(string table, string target)
    {
        var prefix = table.IndexOf('_') is var p and > 0 ? table[..p] : null;
        var bare = target.IndexOf('_') is var q and > 0 ? target[(q + 1)..] : target;
        return prefix is null ? bare + "id" : $"{prefix}_{bare}id";
    }

    // ---- table choice add ---------------------------------------------------------------------------------------------

    private static Command Choice(CliContext ctx)
    {
        var add = ChoiceAdd(ctx);
        return new Command("choice", "Add global choices.") { add };
    }

    private static Command ChoiceAdd(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the global choice, with the publisher prefix." };
        var label = new Option<string>("--label") { Description = "Display name.", Required = true };
        var options = new Option<string>("--options") { Description = "Options as Label=Value pairs, e.g. \"Low=100000000,High=100000001\".", Required = true };
        var solution = SolutionOption();
        var command = new Command("add", "Create a global choice.") { name, label, options, solution };
        command.SetAction(async (parse, ct) =>
        {
            var parsed = ParseOptions(parse.GetValue(options)!);
            var choice = parse.GetValue(name)!;
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "GlobalOptionSetDefinitions",
                SchemaWebApiBodies.Choice(choice, parse.GetValue(label)!, parsed), SolutionHeaders(parse.GetValue(solution)), ct);
            ctx.WriteResult(new { choice, options = parsed.Select(o => new { label = o.Label, value = o.Value }) },
                $"Created choice '{choice}' with {parsed.Count} option(s).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static List<SchemaOption> ParseOptions(string text)
    {
        var result = new List<SchemaOption>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            var equals = part.LastIndexOf('=');
            if (equals <= 0 || !int.TryParse(part[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new CliException(ExitCodes.Usage, $"--options: '{part}' is not Label=Value with an integer value.");
            result.Add(new SchemaOption { Label = part[..equals].Trim(), Value = value });
        }
        if (result.GroupBy(o => o.Value).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new CliException(ExitCodes.Usage, $"--options: the value {duplicate.Key} appears more than once.");
        return result;
    }
}
