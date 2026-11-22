using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using static Xrmd.Metadata.EnvironmentVariableSchema;

namespace Xrmd.Security;

/// <summary>
/// The write rules of the environment variable tables (#167, ruling E2), run by the core on the final row of a Create or Update
/// (after the plug-ins, in the request's transaction), for every caller, the solution import included.
/// </summary>
public static class EnvironmentVariableRules
{
    /// <summary>
    /// For <c>environmentvariabledefinition</c>: <c>schemaname</c> is given on Create, unique among the definitions without regard to
    /// case, and unchanged by an Update. For <c>environmentvariablevalue</c>: the lookup names an existing definition that has no
    /// other value row (on Create, and on an Update that gives the lookup), and a Create without <c>schemaname</c> takes the
    /// definition's. Other tables: nothing. The rows read are locked (UPDLOCK, HOLDLOCK) until commit, so two concurrent writes
    /// cannot both pass.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static async Task ApplyRowAsync(DataSession s, string entityLogicalName, Entity row, bool isCreate, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(row);
        if (entityLogicalName == Definition) await DefinitionAsync(s, row, isCreate, id, ct);
        else if (entityLogicalName == Value) await ValueAsync(s, row, isCreate, id, ct);
    }

    /// <summary>
    /// The request-level part of E2, run by <see cref="SystemTableRules"/> before the privilege checks (which would answer a lookup
    /// to a missing record with 404): a Create or Update of <c>environmentvariablevalue</c> that gives the lookup must name an
    /// existing definition.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument.</exception>
    public static async Task DemandDefinitionGivenAsync(DataSession s, Entity target, bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(target);
        if (target.LogicalName != Value || (!isCreate && !target.Contains(DefinitionLookup))) return;
        var definitionId = DefinitionId(target);
        if (await StoredDefinitionNameAsync(s, definitionId, ct) is null) throw MissingDefinition(definitionId);
    }

    private static async Task DefinitionAsync(DataSession s, Entity row, bool isCreate, Guid id, CancellationToken ct)
    {
        if (!isCreate && !row.Contains(SchemaName)) return;
        var name = row.GetAttributeValue<string>(SchemaName);
        if (isCreate)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw XrmdFaultException.InvalidArgument($"An environment variable definition needs a {SchemaName}.");
            // The key lock compares names ignoring case, as the read does.
            await s.KeyLockAsync(Definition, name, ct);
            await using var cmd = s.Command(s.Dialect.Limit($"""
                SELECT {s.Quote(SchemaName)} FROM {s.DataTable(Definition)}{s.Dialect.LockHint(RowLock.UpdateRange)}
                WHERE LOWER({s.Quote(SchemaName)}) = LOWER(@_name) AND {s.Quote(Definition + "id")} <> @_id
                """, "1") + s.Dialect.LockClause(RowLock.UpdateRange));
            cmd.AddParameter("@_name", DbType.String, name, 100);
            cmd.AddParameter("@_id", DbType.Guid, id);
            if (await cmd.ExecuteScalarAsync(ct) is string existing)
                throw XrmdFaultException.InvalidArgument(
                    $"An environment variable definition named '{existing}' already exists, so '{name}' cannot be created: schema names are compared without regard to case.");
            return;
        }

        var stored = await StoredDefinitionNameAsync(s, id, ct);
        if (stored is not null && !string.Equals(stored, name, StringComparison.Ordinal))
            throw XrmdFaultException.InvalidArgument(
                $"The {SchemaName} of the environment variable definition '{stored}' cannot change once it is created.");
    }

    private static async Task ValueAsync(DataSession s, Entity row, bool isCreate, Guid id, CancellationToken ct)
    {
        if (!isCreate && !row.Contains(DefinitionLookup)) return;
        var definitionId = DefinitionId(row);
        var name = await StoredDefinitionNameAsync(s, definitionId, ct) ?? throw MissingDefinition(definitionId);

        await s.KeyLockAsync(Value, definitionId, ct);
        await using (var cmd = s.Command($"""
                         SELECT COUNT(*) FROM {s.DataTable(Value)}{s.Dialect.LockHint(RowLock.UpdateRange)}
                         WHERE {s.Quote(DefinitionLookup)} = @_definition AND {s.Quote(Value + "id")} <> @_id
                         """))
        {
            cmd.AddParameter("@_definition", DbType.Guid, definitionId);
            cmd.AddParameter("@_id", DbType.Guid, id);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"The environment variable '{name}' already has a value: change that value row instead of adding another (one value per definition).");
        }

        if (isCreate && string.IsNullOrEmpty(row.GetAttributeValue<string>(SchemaName))) row[SchemaName] = name;
    }

    /// <summary>The definition a value row names; 400 when it names none.</summary>
    private static Guid DefinitionId(Entity row) =>
        DuplicateRuleRules.IdOf(row.Contains(DefinitionLookup) ? row[DefinitionLookup] : null) is { } id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"An environment variable value needs {DefinitionLookup}, the definition it gives a value to.");

    private static XrmdFaultException MissingDefinition(Guid id) =>
        XrmdFaultException.InvalidArgument($"The environment variable definition {id} named by {DefinitionLookup} does not exist.");

    /// <summary>The stored <c>schemaname</c> of a definition (read with UPDLOCK, HOLDLOCK); null when it does not exist.</summary>
    private static async Task<string?> StoredDefinitionNameAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT {s.Quote(SchemaName)} FROM {s.DataTable(Definition)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(Definition + "id")} = @_id{s.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter("@_id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
