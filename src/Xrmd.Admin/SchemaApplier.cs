using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Schema;

namespace Xrmd.Admin;

/// <summary>
/// The outcome of <see cref="SchemaApplier.ApplyAsync"/>: what the plan does (one line per request), the custom components
/// the document does not mention (never deleted), the differences that cannot be applied, and whether anything was written
/// (false for a dry run and for a plan with rejections).
/// </summary>
public sealed record SchemaApplyResult(IReadOnlyList<string> Changes, IReadOnlyList<string> Extras, IReadOnlyList<string> Rejections, bool Applied);

/// <summary>
/// Exports the org's schema as YAML and applies a schema YAML (<see cref="SchemaDiff"/>) through the Core, as the
/// authenticated caller. The single implementation behind <c>POST /admin/schema/apply</c> and the MCP tool
/// <c>apply_schema</c>.
/// <para>
/// This class does not check that the caller is a System Administrator: the <c>/admin</c> group's
/// <see cref="AdminOnlyFilter"/> does for the endpoints, and the MCP tool must call
/// <see cref="SecurityGuard.IsAdministratorAsync"/> before calling <see cref="ApplyAsync"/> or <see cref="Export"/>. What it
/// does enforce is the Core's own check: every request demands its metadata privilege (<see cref="MetadataOperations"/>).
/// </para>
/// </summary>
public sealed class SchemaApplier(IMetadataStore store, IDbConnectionFactory factory, CallerResolver callers,
    MetadataOperations metadata, PublisherPrefixRule prefixes, ILogger<SchemaApplier> logger, IMigrationLock? migrationLock = null)
{
    /// <summary>The org's custom schema with the publisher prefix <paramref name="publisher"/>, as the schema YAML.</summary>
    /// <exception cref="XrmdFaultException">400 for an empty publisher and for a prefixed component the YAML cannot describe.</exception>
    public string Export(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) throw XrmdFaultException.InvalidArgument("The publisher prefix is required.");
        try
        {
            var (entities, optionSets) = Published();
            return SchemaYaml.Write(SchemaExporter.Export(entities, optionSets, publisher));
        }
        catch (NotSupportedException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>
    /// Plans <paramref name="yaml"/> against the org and executes the plan's requests in one transaction as the caller, in
    /// a dry run too: the transaction is committed only when <paramref name="dryRun"/> is false and nothing was rejected,
    /// otherwise rolled back. A request the Core refuses (a value out of range, a name it does not accept, …) becomes a
    /// rejection naming the component, the same in both modes, so a dry run predicts the apply. The metadata write lock is
    /// taken before the plan is made, so it is made against what the transaction writes to.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for invalid YAML (naming the line and column); 403 for a missing metadata
    /// privilege.</exception>
    public async Task<SchemaApplyResult> ApplyAsync(string yaml, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        // Migration plan M3 (R4): the MCP tool comes through here, not through the lock middleware; a dry run writes nothing.
        if (!dryRun) migrationLock?.Demand();
        SchemaDocument document;
        try
        {
            document = SchemaYaml.Read(yaml);
        }
        catch (SchemaFormatException ex)
        {
            throw XrmdFaultException.InvalidArgument("The schema YAML is not valid " + ex.Message);
        }

        // Before any change (a dry run too): the document's names all carry its publisher prefix, which a publisher must own.
        if (!prefixes.Prefixes.Contains(document.Publisher))
            throw XrmdFaultException.InvalidArgument(
                $"The schema's publisher prefix '{document.Publisher}' is not the prefix of any publisher of this organization "
                + $"(existing prefixes: {string.Join(", ", prefixes.Prefixes.Order(StringComparer.Ordinal).Select(p => $"'{p}'"))}). "
                + "Create the publisher with 'xrmd publisher create' and apply again.");

        var caller = await callers.ResolveAsync(ct);
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        SchemaPlan plan;
        var rejections = new List<string>();
        bool commit;
        try
        {
            await store.BeginWriteAsync(session, ct);
            plan = Plan(document);
            rejections.AddRange(plan.Rejections);
            if (rejections.Count == 0)
            {
                foreach (var request in plan.Changes)
                {
                    try
                    {
                        await metadata.ExecuteAsync(request, caller, session, ct);
                    }
                    catch (Exception ex) when (Refusal(ex) is { } message)
                    {
                        // The first refusal ends the run: later requests may depend on this one.
                        rejections.Add($"{Describe(request)}: {message}");
                        break;
                    }
                }
            }

            commit = !dryRun && rejections.Count == 0 && plan.Changes.Count > 0;
            // CancellationToken.None: once everything is written, a cancelled caller must not leave the commit half done.
            if (commit) await tx.CommitAsync(CancellationToken.None);
            else await RollbackAsync(tx);
        }
        catch
        {
            await RollbackAsync(tx);
            session.RolledBack();
            throw;
        }

        if (commit)
        {
            try
            {
                session.Committed();
            }
            catch (AggregateException ex)
            {
                logger.LogError(ex, "A callback failed after the schema apply was committed.");
            }
        }
        else session.RolledBack();
        return new SchemaApplyResult([.. plan.Changes.Select(Describe)], plan.Extras, rejections, !dryRun && rejections.Count == 0);
    }

    /// <summary>The message of a refusal by the Core that the plan reports as a rejection; null for anything else (403 included).</summary>
    private static string? Refusal(Exception ex) => ex switch
    {
        XrmdValidationException invalid => invalid.Message,
        XrmdFaultException { HttpStatus: >= 400 and < 500 and not 401 and not 403 } fault => fault.Message,
        _ => null,
    };

    private static async Task RollbackAsync(DbTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The transaction is already gone (doomed and rolled back by the server): nothing is committed.
        }
    }

    private SchemaPlan Plan(SchemaDocument document)
    {
        var (entities, optionSets) = Published();
        return SchemaDiff.Compute(document, entities, optionSets);
    }

    /// <summary>The published tables with their columns and relationships, and the global option sets (as RetrieveAllEntities with all filters).</summary>
    private (IReadOnlyList<EntityMetadata> Entities, IReadOnlyList<OptionSetMetadataBase> OptionSets) Published()
    {
        var entities = (EntityMetadata[])MetadataOperations.Retrieve(store, new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.All })
            .Results["EntityMetadata"];
        var optionSets = (OptionSetMetadataBase[])MetadataOperations.Retrieve(store, new RetrieveAllOptionSetsRequest()).Results["OptionSetMetadata"];
        return (entities, optionSets);
    }

    /// <summary>One line for a request of the plan, e.g. <c>create table xv_project</c>, <c>create column xv_project.xv_budget (money)</c>.</summary>
    internal static string Describe(OrganizationRequest request) => request switch
    {
        CreateOptionSetRequest r => $"create choice {r.OptionSet.Name}",
        UpdateOptionSetRequest r => $"update choice {r.OptionSet.Name}",
        InsertOptionValueRequest r => $"add option {r.Value} ({SchemaColumnTypes.Text(r.Label)}) to choice {r.OptionSetName}",
        CreateEntityRequest r => $"create table {r.Entity.LogicalName}",
        UpdateEntityRequest r => $"update table {r.Entity.LogicalName}",
        CreateAttributeRequest r => $"create column {r.EntityName}.{r.Attribute.LogicalName} ({SchemaColumnTypes.TypeNameOf(r.Attribute) ?? r.Attribute.GetType().Name})",
        UpdateAttributeRequest r => $"update column {r.EntityName}.{r.Attribute.LogicalName}",
        CreateOneToManyRequest r => $"create column {r.OneToManyRelationship.ReferencingEntity}.{r.Lookup.LogicalName} (lookup to {r.OneToManyRelationship.ReferencedEntity})",
        CreateCustomerRelationshipsRequest r => $"create column {r.OneToManyRelationships[0].ReferencingEntity}.{r.Lookup.LogicalName} (customer)",
        CreateManyToManyRequest r => $"create many-to-many {r.ManyToManyRelationship.SchemaName}",
        _ => request.RequestName,
    };
}
