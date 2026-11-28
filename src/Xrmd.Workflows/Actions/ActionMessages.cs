using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Actions;

/// <summary>
/// The <c>sdkmessage</c> of a XAML action (a process of category 3, plan 2.14, ruling W14) and its <c>sdkmessagefilter</c>: their name,
/// the checks of that name, their creation (by the activation and the solution import) and their removal with the action.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The message name is the action's <c>uniquename</c> when it already starts with the publisher's prefix and <c>_</c>, else
/// <c>{prefix}_{uniquename}</c> (the parity export: <c>Recalculate</c> of the publisher <c>xrmv</c> answers <c>xrmv_Recalculate</c>).
/// The prefix is the import's publisher, or for a SetState the publisher of the solution that holds the process (the first one
/// installed other than Default and Active), else the default publisher's.</item>
/// <item>The message keeps the exported <c>SdkMessageId</c> when it is given and free; otherwise its id is derived from its name, as a
/// custom API's (<see cref="SdkMessageCatalog.MessageId"/>). Its filter is on the bound table, or on <c>none</c> for a global action.</item>
/// <item>Deactivation keeps the message (W14: plug-in steps may be registered on it); a call to it then fails (400) instead of running
/// as a custom message without a main operation. Deleting the definition deletes the message and its filters, and is refused (400) while
/// a plug-in step is registered on the message.</item>
/// </list>
/// The <c>categoryname</c> of the message, <see cref="Category"/>, is from memory (checklist 1i).
/// </remarks>
public sealed class ActionMessages(IPipelineCore core, IMetadataStore metadata)
{
    private const string Message = "sdkmessage", Filter = "sdkmessagefilter";

    /// <summary>The <c>sdkmessage.categoryname</c> of an action's message (from memory, checklist 1i).</summary>
    public const string Category = "CustomAction";

    /// <summary>The message name of an action whose <c>uniquename</c> is <paramref name="uniqueName"/>, for the publisher prefix <paramref name="prefix"/>.</summary>
    public static string MessageName(string uniqueName, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(uniqueName);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return uniqueName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ? uniqueName : $"{prefix}_{uniqueName}";
    }

    /// <summary>
    /// The custom API type of an argument's CLR type (the type table the request check and the response share with custom APIs); null
    /// for a type no action argument may have in xrmverse.
    /// </summary>
    public static CustomApiType? TypeOf(string clrType) => clrType switch
    {
        "System.Boolean" => CustomApiType.Boolean,
        "System.DateTime" => CustomApiType.DateTime,
        "System.Decimal" => CustomApiType.Decimal,
        "System.Double" => CustomApiType.Float,
        "System.Int32" => CustomApiType.Integer,
        "System.String" => CustomApiType.String,
        "System.Guid" => CustomApiType.Guid,
        "Microsoft.Xrm.Sdk.Entity" => CustomApiType.Entity,
        "Microsoft.Xrm.Sdk.EntityCollection" => CustomApiType.EntityCollection,
        "Microsoft.Xrm.Sdk.EntityReference" => CustomApiType.EntityReference,
        "Microsoft.Xrm.Sdk.Money" => CustomApiType.Money,
        "Microsoft.Xrm.Sdk.OptionSetValue" => CustomApiType.Picklist,
        _ => null,
    };

    /// <summary>The bound table of an action (<c>primaryentity</c>); null for a global action (<c>none</c> or empty).</summary>
    public static string? BoundEntityOf(WorkflowRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.IsNullOrEmpty(row.PrimaryEntity) || string.Equals(row.PrimaryEntity, S.NoPrimaryEntity, StringComparison.OrdinalIgnoreCase)
            ? null
            : row.PrimaryEntity.ToLowerInvariant();
    }

    /// <summary>
    /// The arguments of an action as request parameters and response properties (the bound <c>Target</c> left out of the inputs).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an argument of a type no action argument may have, naming it.</exception>
    public static (IReadOnlyList<CustomApiField> Inputs, IReadOnlyList<CustomApiField> Outputs) Fields(WorkflowRow row, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(definition);
        var inputs = new List<CustomApiField>();
        var outputs = new List<CustomApiField>();
        foreach (var argument in definition.Arguments)
        {
            if (argument.IsTarget) continue;
            var type = TypeOf(argument.ClrType) ?? throw XrmdFaultException.InvalidArgument(
                $"The action '{row.Name ?? row.Id.ToString()}' has the argument '{argument.Name}' of type {argument.ClrType}, which xrmverse does not support.");
            var field = new CustomApiField(argument.Name, type, argument.EntityTarget, IsOptional: argument.IsInput && !argument.Required);
            (argument.IsInput ? inputs : outputs).Add(field);
        }
        return (inputs, outputs);
    }

    /// <summary>
    /// The prefix of the publisher of the solution holding the process <paramref name="workflowId"/> (the first installed other than
    /// Default and Active), else the default publisher's.
    /// </summary>
    public static async Task<string> PrefixAsync(DataSession s, Guid workflowId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        // Plan 2.7b, Task 7: in the dialect (the SQL Server text is the T-SQL it was: the subquery keeps its line breaks).
        var first = s.Dialect.Limit(
            $"SELECT p.customizationprefix FROM {s.DataTable("solutioncomponent")} sc\n" +
            $"     JOIN {s.DataTable("solution")} so ON so.solutionid = sc.solutionid\n" +
            $"     JOIN {s.DataTable("publisher")} p ON p.publisherid = so.publisherid\n" +
            "     WHERE sc.objectid = @id AND sc.componenttype = 29 AND so.solutionid NOT IN (@default, @active)\n" +
            "       AND p.customizationprefix IS NOT NULL\n" +
            "     ORDER BY so.createdon, so.solutionid", "1");
        await using var cmd = s.Command($"""
            SELECT COALESCE(
                ({first}),
                (SELECT customizationprefix FROM {s.DataTable("publisher")} WHERE publisherid = @publisher))
            """);
        cmd.AddParameter("@id", DbType.Guid, workflowId);
        cmd.AddParameter("@default", DbType.Guid, SystemSeed.DefaultSolutionId);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        cmd.AddParameter("@publisher", DbType.Guid, SystemSeed.DefaultPublisherId);
        return await cmd.ExecuteScalarAsync(ct) is string { Length: > 0 } prefix
            ? prefix
            : throw XrmdFaultException.InvalidArgument($"The process {workflowId} has no publisher prefix to name its action's message.");
    }

    /// <summary>
    /// The message name of the action <paramref name="row"/> once checked (W14): a <c>uniquename</c> of letters, digits and <c>_</c>, and
    /// a name no message the server handles itself, no custom API and no other <c>sdkmessage</c> has (ignoring case). The action's own
    /// message (<see cref="WorkflowRow.SdkMessageId"/>) is not a clash.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the process and the clash.</exception>
    public async Task<string> DemandNameAsync(DataSession s, WorkflowRow row, string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        var process = row.Name ?? row.Id.ToString();
        if (string.IsNullOrEmpty(row.UniqueName))
            throw XrmdFaultException.InvalidArgument($"The action '{process}' has no unique name, so it has no message.");
        if (!CustomApiRules.HasValidNameCharacters(row.UniqueName))
            throw XrmdFaultException.InvalidArgument($"The action '{process}' has the unique name '{row.UniqueName}': letters, digits and '_' only.");
        var name = MessageName(row.UniqueName, prefix);
        if (PipelineExecutor.IsReservedMessageName(core, name))
            throw Clash(process, name, "it is the name of a message xrmverse handles itself");
        var d = s.Dialect;
        if (metadata.GetEntity(s, CustomApiTables.Api) is not null)
        {
            // Plan 2.7b, Task 7: the key locks of the names (nothing on SQL Server, whose range locks do it) in the order the
            // custom API checks take them: the API's name, then the message's.
            await s.KeyLockAsync(CustomApiTables.Api, name, ct);
            await using var api = s.Command(d.Limit(
                $"SELECT {CustomApiTables.UniqueName} FROM {s.DataTable(CustomApiTables.Api)}{d.LockHint(RowLock.UpdateRange)} WHERE {CustomApiTables.UniqueName} = @name",
                "1") + d.LockClause(RowLock.UpdateRange));
            api.AddParameter("@name", DbType.String, name, 256);
            if (await api.ExecuteScalarAsync(ct) is string existing)
                throw Clash(process, name, $"the custom API '{existing}' has that name");
        }
        await s.KeyLockAsync(Message, name, ct);
        await using var other = s.Command(d.Limit(
            $"SELECT name FROM {s.DataTable(Message)}{d.LockHint(RowLock.UpdateRange)} WHERE name = @name AND (@own IS NULL OR sdkmessageid <> @own)",
            "1") + d.LockClause(RowLock.UpdateRange));
        other.AddParameter("@name", DbType.String, name, 256);
        other.AddParameter("@own", DbType.Guid, (object?)row.SdkMessageId ?? DBNull.Value);
        if (await other.ExecuteScalarAsync(ct) is string message)
            throw Clash(process, name, $"the organization already has the message '{message}' (a custom action, a custom API or another message)");
        return name;
    }

    private static XrmdFaultException Clash(string process, string name, string why) =>
        XrmdFaultException.InvalidArgument($"The action '{process}' cannot have the message '{name}': {why}.");

    /// <summary>
    /// Makes sure the action <paramref name="row"/> (a definition of category 3) has its message and filter, in <paramref name="s"/>: its
    /// stored <c>sdkmessageid</c> when that message exists (it must have the action's name), else a new message with the id
    /// <paramref name="preferredId"/> when free; <c>sdkmessageid</c> is then set on the definition.
    /// </summary>
    /// <returns>The row with its <see cref="WorkflowRow.SdkMessageId"/>.</returns>
    /// <exception cref="XrmdFaultException">400 as <see cref="DemandNameAsync"/>, or when the stored message has another name.</exception>
    public async Task<WorkflowRow> EnsureAsync(DataSession s, WorkflowRow row, string prefix, Guid? preferredId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        var name = await DemandNameAsync(s, row, prefix, ct);
        var entity = BoundEntityOf(row) ?? S.NoPrimaryEntity;
        if (row.SdkMessageId is { } stored && await NameOfAsync(s, stored, ct) is { } storedName)
        {
            if (!string.Equals(storedName, name, StringComparison.OrdinalIgnoreCase))
                throw XrmdFaultException.InvalidArgument(
                    $"The action '{row.Name ?? row.Id.ToString()}' has the message '{storedName}', not '{name}': the message of an action cannot be renamed.");
            await EnsureFilterAsync(s, stored, name, entity, ct);
            return row;
        }

        var id = preferredId is { } preferred && preferred != Guid.Empty && await NameOfAsync(s, preferred, ct) is null
            ? preferred
            : await NameOfAsync(s, SdkMessageCatalog.MessageId(name), ct) is null ? SdkMessageCatalog.MessageId(name) : Guid.NewGuid();
        await core.ExecuteCoreAsync(new Request
        {
            Name = "Create",
            Parameters = { ["Target"] = new Record(Message, id) { ["name"] = name, ["categoryname"] = Category, ["isprivate"] = false } },
        }, new CoreCall(Caller.Internal, s), ct);
        await EnsureFilterAsync(s, id, name, entity, ct);
        await core.ExecuteCoreAsync(new Request
        {
            Name = "Update",
            Parameters =
            {
                ["Target"] = new Record(S.Table, row.Id) { [S.SdkMessageId] = new RecordReference { LogicalName = Message, Id = id } },
            },
        }, new CoreCall(Caller.Internal, s), ct);
        return row with { SdkMessageId = id };
    }

    private async Task EnsureFilterAsync(DataSession s, Guid messageId, string name, string entity, CancellationToken ct)
    {
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT 1 FROM {s.DataTable(Filter)} WHERE sdkmessageid = @message AND primaryobjecttypecode = @entity", "1")))
        {
            cmd.AddParameter("@message", DbType.Guid, messageId);
            cmd.AddParameter("@entity", DbType.String, entity, 128);
            if (await cmd.ExecuteScalarAsync(ct) is not null) return;
        }
        var filterId = SdkMessageCatalog.FilterId(name, entity);
        if (await ExistsAsync(s, Filter, "sdkmessagefilterid", filterId, ct)) filterId = Guid.NewGuid();
        await core.ExecuteCoreAsync(new Request
        {
            Name = "Create",
            Parameters =
            {
                ["Target"] = new Record(Filter, filterId)
                {
                    ["name"] = $"{name}:{entity}",
                    ["sdkmessageid"] = new RecordReference { LogicalName = Message, Id = messageId },
                    ["primaryobjecttypecode"] = entity,
                    ["secondaryobjecttypecode"] = S.NoPrimaryEntity,
                    ["isvisible"] = true,
                    ["iscustomprocessingstepallowed"] = true,
                },
            },
        }, new CoreCall(Caller.Internal, s), ct);
    }

    /// <summary>
    /// Before the delete of the <c>workflow</c> row <paramref name="workflowId"/>, in its transaction: when it is an action's definition
    /// with a message, that message and its filters are deleted (no other definition shares it: <see cref="DemandNameAsync"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when a plug-in step is registered on the message: the delete is refused.</exception>
    public async Task OnWorkflowDeletingAsync(DataSession s, Guid workflowId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        Guid messageId;
        string? process;
        await using (var cmd = s.Command(
                         $"SELECT {s.Quote(S.SdkMessageId)}, {s.Quote(S.Name)} FROM {s.DataTable(S.Table)} WHERE {s.Quote(S.WorkflowId)} = @id " +
                         $"AND {s.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND {s.Quote(S.CategoryColumn)} = {(int)S.Category.Action} AND {s.Quote(S.SdkMessageId)} IS NOT NULL"))
        {
            cmd.AddParameter("@id", DbType.Guid, workflowId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            (messageId, process) = (rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1));
        }
        // The key lock of the message's steps is the one a step registration takes (CustomApiAuthoring): nothing on SQL Server.
        await s.KeyLockAsync("sdkmessageprocessingstep", messageId, ct);
        await using (var cmd = s.Command(s.Dialect.Limit(
                         $"SELECT name FROM {s.DataTable("sdkmessageprocessingstep")}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE sdkmessageid = @message ORDER BY name",
                         "1") + s.Dialect.LockClause(RowLock.UpdateRange)))
        {
            cmd.AddParameter("@message", DbType.Guid, messageId);
            if (await cmd.ExecuteScalarAsync(ct) is { } step)
                throw XrmdFaultException.InvalidArgument(
                    $"The action '{process ?? workflowId.ToString()}' cannot be deleted: the plug-in step '{step}' is registered on its message. Delete the step first.");
        }
        if (await NameOfAsync(s, messageId, ct) is null) return;
        var filters = new List<Guid>();
        await using (var cmd = s.Command($"SELECT sdkmessagefilterid FROM {s.DataTable(Filter)} WHERE sdkmessageid = @message"))
        {
            cmd.AddParameter("@message", DbType.Guid, messageId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) filters.Add(rd.GetGuid(0));
        }
        var call = new CoreCall(Caller.Internal, s);
        foreach (var filter in filters) await core.ExecuteCoreAsync(DeleteOf(Filter, filter), call, ct);
        await core.ExecuteCoreAsync(DeleteOf(Message, messageId), call, ct);
    }

    /// <summary>The model Delete of the row <paramref name="id"/> of <paramref name="table"/>.</summary>
    private static Request DeleteOf(string table, Guid id) =>
        new() { Name = "Delete", Parameters = { ["Target"] = new RecordReference { LogicalName = table, Id = id } } };

    private static async Task<string?> NameOfAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT name FROM {s.DataTable(Message)} WHERE sdkmessageid = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => null,
            DBNull => "",
            var name => (string)name,
        };
    }

    private static async Task<bool> ExistsAsync(DataSession s, string table, string key, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT 1 FROM {s.DataTable(table)} WHERE {s.Quote(key)} = @id", "1"));
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
