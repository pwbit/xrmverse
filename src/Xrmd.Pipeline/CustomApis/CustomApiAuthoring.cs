using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Pipeline.CustomApis;

/// <summary>
/// The rules of a custom API authored outside a solution import (plan 3.1): a Create, Update or Delete of <c>customapi</c>,
/// <c>customapirequestparameter</c> or <c>customapiresponseproperty</c> by a caller other than <see cref="Caller.Internal"/>
/// (the import, restore and cascade delete keep their own rules). The <see cref="PipelineExecutor"/> runs it at stage 20,
/// right before the core operation, in the pipeline transaction, so a refusal or a later failure leaves nothing behind.
/// <list type="bullet">
/// <item>Create of an API: a flag or option left out is stored as the import stores it (false, 0), the definition rules of the import (<see cref="CustomApiRules"/>, C1 and C2, in the import's
/// order), a unique name free across APIs (ignoring case, read with UPDLOCK and HOLDLOCK so concurrent Creates serialize),
/// an existing plug-in type that is not a workflow activity; then the API's <c>sdkmessage</c> and <c>sdkmessagefilter</c>,
/// written as the import writes them, and <c>sdkmessageid</c> set on the target (a caller may not give it).</item>
/// <item>Update of an API: an immutable column may only repeat its stored value (the two table-name columns ignoring case, an
/// empty one meaning none); <c>isprivate</c> is copied to the API's
/// message.</item>
/// <item>Delete of an API: refused while a step is registered on its message; after the core Delete (the parameters and
/// properties go with it, Delete Cascade), its filter and message are deleted.</item>
/// <item>Update of an API delivered by a managed solution (stored <c>ismanaged</c> 1) is refused (ruling R-A); its row
/// Delete is refused by the generic managed rules of the core (plan 2.6).</item>
/// <item>Create of a request parameter or response property: a <c>customapiid</c>, an API not managed, the definition rules of
/// the import for the new field (<see cref="CustomApiRules.FieldProblem"/>) and no other field of that kind with its unique
/// name (ignoring case, <see cref="CustomApiRules.DuplicateFieldProblem"/>); the API row is read with UPDLOCK and HOLDLOCK, so
/// concurrent Creates on one API serialize.</item>
/// <item>Update of a parameter or property: an API not managed; an immutable column may only repeat its stored value.
/// Delete: an API not managed.</item>
/// <item>A Create of any of the three tables with <c>RelatedEntities</c> (deep insert) is refused.</item>
/// </list>
/// Every refusal is a 400 InvalidArgument naming the API (and the parameter or property).
/// </summary>
/// <remarks>
/// Where each check lives: a lookup that must exist, or a column that must not be given, is checked first by
/// <c>SystemTableRules</c> (Xrmd.Security), because the security guard reads every lookup's record before stage 20 (a missing
/// one would be its 404) and the core refuses an unknown column with a generic 400: <c>sdkmessageid</c> on an API (given on a
/// Create, changed on an Update), an unknown <c>plugintypeid</c>, a <c>customapiid</c> naming no API on a parameter or
/// property Create or changed on its Update (and so any Associate or Disassociate over the API's relationships). Every other
/// rule is here; the checks above are repeated here as a second line. A column the table does not have (<c>isoptional</c> on
/// a response property, <c>iscustomizable</c>) is the usual 400 for an unknown attribute, which the organization service
/// gives before any of these; the <see cref="CustomApiRules.FieldProblem"/> check of <c>isoptional</c> stays as a second line.
/// </remarks>
public sealed class CustomApiAuthoring(IPipelineCore core, IMetadataStore store, PublisherPrefixRule prefixes,
    ILogger<CustomApiAuthoring>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    private const string Data = DdlGenerator.DataSchema;
    private const string Message = "sdkmessage";
    private const string Filter = "sdkmessagefilter";
    private const string NoEntity = "none";
    private const string IsManaged = "ismanaged";

    /// <summary>The columns of <c>customapi</c> that cannot change once it is created (Learn).</summary>
    private static readonly string[] ImmutableApiColumns =
    [
        CustomApiTables.UniqueName, CustomApiTables.BindingType, CustomApiTables.BoundEntityLogicalName, CustomApiTables.IsFunction,
        CustomApiTables.AllowedCustomProcessingStepType, CustomApiTables.WorkflowSdkStepEnabled, CustomApiTables.SdkMessageId,
    ];

    /// <summary>The columns of a request parameter that cannot change once it is created (Learn).</summary>
    private static readonly string[] ImmutableParameterColumns =
    [
        CustomApiTables.UniqueName, CustomApiTables.Type, CustomApiTables.IsOptional, CustomApiTables.LogicalEntityName, CustomApiTables.ApiId,
    ];

    /// <summary>The columns of a response property that cannot change once it is created (Learn).</summary>
    private static readonly string[] ImmutablePropertyColumns =
        [CustomApiTables.UniqueName, CustomApiTables.Type, CustomApiTables.LogicalEntityName, CustomApiTables.ApiId];

    /// <summary>
    /// Before the core Create of <paramref name="target"/>: refuses a deep insert on the three tables, checks a new API and
    /// writes its message and filter (on <paramref name="s"/>, in its transaction), then sets <c>sdkmessageid</c> on the target.
    /// Nothing for any other table.
    /// </summary>
    /// <param name="request">The Create request (its target is <paramref name="target"/>).</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API.</exception>
    public async Task BeforeCreateAsync(OrganizationRequest request, Entity target, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        if (!CustomApiTables.All.Contains(target.LogicalName)) return;
        if (target.RelatedEntities.Count > 0)
            throw XrmdFaultException.InvalidArgument(
                $"Creating related records in the same request is not supported for {target.LogicalName}: create the request parameters and response properties with their own Create.");
        if (target.LogicalName == CustomApiTables.Api) await BeforeCreateApiAsync(target, s, ct);
        else await BeforeCreateFieldAsync(target, s, ct);
    }

    /// <summary>
    /// Before the core Update of <paramref name="target"/>, whose stored row is <paramref name="existing"/>: the API (of a
    /// parameter or property: its API) is not managed; an immutable column may only repeat its stored value (strings compared
    /// ordinally, so a change of case is a change; the two table-name columns, <c>boundentitylogicalname</c> and
    /// <c>logicalentityname</c>, are lower-cased and an empty one is null first, as on Create); a new <c>isprivate</c> of an API
    /// is written to its message; an <c>executeprivilegename</c> that names no privilege is logged as a warning. Nothing for any
    /// other table.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API (and the parameter or property) and the column.</exception>
    public async Task BeforeUpdateAsync(Entity target, Entity existing, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(existing);
        if (target.LogicalName is CustomApiTables.RequestParameter or CustomApiTables.ResponseProperty)
        {
            await BeforeUpdateFieldAsync(target, existing, s, ct);
            return;
        }
        if (target.LogicalName != CustomApiTables.Api) return;
        var name = existing.GetAttributeValue<string>(CustomApiTables.UniqueName) ?? existing.Id.ToString("D");
        if (await IsManagedAsync(existing.Id, s, ct)) throw ManagedApi(name);
        foreach (var column in ImmutableApiColumns)
            if (target.Attributes.TryGetValue(column, out var given) && !SameValue(Canonical(column, given), Canonical(column, ValueOf(existing, column))))
                throw XrmdFaultException.InvalidArgument($"{column} of custom API '{name}' cannot be changed once created.");
        if (target.GetAttributeValue<EntityReference>(CustomApiTables.PluginTypeId) is { } type)
            await DemandPluginTypeAsync(name, "updated", type.Id, s, ct);

        if (target.Attributes.TryGetValue(CustomApiTables.IsPrivate, out var isPrivate)
            && existing.GetAttributeValue<EntityReference>(CustomApiTables.SdkMessageId) is { } message)
            await core.ExecuteCoreAsync(new UpdateRequest { Target = new Entity(Message, message.Id) { ["isprivate"] = isPrivate as bool? ?? false } },
                Caller.Internal, s, ct);
        await WarnUnknownPrivilegeAsync(target, name, s, ct);
    }

    /// <summary>
    /// Before the core Delete of <paramref name="target"/>: an API with a step registered on its message is refused (the step
    /// is read with UPDLOCK and HOLDLOCK, so a step registered concurrently waits for this Delete); a parameter or property of
    /// a managed API is refused. Returns the API's message id, for <see cref="AfterDeleteAsync"/>; null for any other table, a
    /// parameter or property, or an API that does not exist (the core answers 404) or has no message.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API and the step.</exception>
    public async Task<Guid?> BeforeDeleteAsync(EntityReference target, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.LogicalName is CustomApiTables.RequestParameter or CustomApiTables.ResponseProperty)
        {
            if (await FieldApiAsync(target.LogicalName, target.Id, s, ct) is { Managed: true } api) throw ManagedApi(api.Name);
            return null;
        }
        if (target.LogicalName != CustomApiTables.Api) return null;
        // Plan 2.7a, Task 6: the OUTER APPLY (TOP 1 … UPDLOCK, HOLDLOCK) became the read of the API, then the key lock of its
        // message and a locking read of its first step.
        string name;
        Guid messageId;
        await using (var cmd = s.Command($"""
            SELECT c.{CustomApiTables.UniqueName}, c.{CustomApiTables.SdkMessageId}
            FROM {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)} AS c
            WHERE c.{CustomApiTables.ApiId} = @id
            """))
        {
            cmd.AddParameter("@id", DbType.Guid, target.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || rd.IsDBNull(1)) return null;
            name = rd.IsDBNull(0) ? target.Id.ToString("D") : rd.GetString(0);
            messageId = rd.GetGuid(1);
        }
        var d = s.Dialect;
        await s.KeyLockAsync("sdkmessageprocessingstep", messageId, ct);
        await using (var step = s.Command(d.Limit($"""
            SELECT p.name, p.sdkmessageprocessingstepid FROM {s.Quote(Data)}.{s.Quote("sdkmessageprocessingstep")} AS p{d.LockHint(RowLock.UpdateRange)}
            WHERE p.sdkmessageid = @message ORDER BY p.name, p.sdkmessageprocessingstepid
            """, "1") + d.LockClause(RowLock.UpdateRange)))
        {
            step.AddParameter("@message", DbType.Guid, messageId);
            await using var rd = await step.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
                throw XrmdFaultException.InvalidArgument(
                    $"Custom API '{name}' cannot be deleted: the step '{(rd.IsDBNull(0) ? "" : rd.GetString(0))}' ({rd.GetGuid(1)}) is registered on its message. Delete the step first.");
        }
        return messageId;
    }

    /// <summary>After the core Delete of an API: deletes its filters, then its message (<paramref name="messageId"/>).</summary>
    public async Task AfterDeleteAsync(Guid messageId, DataSession s, CancellationToken ct)
    {
        var filters = new List<Guid>();
        await using (var cmd = s.Command($"SELECT sdkmessagefilterid FROM {s.Quote(Data)}.{s.Quote(Filter)} WHERE sdkmessageid = @message"))
        {
            cmd.AddParameter("@message", DbType.Guid, messageId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) filters.Add(rd.GetGuid(0));
        }
        foreach (var filter in filters)
            await core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(Filter, filter) }, Caller.Internal, s, ct);
        await core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(Message, messageId) }, Caller.Internal, s, ct);
    }

    // ---- Create of an API -------------------------------------------------------------------------------------------

    /// <summary>
    /// The checks of a new API, in the import's order (C1, the entity sets, then C2: reserved name, existing message,
    /// characters, publisher prefix), its unique name, its plug-in type (C5); then its message and filter.
    /// </summary>
    private async Task BeforeCreateApiAsync(Entity target, DataSession s, CancellationToken ct)
    {
        var name = target.GetAttributeValue<string>(CustomApiTables.UniqueName);
        if (string.IsNullOrEmpty(name)) throw XrmdFaultException.InvalidArgument("A custom API cannot be created without a uniquename.");
        if (target.Attributes.ContainsKey(CustomApiTables.SdkMessageId))
            throw Refused(name, "sdkmessageid is set by the server, which creates the API's message; leave it out");

        if (target.GetAttributeValue<string>(CustomApiTables.BoundEntityLogicalName) is { } given)
            target[CustomApiTables.BoundEntityLogicalName] = Canonical(CustomApiTables.BoundEntityLogicalName, given);
        // Dataverse stores false and 0 for what is left out; an import writes them too, so an authored API reads back as an imported one.
        foreach (var flag in new[] { CustomApiTables.IsPrivate, CustomApiTables.WorkflowSdkStepEnabled, CustomApiTables.IsFunction })
            if (target.GetAttributeValue<object>(flag) is null) target[flag] = false;
        foreach (var option in new[] { CustomApiTables.BindingType, CustomApiTables.AllowedCustomProcessingStepType })
            if (target.GetAttributeValue<object>(option) is null) target[option] = new OptionSetValue(0);
        var shape = new CustomApiShape(name, Int(target, CustomApiTables.BindingType),
            target.GetAttributeValue<string>(CustomApiTables.BoundEntityLogicalName), Bool(target, CustomApiTables.IsFunction),
            Bool(target, CustomApiTables.WorkflowSdkStepEnabled), Int(target, CustomApiTables.AllowedCustomProcessingStepType), [], []);
        if (CustomApiRules.ApiProblem(shape, table => store.GetEntity(table.ToLowerInvariant()) is not null) is { } problem)
            throw Refused(name, problem);
        var entitySets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in store.GetAllEntities())
            if (entity.EntitySetName is { } set) entitySets[set] = entity.LogicalName;
        if (CustomApiRules.EntitySetProblem(shape, entitySets) is { } setProblem) throw Refused(name, setProblem);

        // An API of that name (ignoring case) first: it passed the name rules, so this is the refusal that says what is wrong.
        await DemandNewNameAsync(name, s, ct);
        await DemandNameAsync(name, s, ct);
        if (target.GetAttributeValue<EntityReference>(CustomApiTables.PluginTypeId) is { } type)
            await DemandPluginTypeAsync(name, Created, type.Id, s, ct);

        var messageId = SdkMessageCatalog.MessageId(name);
        await core.ExecuteCoreAsync(new CreateRequest
        {
            Target = new Entity(Message, messageId)
            {
                ["name"] = name, ["categoryname"] = CustomApiTables.MessageCategory, ["isprivate"] = Bool(target, CustomApiTables.IsPrivate),
            },
        }, Caller.Internal, s, ct);
        var entityName = shape.BoundEntity ?? NoEntity;
        await core.ExecuteCoreAsync(new CreateRequest
        {
            Target = new Entity(Filter, SdkMessageCatalog.FilterId(name, entityName))
            {
                ["name"] = $"{name}:{entityName}",
                ["sdkmessageid"] = new EntityReference(Message, messageId),
                ["primaryobjecttypecode"] = entityName,
                ["secondaryobjecttypecode"] = NoEntity,
                ["isvisible"] = true,
                ["iscustomprocessingstepallowed"] = shape.AllowedStepType != 0,
            },
        }, Caller.Internal, s, ct);
        target[CustomApiTables.SdkMessageId] = new EntityReference(Message, messageId);
        await WarnUnknownPrivilegeAsync(target, name, s, ct);
    }

    /// <summary>
    /// Logs the warning the import logs when the target's <c>executeprivilegename</c> names no privilege of the organization
    /// (nobody holds it, so only System Administrators and internal calls pass). Never a failure.
    /// </summary>
    private async Task WarnUnknownPrivilegeAsync(Entity target, string name, DataSession s, CancellationToken ct)
    {
        if (target.GetAttributeValue<string>(CustomApiTables.ExecutePrivilegeName) is not { Length: > 0 } privilege) return;
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT 1 FROM {s.Quote(Data)}.{s.Quote("privilege")} WHERE {s.Dialect.TextKey("name")} = {s.Dialect.TextKey("@name")}", "1"));
        cmd.AddParameter("@name", DbType.String, privilege, 256);
        if (await cmd.ExecuteScalarAsync(ct) is not null) return;
        _logger.LogWarning(
            "The executeprivilegename '{Privilege}' of custom API '{CustomApi}' names no privilege of the organization: no user holds it, so only System Administrators can call the API.",
            privilege, name);
    }

    /// <summary>
    /// C2, as the import checks it: no message the server handles itself, no <c>sdkmessage</c> of the organization (ignoring
    /// case) other than an API's of that name (which <see cref="DemandNewNameAsync"/> reports), letters, digits and <c>_</c>
    /// only, the prefix of an existing publisher.
    /// </summary>
    private async Task DemandNameAsync(string name, DataSession s, CancellationToken ct)
    {
        if (PipelineExecutor.IsReservedMessageName(core, name))
            throw Refused(name, $"'{name}' is the name of a message xrmverse handles itself");
        await s.KeyLockAsync(Message, name, ct);
        await using (var cmd = s.Command(s.Dialect.Limit($"""
            SELECT m.name FROM {s.Quote(Data)}.{s.Quote(Message)} AS m{s.Dialect.LockHint(RowLock.UpdateRange)}
            WHERE {s.Dialect.TextKey("m.name")} = {s.Dialect.TextKey("@name")} AND NOT EXISTS (SELECT 1 FROM {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)} AS c
                WHERE c.{CustomApiTables.SdkMessageId} = m.sdkmessageid AND {s.Dialect.TextKey($"c.{CustomApiTables.UniqueName}")} = {s.Dialect.TextKey("@name")})
            """, "1") + s.Dialect.LockClause(RowLock.UpdateRange, "m")))
        {
            cmd.AddParameter("@name", DbType.String, name, 256);
            if (await cmd.ExecuteScalarAsync(ct) is string message)
                throw Refused(name, $"the organization already has the message '{message}' (a custom action or another message)");
        }
        if (!CustomApiRules.HasValidNameCharacters(name)) throw Refused(name, "its uniquename may hold letters, digits and '_' only");
        try
        {
            prefixes.Demand(name, "custom API");
        }
        catch (XrmdValidationException ex)
        {
            throw Refused(name, ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>
    /// No API has the unique name yet (ignoring case). Read with UPDLOCK and HOLDLOCK: a concurrent Create of the same name
    /// waits for this transaction and then finds the row.
    /// </summary>
    private static async Task DemandNewNameAsync(string name, DataSession s, CancellationToken ct)
    {
        await s.KeyLockAsync(CustomApiTables.Api, name, ct);
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT {CustomApiTables.UniqueName} FROM {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {CustomApiTables.UniqueName} = @name", "1")
            + s.Dialect.LockClause(RowLock.UpdateRange));
        cmd.AddParameter("@name", DbType.String, name, 256);
        if (await cmd.ExecuteScalarAsync(ct) is string)
            throw XrmdFaultException.InvalidArgument($"Custom API '{name}' already exists.");
    }

    /// <summary>C5: the plug-in type of the main operation exists and is not a workflow activity.</summary>
    /// <param name="verb">What is refused: <c>created</c> or <c>updated</c>.</param>
    private static async Task DemandPluginTypeAsync(string name, string verb, Guid typeId, DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT isworkflowactivity FROM {s.Quote(Data)}.{s.Quote("plugintype")} WHERE plugintypeid = @id");
        cmd.AddParameter("@id", DbType.Guid, typeId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw Refused(name, $"its plug-in type '{typeId}' (plugintypeid) does not exist", verb);
        if (!rd.IsDBNull(0) && rd.GetBoolean(0))
            throw Refused(name, $"its plug-in type '{typeId}' is a workflow activity, not a plug-in", verb);
    }

    // ---- Request parameters and response properties ------------------------------------------------------------------

    /// <summary>A stored API as the field rules read it.</summary>
    private sealed record StoredApi(string Name, bool Managed, CustomApiShape Shape);

    /// <summary>
    /// The checks of a new request parameter or response property: its API (given, existing, not managed, read with UPDLOCK
    /// and HOLDLOCK), its unique name and type, the import's field rules and no duplicate unique name of its kind on the API.
    /// </summary>
    private async Task BeforeCreateFieldAsync(Entity target, DataSession s, CancellationToken ct)
    {
        var request = target.LogicalName == CustomApiTables.RequestParameter;
        var what = request ? "request parameter" : "response property";
        var field = target.GetAttributeValue<string>(CustomApiTables.UniqueName);
        if (string.IsNullOrEmpty(field))
            throw XrmdFaultException.InvalidArgument($"A custom API {what} cannot be created without a uniquename.");
        if (target.GetAttributeValue<EntityReference>(CustomApiTables.ApiId) is not { } apiRef)
            throw XrmdFaultException.InvalidArgument($"Custom API {what} '{field}' cannot be created: customapiid is required.");
        var api = await LoadApiAsync(apiRef.Id, target.LogicalName, s, ct)
            ?? throw XrmdFaultException.InvalidArgument(
                $"Custom API {what} '{field}' cannot be created: its custom API '{apiRef.Id}' (customapiid) does not exist.");
        if (api.Managed) throw ManagedApi(api.Name);
        if (ValueOf(target, CustomApiTables.Type) is null)
            throw FieldRefused(api.Name, what, field, "type is required");
        if (target.GetAttributeValue<string>(CustomApiTables.LogicalEntityName) is { } table)
            target[CustomApiTables.LogicalEntityName] = Canonical(CustomApiTables.LogicalEntityName, table);
        if (request && target.GetAttributeValue<object>(CustomApiTables.IsOptional) is null) target[CustomApiTables.IsOptional] = false;

        var shape = new CustomApiFieldShape(field, Int(target, CustomApiTables.Type),
            target.GetAttributeValue<string>(CustomApiTables.LogicalEntityName),
            target.Attributes.ContainsKey(CustomApiTables.IsOptional));
        var fields = (request ? api.Shape.RequestParameters : api.Shape.ResponseProperties).Append(shape).ToList();
        var withField = request ? api.Shape with { RequestParameters = fields } : api.Shape with { ResponseProperties = fields };
        var problem = CustomApiRules.FieldProblem(withField, shape, request, name => store.GetEntity(name.ToLowerInvariant()) is not null)
            ?? CustomApiRules.DuplicateFieldProblem(withField);
        if (problem is not null) throw FieldRefused(api.Name, what, field, problem);
    }

    /// <summary>
    /// The checks of an Update of a request parameter or response property (<paramref name="existing"/>): its API is not
    /// managed; an immutable column only repeats its stored value.
    /// </summary>
    private async Task BeforeUpdateFieldAsync(Entity target, Entity existing, DataSession s, CancellationToken ct)
    {
        var request = target.LogicalName == CustomApiTables.RequestParameter;
        var field = existing.GetAttributeValue<string>(CustomApiTables.UniqueName) ?? existing.Id.ToString("D");
        var api = await FieldApiAsync(target.LogicalName, existing.Id, s, ct);
        if (api is { } stored && stored.Managed) throw ManagedApi(stored.Name);
        var apiName = api?.Name ?? existing.GetAttributeValue<EntityReference>(CustomApiTables.ApiId)?.Id.ToString("D");
        var subject = $"'{apiName}' {(request ? "request parameter" : "response property")} '{field}'";
        foreach (var column in request ? ImmutableParameterColumns : ImmutablePropertyColumns)
            if (target.Attributes.TryGetValue(column, out var given) && !SameValue(Canonical(column, given), Canonical(column, ValueOf(existing, column))))
                throw XrmdFaultException.InvalidArgument($"{column} of custom API {subject} cannot be changed once created.");
    }

    /// <summary>The unique name and managed flag of the API of a stored parameter or property; null when it has none.</summary>
    private static async Task<(string Name, bool Managed)?> FieldApiAsync(string table, Guid fieldId, DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT a.{CustomApiTables.UniqueName}, a.{IsManaged} FROM {s.Quote(Data)}.{s.Quote(table)} AS f
            JOIN {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)} AS a ON a.{CustomApiTables.ApiId} = f.{CustomApiTables.ApiId}
            WHERE f.{s.Quote(table + "id")} = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, fieldId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return (rd.IsDBNull(0) ? "" : rd.GetString(0), !rd.IsDBNull(1) && rd.GetBoolean(1));
    }

    /// <summary>True when the stored API <paramref name="apiId"/> was delivered by a managed solution.</summary>
    private static async Task<bool> IsManagedAsync(Guid apiId, DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT {IsManaged} FROM {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)} WHERE {CustomApiTables.ApiId} = @id");
        cmd.AddParameter("@id", DbType.Guid, apiId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>
    /// The stored API <paramref name="apiId"/>, read with UPDLOCK and HOLDLOCK, with its fields of <paramref name="fieldTable"/>
    /// (the other kind is left empty: the rules compare a field with its own kind only); null when it does not exist.
    /// </summary>
    private static async Task<StoredApi?> LoadApiAsync(Guid apiId, string fieldTable, DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT {CustomApiTables.UniqueName}, {CustomApiTables.BindingType}, {CustomApiTables.BoundEntityLogicalName}, {CustomApiTables.IsFunction},
                {CustomApiTables.WorkflowSdkStepEnabled}, {CustomApiTables.AllowedCustomProcessingStepType}, {IsManaged}
            FROM {s.Quote(Data)}.{s.Quote(CustomApiTables.Api)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {CustomApiTables.ApiId} = @id{s.Dialect.LockClause(RowLock.UpdateRange)};
            SELECT {CustomApiTables.UniqueName}, {CustomApiTables.Type}, {CustomApiTables.LogicalEntityName}
            FROM {s.Quote(Data)}.{s.Quote(fieldTable)} WHERE {CustomApiTables.ApiId} = @id;
            """);
        cmd.AddParameter("@id", DbType.Guid, apiId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var name = rd.IsDBNull(0) ? apiId.ToString("D") : rd.GetString(0);
        var binding = rd.IsDBNull(1) ? 0 : rd.GetInt32(1);
        var bound = rd.IsDBNull(2) ? null : rd.GetString(2);
        bool Flag(int i) => !rd.IsDBNull(i) && rd.GetBoolean(i);
        var (isFunction, workflow, managed) = (Flag(3), Flag(4), Flag(6));
        var stepType = rd.IsDBNull(5) ? 0 : rd.GetInt32(5);
        await rd.NextResultAsync(ct);
        var fields = new List<CustomApiFieldShape>();
        while (await rd.ReadAsync(ct))
            fields.Add(new CustomApiFieldShape(rd.IsDBNull(0) ? "" : rd.GetString(0), rd.IsDBNull(1) ? 0 : rd.GetInt32(1),
                rd.IsDBNull(2) ? null : rd.GetString(2), false));
        var request = fieldTable == CustomApiTables.RequestParameter;
        return new StoredApi(name, managed,
            new CustomApiShape(name, binding, bound, isFunction, workflow, stepType, request ? fields : [], request ? [] : fields));
    }

    /// <summary>The refusal of a change to a managed API or its parameters and properties (ruling R-A).</summary>
    private static XrmdFaultException ManagedApi(string name) =>
        XrmdFaultException.InvalidArgument($"Custom API '{name}' is managed: change it in its solution and import an update.");

    /// <summary>The refusal of a new parameter or property <paramref name="field"/> of the API <paramref name="api"/>.</summary>
    private static XrmdFaultException FieldRefused(string api, string what, string field, string reason) =>
        XrmdFaultException.InvalidArgument($"Custom API '{api}' cannot take the {what} '{field}': {reason}.");

    // ---- values -----------------------------------------------------------------------------------------------------

    private const string Created = "created";

    /// <summary>A refusal of the API <paramref name="name"/> (<paramref name="verb"/>: created or updated): 400 InvalidArgument.</summary>
    private static XrmdFaultException Refused(string name, string reason, string verb = Created) =>
        XrmdFaultException.InvalidArgument($"Custom API '{name}' cannot be {verb}: {reason}.");

    /// <summary>An option or whole-number value of the target (0 when absent or null).</summary>
    private static int Int(Entity target, string column) => Normalize(ValueOf(target, column)) as int? ?? 0;

    /// <summary>A yes/no value of the target (false when absent or null).</summary>
    private static bool Bool(Entity target, string column) => ValueOf(target, column) as bool? ?? false;

    /// <summary>
    /// True when a given value equals the stored one: a lookup by id (null equals null), a choice by value, a string ordinally
    /// (a change of case is a change, except in the two table-name columns, which <see cref="Canonical"/> lower-cases), a missing
    /// yes/no or choice as its default (false, 0).
    /// </summary>
    private static bool SameValue(object? given, object? stored)
    {
        var (a, b) = (Normalize(given), Normalize(stored));
        if (a is bool || b is bool) return (a as bool? ?? false) == (b as bool? ?? false);
        if (a is int || b is int) return (a as int? ?? 0) == (b as int? ?? 0);
        if (a is string || b is string) return string.Equals(a as string, b as string, StringComparison.Ordinal);
        return Equals(a, b);
    }

    /// <summary>
    /// A value as it is stored and compared: the two table-name columns (<c>boundentitylogicalname</c>, <c>logicalentityname</c>)
    /// are lower case and an empty one is null, as on Create; every other column as it is.
    /// </summary>
    private static object? Canonical(string column, object? value) =>
        column is CustomApiTables.BoundEntityLogicalName or CustomApiTables.LogicalEntityName && value is string text
            ? (text.Length == 0 ? null : text.ToLowerInvariant())
            : value;

    private static object? ValueOf(Entity entity, string column) => entity.Attributes.TryGetValue(column, out var value) ? value : null;

    private static object? Normalize(object? value) => value switch
    {
        EntityReference reference => reference.Id,
        OptionSetValue option => option.Value,
        _ => value,
    };
}
