using System.Data;
using System.Data.Common;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Sandbox;

namespace Xrmd.Pipeline.Registration;

/// <summary>
/// Checks a Create/Update of the plug-in registration entities before the core operation writes it, as Dataverse does,
/// so that a registration that could never run is refused up front (400 <c>InvalidArgument</c>):
/// <list type="bullet">
/// <item><c>pluginassembly</c>: <c>content</c> must be a .NET assembly (read with a <see cref="MetadataReader"/>, no code
/// runs); on Create, missing <c>name</c>/<c>version</c>/<c>culture</c>/<c>publickeytoken</c> are filled from its metadata.</item>
/// <item><c>plugintype</c>: <c>typename</c> must be an <see cref="IPlugin"/> of the assembly, or a custom workflow activity (a
/// class deriving from <c>System.Activities.CodeActivity</c>, read from the metadata; plan 2.14, W17), for which
/// <c>isworkflowactivity</c> is set; a type marked <c>isworkflowactivity</c> must be one.</item>
/// <item><c>sdkmessageprocessingstep</c>: stage 10/20/40, asynchronous only on 40, a filter of its own message,
/// filtering attributes only on Update and only of existing attributes; on the message of a custom API, what its
/// <c>allowedcustomprocessingsteptype</c> allows (plan 15, C4: 0 no step, 1 asynchronous stage 40 only, 2 any); bulk messages
/// only on a standard table's <c>CreateMultiple</c>/<c>UpdateMultiple</c> (#76, ruling B8).</item>
/// <item>No step on a filter of the <c>serviceendpoint</c> table (plan 3.2: its messages are not events).</item>
/// <item>A step on a webhook (<c>eventhandler</c> a <c>serviceendpoint</c>, plan 3.2): no <c>plugintypeid</c> and no
/// <c>configuration</c>.</item>
/// <item><c>sdkmessageprocessingstepimage</c>: no pre-image on Create, no post-image on Delete.</item>
/// <item>No Associate or Disassociate over the relationships of <c>sdkmessageprocessingstep.eventhandler</c>
/// (<see cref="DemandNotEventHandlerAssociate"/>).</item>
/// </list>
/// An Update is checked on the record as it will be (<paramref name="existing"/> overlaid with the target).
/// On an SDK-free server (plan S.2a) a Create or Update of a <c>pluginassembly</c> and a Create of a <c>plugintype</c> are refused
/// (400 <c>FeatureNotAvailable</c>) before any other check: such a server cannot load a plug-in built on the Microsoft SDK.
/// </summary>
public sealed class RegistrationValidator(IMetadataStore store, CustomApiRegistry customApis, PluginAssemblyLoader loader, SdkModeInfo? sdkMode = null)
{
    private const int PreImage = 0;
    private const int PostImage = 1;
    private const int BothImages = 2;

    private static readonly int[] ValidStages = [10, 20, 40];

    /// <summary>
    /// Validates <paramref name="target"/> of <paramref name="entity"/> (any other entity is ignored); on a Create of a
    /// <c>pluginassembly</c> (<paramref name="existing"/> null) adds the missing assembly attributes to it.
    /// </summary>
    /// <param name="entity">The logical name of the target.</param>
    /// <param name="target">The record sent by the caller (Create or Update).</param>
    /// <param name="existing">The stored record for an Update; null for a Create.</param>
    /// <param name="s">The session (and transaction) of the message: lookups are read through it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> with the reason.</exception>
    public async Task ValidateAsync(string entity, Entity target, Entity? existing, DataSession s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(s);
        var name = entity.ToLowerInvariant();
        if (sdkMode?.IsSdkFree == true && (name == "pluginassembly" || name == "plugintype" && existing is null))
            throw new XrmdFaultException(ErrorCodes.FeatureNotAvailable, SdkFreeMessages.PluginAssembly, 400);
        switch (name)
        {
            case "pluginassembly":
                ValidateAssembly(target, existing is null);
                await DemandUniqueAssemblyNameAsync(target, existing, s, ct);
                break;
            case "plugintype":
                await ValidateTypeAsync(target, existing, s, ct);
                break;
            case "sdkmessageprocessingstep":
                await ValidateStepAsync(target, existing, s, ct);
                break;
            case "sdkmessageprocessingstepimage":
                await ValidateImageAsync(target, existing, s, ct);
                break;
        }
    }

    /// <summary>The 1:N relationships of <c>sdkmessageprocessingstep.eventhandler</c> (plan 3.2).</summary>
    private static readonly HashSet<string> EventHandlerRelationships = new(StringComparer.OrdinalIgnoreCase)
    {
        "plugintype_sdkmessageprocessingstep", "serviceendpoint_sdkmessageprocessingstep",
    };

    /// <summary>
    /// Refuses (400) an Associate or Disassociate over a relationship of <c>sdkmessageprocessingstep.eventhandler</c> (plan 3.2):
    /// it would change a step's handler without the step rules of <see cref="ValidateAsync"/>; an Update of <c>eventhandler</c>
    /// goes through them.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c>.</exception>
    public static void DemandNotEventHandlerAssociate(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestName is not { } name
            || !name.Equals("Associate", StringComparison.OrdinalIgnoreCase) && !name.Equals("Disassociate", StringComparison.OrdinalIgnoreCase)
            || !request.Parameters.TryGetValue("Relationship", out var r) || r is not Relationship { SchemaName: { } schema }
            || !EventHandlerRelationships.Contains(schema)) return;
        throw XrmdFaultException.InvalidArgument(
            $"Relationship '{schema}' cannot be used with Associate or Disassociate: update the step's eventhandler instead.");
    }

    private static void ValidateAssembly(Entity target, bool create)
    {
        if (!target.Attributes.TryGetValue("content", out var value) || value is null) return;
        var name = ReadAssemblyName(value as string)
                   ?? throw XrmdFaultException.InvalidArgument("The assembly content is not a valid plug-in assembly.");
        if (!create) return;

        FillIfMissing(target, "name", name.Name);
        FillIfMissing(target, "version", name.Version?.ToString());
        FillIfMissing(target, "culture", string.IsNullOrEmpty(name.CultureName) ? "neutral" : name.CultureName);
        var token = name.GetPublicKeyToken();
        FillIfMissing(target, "publickeytoken", token is { Length: > 0 } ? Convert.ToHexString(token).ToLowerInvariant() : null);
    }

    /// <summary>
    /// A plug-in assembly name is unique in the organization, ignoring case (issue #70): a Create, or an Update that renames,
    /// to the name of another <c>pluginassembly</c> row is 412 <c>DuplicateRecord</c>. The columns follow the server's
    /// collation, which may be case-sensitive, so the names are compared with <c>LOWER</c>; two concurrent registrations of
    /// one name are serialized by a transaction applock on that name (<see cref="NameLockResource"/>), not by locking the table.
    /// </summary>
    private static async Task DemandUniqueAssemblyNameAsync(Entity target, Entity? existing, DataSession s, CancellationToken ct)
    {
        var name = target.GetAttributeValue<string>("name");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (existing is not null && string.Equals(existing.GetAttributeValue<string>("name"), name, StringComparison.OrdinalIgnoreCase)) return;
        if (s.Transaction is not null)
        {
            try
            {
                await s.Dialect.AcquireAppLockAsync(s, NameLockResource(name), session: false, TimeSpan.FromMilliseconds(30_000), ct);
            }
            catch (AppLockException ex)
            {
                throw new InvalidOperationException($"Could not lock the plug-in assembly name '{name}' ({ex.Detail}).", ex);
            }
        }

        var self = existing is null ? Guid.Empty : existing.Id != Guid.Empty ? existing.Id : target.Id;
        await using var cmd = s.Command(
            s.Dialect.Limit($"SELECT pluginassemblyid FROM data.pluginassembly WHERE LOWER(name) = LOWER(@name) AND pluginassemblyid <> @self", "1"));
        cmd.AddParameter("@name", DbType.String, name, 4000);
        cmd.AddParameter("@self", DbType.Guid, self);
        if (await cmd.ExecuteScalarAsync(ct) is Guid other)
            throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                $"A plug-in assembly named '{name}' already exists ({other}): update that assembly instead of registering another one with the same name.",
                412);
    }

    /// <summary>The applock of a plug-in assembly name, lower-cased (a long name by its SHA-256: resources hold 255 characters).</summary>
    private static string NameLockResource(string name)
    {
        var lower = name.ToLowerInvariant();
        return "pluginassembly-name:" + (lower.Length <= 200
            ? lower
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lower))));
    }

    private static void FillIfMissing(Entity target, string attribute, string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(target.GetAttributeValue<string>(attribute))) target[attribute] = value;
    }

    /// <summary>The identity of the assembly in base64 <paramref name="content"/>; null when it is not a .NET assembly.</summary>
    private static System.Reflection.AssemblyName? ReadAssemblyName(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var pe = new PEReader(new MemoryStream(Convert.FromBase64String(content), writable: false));
            if (!pe.HasMetadata) return null;
            var reader = pe.GetMetadataReader();
            return reader.IsAssembly ? reader.GetAssemblyDefinition().GetAssemblyName() : null;
        }
        catch (Exception ex) when (ex is FormatException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task ValidateTypeAsync(Entity target, Entity? existing, DataSession s, CancellationToken ct)
    {
        // An Update that touches none of these keeps a type that was valid when it was registered.
        if (existing is not null && !new[] { "typename", "pluginassemblyid", "isworkflowactivity" }.Any(target.Attributes.ContainsKey))
            return;

        var typeName = Value(target, existing, "typename") as string;
        if (RefId(Value(target, existing, "pluginassemblyid")) is not { } assemblyId || string.IsNullOrWhiteSpace(typeName)) return;

        string? assemblyName = null;
        string? content = null;
        await using (var cmd = s.Command("SELECT name, content FROM data.pluginassembly WHERE pluginassemblyid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, assemblyId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                assemblyName = rd.IsDBNull(0) ? null : rd.GetString(0);
                content = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }

        var bytes = Decode(content);
        // Plan 2.14, W17: a custom workflow activity is recognised from the metadata only (it is not loaded for the check), and
        // marked as one, as Dataverse does whether or not the caller sent the flag.
        if (bytes is not null && PluginAssemblyLoader.IsWorkflowActivityType(bytes, typeName))
        {
            target["isworkflowactivity"] = true;
            return;
        }
        if (target.GetAttributeValue<bool?>("isworkflowactivity") is true)
            throw XrmdFaultException.InvalidArgument(
                $"Plug-in type '{typeName}' is marked as a workflow activity but does not derive from {PluginAssemblyLoader.CodeActivityTypeName}.");

        if (bytes is null || !loader.IsPluginType(bytes, typeName))
            throw XrmdFaultException.InvalidArgument(
                $"Plug-in type '{typeName}' was not found in assembly '{assemblyName ?? assemblyId.ToString()}' or does not implement IPlugin.");
        // A type that was an activity and now names a plug-in is a plug-in.
        if (existing?.GetAttributeValue<bool?>("isworkflowactivity") is true) target["isworkflowactivity"] = false;
    }

    /// <summary>The bytes of a base64 assembly content; null when there is none or it is not base64.</summary>
    private static byte[]? Decode(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        try { return Convert.FromBase64String(content); }
        catch (FormatException) { return null; }
    }

    private async Task ValidateStepAsync(Entity target, Entity? existing, DataSession s, CancellationToken ct)
    {
        ValidateWebhookStep(target, existing);
        var stage = Int(Value(target, existing, "stage"));
        if (stage is { } n && !ValidStages.Contains(n)) throw XrmdFaultException.InvalidArgument($"Invalid stage {n}.");
        if (Int(Value(target, existing, "mode")) == 1 && stage != 40)
            throw XrmdFaultException.InvalidArgument("Asynchronous steps must be registered on stage 40.");

        var messageId = RefId(Value(target, existing, "sdkmessageid"));
        var messageName = messageId is { } m ? await MessageNameAsync(m, s, ct) : null;
        if (messageId is { } apiMessage && messageName is not null)
            await DemandAllowedByCustomApiAsync(apiMessage, messageName, stage, Int(Value(target, existing, "mode")), s, ct);

        string? entity = null;
        var filterGiven = RefId(Value(target, existing, "sdkmessagefilterid")) is not null;
        if (RefId(Value(target, existing, "sdkmessagefilterid")) is { } filterId)
        {
            await using var cmd = s.Command(
                "SELECT sdkmessageid, primaryobjecttypecode FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @id");
            cmd.AddParameter("@id", DbType.Guid, filterId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var filterMessage = rd.IsDBNull(0) ? (Guid?)null : rd.GetGuid(0);
                if (filterMessage != messageId)
                    throw XrmdFaultException.InvalidArgument($"The message filter does not belong to message '{messageName}'.");
                entity = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
        }
        DemandBulkStepAllowed(messageName, entity, filterGiven);

        // Plan 3.2: Learn lists the messages of serviceendpoint as "Is Event: False": no step is registered on the table.
        if (string.Equals(entity, ServiceEndpointSchema.Table, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument("Steps cannot be registered on the serviceendpoint table.");

        var filtering = (Value(target, existing, "filteringattributes") as string)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (filtering.Length == 0) return;
        // #76, ruling B7: an UpdateMultiple step filters too (it fires when any target carries one of the attributes).
        if (!string.Equals(messageName, "Update", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(messageName, "UpdateMultiple", StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument("Filtering attributes are supported only for the Update and UpdateMultiple messages.");

        if (entity is null || store.GetEntity(entity) is not { } metadata) return; // no entity to check against
        foreach (var attribute in filtering)
        {
            if (!metadata.Attributes.Any(a => string.Equals(a.LogicalName, attribute, StringComparison.OrdinalIgnoreCase)))
                throw XrmdFaultException.InvalidArgument($"Attribute '{attribute}' not found on entity '{entity}'.");
        }
    }

    /// <summary>
    /// #76, ruling B8: a step on a bulk message needs a table filter; on an elastic table it is refused (register on the
    /// single-record message); a message that is not an event on the standard table (<c>Upsert</c>, <c>UpsertMultiple</c>,
    /// <c>DeleteMultiple</c>, through a forged filter) is refused too. <c>Upsert</c> without a filter, and on an elastic table, stays.
    /// </summary>
    private void DemandBulkStepAllowed(string? message, string? entity, bool filterGiven)
    {
        if (message is null) return;
        var bulk = SdkMessageCatalog.BulkMessages.FirstOrDefault(m => string.Equals(m, message, StringComparison.OrdinalIgnoreCase));
        var isUpsert = string.Equals(message, "Upsert", StringComparison.OrdinalIgnoreCase);
        if (bulk is null && !isUpsert) return;
        if (bulk is not null && !filterGiven)
            throw XrmdFaultException.InvalidArgument($"Plug-in steps on '{bulk}' must name a table (sdkmessagefilterid).");
        if (entity is null || store.GetEntity(entity) is not { } metadata) return;
        if (ElasticTables.IsElastic(metadata))
        {
            if (bulk is not null)
                throw XrmdFaultException.InvalidArgument(
                    $"Plug-in steps on '{bulk}' are not supported for the elastic table '{entity}': register on the single-record message.");
            return;
        }
        if (!SdkMessageCatalog.IsEventOn(message, metadata))
            throw XrmdFaultException.InvalidArgument(
                $"'{bulk ?? "Upsert"}' is not an event on the standard table '{entity}': register on Create or Update.");
    }

    /// <summary>
    /// Plan 3.2: a step whose <c>eventhandler</c> is a <c>serviceendpoint</c> (a webhook step) names no plug-in type
    /// (<c>plugintypeid</c> null) and has no <c>configuration</c>; the other step rules apply to it as to a plug-in step.
    /// </summary>
    private static void ValidateWebhookStep(Entity target, Entity? existing)
    {
        if (Value(target, existing, "eventhandler") is not EntityReference { LogicalName: ServiceEndpointSchema.Table }) return;
        var name = Value(target, existing, "name") as string;
        if (Value(target, existing, "plugintypeid") is not null)
            throw XrmdFaultException.InvalidArgument($"Step '{name}' names both a plug-in type and a service endpoint.");
        if (Value(target, existing, "configuration") is string { Length: > 0 })
            throw XrmdFaultException.InvalidArgument($"Step '{name}' on a webhook takes no configuration.");
    }

    /// <summary>
    /// Plan 15, C4: a step on the message of a custom API follows its <c>allowedcustomprocessingsteptype</c>: 0 refuses any
    /// step, 1 accepts only asynchronous stage 40 steps, 2 accepts any valid step. The type is read from the
    /// <see cref="CustomApiRegistry"/>; an API it does not have yet (written in this transaction, e.g. by the solution import
    /// that also brings the step) is read in <paramref name="s"/>, only when <paramref name="s"/> has a transaction and the
    /// message is not one of the catalog's. No effect on any other message.
    /// </summary>
    private async Task DemandAllowedByCustomApiAsync(Guid messageId, string messageName, int? stage, int? mode, DataSession s,
        CancellationToken ct)
    {
        // A message of the catalog is never an API's (C2 refuses the name).
        if (SdkMessageCatalog.IsCatalogMessage(messageName)) return;
        int allowed;
        string apiName;
        if (await customApis.FindAsync(messageName, ct) is { } api)
        {
            (allowed, apiName) = (api.AllowedStepType, api.UniqueName);
        }
        else
        {
            // Outside a transaction no API is uncommitted: the registry, which every writer of the API tables invalidates, is complete.
            if (s.Transaction is null) return;
            await using var cmd = s.Command(
                s.Dialect.Limit($"SELECT allowedcustomprocessingsteptype, uniquename FROM data.{CustomApiTables.Api} WHERE sdkmessageid = @id", "1"));
            cmd.AddParameter("@id", DbType.Guid, messageId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            (allowed, apiName) = (rd.IsDBNull(0) ? 0 : rd.GetInt32(0), rd.IsDBNull(1) ? messageName : rd.GetString(1));
        }
        switch (allowed)
        {
            case 0:
                throw XrmdFaultException.InvalidArgument(
                    $"Custom API '{apiName}' accepts no processing step: its allowedcustomprocessingsteptype is 0 (None).");
            case 1 when stage != 40 || mode != 1:
                throw XrmdFaultException.InvalidArgument(
                    $"Custom API '{apiName}' accepts asynchronous steps on stage 40 only: its allowedcustomprocessingsteptype is 1 (Async Only).");
        }
    }

    private static async Task ValidateImageAsync(Entity target, Entity? existing, DataSession s, CancellationToken ct)
    {
        if (Int(Value(target, existing, "imagetype")) is not { } type
            || RefId(Value(target, existing, "sdkmessageprocessingstepid")) is not { } stepId) return;

        string? message;
        int? stage = null, mode = null, handlerType = null;
        await using (var cmd = s.Command("""
            SELECT m.name, st.stage, st.mode, st.eventhandlertype FROM data.sdkmessageprocessingstep st
            JOIN data.sdkmessage m ON m.sdkmessageid = st.sdkmessageid
            WHERE st.sdkmessageprocessingstepid = @id
            """))
        {
            cmd.AddParameter("@id", DbType.Guid, stepId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            message = null;
            if (await rd.ReadAsync(ct))
            {
                message = rd.IsDBNull(0) ? null : rd.GetString(0);
                stage = rd.IsDBNull(1) ? null : Convert.ToInt32(rd.GetValue(1));
                mode = rd.IsDBNull(2) ? null : Convert.ToInt32(rd.GetValue(2));
                handlerType = rd.IsDBNull(3) ? null : Convert.ToInt32(rd.GetValue(3));
            }
        }

        var multiple = message is not null && SdkMessageCatalog.MergePairs.Values.Contains(message, StringComparer.OrdinalIgnoreCase);
        // #76, ruling B6: the remote execution context of an asynchronous or webhook step has no image collections.
        if (multiple && (mode == 1 || handlerType == ServiceEndpointSchema.ObjectTypeCode))
            throw XrmdFaultException.InvalidArgument(
                $"Images on asynchronous or webhook steps of '{message}' are not supported by xrmverse: the remote execution context has no image collections.");

        var invalid = string.Equals(message, "Create", StringComparison.OrdinalIgnoreCase) && type is PreImage or BothImages
                      || string.Equals(message, "CreateMultiple", StringComparison.OrdinalIgnoreCase) && type is PreImage or BothImages
                      || string.Equals(message, "Delete", StringComparison.OrdinalIgnoreCase) && type is PostImage or BothImages
                      || multiple && stage != 40 && type is PostImage or BothImages;
        if (invalid) throw XrmdFaultException.InvalidArgument($"Image type {type} is not valid for message '{message}'.");
    }

    private static async Task<string?> MessageNameAsync(Guid messageId, DataSession s, CancellationToken ct)
    {
        await using var cmd = s.Command("SELECT name FROM data.sdkmessage WHERE sdkmessageid = @id");
        cmd.AddParameter("@id", DbType.Guid, messageId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>The attribute as the record will be: the target's value when it sends one, else the stored one.</summary>
    private static object? Value(Entity target, Entity? existing, string attribute) =>
        target.Attributes.TryGetValue(attribute, out var value) ? value
        : existing is not null && existing.Attributes.TryGetValue(attribute, out var stored) ? stored
        : null;

    private static int? Int(object? value) => value switch
    {
        OptionSetValue o => o.Value,
        int i => i,
        _ => null,
    };

    private static Guid? RefId(object? value) => value switch
    {
        EntityReference r => r.Id,
        Guid g => g,
        _ => null,
    };
}
