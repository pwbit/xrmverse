using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Admin;

/// <summary>The hierarchy security settings as the Admin API shows them.</summary>
public sealed record HierarchySecurityView(bool Enabled, int Depth, IReadOnlyList<string> ExcludedTables);

/// <summary>
/// <c>GET|PUT /admin/hierarchy-security</c> (plan 2.5, task 6). The two <c>organization</c> columns are written through the
/// pipeline as the caller (plug-in steps on <c>organization</c> Update fire, and the System Administrator rule and the value
/// rules of <c>SystemTableRules</c> apply); the
/// excluded tables are replaced by <see cref="SecurityCache.SetHierarchyExclusionsAsync"/>. Both run in one transaction: a
/// refused depth leaves the exclusions as they were.
/// </summary>
public sealed class HierarchySecuritySettings(IDbConnectionFactory factory, PipelineExecutor executor, IMetadataStore store,
    SecurityCache cache, ILogger<HierarchySecuritySettings> logger)
{
    private const string Organization = "organization";

    /// <summary>The settings now (from the security cache).</summary>
    public async Task<HierarchySecurityView> GetAsync(CancellationToken ct) => ViewOf(await cache.GetHierarchySettingsAsync(ct));

    /// <summary>
    /// Writes the given parts (null = leave as is) and returns the settings after the commit.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown table or a value the organization rules refuse; 403 when
    /// the caller is no System Administrator.</exception>
    public async Task<HierarchySecurityView> SetAsync(Caller caller, bool? enabled, int? depth, IReadOnlyList<string>? excludedTables,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var tables = excludedTables?.Select(t => Resolve(t)).ToList();

        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        try
        {
            if (enabled is not null || depth is not null)
            {
                var organization = new Entity(Organization, await OrganizationIdAsync(session, ct));
                if (enabled is { } e) organization["ishierarchicalsecuritymodelenabled"] = e;
                if (depth is { } d) organization["maxdepthforhierarchicalsecuritymodel"] = d;
                // Through the pipeline, as an Update by the Web API or the SDK: the guard, the plug-in steps registered on
                // organization Update and the core operation, in this transaction (the executor does not own it).
                await executor.ExecuteAsync(new UpdateRequest { Target = organization },
                    new InvocationContext(caller, caller.UserId, 1, null, session, Guid.NewGuid()), ct);
                // The executor invalidates only the transaction it owns; this one is ours.
                session.OnCommitted(cache.Invalidate);
            }
            if (tables is not null)
            {
                try
                {
                    await cache.SetHierarchyExclusionsAsync(session, tables, ct);
                }
                catch (ArgumentException ex)
                {
                    throw XrmdFaultException.InvalidArgument(ex.Message);
                }
            }
            // CancellationToken.None: once everything is written, a cancelled caller must not leave the commit half done.
            await tx.CommitAsync(CancellationToken.None);
        }
        catch
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The transaction is already gone (doomed and rolled back by the server): nothing is committed.
            }
            session.RolledBack();
            throw;
        }

        try
        {
            session.Committed();
        }
        catch (AggregateException ex)
        {
            logger.LogError(ex, "A callback failed after the hierarchy security settings were committed.");
        }
        return await GetAsync(ct);
    }

    /// <summary>The logical name of an existing table, in the case the metadata has it; 400 otherwise.</summary>
    private string Resolve(string table) =>
        (string.IsNullOrWhiteSpace(table) ? null : store.GetEntity(table.Trim().ToLowerInvariant()))?.LogicalName
        ?? throw XrmdFaultException.InvalidArgument($"'{table}' is not a table of this organization; it cannot be excluded from hierarchy security.");

    private static async Task<Guid> OrganizationIdAsync(DataSession session, CancellationToken ct)
    {
        await using var cmd = session.Command(session.Dialect.Limit("SELECT organizationid FROM data.organization", "1"));
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : throw new InvalidOperationException("The organization row is missing.");
    }

    private static HierarchySecurityView ViewOf(HierarchySettings s) =>
        new(s.Enabled, s.Depth, s.ExcludedTables.OrderBy(t => t, StringComparer.Ordinal).ToList());
}
