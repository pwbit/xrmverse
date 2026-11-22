using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Schema;
using Xrmd.Security;
using Xrmd.Workflows.Actions;
using Xrmd.Workflows.Custom;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Authoring;

/// <summary>What an apply did, or would do in a dry run, to the process definition (plan 2.21, A14).</summary>
public enum ApplyAction { Created, Updated, Unchanged }

/// <summary>The options of an apply (A15, A18, A19): the solution to put the process in, activate it after the write, or only compute.</summary>
public sealed record ApplyOptions(string? Solution, bool Activate, bool DryRun);

/// <summary>
/// The outcome of an apply: the process (its id and name), what was (or would be) done to its definition, its state after the apply
/// (<c>draft</c> or <c>activated</c>) and whether that changed, the shapes no real export confirmed (A10), and the errors. A non-empty
/// <paramref name="Errors"/> means nothing was written.
/// </summary>
public sealed record ApplyResult(Guid Id, string Name, ApplyAction Action, string State, bool StateChanged, IReadOnlyList<string> AssumedShapes,
    IReadOnlyList<CompileError> Errors);

/// <summary>
/// A process definition as the authoring API lists it: its kind (<c>workflow</c>, <c>businessRule</c>, <c>action</c>), its table, the mode
/// of a workflow (<c>realtime</c>, <c>background</c>; null for the other kinds), its state and the number of steps xrmverse cannot run.
/// </summary>
public sealed record WorkflowSummary(Guid Id, string Name, string Kind, string Entity, string? Mode, string State, int UnsupportedSteps);

/// <summary>
/// Adds an existing process to a solution as the caller, in the caller's transaction (<c>AddSolutionComponent</c>, a message answered
/// outside the pipeline by the metadata operations of <c>Xrmd.Core</c>, which this project cannot reference).
/// </summary>
public interface IAuthoringSolutionComponents
{
    /// <exception cref="XrmdFaultException">403 without <c>prvWriteSolution</c>; 400 for a managed solution; 404 for an unknown one.</exception>
    Task AddWorkflowAsync(Caller caller, string solution, Guid workflowId, DataSession s, CancellationToken ct);
}

/// <summary>
/// Workflow authoring as code (plan 2.21, A13–A15, A18, A19): applies a workflow document to the organization, exports a definition as
/// YAML or XAML, lists the definitions and activates or deactivates one, always as the resolved caller. Every write is a request through
/// the <see cref="PipelineExecutor"/> (its privileges, plug-in steps, real-time workflows and the A16 validation of process writes run as
/// for any client), never a <see cref="WorkflowStore"/> write. An apply demands <c>prvReadWorkflow</c> first (dry runs included). Before
/// anything else is computed, the process the document's id (or its name-based id) names is retrieved as the caller (final review I1):
/// one the caller cannot read refuses the apply with one generic error. The records a lookup literal names, the A13 name clashes and the
/// child workflows are those the caller may read (each retrieved as the caller); the other reads that decide what to write (the
/// stored columns of a readable definition, actions) are made without security, as the writes that follow are checked.
/// </summary>
/// <remarks>
/// <para>An apply compiles the document, resolves its identity (A13: the document's <c>id</c>, else the name-based id; another definition
/// of the same category and table with the same name is refused; the category, table and action message of an existing definition cannot
/// change) and compares the compiled row and XAML with the stored definition, on the columns the compiled row holds (A14):
/// <list type="bullet">
/// <item>a new definition is created Draft (in <see cref="ApplyOptions.Solution"/> when given);</item>
/// <item>a changed one is updated in place, an Activated one by three requests (deactivate, update, activate: Task 5 refuses an Update that
/// changes the state with other columns, and an Update of an Activated definition);</item>
/// <item>an unchanged one is not written at all (no Update, no SetState, no audit row);</item>
/// <item>an existing definition not yet in the solution is added to it (<see cref="IAuthoringSolutionComponents"/>);</item>
/// <item><see cref="ApplyOptions.Activate"/> activates a Draft definition after the write.</item>
/// </list>
/// All of it runs in one transaction (A19): a failure of any request rolls everything back. A dry run computes the same outcome and
/// writes nothing. Errors of the document (compile errors, identity refusals) are returned in <see cref="ApplyResult.Errors"/>; the faults
/// of the requests (403 privileges, 400 refusals) are thrown.</para>
/// <para>The <c>introducedversion</c> is written on Create only (the version a process was introduced in), and the owner is the caller's
/// on Create (the core's default).</para>
/// </remarks>
public sealed class WorkflowAuthoring(IDbConnectionFactory factory, PipelineExecutor executor, IPipelineCore core, IMetadataStore metadata,
    CustomApiRegistry apis, StepRegistry steps, PluginAssemblyLoader loader, WorkflowRegistry workflows, AsyncOperationQueue queue,
    IAuthoringSolutionComponents solutions, SecurityGuard guard, IMigrationLock? migrationLock = null)
{
    /// <summary>The privilege an apply demands first, dry runs included (fix round 1): the compile reads process definitions globally.</summary>
    public const string ReadPrivilege = "prvReadWorkflow";

    public const string DraftState = "draft", ActivatedState = "activated";
    private const int WorkflowComponent = 29;

    /// <summary>The kinds of the YAML by category (A3); the authoring API knows no other category.</summary>
    private static readonly IReadOnlyDictionary<int, string> Kinds = new Dictionary<int, string>
    {
        [(int)S.Category.Workflow] = "workflow", [(int)S.Category.BusinessRule] = "businessRule", [(int)S.Category.Action] = "action",
    };

    // ---- apply (A13, A14, A15, A19) ---------------------------------------------------------------------------------------------

    /// <summary>Applies the workflow document <paramref name="yaml"/> as <paramref name="caller"/> (see the remarks of the class).</summary>
    /// <exception cref="XrmdFaultException">The fault of a request: 403 for a missing privilege (nothing is written), 400 for a refused write.</exception>
    public async Task<ApplyResult> ApplyAsync(Caller caller, string yaml, ApplyOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(options);
        // Migration plan M3 (R4): the MCP tool comes through here, not through the lock middleware; a dry run writes nothing.
        if (!options.DryRun) migrationLock?.Demand();
        // Fix round 1: the compile reads definitions (the A13 name clash, child workflows, actions) without security, so the caller
        // must be able to read processes at all, before anything is compiled, dry run or not.
        await guard.DemandPrivilegeAsync(caller, ReadPrivilege, S.Table, ct);
        await using var conn = await factory.OpenAsync(ct);
        if (options.DryRun) return await ApplyAsync(new DataSession(conn, null), caller, yaml, options, ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        ApplyResult result;
        try
        {
            result = await ApplyAsync(session, caller, yaml, options, ct);
            // The executor invalidates only the transactions it owns: this one is the service's (as HierarchySecuritySettings does).
            session.OnCommitted(() =>
            {
                workflows.Invalidate();
                // An action's activation writes its sdkmessage and filter (plan 2.14, W14).
                steps.Invalidate();
                queue.Signal();
            });
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
        session.Committed();
        return result;
    }

    private async Task<ApplyResult> ApplyAsync(DataSession s, Caller caller, string yaml, ApplyOptions options, CancellationToken ct)
    {
        // Final review I1: the process the document's id (or its name-based id) names is retrieved as the caller before anything else
        // is computed, dry run or not: one the caller cannot read is a single generic refusal, which tells nothing of it.
        var (identified, identifiedName) = Identify(yaml);
        if (!await ReadableAsync(s, caller, identified, ct)) return Unreadable(identified, identifiedName);

        var env = await EnvironmentAsync(s, caller, ct);
        CompileResult compiled;
        try
        {
            compiled = await WorkflowCompiler.CompileAsync(yaml, env, options.Solution, ct);
        }
        catch (WorkflowCompileException ex)
        {
            return await RefusedAsync(s, identified, identifiedName, ex.Errors, ct);
        }

        var row = compiled.Row;
        if (row.Id != identified && !await ReadableAsync(s, caller, row.Id, ct)) return Unreadable(row.Id, row.Name ?? "");
        var existing = await ReadAsync(s, row.Id, lockRow: !options.DryRun, ct);
        var errors = existing is null ? await NameClashesAsync(s, caller, row, ct) : Unchangeable(existing, row);
        if (errors.Count > 0) return await RefusedAsync(s, row.Id, row.Name ?? "", errors, ct);

        var changes = existing is null ? null : Changes(existing, row);
        var action = existing is null ? ApplyAction.Created : changes!.Count == 0 ? ApplyAction.Unchanged : ApplyAction.Updated;
        var wasActivated = existing?.StateCode == S.ActivatedState;
        var activated = wasActivated || options.Activate;
        var result = new ApplyResult(row.Id, row.Name ?? "", action, activated ? ActivatedState : DraftState, activated != wasActivated,
            compiled.AssumedShapes, []);
        if (options.DryRun) return result;

        var invocation = new InvocationContext(caller, caller.UserId, 1, null, s, Guid.NewGuid());
        if (existing is null)
        {
            var create = new CreateRequest { Target = NewEntity(row) };
            if (options.Solution is { } solution) create.Parameters["SolutionUniqueName"] = solution;
            await executor.ExecuteAsync(create, invocation, ct);
        }
        else
        {
            if (changes!.Count > 0)
            {
                // A14: an Activated definition is deactivated, updated and activated again, as three requests (Task 5).
                if (wasActivated) await SetStateAsync(row.Id, activate: false, invocation, ct);
                var update = new Entity(S.Table, row.Id);
                foreach (var (column, value) in changes) update[column] = value;
                await executor.ExecuteAsync(new UpdateRequest { Target = update }, invocation, ct);
                if (wasActivated) await SetStateAsync(row.Id, activate: true, invocation, ct);
            }
            if (options.Solution is { } solution && !await InSolutionAsync(s, solution, row.Id, ct))
                await solutions.AddWorkflowAsync(caller, solution, row.Id, s, ct);
        }
        if (row.Category == (int)S.Category.Action) await EnsureActionMessageAsync(s, row.Id, ct);
        if (options.Activate && !wasActivated) await SetStateAsync(row.Id, activate: true, invocation, ct);
        return result;
    }

    /// <summary>
    /// Plan 2.21, Task 8 (fix round): an authored action has its message from its write, whatever its state, as the solution import gives
    /// one to an imported Draft action (2.14 W14, <c>SolutionImporter.WriteWorkflowAsync</c>): the same <see cref="ActionMessages.EnsureAsync"/>,
    /// with the prefix of the solution that now holds the process (<see cref="ActionMessages.PrefixAsync"/>, which the compile checked,
    /// A15) and the id derived from the message name, as the activation would create it. So the solution export of a Draft action writes
    /// its <c>SdkMessageId</c> as the real export does, and a call to the message is the 400 of an action that is not activated. Written
    /// in the apply's transaction; a message the action already has is kept (its name cannot change).
    /// </summary>
    private async Task EnsureActionMessageAsync(DataSession s, Guid id, CancellationToken ct)
    {
        if (await ReadAsync(s, id, lockRow: false, ct) is not { Type: (int)S.Type.Definition } stored) return;
        await new ActionMessages(core, metadata).EnsureAsync(s, stored, await ActionMessages.PrefixAsync(s, id, ct), null, ct);
    }

    private Task SetStateAsync(Guid id, bool activate, InvocationContext invocation, CancellationToken ct) =>
        executor.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, id),
            State = new OptionSetValue(activate ? S.ActivatedState : S.DraftState),
            Status = new OptionSetValue(activate ? S.ActivatedStatus : S.DraftStatus),
        }, invocation with { CorrelationId = Guid.NewGuid() }, ct);

    /// <summary>The id and name of a document that does not compile, when it can be read; an empty id and name otherwise.</summary>
    private static (Guid Id, string Name) Identify(string yaml)
    {
        try
        {
            var d = WorkflowYaml.Read(yaml);
            return (d.Id ?? WorkflowIdentity.IdOf(d.Kind, d.Entity, d.Name), d.Name);
        }
        catch (Exception ex) when (ex is SchemaFormatException or ArgumentException)
        {
            return (Guid.Empty, "");
        }
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may read the process <paramref name="id"/>: true when no row has that id (or the id is empty),
    /// else a Retrieve as the caller through the pipeline (final review I1, <see cref="AuthoringEnvironment.CanReadProcessAsync"/>).
    /// </summary>
    private async Task<bool> ReadableAsync(DataSession s, Caller caller, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) return true;
        await using (var cmd = s.Command($"SELECT 1 FROM {s.DataTable(S.Table)} WHERE {s.Quote(S.WorkflowId)} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            if (await cmd.ExecuteScalarAsync(ct) is null) return true;
        }
        return await AuthoringEnvironment.CanReadProcessAsync(executor, caller, s, id, ct);
    }

    /// <summary>The error of an apply whose id names a process the caller cannot read (final review I1): no stored column in it.</summary>
    internal static string UnreadableMessage(Guid id) => $"The process with id {id:D} cannot be read or changed by you.";

    /// <summary>
    /// The refused apply of a process the caller cannot read: the document's id and name, and constants for the rest (no change, Draft,
    /// no assumed shape), so nothing of the stored process (its name, state or category) reaches the result.
    /// </summary>
    private static ApplyResult Unreadable(Guid id, string name) =>
        new(id, name, ApplyAction.Unchanged, DraftState, false, [], [new CompileError(null, UnreadableMessage(id))]);

    /// <summary>A refused apply: nothing is written; the state is the stored definition's (one the caller may read: <see cref="ReadableAsync"/>).</summary>
    private async Task<ApplyResult> RefusedAsync(DataSession s, Guid id, string name, IReadOnlyList<CompileError> errors, CancellationToken ct)
    {
        var existing = id == Guid.Empty ? null : await ReadAsync(s, id, lockRow: false, ct);
        var state = existing?.StateCode == S.ActivatedState ? ActivatedState : DraftState;
        return new ApplyResult(id, name, existing is null ? ApplyAction.Created : ApplyAction.Unchanged, state, false, [], errors);
    }

    /// <summary>
    /// A13: another definition of the same category and table with the same name (and another id) refuses a new one. A clash the caller
    /// may read names its id (final review I1: each is retrieved as the caller); one the caller cannot read refuses without it, only
    /// when no readable clash does.
    /// </summary>
    private async Task<List<CompileError>> NameClashesAsync(DataSession s, Caller caller, WorkflowRow row, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT {s.Quote(S.WorkflowId)}, {s.Quote(S.Name)} FROM {s.DataTable(S.Table)}
            WHERE {s.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND {s.Quote(S.CategoryColumn)} = @category AND {s.Quote(S.PrimaryEntity)} = @entity
              AND {s.Quote(S.Name)} = @name AND {s.Quote(S.WorkflowId)} <> @id
            ORDER BY {s.Quote(S.WorkflowId)}
            """);
        cmd.AddParameter("@category", DbType.Int32, row.Category);
        cmd.AddParameter("@entity", DbType.String, row.PrimaryEntity ?? "", 128);
        cmd.AddParameter("@name", DbType.String, row.Name ?? "", 4000);
        cmd.AddParameter("@id", DbType.Guid, row.Id);
        var clashes = new List<Guid>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                if (string.Equals(rd.IsDBNull(1) ? "" : rd.GetString(1), row.Name, StringComparison.Ordinal))
                    clashes.Add(rd.GetGuid(0));
        }
        var errors = new List<CompileError>();
        var unreadable = false;
        foreach (var other in clashes)
        {
            if (!await AuthoringEnvironment.CanReadProcessAsync(executor, caller, s, other, ct))
            {
                unreadable = true;
                continue;
            }
            errors.Add(new CompileError(null,
                $"A process named '{row.Name}' already exists with id {other:D}; add `id: {other:D}` to the file to update it."));
        }
        if (unreadable && errors.Count == 0)
            errors.Add(new CompileError(null, $"A process named '{row.Name}' already exists; add its id to the file to update it."));
        return errors;
    }

    /// <summary>A13: the category, table and (action) message of an existing definition cannot change; it must be a definition.</summary>
    private static List<CompileError> Unchangeable(WorkflowRow existing, WorkflowRow row)
    {
        var errors = new List<CompileError>();
        var name = existing.Name ?? existing.Id.ToString("D");
        if (existing.Type != (int)S.Type.Definition)
        {
            errors.Add(new CompileError(null, $"The id {row.Id:D} names the process '{name}' of type {existing.Type}: only a definition (type 1) is authored."));
            return errors;
        }
        if (existing.Category != row.Category)
            errors.Add(new CompileError(null,
                $"The process '{name}' ({existing.Id:D}) is of category {existing.Category} ({KindOf(existing.Category)}), not {row.Category} " +
                $"({KindOf(row.Category)}): the category of a process cannot change after it is saved."));
        if (!string.Equals(existing.PrimaryEntity, row.PrimaryEntity, StringComparison.OrdinalIgnoreCase))
            errors.Add(new CompileError(null,
                $"The process '{name}' ({existing.Id:D}) is on the table '{existing.PrimaryEntity}', not '{row.PrimaryEntity}': the table of a " +
                "process cannot change after it is saved."));
        if (existing.Category == (int)S.Category.Action && row.Category == existing.Category
            && !string.Equals(existing.UniqueName, row.UniqueName, StringComparison.Ordinal))
            errors.Add(new CompileError(null,
                $"The action '{name}' ({existing.Id:D}) has the message uniquename '{existing.UniqueName}', not '{row.UniqueName}': the message " +
                "of an action cannot change after it is saved."));
        return errors;
    }

    private static string KindOf(int category) => Kinds.GetValueOrDefault(category) ?? "not authored";

    /// <summary>
    /// The columns an apply writes, with their value in the compiled row: what the YAML holds (A3, A4), the XAML and the constants of the
    /// kind. The category, table, type and uniquename are written on Create only (they cannot change). Option values are
    /// <see cref="OptionSetValue"/>s; an absent value is null.
    /// </summary>
    private static List<(string Column, object? Value)> Columns(WorkflowRow row)
    {
        static OptionSetValue? Option(int? value) => value is { } v ? new OptionSetValue(v) : null;
        return
        [
            (S.Name, row.Name), (S.Description, row.Description), (S.ModeColumn, Option(row.Mode)), (S.ScopeColumn, Option(row.Scope)),
            (S.Xaml, row.Xaml), (S.TriggerOnCreate, row.TriggerOnCreate), (S.TriggerOnDelete, row.TriggerOnDelete),
            (S.TriggerOnUpdateAttributeList, row.TriggerOnUpdateAttributes), (S.CreateStage, Option(row.CreateStage)),
            (S.UpdateStage, Option(row.UpdateStage)), (S.DeleteStage, Option(row.DeleteStage)), (S.RunAsColumn, Option(row.RunAs)),
            (S.ProcessTriggerScope, Option(row.ProcessTriggerScope)), (S.OnDemand, row.OnDemand), (S.Subprocess, row.Subprocess),
            (S.AsyncAutoDelete, row.AsyncAutoDelete), (S.SyncWorkflowLogOnFailure, row.SyncWorkflowLogOnFailure), (S.IsTransacted, row.IsTransacted),
        ];
    }

    /// <summary>The Create's Target: every column of <see cref="Columns"/> that has a value, the identity, the introduced version and the rank.</summary>
    private static Entity NewEntity(WorkflowRow row)
    {
        var entity = new Entity(S.Table, row.Id)
        {
            [S.CategoryColumn] = new OptionSetValue(row.Category),
            [S.TypeColumn] = new OptionSetValue(row.Type),
            [S.PrimaryEntity] = row.PrimaryEntity,
            [S.IntroducedVersion] = row.IntroducedVersion,
            [S.IsCustomizable] = row.IsCustomizable,
            [S.Rank] = row.Rank,
        };
        if (row.UniqueName is { } uniqueName) entity[S.UniqueName] = uniqueName;
        foreach (var (column, value) in Columns(row))
            if (value is not null) entity[column] = value;
        return entity;
    }

    /// <summary>
    /// The columns of <see cref="Columns"/> whose compiled value differs from the stored one (an empty text equals null; the update
    /// trigger columns are compared as a set, ignoring case, spaces and order: <see cref="NormalizedColumns"/>).
    /// </summary>
    private static List<(string Column, object? Value)> Changes(WorkflowRow existing, WorkflowRow row)
    {
        var stored = Columns(existing).ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);
        return Columns(row).Where(c => c.Column == S.TriggerOnUpdateAttributeList
                ? NormalizedColumns(stored[c.Column] as string) != NormalizedColumns(c.Value as string)
                : !Same(stored[c.Column], c.Value))
            .ToList();
    }

    /// <summary>A column list (<c>triggeronupdateattributelist</c>) split at commas, trimmed, lowercased, sorted and joined again.</summary>
    internal static string NormalizedColumns(string? list) => string.Join(",",
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null or "", null or "") => true,
        (OptionSetValue x, OptionSetValue y) => x.Value == y.Value,
        _ => Equals(a, b),
    };

    /// <summary>The stored definition (any row of that id), read without security; with <paramref name="lockRow"/> update-locked to the end of the transaction.</summary>
    private async Task<WorkflowRow?> ReadAsync(DataSession s, Guid id, bool lockRow, CancellationToken ct)
    {
        if (lockRow)
        {
            // The key lock first (nothing on SQL Server), as WorkflowStore takes it before the row lock of a workflow row.
            await s.KeyLockAsync(S.Table, id, ct);
            await using var cmd = s.Command(
                $"SELECT 1 FROM {s.DataTable(S.Table)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE {s.Quote(S.WorkflowId)} = @id{s.Dialect.LockClause(RowLock.UpdateRange)}");
            cmd.AddParameter("@id", DbType.Guid, id);
            if (await cmd.ExecuteScalarAsync(ct) is null) return null;
        }
        return await core.RetrieveImageAsync(new EntityReference(S.Table, id), null, s, ct) is { } entity ? WorkflowStore.FromEntity(entity) : null;
    }

    private static async Task<bool> InSolutionAsync(DataSession s, string solution, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command($"""
            SELECT COUNT(*) FROM {s.DataTable("solutioncomponent")} sc JOIN {s.DataTable("solution")} so ON so.solutionid = sc.solutionid
            WHERE {s.Dialect.TextKey("so.uniquename")} = {s.Dialect.TextKey("@solution")} AND sc.objectid = @id AND sc.componenttype = {WorkflowComponent}
            """);
        cmd.AddParameter("@solution", DbType.String, solution, 256);
        cmd.AddParameter("@id", DbType.Guid, id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
    }

    // ---- export, list, state (A12, A18) -----------------------------------------------------------------------------------------

    /// <summary>
    /// The definition <paramref name="nameOrId"/> as YAML (<paramref name="format"/> <c>yaml</c>: the decompiled canonical document, which
    /// always has its <c>id</c>) or its stored XAML (<c>xaml</c>), read as <paramref name="caller"/> (<c>prvReadWorkflow</c>).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown name or id; 400 for a name of several definitions, an unknown format or a
    /// definition with no YAML form ("not representable", with every reason); 403 without Read.</exception>
    public async Task<string> ExportAsync(Caller caller, string nameOrId, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var yaml = (format ?? "yaml").Trim().ToLowerInvariant() switch
        {
            "yaml" or "" => true,
            "xaml" => false,
            _ => throw XrmdFaultException.InvalidArgument($"The format '{format}' is not known: it is yaml or xaml."),
        };
        await using var conn = await factory.OpenAsync(ct);
        var s = new DataSession(conn, null);
        var row = await DefinitionAsync(s, caller, nameOrId, ct);
        if (!yaml)
            return row.Xaml ?? throw XrmdFaultException.InvalidArgument($"The process '{row.Name}' ({row.Id:D}) has no XAML.");
        return await WorkflowDecompiler.DecompileAsync(row, await EnvironmentAsync(s, caller, ct), ct) switch
        {
            DecompileResult.Representable r => WorkflowYaml.Write(r.Document),
            DecompileResult.NotRepresentable n => throw XrmdFaultException.InvalidArgument(
                $"The process '{row.Name}' ({row.Id:D}) is not representable in YAML: {string.Join(" ", n.Reasons)}"),
            _ => throw new InvalidOperationException("Unknown decompile result."),
        };
    }

    /// <summary>
    /// The definitions of <paramref name="kind"/> (<c>workflow</c>, <c>businessRule</c>, <c>action</c>; null for all three) on
    /// <paramref name="entity"/> (null for any table) that <paramref name="caller"/> may read, by name.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown kind; 403 without <c>prvReadWorkflow</c>.</exception>
    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(Caller caller, string? kind, string? entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        int[] categories = string.IsNullOrWhiteSpace(kind)
            ? [.. Kinds.Keys]
            : [Kinds.FirstOrDefault(k => string.Equals(k.Value, kind.Trim(), StringComparison.OrdinalIgnoreCase)) is { Value: not null } found
                ? found.Key
                : throw XrmdFaultException.InvalidArgument($"The kind '{kind}' is not known: it is workflow, businessRule or action.")];
        var query = new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(S.Name, S.CategoryColumn, S.PrimaryEntity, S.ModeColumn, "statecode", S.Xaml, S.UnsupportedStepsColumn, S.TypeColumn),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression(S.TypeColumn, ConditionOperator.Equal, (int)S.Type.Definition),
                    new ConditionExpression(S.CategoryColumn, ConditionOperator.In, categories.Cast<object>().ToArray()),
                },
            },
            Orders = { new OrderExpression(S.Name, OrderType.Ascending) },
        };
        if (!string.IsNullOrWhiteSpace(entity)) query.Criteria.AddCondition(S.PrimaryEntity, ConditionOperator.Equal, entity.Trim());
        var response = await executor.ExecuteAsync(new RetrieveMultipleRequest { Query = query }, Invocation(caller, null), ct);
        return ((EntityCollection)response.Results["EntityCollection"]).Entities.Select(e => Summary(WorkflowStore.FromEntity(e))).ToList();
    }

    /// <summary>Activates or deactivates the definition <paramref name="nameOrId"/> as <paramref name="caller"/> (A17); nothing when it is already in that state.</summary>
    /// <exception cref="XrmdFaultException">404 for an unknown name or id; 400 for a name of several definitions or a definition that cannot
    /// be activated; 403 for a missing privilege.</exception>
    public async Task<WorkflowSummary> SetStateAsync(Caller caller, string nameOrId, bool activate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        migrationLock?.Demand();
        WorkflowRow row;
        await using (var conn = await factory.OpenAsync(ct))
            row = await DefinitionAsync(new DataSession(conn, null), caller, nameOrId, ct);
        var id = row.Id;
        if ((row.StateCode == S.ActivatedState) == activate) return Summary(row);
        // The executor owns the transaction of a request without a session: it invalidates the registries after its commit.
        await SetStateAsync(id, activate, Invocation(caller, null), ct);
        await using (var conn = await factory.OpenAsync(ct))
            return Summary(await RetrieveAsync(new DataSession(conn, null), caller, id, ct));
    }

    /// <summary>
    /// The definition (<c>type</c> 1) <paramref name="nameOrId"/> names, read as <paramref name="caller"/> through the pipeline (fix
    /// round 1, minor 1): an id is retrieved (Read on that process); a name is looked up by a RetrieveMultiple, so only the definitions
    /// the caller may read are found, and matched exactly (the column's collation ignores case).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 <c>0x80040217</c> when there is none (a process the caller cannot read by its name is
    /// none); 400 when several definitions have the name, listing their ids; 403 without Read on the process an id names.</exception>
    public async Task<WorkflowRow> DefinitionAsync(DataSession s, Caller caller, string nameOrId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(nameOrId)) throw XrmdFaultException.InvalidArgument("A process is named by its name or its id.");
        if (Guid.TryParse(nameOrId, out var id))
        {
            var row = await RetrieveAsync(s, caller, id, ct);
            return row.Type == (int)S.Type.Definition
                ? row
                : throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"No process definition has the id {id:D}.", 404);
        }
        var query = new QueryExpression(S.Table)
        {
            ColumnSet = new ColumnSet(S.Name),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression(S.TypeColumn, ConditionOperator.Equal, (int)S.Type.Definition),
                    new ConditionExpression(S.Name, ConditionOperator.Equal, nameOrId),
                },
            },
            Orders = { new OrderExpression(S.WorkflowId, OrderType.Ascending) },
        };
        var response = await executor.ExecuteAsync(new RetrieveMultipleRequest { Query = query }, Invocation(caller, s), ct);
        var found = ((EntityCollection)response.Results["EntityCollection"]).Entities
            .Where(e => string.Equals(e.GetAttributeValue<string>(S.Name), nameOrId, StringComparison.Ordinal))
            .Select(e => e.Id)
            .ToList();
        return found.Count switch
        {
            0 => throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"No process definition is named '{nameOrId}'.", 404),
            1 => await RetrieveAsync(s, caller, found[0], ct),
            _ => throw XrmdFaultException.InvalidArgument(
                $"{found.Count} process definitions are named '{nameOrId}': name one by its id ({string.Join(", ", found.Select(f => f.ToString("D")))})."),
        };
    }

    /// <summary>The definition, retrieved as <paramref name="caller"/> through the pipeline (Read on it).</summary>
    private async Task<WorkflowRow> RetrieveAsync(DataSession s, Caller caller, Guid id, CancellationToken ct)
    {
        var response = await executor.ExecuteAsync(new RetrieveRequest { Target = new EntityReference(S.Table, id), ColumnSet = new ColumnSet(true) },
            Invocation(caller, s), ct);
        return WorkflowStore.FromEntity((Entity)response.Results["Entity"]);
    }

    private static InvocationContext Invocation(Caller caller, DataSession? s) => new(caller, caller.UserId, 1, null, s, Guid.NewGuid());

    private static WorkflowSummary Summary(WorkflowRow row) => new(row.Id, row.Name ?? "", KindOf(row.Category), row.PrimaryEntity ?? S.NoPrimaryEntity,
        row.Category == (int)S.Category.Workflow ? (row.Mode == 1 ? "realtime" : "background") : null,
        row.StateCode == S.ActivatedState ? ActivatedState : DraftState, UnsupportedStepsOf(row));

    /// <summary>The steps xrmverse cannot run: the lines of <c>xrmverse_unsupportedsteps</c> (written at activation), else the parse's; 1 for XAML that does not parse.</summary>
    private static int UnsupportedStepsOf(WorkflowRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.UnsupportedSteps))
            return row.UnsupportedSteps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (string.IsNullOrEmpty(row.Xaml)) return 0;
        try
        {
            return XamlWorkflowParser.Parse(row.Xaml, row.Category, WorkflowSdk.IsAvailable).Unsupported.Count;
        }
        catch (XamlWorkflowException)
        {
            return 1;
        }
    }

    /// <summary>The compiler's view of the organization in <paramref name="s"/> (<see cref="AuthoringEnvironment"/>).</summary>
    internal async Task<IAuthoringEnvironment> EnvironmentAsync(DataSession s, Caller caller, CancellationToken ct) =>
        await AuthoringEnvironment.CreateAsync(s, caller, executor, metadata, core, apis, steps, loader, ct);
}
