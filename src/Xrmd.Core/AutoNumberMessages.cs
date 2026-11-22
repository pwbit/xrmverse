using System.Data.Common;
using System.Globalization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Core;

/// <summary>
/// The seed messages of the autonumber columns (autonumber plan, ruling N6), outside the plug-in pipeline like the other
/// metadata messages: <c>SetAutoNumberSeed</c> (<c>EntityName</c>, <c>AttributeName</c>, <c>Value</c>; requires
/// <c>prvWriteAttribute</c>) restarts the column's sequence at <c>Value</c>; <c>GetAutoNumberSeed</c> answers
/// <c>AutoNumberSeedValue</c> (the last seed set, 1000 by default) and <c>GetNextAutoNumberValue</c> answers
/// <c>NextAutoNumberValue</c> (what the next Create takes); the two reads are open to every organization member.
/// </summary>
public sealed class AutoNumberMessages(IMetadataStore store, SecurityGuard guard, IDbConnectionFactory factory)
{
    private const string SetAutoNumberSeed = "SetAutoNumberSeed";
    private const string GetAutoNumberSeed = "GetAutoNumberSeed";
    private const string GetNextAutoNumberValue = "GetNextAutoNumberValue";

    /// <summary>The three messages.</summary>
    public static IReadOnlyList<string> Messages { get; } = [SetAutoNumberSeed, GetAutoNumberSeed, GetNextAutoNumberValue];

    /// <summary>True for one of <see cref="Messages"/> (ignoring case).</summary>
    public static bool IsAutoNumberMessage(string? name) => name is not null && Messages.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="request"/> for <paramref name="caller"/>, in <paramref name="session"/> when given.</summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied; 400 for a missing or invalid parameter, an unknown table or
    /// column, or a column without an AutoNumberFormat.</exception>
    /// <exception cref="NotSupportedException">Not an autonumber message.</exception>
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, Caller caller, DataSession? session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var name = Messages.FirstOrDefault(m => string.Equals(m, request.RequestName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new NotSupportedException($"Request '{request.RequestName}' is not an autonumber message.");
        var attribute = Column(request);
        if (name == SetAutoNumberSeed)
        {
            var value = Value(request);
            await guard.DemandPrivilegeAsync(caller, PrivilegeNames.WriteAttribute, "Attributes", ct);
            await InSessionAsync(session, transaction: true, async s =>
            {
                await using var cmd = s.Command(AutoNumberSequences.ResetSql(attribute, value, s.Dialect));
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            return new SetAutoNumberSeedResponse();
        }

        long seed = 0, next = 0;
        await InSessionAsync(session, transaction: false, async s =>
        {
            await using var cmd = s.Command(AutoNumberSequences.ReadSql(attribute, s.Dialect));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) (seed, next) = (reader.GetInt64(0), reader.GetInt64(1));
            else (seed, next) = (AutoNumberFormat.DefaultSeed, AutoNumberFormat.DefaultSeed); // not created yet (an older store)
        }, ct);
        // The typed responses of the SDK, so that a caller's cast works (the ServiceClient maps them by name the same way).
        OrganizationResponse response = name == GetAutoNumberSeed ? new GetAutoNumberSeedResponse() : new GetNextAutoNumberValueResponse();
        if (name == GetAutoNumberSeed) response.Results["AutoNumberSeedValue"] = seed;
        else response.Results["NextAutoNumberValue"] = next;
        return response;
    }

    /// <summary>The autonumber column the request names.</summary>
    private StringAttributeMetadata Column(OrganizationRequest request)
    {
        var entityName = Text(request, "EntityName");
        var attributeName = Text(request, "AttributeName");
        var entity = store.GetEntity(entityName.ToLowerInvariant())
                     ?? throw XrmdFaultException.InvalidArgument($"Entity '{entityName}' does not exist.");
        var attribute = (entity.Attributes ?? []).FirstOrDefault(a => string.Equals(a.LogicalName, attributeName, StringComparison.OrdinalIgnoreCase))
                        ?? throw XrmdFaultException.InvalidArgument($"Attribute '{attributeName}' does not exist on entity '{entity.LogicalName}'.");
        if (attribute is not StringAttributeMetadata { AutoNumberFormat: { Length: > 0 } } column)
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' is not an autonumber column: it has no AutoNumberFormat.");
        return column;
    }

    private static string Text(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is string { Length: > 0 } text ? text : throw Missing(request, name);

    private static long Value(OrganizationRequest request)
    {
        if (!request.Parameters.TryGetValue("Value", out var raw) || raw is not (long or int)) throw Missing(request, "Value");
        var value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        if (value < 0) throw XrmdFaultException.InvalidArgument($"The parameter 'Value' of request '{request.RequestName}' cannot be negative ({value}).");
        return value;
    }

    private static XrmdFaultException Missing(OrganizationRequest request, string name) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.RequestName}' is missing or of the wrong type.");

    private async Task InSessionAsync(DataSession? session, bool transaction, Func<DataSession, Task> work, CancellationToken ct)
    {
        if (session is not null)
        {
            await work(session);
            return;
        }
        await using var conn = await factory.OpenAsync(ct);
        if (!transaction)
        {
            await work(new DataSession(conn, null));
            return;
        }
        await using var tx = await conn.BeginTransactionAsync(ct);
        var own = new DataSession(conn, tx);
        try
        {
            await work(own);
            await tx.CommitAsync(CancellationToken.None);
            own.Committed();
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // Already rolled back by the server: nothing is committed.
            }
            own.RolledBack();
            throw;
        }
    }
}
