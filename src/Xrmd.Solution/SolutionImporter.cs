using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Workflows;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Solution;

/// <summary>The options of an import (the parameters of <c>ImportSolutionRequest</c> xrmverse reads).</summary>
/// <param name="ImportJobId">The <c>importjobid</c> of the log; a new one when null.</param>
/// <param name="OverwriteUnmanagedCustomizations">For a managed file (plan 2.6, ruling M6): the Active markers of its
/// components are removed before it is applied, except where a higher managed layer exists, so the file wins over unmanaged
/// customizations. No effect on an unmanaged file, whose components always replace the unmanaged ones.</param>
/// <param name="PublishWorkflows">Activates every imported process of a category xrmverse runs (0, 2 and 3), whatever the
/// <c>StateCode</c> of the file (plan 2.14, ruling W22; checklist 1h). An activation the validation refuses is a warning of the
/// import job log and leaves the process Draft. Processes of the other categories are kept for export and never run (W2).</param>
/// <param name="HoldingSolution">Import a managed file as the holding solution <c>{name}_Upgrade</c> (ruling M11,
/// <see cref="Layers.ImportKind.Stage"/>).</param>
/// <param name="Upgrade">The import of <c>StageAndUpgrade</c> (ruling M11): a managed file whose solution is installed is staged
/// as with <paramref name="HoldingSolution"/> (and promoted in the same transaction by the caller's
/// <see cref="SolutionImporter.UpgradeStep"/>); one whose solution is not installed is a plain install.</param>
public sealed record ImportOptions(Guid? ImportJobId, bool OverwriteUnmanagedCustomizations, bool PublishWorkflows,
    bool HoldingSolution = false, bool Upgrade = false);

/// <summary>How <see cref="SolutionImporter"/> applies the components of a package (plan 2.6, ruling M5).</summary>
public enum ApplyMode
{
    /// <summary>A solution import: the components, the publisher and solution rows, <c>solutioncomponent</c> and <c>importjob</c>.</summary>
    Import,

    /// <summary>
    /// The restore of layers (<see cref="Layers.LayerRestorer"/>): the components only, as <see cref="Caller.Internal"/>. No
    /// publisher or solution row, no <c>solutioncomponent</c>, no <c>importjob</c>, no Active marker; a table carried only for its
    /// columns, views or forms (not a root component of the package) is left as it is.
    /// </summary>
    Restore,
}

/// <summary>The outcome of a committed import.</summary>
/// <param name="ImportJobId">The <c>importjob</c> written with the log.</param>
/// <param name="SolutionId">The <c>solution</c> row created or updated.</param>
/// <param name="Warnings">What was imported with a difference from Dataverse (also in the log).</param>
public sealed record ImportResult(Guid ImportJobId, Guid SolutionId, IReadOnlyList<string> Warnings);

/// <summary>
/// The synchronous solution import (spec §7): reads the solution file, checks its dependencies and the rules of the system
/// tables before any write, then applies every component in one transaction and logs the job in <c>importjob</c>.
/// </summary>
/// <remarks>
/// <para>Order. Metadata first, as the session rules require (a metadata write after a <c>data.*</c> write could deadlock
/// the store's write lock against the SQL locks): global option sets, entities with their non-lookup attributes (first
/// pass), relationships with their lookups (second pass). Then the rows in the §7 order: publisher, solution, roles with their
/// privileges, views/forms/site maps/apps (opaque XML), web resources, custom controls (plan 3.5 UI-1d: with their files), plug-in assemblies, plug-in types, custom APIs (plan 15:
/// each with its <c>sdkmessage</c> and <c>sdkmessagefilter</c> rows, then its <c>customapi</c> row, its request parameters and
/// response properties), environment variable definitions and their values (#167), processes (plan 2.14: each through the <c>WorkflowStore</c> with its triggers, then activated as the
/// file or <c>PublishWorkflows</c> asks, ruling W22), steps with their images, the <c>solutioncomponent</c> rows and the <c>importjob</c>. The publisher depends on no metadata, so writing it
/// after the metadata changes no dependency.</para>
/// <para>What is not applied. xrmverse generates its own system attributes: of those in the file, the labels, the required
/// level and the state/status options are applied to the generated ones, any other difference is a warning naming the
/// property, and those xrmverse does not generate are warnings. System relationships are skipped. A custom activity
/// (<c>IsActivity</c> 1, plan 2.11 A19) is created as CreateEntity creates one: its activity columns (A2, party lists,
/// <c>activityid</c>) and the relationships of the activity model (regarding, notes, parties) are derived by the server, the
/// file's are not imported, and each difference of the file from them (labels, settings, a relationship named otherwise or
/// marked custom) is a warning; <c>HasRelatedActivities</c> and <c>HasRelatedNotes</c> switch a table on, never off. A re-import updates
/// what the store can update (labels, required level, larger MaxLength, new local options); any other difference of an
/// existing custom attribute or relationship is a 400 naming the property. The entity-level nodes xrmverse does not apply (ribbons,
/// charts, control configurations) and the root nodes of unsupported components are kept in
/// <c>solution.xrmverse_unsupportedxml</c> (for export) and logged as warnings, and so are the system attributes of an
/// unsupported type. A managed file is installed or updated with layers (plan 2.6, <see cref="Layers.ManagedImport"/>): each
/// component gets the solution's Managed layer, and the file is applied to a component only where no layer above it wins
/// (rulings M6 to M8); a patch file is refused (ruling M12). Anything else xrmverse cannot represent
/// fails the import (400): ownership other than user or organization (and other than user for an activity), polymorphic custom lookups other than a
/// Customer column (which must have exactly its two relationships, to account and contact), an unknown message id of a step, an unknown form type.</para>
/// <para>Rows go through the core operations on the import's session (with the <see cref="RegistrationValidator"/> for the
/// plug-in registrations), with no privilege check per component (plan ruling) but with the
/// <see cref="SystemTableRules"/> (reserved role ids, steps impersonating another user), which are checked in the
/// preparation, before the transaction.</para>
/// </remarks>
public sealed class SolutionImporter(IMetadataStore store, IDbConnectionFactory factory, IPipelineCore core,
    SolutionComponentTracker tracker, SecurityAdministration security, SecurityCache securityCache, SecurityGuard guard,
    RegistrationValidator validator, StepRegistry registry, AsyncOperationQueue queue, PublisherPrefixRule prefixes,
    CustomApiRegistry customApis, PipelineExecutor executor, Layers.ManagedImport managed, WorkflowRegistry workflowRegistry,
    ILogger<SolutionImporter> logger, IComputedColumns? computed = null, IRollupColumns? rollups = null)
{
    /// <summary>The processes written as <see cref="Caller.Internal"/> on the import's core (plan 2.14, ruling W3).</summary>
    private readonly WorkflowStore _workflowStore = new(core, workflowRegistry);

    /// <summary>The files of the custom controls (plan 3.5 UI-1d, R2), written in the import's transaction.</summary>
    private readonly ICustomControlFiles _controlFiles = new CustomControlFiles(factory);

    /// <summary>The activation of the imported processes (ruling W22), on <see cref="_workflowStore"/>.</summary>
    private WorkflowActivation WorkflowActivation => _workflowActivation ??= new(_workflowStore, guard, ActionMessages, store);

    private Xrmd.Workflows.Actions.ActionMessages ActionMessages => _actionMessages ??= new(core, store);

    private Xrmd.Workflows.Actions.ActionMessages? _actionMessages;

    private WorkflowActivation? _workflowActivation;

    /// <summary>The roles no solution may define (their roleid is a standard role's template id).</summary>
    private static readonly HashSet<Guid> StandardRoleIds =
        [StandardRoles.SystemAdministrator, StandardRoles.SystemCustomizer, StandardRoles.BasicUser, StandardRoles.Delegate];

    /// <summary>The <c>systemform.type</c> of each <c>forms/@type</c> of an export (not verified on Learn).</summary>
    private static readonly Dictionary<string, int> FormTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = 0, ["main"] = 2, ["mobile"] = 5, ["quick"] = 6, ["quickCreate"] = 7, ["card"] = 11,
    };

    /// <summary>
    /// Imports <paramref name="customizationFile"/> in a transaction of its own (ImportSolution): demands
    /// <c>prvImportCustomization</c>, prepares, applies and commits; then the metadata, security and step caches follow the
    /// commit. On any failure everything is rolled back and the <c>importjob</c> (<c>progress</c> 0, the log with the error)
    /// is written afterwards in a transaction of its own; the failure is rethrown (non-fault input errors as 400).
    /// </summary>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; 400 for an invalid file, a missing
    /// dependency or a component xrmverse cannot represent; the faults of the core operations.</exception>
    public async Task<ImportResult> ImportInOwnTransactionAsync(Caller caller, byte[] customizationFile, ImportOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(customizationFile);
        ArgumentNullException.ThrowIfNull(options);
        // Before any connection of the import: the check opens its own.
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        return await ImportPrivilegedAsync(caller, customizationFile, options, null, ct);
    }

    /// <summary>
    /// The second half of <c>StageAndUpgrade</c> (plan 2.6, ruling M11): promotes the holding solution of
    /// <paramref name="uniqueName"/> in <paramref name="session"/>, the import's transaction, once the stage is applied, and
    /// returns the id of the promoted solution. The promote lives in the Core (it deletes components through the Core's
    /// operations), so its caller hands it to the importer.
    /// </summary>
    public delegate Task<Guid> UpgradeStep(DataSession session, Caller caller, string uniqueName, CancellationToken ct);

    /// <summary>
    /// <c>StageAndUpgrade</c> (plan 2.6, ruling M11): demands <c>prvImportCustomization</c>, then imports
    /// <paramref name="customizationFile"/> as <see cref="ImportInOwnTransactionAsync"/> does with
    /// <see cref="ImportOptions.Upgrade"/>: an installed managed solution is staged as <c>{name}_Upgrade</c> and promoted by
    /// <paramref name="promote"/> in the same transaction; a solution that is not installed is installed.
    /// </summary>
    /// <returns>The import's outcome; its <see cref="ImportResult.SolutionId"/> is the promoted solution's (the holding
    /// solution's id) or the installed one's.</returns>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; the faults of the import and of the
    /// promote.</exception>
    public async Task<ImportResult> StageAndUpgradeAsync(Caller caller, byte[] customizationFile, ImportOptions options, UpgradeStep promote,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(customizationFile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(promote);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        return await ImportPrivilegedAsync(caller, customizationFile, options with { Upgrade = true }, promote, ct);
    }

    /// <summary>
    /// <see cref="ImportInOwnTransactionAsync"/> once <c>prvImportCustomization</c> has been demanded; with
    /// <paramref name="upgrade"/>, a stage is promoted in the same transaction.
    /// </summary>
    private async Task<ImportResult> ImportPrivilegedAsync(Caller caller, byte[] customizationFile, ImportOptions options,
        UpgradeStep? upgrade, CancellationToken ct)
    {
        var jobId = options.ImportJobId ?? Guid.NewGuid();
        var log = new ImportJobLog(DateTime.UtcNow);
        ImportResult result;
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await EnsureNewImportJobAsync(new DataSession(conn, null), jobId, ct);
            var plan = await PrepareAsync(new DataSession(conn, null), caller, customizationFile, options, log, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            var session = new DataSession(conn, tx);
            try
            {
                result = await ApplyAsync(session, caller, plan, options, jobId, log, ct);
                if (upgrade is not null && plan.Kind == Layers.ImportKind.Stage)
                    result = result with { SolutionId = await upgrade(session, caller, plan.Package.Solution.UniqueName, ct) };
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
                ReportRolledBack(session, jobId);
                throw;
            }

            // Committed: the import succeeded whatever a callback does (the store published first; all callbacks ran).
            try
            {
                session.Committed();
            }
            catch (AggregateException ex)
            {
                logger.LogError(ex, "Solution import {ImportJobId} committed, but a commit callback failed.", jobId);
            }
        }
        catch (Exception ex)
        {
            // A cancelled import is logged too (the log is written with CancellationToken.None).
            var fault = AsFault(ex);
            await WriteFailedJobAsync(caller, jobId, log, fault?.ErrorCode ?? ErrorCodes.Unexpected, ex.Message);
            if (fault is null || ReferenceEquals(fault, ex)) throw;
            throw fault;
        }
        return result;
    }

    /// <summary>
    /// Queues an import (ImportSolutionAsync, ExecuteAsync of ImportSolution): demands <c>prvImportCustomization</c> before
    /// any connection, refuses (400) an <c>ImportJobId</c> that an <c>importjob</c> or another queued import already has, writes
    /// the <c>asyncoperation</c> (<see cref="AsyncOperationQueue.EnqueueImportAsync"/>: operationtype 203, the file in
    /// <c>data</c>) and wakes the worker, which runs it as <paramref name="caller"/> (<see cref="ImportQueuedAsync"/>). The file
    /// is not read here, as in Dataverse: an invalid file fails the job, not the request.
    /// </summary>
    /// <remarks><c>OverwriteUnmanagedCustomizations</c> and <c>HoldingSolution</c> travel with the file
    /// (<see cref="OverwritePrefix"/>, <see cref="HoldingPrefix"/>).</remarks>
    /// <returns>The <c>asyncoperationid</c> of the job and the <c>importjobid</c> its run writes.</returns>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; 400 for an import job id in use.</exception>
    public async Task<(Guid AsyncOperationId, Guid ImportJobId)> EnqueueAsync(Caller caller, byte[] customizationFile,
        ImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(customizationFile);
        ArgumentNullException.ThrowIfNull(options);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        return await EnqueuePrivilegedAsync(caller, customizationFile, options, AsyncOperationQueue.ImportSolution,
            AsyncOperationQueue.ImportSolutionMessage, ct);
    }

    /// <summary>
    /// Queues a <c>StageAndUpgradeAsync</c> (or an <c>ExecuteAsync</c> of <c>StageAndUpgrade</c>; plan 2.6, ruling M11):
    /// demands <c>prvImportCustomization</c> and makes the cheap checks only (ruling R15): the file is readable, not a patch,
    /// managed, and its version and the pending holding solution allow the upgrade (<see cref="Layers.ManagedImport.ClassifyAsync"/>).
    /// Ruling R15: the upgrade itself is not run at request time; a data loss (ruling M10) or a dependency (0x8004F01D) fails
    /// the job with the same message, in the job's one transaction, so it changes nothing. Then writes the job
    /// (operationtype 211, the file in <c>data</c>) and wakes the worker, which runs it with <see cref="ImportQueuedAsync"/>
    /// and the promote of the Core.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job and the <c>importjobid</c> its run writes.</returns>
    /// <exception cref="XrmdFaultException">403 without <c>prvImportCustomization</c>; 400 for an unreadable file, a patch, an
    /// unmanaged file, a version or pending upgrade that refuses it, or an import job id in use.</exception>
    public async Task<(Guid AsyncOperationId, Guid ImportJobId)> EnqueueStageAndUpgradeAsync(Caller caller, byte[] customizationFile,
        ImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(customizationFile);
        ArgumentNullException.ThrowIfNull(options);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.ImportCustomization, "solution", ct);
        options = options with { Upgrade = true };
        try
        {
            var package = ReadPackage(customizationFile);
            await using var conn = await factory.OpenAsync(ct);
            await managed.ClassifyAsync(new DataSession(conn, null), package, options, ct);
        }
        catch (Exception ex) when (AsFault(ex) is { } fault && !ReferenceEquals(fault, ex))
        {
            throw fault;
        }
        return await EnqueuePrivilegedAsync(caller, customizationFile, options, AsyncOperationQueue.StageAndUpgrade,
            AsyncOperationQueue.StageAndUpgradeMessage, ct);
    }

    private async Task<(Guid AsyncOperationId, Guid ImportJobId)> EnqueuePrivilegedAsync(Caller caller, byte[] customizationFile,
        ImportOptions options, int operationType, string messageName, CancellationToken ct)
    {
        var jobId = options.ImportJobId ?? Guid.NewGuid();
        Guid asyncOperationId;
        await using (var conn = await factory.OpenAsync(ct))
        {
            // One INSERT: no transaction, nothing to report to the session.
            var session = new DataSession(conn, null);
            await EnsureNewImportJobAsync(session, jobId, ct);
            if (await AsyncOperationQueue.ImportQueuedAsync(session, jobId, ct))
                throw XrmdFaultException.InvalidArgument($"An import job with id {jobId} is already queued.");
            var data = (options.OverwriteUnmanagedCustomizations ? OverwritePrefix : "") + (options.HoldingSolution ? HoldingPrefix : "")
                + (options.PublishWorkflows ? PublishWorkflowsPrefix : "") + Convert.ToBase64String(customizationFile);
            asyncOperationId = await queue.EnqueueImportAsync(session, caller, operationType, messageName, data, jobId, ct);
        }
        queue.Signal();
        return (asyncOperationId, jobId);
    }

    /// <summary>
    /// Runs a queued import as its <paramref name="owner"/> (the worker's side of <see cref="EnqueueAsync"/>): decodes the file
    /// of <paramref name="data"/> (base64), demands <c>prvImportCustomization</c> of the owner (at run time: the owner may have
    /// lost it since the job was queued) and imports as <see cref="ImportInOwnTransactionAsync"/> does. Any failure before the
    /// import starts (no or unreadable file, the privilege, a database error) also writes the failed <c>importjob</c>
    /// (<c>progress</c> 0, the log with the error), so every run that fails has its log; a cancellation does not (the job is
    /// interrupted, not failed by the import). <c>OverwriteUnmanagedCustomizations</c>, <c>HoldingSolution</c> and
    /// <c>PublishWorkflows</c> come with the file (<see cref="OverwritePrefix"/>, <see cref="HoldingPrefix"/>,
    /// <see cref="PublishWorkflowsPrefix"/>). With <paramref name="upgrade"/> (a queued <c>StageAndUpgrade</c>, operationtype
    /// 211), the import is <see cref="StageAndUpgradeAsync"/>'s.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing or unreadable file; 403 without the privilege; the faults of
    /// <see cref="ImportInOwnTransactionAsync"/>.</exception>
    public async Task<ImportResult> ImportQueuedAsync(Caller owner, string? data, Guid importJobId, CancellationToken ct,
        UpgradeStep? upgrade = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        byte[] file;
        var overwrite = data?.StartsWith(OverwritePrefix, StringComparison.Ordinal) == true;
        if (overwrite) data = data![OverwritePrefix.Length..];
        var holding = data?.StartsWith(HoldingPrefix, StringComparison.Ordinal) == true;
        if (holding) data = data![HoldingPrefix.Length..];
        var publish = data?.StartsWith(PublishWorkflowsPrefix, StringComparison.Ordinal) == true;
        if (publish) data = data![PublishWorkflowsPrefix.Length..];
        try
        {
            file = string.IsNullOrEmpty(data)
                ? throw XrmdFaultException.InvalidArgument("The system job carries no solution file (data).")
                : Convert.FromBase64String(data);
            await guard.DemandPrivilegeAsync(owner, PrivilegeNames.ImportCustomization, "solution", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fault = ex switch
            {
                XrmdFaultException f => f,
                FormatException => XrmdFaultException.InvalidArgument("The solution file of the system job (data) is not valid base64."),
                _ => null,
            };
            await WriteFailedJobAsync(owner, importJobId, new ImportJobLog(DateTime.UtcNow), fault?.ErrorCode ?? ErrorCodes.Unexpected,
                fault?.Message ?? ex.Message);
            if (fault is null || ReferenceEquals(fault, ex)) throw;
            throw fault;
        }
        return await ImportPrivilegedAsync(owner, file, new ImportOptions(importJobId, overwrite, publish, holding, Upgrade: upgrade is not null),
            upgrade, ct);
    }

    /// <summary>
    /// What a queued import's <c>data</c> starts with when the request had <c>OverwriteUnmanagedCustomizations</c> true, ahead
    /// of the file as base64 (whose alphabet has no colon).
    /// </summary>
    public const string OverwritePrefix = "overwrite:";

    /// <summary>
    /// What a queued import's <c>data</c> carries (after <see cref="OverwritePrefix"/>, if any) when the request had
    /// <c>HoldingSolution</c> true.
    /// </summary>
    public const string HoldingPrefix = "holding:";

    /// <summary>
    /// What a queued import's <c>data</c> carries (after <see cref="HoldingPrefix"/>, if any) when the request had
    /// <c>PublishWorkflows</c> true (plan 2.14, ruling W22).
    /// </summary>
    public const string PublishWorkflowsPrefix = "publishworkflows:";

    /// <summary>
    /// The largest <c>CustomizationFile</c> an import accepts: 95 MB (Learn, "Import solutions": "The maximum size of a
    /// solution file is 95 MB"; read as 95 × 1024 × 1024 bytes, the unit is not stated). A larger file is 400
    /// <see cref="ErrorCodes.ImportFileTooLargeToUpload"/>.
    /// </summary>
    public const int MaxCustomizationFileBytes = 95 * 1024 * 1024;

    /// <summary>The message of the 400 for a <c>SolutionParameters</c> with a value (see <see cref="ReadRequest"/>).</summary>
    public const string SolutionParametersNotSupported =
        "SolutionParameters is not supported: xrmverse has no staged solution uploads nor solution component options.";

    /// <summary>
    /// The file and options of an <c>ImportSolution</c> request: <c>CustomizationFile</c> (required), <c>ImportJobId</c>,
    /// <c>OverwriteUnmanagedCustomizations</c>, <c>PublishWorkflows</c>, <c>HoldingSolution</c> (what the import does with it
    /// is decided once the file is read, <see cref="Layers.ManagedImport.ClassifyAsync"/>). <c>ConvertToManaged</c> set to true
    /// is refused (400: deprecated, Learn); so are a non-empty <c>ComponentParameters</c> or <c>SolutionParameters</c> (400
    /// naming the parameter), which xrmverse cannot apply. The internal template and layering options (<c>IsTemplateMode</c>
    /// true, a non-empty <c>TemplateSuffix</c> or <c>TemplateDisplayNamePrefix</c>, any <c>LayerDesiredOrder</c>) are refused
    /// too (400 naming the option): xrmverse has no solution templates and places layers by install order only. <c>SkipProductUpdateDependencies</c>, <c>SkipQueueRibbonJob</c> and
    /// <c>AsyncRibbonProcessing</c> are accepted whatever their value: xrmverse has no product-update dependencies and queues
    /// no ribbon job, so skipping either changes nothing.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing file or a refused option; 400
    /// <see cref="ErrorCodes.ImportFileTooLargeToUpload"/> for a file over <see cref="MaxCustomizationFileBytes"/>.</exception>
    public static (byte[] File, ImportOptions Options) ReadRequest(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool Flag(string name) => request.Parameters.TryGetValue(name, out var value) && value is true;
        var file = request.Parameters.TryGetValue("CustomizationFile", out var f) && f is byte[] { Length: > 0 } bytes
            ? bytes
            : throw XrmdFaultException.InvalidArgument(
                $"Required parameter 'CustomizationFile' of request '{request.RequestName}' is missing or empty.");
        if (file.Length > MaxCustomizationFileBytes)
            throw new XrmdFaultException(ErrorCodes.ImportFileTooLargeToUpload, "The import file is too large to upload.", 400);
        if (Flag("ConvertToManaged"))
            throw XrmdFaultException.InvalidArgument(
                "ConvertToManaged is not supported: the option is deprecated, and xrmverse does not convert unmanaged components to managed.");
        if (request.Parameters.TryGetValue("ComponentParameters", out var components) && components is EntityCollection { Entities.Count: > 0 })
            throw XrmdFaultException.InvalidArgument(
                "ComponentParameters is not supported: xrmverse does not override component values (connection references, environment variables) on import.");
        if (request.Parameters.TryGetValue("SolutionParameters", out var solution) && HasValues(solution))
            throw XrmdFaultException.InvalidArgument(SolutionParametersNotSupported);
        if (Flag("IsTemplateMode"))
            throw XrmdFaultException.InvalidArgument("IsTemplateMode is not supported: xrmverse has no solution templates.");
        foreach (var template in new[] { "TemplateSuffix", "TemplateDisplayNamePrefix" })
            if (request.Parameters.TryGetValue(template, out var text) && text is string { Length: > 0 })
                throw XrmdFaultException.InvalidArgument($"{template} is not supported: xrmverse has no solution templates.");
        if (request.Parameters.TryGetValue("LayerDesiredOrder", out var layer) && layer is not null)
            throw XrmdFaultException.InvalidArgument("LayerDesiredOrder is not supported: xrmverse places a managed solution's layers by install order only.");
        Guid? jobId = request.Parameters.TryGetValue("ImportJobId", out var j) && j is Guid id && id != Guid.Empty ? id : null;
        return (file, new ImportOptions(jobId, Flag("OverwriteUnmanagedCustomizations"), Flag("PublishWorkflows"), Flag("HoldingSolution")));
    }

    /// <summary>
    /// True when <paramref name="value"/> is set: any object but one whose public properties (other than
    /// <c>ExtensionData</c>) all hold their default value, such as an empty <c>SolutionParameters</c>.
    /// </summary>
    private static bool HasValues(object? value)
    {
        if (value is null) return false;
        foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.Name == "ExtensionData" || property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
            var v = property.GetValue(value);
            if (v is null) continue;
            var type = v.GetType();
            if (type.IsValueType && v.Equals(Activator.CreateInstance(type))) continue;
            if (v is System.Collections.ICollection { Count: 0 }) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The fault an exception of the import becomes: input errors (<see cref="XrmdValidationException"/> of the store, an
    /// <see cref="ArgumentException"/>) are 400; null for any other (an internal error stays what it is).
    /// </summary>
    private static XrmdFaultException? AsFault(Exception ex) => ex switch
    {
        XrmdFaultException fault => fault,
        ArgumentException => XrmdFaultException.InvalidArgument(ex.Message), // XrmdValidationException included
        _ => null,
    };

    /// <summary>
    /// Reports the rollback to the session; a failing callback (they all run) is logged, so that the exception that caused
    /// the rollback is the one the caller sees.
    /// </summary>
    private void ReportRolledBack(DataSession session, Guid jobId)
    {
        try
        {
            session.RolledBack();
        }
        catch (AggregateException ex)
        {
            logger.LogError(ex, "Solution import {ImportJobId} rolled back, but a rollback callback failed.", jobId);
        }
    }

    /// <summary>
    /// Runs a store create: its <see cref="MetadataComponentExistsException"/> (the component exists) is 412
    /// DuplicateRecord, as MetadataOperations does for its creates; any other exception stays what it is.
    /// </summary>
    private static async Task CreatingAsync(Func<Task> create)
    {
        try
        {
            await create();
        }
        catch (MetadataComponentExistsException ex)
        {
            throw new XrmdFaultException(ErrorCodes.DuplicateRecord, ex.Message, 412);
        }
    }

    private async Task EnsureNewImportJobAsync(DataSession s, Guid jobId, CancellationToken ct)
    {
        if (await ExistsAsync(s, "importjob", jobId, ct))
            throw XrmdFaultException.InvalidArgument($"An import job with id {jobId} already exists.");
    }

    // ---- preparation (no write) -------------------------------------------------------------------------------------

    /// <summary>
    /// A row written by the import: created or updated, logged in a group of the log, tracked as a component. A row that is
    /// not a component of its own (<paramref name="IsComponent"/> false: the message, filter, parameters and properties of a
    /// custom API) is neither logged nor tracked.
    /// </summary>
    /// <param name="ApiFilterEntity">For the <c>sdkmessagefilter</c> of a custom API: its table, or <c>none</c> (lowercase).</param>
    /// <param name="Workflow">For a process (plan 2.14, ruling W22): the process, written through the <see cref="WorkflowStore"/>
    /// with its triggers instead of <paramref name="Target"/> (which carries its name and kept XML only).</param>
    /// <param name="ControlFiles">For a custom control (plan 3.5 UI-1d, R3): the files of its manifest's folder, which replace its
    /// files once the row is written.</param>
    private sealed record RowWrite(Entity Target, bool Create, string Group, string LogId, string? LogName, int ComponentType,
        string? RootKey, bool IsComponent = true, string? ApiFilterEntity = null, PackageWorkflow? Workflow = null,
        IReadOnlyDictionary<string, byte[]>? ControlFiles = null);

    /// <summary>What the preparation decided: the package, the existing rows and the row writes in apply order.</summary>
    /// <param name="FieldPermissions">The permissions each imported field security profile gets (the file's, less those skipped).</param>
    /// <param name="Targets">In a restore, the metadata components it writes (<see cref="RestoreTargets"/>); null in an import.</param>
    /// <param name="Kind">What the import does (plan 2.6, ruling M6); <see cref="Layers.ImportKind.Unmanaged"/> in a restore.</param>
    /// <param name="Kept">In a managed import, the components (by payload key, <see cref="Layers.LayerPayload.Key"/>) whose
    /// file content is not applied because a layer above wins (ruling M7); they are still logged, tracked and layered.</param>
    /// <param name="SolutionState">In an import, the solution rows the decision was made on (<see cref="SolutionStateAsync"/>),
    /// read again under the lock (final review I1).</param>
    /// <param name="PublishWorkflows">Activate every imported process, whatever the state of the file (plan 2.14, ruling W22).</param>
    /// <param name="Prefix">In an import, the prefix of the solution's publisher (the kept one when it exists): an action's message is
    /// named with it (plan 2.14, W14).</param>
    private sealed record Plan(SolutionPackage Package, Guid RootBusinessUnitId, Guid PublisherId, bool PublisherExists,
        Guid SolutionId, bool SolutionExists, List<RowWrite> Rows,
        IReadOnlyDictionary<Guid, IReadOnlyList<PackageFieldPermission>> FieldPermissions, RestoreTargets? Targets = null,
        Layers.ImportKind Kind = Layers.ImportKind.Unmanaged, IReadOnlySet<(int Type, string Key)>? Kept = null,
        string? SolutionState = null, bool PublishWorkflows = false, string? Prefix = null)
    {
        /// <summary>True when the import leaves the component <paramref name="type"/> <paramref name="nameOrId"/> as it is.</summary>
        public bool Keeps(int type, string nameOrId) => Kept is { Count: > 0 } kept && kept.Contains(Layers.LayerPayload.Key(type, nameOrId));

        /// <summary>True when the import leaves the component whose payload key is <paramref name="source"/> as it is.</summary>
        public bool Keeps((int Type, string Key) source) => Kept is { Count: > 0 } kept && kept.Contains(source);

        /// <summary>True for a managed install, update or stage (ruling M6).</summary>
        public bool IsManaged => Kind is Layers.ImportKind.Install or Layers.ImportKind.Update or Layers.ImportKind.Stage;

        /// <summary>The unique name of the solution row the import writes: the file's, or <c>{name}_Upgrade</c> for a stage.</summary>
        public string SolutionName => Kind == Layers.ImportKind.Stage
            ? Package.Solution.UniqueName + Layers.ManagedImport.HoldingSuffix
            : Package.Solution.UniqueName;
    }

    /// <summary>
    /// The metadata components a restore writes (ruling R9): those of the package whose (type, id) the caller requested, by
    /// name (tables; columns as <c>table.column</c>; relationships; global option sets). The others of the package are there
    /// only for the reader (a lookup's relationships, the table of a column, ...) and are not written.
    /// </summary>
    private sealed record RestoreTargets(HashSet<string> Entities, HashSet<string> Attributes, HashSet<string> Relationships,
        HashSet<string> OptionSets);

    /// <summary>
    /// Reads the solution file: a patch is refused (ruling M12), then the package, whose unique name must fit and must not be
    /// a system solution's.
    /// </summary>
    private static SolutionPackage ReadPackage(byte[] file)
    {
        var (solutionXml, customizationsXml, files) = SolutionZip.Read(file);
        // A patch is refused before anything else (plan 2.6, ruling M12): never imported as a plain solution.
        var manifest = solutionXml.Root?.Element("SolutionManifest");
        if (PatchDetector.IsPatch(manifest, (string?)manifest?.Element("UniqueName")))
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{(string?)manifest?.Element("UniqueName")}' is a solution patch: {PatchDetector.NotSupported}.");
        var package = CustomizationsReader.Read(solutionXml, customizationsXml, files);
        var name = package.Solution.UniqueName;
        if (name.Length > SolutionComponentTracker.UniqueNameMaxLength)
            throw XrmdFaultException.InvalidArgument(
                $"The solution unique name '{name}' is longer than {SolutionComponentTracker.UniqueNameMaxLength} characters.");
        if (string.Equals(name, SystemSeed.DefaultSolutionName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, SystemSeed.ActiveSolutionName, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument($"The system solution '{name}' cannot be imported.");
        return package;
    }

    private async Task<Plan> PrepareAsync(DataSession s, Caller caller, byte[] file, ImportOptions options, ImportJobLog log, CancellationToken ct)
    {
        var package = ReadPackage(file);
        log.Solution = package.Solution;
        var name = package.Solution.UniqueName;
        // The rows the decision is made on, before the decision: ApplyAsync reads them again under the lock (final review I1).
        var state = await SolutionStateAsync(s, name, locked: false, ct);
        var kind = await managed.ClassifyAsync(s, package, options, ct);

        if (MicrosoftPublisher.Matches(package.Publisher.UniqueName, package.Publisher.CustomizationPrefix))
        {
            var text = $"The solution '{name}' comes from a Microsoft publisher ('{package.Publisher.UniqueName}', prefix '{package.Publisher.CustomizationPrefix}'). Microsoft first-party solutions are not supported by xrmverse.";
            logger.LogWarning("The solution '{Solution}' comes from a Microsoft publisher ('{Publisher}', prefix '{Prefix}'). Microsoft first-party solutions are not supported by xrmverse.",
                name, package.Publisher.UniqueName, package.Publisher.CustomizationPrefix);
            log.Warn(ImportJobLog.Manifest, name, null, text);
        }

        CheckDependencies(package);
        CheckReimportDifferences(package);
        LogNotApplied(package, log);
        var fieldPermissions = PlanFieldPermissions(package, log);
        await DemandFieldSecurityImportAsync(caller, package, ct);

        var root = await RootBusinessUnitAsync(s, ct);
        Guid? publisherId = null;
        string? keptPrefix = null;
        await using (var cmd = s.Command(
                         s.Dialect.Limit($"SELECT publisherid, customizationprefix, customizationoptionvalueprefix FROM {s.DataTable("publisher")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1")))
        {
            cmd.AddParameter("@name", DbType.String, package.Publisher.UniqueName, 256);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                publisherId = rd.GetGuid(0);
                var prefix = keptPrefix = rd.IsDBNull(1) ? null : rd.GetString(1);
                int? valuePrefix = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                if (!string.Equals(prefix, package.Publisher.CustomizationPrefix, StringComparison.OrdinalIgnoreCase)
                    || valuePrefix != package.Publisher.CustomizationOptionValuePrefix)
                    log.Warn(ImportJobLog.Manifest, name, null,
                        $"Publisher '{package.Publisher.UniqueName}' is kept with its customization prefix '{prefix}' and option value prefix {valuePrefix}; the solution file has '{package.Publisher.CustomizationPrefix}' and {package.Publisher.CustomizationOptionValuePrefix}.");
            }
        }
        Guid? solutionId = null;
        await using (var cmd = s.Command(s.Dialect.Limit($"SELECT solutionid, publisherid FROM {s.DataTable("solution")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1")))
        {
            cmd.AddParameter("@name", DbType.String, name, SolutionComponentTracker.UniqueNameMaxLength);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                solutionId = rd.GetGuid(0);
                var owner = rd.IsDBNull(1) ? (Guid?)null : rd.GetGuid(1);
                if (owner != publisherId)
                    throw XrmdFaultException.InvalidArgument(
                        $"The solution '{name}' already exists with another publisher than '{package.Publisher.UniqueName}'.");
            }
        }

        // An existing publisher is kept as it is: its prefix, not the file's, is the one the import's components may carry.
        CheckPublisherPrefixes(package, keptPrefix ?? package.Publisher.CustomizationPrefix);
        var apiRows = await PlanCustomApisAsync(s, package, keptPrefix ?? package.Publisher.CustomizationPrefix, ct);
        var rows = await PlanRowsAsync(s, package, root, apiRows, keptPrefix ?? package.Publisher.CustomizationPrefix, ct);
        await EnforceSystemTableRulesAsync(s, caller, rows, ct);
        // A stage writes the holding solution {name}_Upgrade, a new row (ruling M11); the base row checked the publisher above.
        if (kind == Layers.ImportKind.Stage) solutionId = null;
        return new Plan(package, root, publisherId ?? Guid.NewGuid(), publisherId is not null, solutionId ?? Guid.NewGuid(),
            solutionId is not null, rows, fieldPermissions, Kind: kind, SolutionState: state, PublishWorkflows: options.PublishWorkflows,
            Prefix: keptPrefix ?? package.Publisher.CustomizationPrefix);
    }

    private static async Task<Guid> RootBusinessUnitAsync(DataSession s, CancellationToken ct) =>
        await ScalarAsync(s, s.Dialect.Limit($"SELECT businessunitid FROM {s.DataTable("businessunit")} WHERE parentbusinessunitid IS NULL ORDER BY createdon", "1"), ct)
            as Guid? ?? throw new InvalidOperationException("The root business unit does not exist.");

    /// <summary>
    /// The preparation of a restore (<see cref="ApplyMode.Restore"/>), on the restore's own session: the dependency check of
    /// an import, the differences of the requested components the store cannot apply (400; a relationship's cascade
    /// configuration is a warning) and the row plan of the requested row components, without the publisher, the solution,
    /// the prefix rule, the custom API name rule or the privilege checks (the layers restored passed them when their solution
    /// was imported; the restore runs as <see cref="Caller.Internal"/>).
    /// </summary>
    private async Task<Plan> PrepareRestoreAsync(DataSession s, SolutionPackage package, IReadOnlySet<(int Type, Guid Id)> requested,
        ImportJobLog log, CancellationToken ct)
    {
        log.Solution = package.Solution;
        CheckDependencies(package);
        var targets = RestoreTargetsOf(package, requested);
        CheckRestoreDifferences(package, targets, log);
        var fieldPermissions = PlanFieldPermissions(package, log);
        var root = await RootBusinessUnitAsync(s, ct);
        var apiRows = await PlanCustomApisAsync(s, package, publisherPrefix: null, ct);
        var rows = RequestedRows(await PlanRowsAsync(s, package, root, apiRows, prefix: null, ct), requested);
        // No publisher or solution in a restore: their ids are unused.
        return new Plan(package, root, Guid.Empty, true, Guid.Empty, true, rows, fieldPermissions, targets);
    }

    /// <summary>
    /// The metadata components of <paramref name="package"/> that <paramref name="requested"/> names, by the id they have in
    /// xrmverse (a global option set's derives from its name).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a requested table, column, relationship or option set that the package
    /// does not hold or xrmverse does not have: a restore writes existing components.</exception>
    private RestoreTargets RestoreTargetsOf(SolutionPackage package, IReadOnlySet<(int Type, Guid Id)> requested)
    {
        var targets = new RestoreTargets(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));
        var matched = new HashSet<(int, Guid)>();
        void Match(int type, Guid? id, HashSet<string> set, string name)
        {
            if (id is not { } value || !requested.Contains((type, value))) return;
            matched.Add((type, value));
            set.Add(name);
        }

        foreach (var optionSet in package.OptionSets.Where(o => o.Name is not null && store.GetOptionSet(o.Name) is not null))
            Match(ComponentType.OptionSet, MetadataStore.OptionSetId(optionSet.Name!), targets.OptionSets, optionSet.Name!);
        foreach (var entity in package.Entities)
        {
            if (store.GetEntity(entity.LogicalName) is not { } published) continue;
            Match(ComponentType.Entity, published.MetadataId, targets.Entities, entity.LogicalName);
            var attributes = (entity.Attributes ?? []).Cast<AttributeMetadata>()
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName));
            foreach (var attribute in attributes)
                Match(ComponentType.Attribute, (published.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName)?.MetadataId,
                    targets.Attributes, $"{entity.LogicalName}.{attribute.LogicalName}");
        }
        foreach (var relationship in package.Relationships)
            Match(ComponentType.EntityRelationship, store.GetRelationship(relationship.SchemaName)?.MetadataId, targets.Relationships,
                relationship.SchemaName);

        int[] metadata = [ComponentType.Entity, ComponentType.Attribute, ComponentType.EntityRelationship, ComponentType.OptionSet];
        foreach (var (type, id) in requested.Where(r => metadata.Contains(r.Type) && !matched.Contains(r)))
            throw XrmdFaultException.InvalidArgument(
                $"The component {id} of type {type} cannot be restored: its layer does not hold it, or xrmverse does not have it.");
        return targets;
    }

    /// <summary>
    /// The differences of the requested components that a restore cannot apply, before any write (as
    /// <see cref="CheckReimportDifferences"/>, for <paramref name="targets"/> only): a table's or custom column's is a 400
    /// naming the component and the property; a custom relationship's cascade configuration is a warning naming it (the
    /// cascade is not reverted, ruling R10), any other difference of it a 400.
    /// </summary>
    private void CheckRestoreDifferences(SolutionPackage package, RestoreTargets targets, ImportJobLog log)
    {
        foreach (var entity in package.Entities)
        {
            if (store.GetEntity(entity.LogicalName) is not { } published) continue;
            if (targets.Entities.Contains(entity.LogicalName) && MetadataDifferences.OfEntity(published, entity) is { Count: > 0 } entityDifferences)
                throw XrmdFaultException.InvalidArgument(
                    $"Entity '{entity.LogicalName}' cannot be restored: {string.Join("; ", entityDifferences)}.");
            var custom = CustomAttributes(entity)
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName && l.IsCustomAttribute == true))
                .Where(a => targets.Attributes.Contains($"{entity.LogicalName}.{a.LogicalName}"));
            foreach (var attribute in custom)
            {
                if ((published.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName) is not { } current) continue;
                if (current.GetType() != attribute.GetType()) continue; // the store's 400 names the type change
                if (MetadataDifferences.OfAttribute(current, attribute, system: false) is { Count: > 0 } differences)
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' cannot be restored: {string.Join("; ", differences)}.");
            }
        }
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true && targets.Relationships.Contains(r.SchemaName)))
        {
            if (store.GetRelationship(relationship.SchemaName) is not { } current) continue;
            var differences = MetadataDifferences.OfRelationship(current, relationship);
            var cascade = differences.Where(d => d.Property.StartsWith("CascadeConfiguration.", StringComparison.Ordinal)).ToList();
            foreach (var difference in cascade)
                log.Warn(ImportJobLog.Relationships, relationship.SchemaName, relationship.SchemaName,
                    $"Relationship '{relationship.SchemaName}' keeps its cascade configuration: a restore does not change it ({difference}).");
            if (differences.Except(cascade).ToList() is { Count: > 0 } other)
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{relationship.SchemaName}' cannot be restored: {string.Join("; ", other)}.");
        }
    }

    /// <summary>
    /// The row writes of the requested row components (ruling R9): a component row whose (type, id) is requested, with what it
    /// owns (an assembly's plug-in types, a step's images, a custom API's message, filter, parameters and properties).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a requested row component that the layers do not hold or xrmverse does not
    /// have: a restore never creates a component.</exception>
    private static List<RowWrite> RequestedRows(List<RowWrite> rows, IReadOnlySet<(int Type, Guid Id)> requested)
    {
        bool Requested(int type, Guid id) => requested.Contains((ComponentType.Normalize(type), id));
        var kept = new List<RowWrite>();
        // The rows of one custom API follow one another, its message first.
        List<RowWrite>? api = null;
        void FlushApi()
        {
            if (api is not null && api.Any(r => r.ComponentType == ComponentType.CustomApi && Requested(r.ComponentType, r.Target.Id)))
                kept.AddRange(api);
            api = null;
        }
        foreach (var row in rows)
        {
            if (IsCustomApiRow(row))
            {
                if (row.Target.LogicalName == ApiMessage) FlushApi();
                (api ??= []).Add(row);
                continue;
            }
            FlushApi();
            var owner = row.ComponentType switch
            {
                ComponentType.PluginType => (ComponentType.PluginAssembly, row.Target.GetAttributeValue<EntityReference>("pluginassemblyid").Id),
                ComponentType.SdkMessageProcessingStepImage =>
                    (ComponentType.SdkMessageProcessingStep, row.Target.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid").Id),
                _ => (row.ComponentType, row.Target.Id),
            };
            if (Requested(owner.Item1, owner.Item2)) kept.Add(row);
        }
        FlushApi();

        // A restore writes existing components: a requested row component the layers do not hold or the organization does not
        // have is refused, as a requested table or column is (RestoreTargetsOf).
        int[] metadata = [ComponentType.Entity, ComponentType.Attribute, ComponentType.EntityRelationship, ComponentType.OptionSet];
        var components = kept.Where(r => r.IsComponent).GroupBy(r => (ComponentType.Normalize(r.ComponentType), r.Target.Id))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var (type, id) in requested.Where(r => !metadata.Contains(r.Type)))
            if (!components.TryGetValue((type, id), out var row) || row.Create)
                throw XrmdFaultException.InvalidArgument(
                    $"The component {id} of type {type} cannot be restored: its layer does not hold it, or xrmverse does not have it.");
        return kept;
    }

    /// <summary>
    /// The minimal dependency check of §7 and the components xrmverse cannot represent, before any write: every entity a
    /// lookup, relationship, view, form or step names and every global option set an attribute uses is in the package or in
    /// the organization (400 naming it otherwise).
    /// </summary>
    private void CheckDependencies(SolutionPackage package)
    {
        var packageEntities = package.Entities.Select(e => e.LogicalName).ToHashSet(StringComparer.Ordinal);
        var packageOptionSets = package.OptionSets.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void EnsureEntity(string? entity, string usedBy)
        {
            if (entity is null || packageEntities.Contains(entity) || store.GetEntity(entity) is not null) return;
            throw XrmdFaultException.InvalidArgument(
                $"The {usedBy} references entity '{entity}', which is neither in the solution nor in the organization.");
        }

        foreach (var entity in package.Entities)
        {
            // The same rule as CreateEntity's (SDK and Web API).
            MetadataRequestRules.DemandSupportedEntity(entity, isNew: store.GetEntity(entity.LogicalName) is null, ownershipRequired: true);
            foreach (var attribute in CustomAttributes(entity))
            {
                if (attribute is EnumAttributeMetadata { OptionSet: { IsGlobal: true, Name: { } global } }
                    && !packageOptionSets.Contains(global) && store.GetOptionSet(global) is null)
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' uses the global option set '{global}', which is neither in the solution nor in the organization.");
            }
        }

        var customOneToMany = package.Relationships.OfType<OneToManyRelationshipMetadata>().Where(r => r.IsCustomRelationship == true).ToList();
        foreach (var lookup in package.Lookups.Where(l => l.IsCustomAttribute == true))
        {
            var where = $"lookup '{lookup.EntityLogicalName}.{lookup.LogicalName}'";
            foreach (var target in lookup.Targets ?? []) EnsureEntity(target, where);
            var ofLookup = customOneToMany.Where(r => r.ReferencingEntity == lookup.EntityLogicalName && r.ReferencingAttribute == lookup.LogicalName).ToList();
            if (CustomerColumns.IsCustomer(lookup))
            {
                // A Customer column is exactly its lookup with one relationship to account and one to contact.
                if (ofLookup.Count != CustomerColumns.Targets.Count || !CustomerColumns.AreCustomerTargets(ofLookup.Select(r => r.ReferencedEntity)))
                    throw XrmdFaultException.InvalidArgument(CustomerColumns.TargetsMessage(
                        $"The {where} (its relationships in the solution: {(ofLookup.Count == 0 ? "none" : string.Join(", ", ofLookup.Select(r => $"'{r.SchemaName}' to '{r.ReferencedEntity}'")))})"));
                continue;
            }
            if (ofLookup.Count > 1 || lookup.Targets is { Length: > 1 })
                throw XrmdFaultException.InvalidArgument(
                    $"The {where} is polymorphic (several relationships): xrmverse imports a polymorphic lookup only as a Customer column (account and contact).");
        }
        foreach (var relationship in customOneToMany)
        {
            EnsureEntity(relationship.ReferencedEntity, $"relationship '{relationship.SchemaName}'");
            EnsureEntity(relationship.ReferencingEntity, $"relationship '{relationship.SchemaName}'");
            if (!package.Lookups.Any(l => l.EntityLogicalName == relationship.ReferencingEntity && l.LogicalName == relationship.ReferencingAttribute))
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{relationship.SchemaName}' has no lookup '{relationship.ReferencingEntity}.{relationship.ReferencingAttribute}' in the solution.");
        }
        CheckHierarchicalRelationships(customOneToMany);
        foreach (var relationship in package.Relationships.OfType<ManyToManyRelationshipMetadata>().Where(r => r.IsCustomRelationship == true))
        {
            EnsureEntity(relationship.Entity1LogicalName, $"relationship '{relationship.SchemaName}'");
            EnsureEntity(relationship.Entity2LogicalName, $"relationship '{relationship.SchemaName}'");
        }
        foreach (var opaque in package.Opaque) EnsureEntity(opaque.EntityLogicalName, $"component {opaque.Id} of type {opaque.Type}");
        foreach (var step in package.Steps) EnsureEntity(step.PrimaryEntity, $"step '{step.Name}'");
    }

    /// <summary>
    /// The rules of CreateOneToMany for the hierarchical relationships of the solution (plan 2.5, ruling H11), before any write:
    /// a hierarchical relationship references its own table, and a table has at most one, counting the one the organization
    /// already has under another name.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the relationships.</exception>
    private void CheckHierarchicalRelationships(IReadOnlyList<OneToManyRelationshipMetadata> customOneToMany)
    {
        var hierarchical = customOneToMany.Where(r => r.IsHierarchical == true).ToList();
        foreach (var r in hierarchical.Where(r => r.ReferencedEntity != r.ReferencingEntity))
            throw XrmdFaultException.InvalidArgument(
                $"Relationship '{r.SchemaName}' cannot be hierarchical: a hierarchical relationship must reference its own table, " +
                $"but it goes from '{r.ReferencingEntity}' to '{r.ReferencedEntity}'.");
        foreach (var table in hierarchical.GroupBy(r => r.ReferencingEntity, StringComparer.Ordinal))
        {
            var names = table.Select(r => r.SchemaName).ToList();
            if (store.GetHierarchicalRelationship(table.Key) is { } existing && !names.Contains(existing.SchemaName, StringComparer.OrdinalIgnoreCase))
                names.Insert(0, existing.SchemaName);
            if (names.Count > 1)
                throw XrmdFaultException.InvalidArgument(
                    $"Table '{table.Key}' would have {names.Count} hierarchical relationships ({string.Join(", ", names.Select(n => $"'{n}'"))}): " +
                    "a table can have only one.");
        }
    }

    /// <summary>
    /// The publisher prefix rule for the components the import creates (new tables, columns, relationships and global option
    /// sets, not the ones it updates), before any write: the publisher of the solution counts as existing, though the import
    /// creates it only later, and no other publisher's prefix is accepted that the organization does not have.
    /// </summary>
    private void CheckPublisherPrefixes(SolutionPackage package, string publisherPrefix)
    {
        string[] own = [publisherPrefix];
        foreach (var optionSet in package.OptionSets.Where(o => store.GetOptionSet(o.Name) is null))
            prefixes.Demand(optionSet.Name, "choice", own);
        foreach (var entity in package.Entities)
        {
            var published = store.GetEntity(entity.LogicalName);
            if (published is null) prefixes.Demand(entity.LogicalName, "table", own);
            var existing = (published?.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
            var custom = CustomAttributes(entity)
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName && l.IsCustomAttribute == true));
            // The primary name of a custom activity is 'subject', without a prefix (plan 2.11 A4).
            foreach (var attribute in custom.Where(a => !existing.Contains(a.LogicalName)
                                                        && !(entity.IsActivity == true && a.LogicalName == ActivitySchema.Subject)))
                prefixes.Demand(attribute.LogicalName, "column", own);
        }
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true && store.GetRelationship(r.SchemaName) is null))
            prefixes.Demand(relationship.SchemaName, "relationship", own);
    }

    /// <summary>
    /// A re-import changes what the metadata store can update (entity labels; attribute labels, required level, a larger
    /// MaxLength, new options of a local option set; a relationship's menu configuration) and nothing else: any other
    /// difference of an existing entity (ownership, primary attributes, entity set name), custom attribute or custom
    /// relationship is a 400 naming the component and the property, before any write; so are a type change and a smaller
    /// MaxLength of a custom column, which the store cannot apply (also for a column a layer above keeps).
    /// </summary>
    private void CheckReimportDifferences(SolutionPackage package)
    {
        foreach (var entity in package.Entities)
        {
            if (store.GetEntity(entity.LogicalName) is not { } published) continue;
            var entityDifferences = MetadataDifferences.OfEntity(published, entity);
            if (entityDifferences.Count > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"Entity '{entity.LogicalName}' cannot be changed by a solution import: {string.Join("; ", entityDifferences)}.");
            var custom = CustomAttributes(entity)
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName && l.IsCustomAttribute == true));
            foreach (var attribute in custom)
            {
                if ((published.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName) is not { } current) continue;
                // What the store cannot apply is refused here, before any write, applied or kept by a layer above (ruling M7).
                if (current.GetType() != attribute.GetType())
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' cannot be changed by a solution import: it is a {current.GetType().Name} in xrmverse and a {attribute.GetType().Name} in the file (the type cannot change).");
                if (MaxLengthOf(attribute) is { } requested && MaxLengthOf(current) is { } length && requested < length)
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' cannot be changed by a solution import: MaxLength can only grow (it is {length}, the file has {requested}).");
                var differences = MetadataDifferences.OfAttribute(current, attribute, system: false);
                if (differences.Count > 0)
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{entity.LogicalName}.{attribute.LogicalName}' cannot be changed by a solution import: {string.Join("; ", differences)}.");
            }
        }
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true))
        {
            if (store.GetRelationship(relationship.SchemaName) is not { } current) continue;
            var differences = MetadataDifferences.OfRelationship(current, relationship);
            if (differences.Count > 0)
                throw XrmdFaultException.InvalidArgument(
                    $"Relationship '{relationship.SchemaName}' cannot be changed by a solution import: {string.Join("; ", differences)}.");
        }
    }

    private static int? MaxLengthOf(AttributeMetadata attribute) => attribute switch
    {
        StringAttributeMetadata text => text.MaxLength,
        MemoAttributeMetadata memo => memo.MaxLength,
        _ => null,
    };

    /// <summary>Logs as warnings what the import keeps but does not apply, and the system attributes it does not create.</summary>
    private void LogNotApplied(SolutionPackage package, ImportJobLog log)
    {
        if (package.DeclaredMissingDependencies > 0)
            log.Warn(ImportJobLog.Manifest, package.Solution.UniqueName, null,
                $"The solution declares {package.DeclaredMissingDependencies} missing dependencies: xrmverse does not verify the declared list, only that the tables and choices the components use exist.");
        foreach (var folder in (package.KeptFiles ?? new Dictionary<string, byte[]>()).Keys
                     .GroupBy(path => path.Contains('/') ? path[..(path.IndexOf('/') + 1)] : path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            log.Warn(ImportJobLog.Manifest, package.Solution.UniqueName, null,
                $"The '{folder.Key}' entries of the solution file ({folder.Count()} {(folder.Count() == 1 ? "file" : "files")}) are kept for export but not applied: xrmverse does not read them.");
        // A root component of a type xrmverse does not track is never ignored silently (plan D3).
        foreach (var (type, key, _) in package.RootComponents.Where(r => !ComponentType.IsSupported(ComponentType.Normalize(r.Type))))
            log.Warn(ImportJobLog.Manifest, package.Solution.UniqueName, null,
                $"Root component of type {type} ({(Guid.TryParse(key, out var rootId) ? rootId.ToString("B") : key)}) is not applied: xrmverse does not support the component type.");
        // The entity maps are applied (#143); their node is kept only for the export.
        foreach (var node in package.Unsupported.Where(u => u.Name.LocalName != "EntityMaps"))
            log.Warn(ImportJobLog.Manifest, package.Solution.UniqueName, null,
                $"The '{node.Name.LocalName}' components are kept for export but not applied: xrmverse does not support them.");
        // Plan 2.14, ruling W2: each kept process (a category xrmverse does not run) is named.
        foreach (var process in package.Unsupported.Where(u => u.Name.LocalName == "Workflows").SelectMany(u => u.Elements("Workflow")))
        {
            var id = (string?)process.Attribute("WorkflowId");
            var processName = (string?)process.Attribute("Name") ?? id ?? "(no name)";
            var logId = Guid.TryParse(id, out var processId) ? processId.ToString("D") : processName;
            log.Warn(ImportJobLog.Processes, logId, processName, CustomizationsReader.Value(process, "Category") is { } category
                ? $"Process '{processName}' (category {category}) is kept for export but not run: category {category} is not run by xrmverse."
                : $"Process '{processName}' has no Category: it is kept for export but not run.");
        }
        foreach (var (entity, xml) in package.EntityUnsupported)
            log.Warn(ImportJobLog.Entities, entity, null, xml.Name.LocalName == "RibbonDiffXml"
                // Plan 3.5 UI-1e (ruling E1): the UI applies the ribbon commands of forms.
                ? $"The 'RibbonDiffXml' of entity '{entity}' is applied to forms; grid commands, HideCustomAction and Url actions are kept for export but not applied."
                : $"The '{xml.Name.LocalName}' of entity '{entity}' is kept for export but not applied: xrmverse does not support it.");
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true))
            if (package.RelationshipDescriptions?.GetValueOrDefault(relationship.SchemaName) is { } description)
                log.Warn(ImportJobLog.Relationships, relationship.SchemaName, relationship.SchemaName,
                    $"The description of relationship '{relationship.SchemaName}' ('{Text(description)}') is not kept: xrmverse has no relationship descriptions.");
        // The relationships of the activity model are derived (plan 2.11 A19, ruling Z5): the file's one is not imported, and a
        // file that names it otherwise or marks it custom is told so (a client using its name or navigations would not find them).
        foreach (var relationship in package.Relationships.OfType<OneToManyRelationshipMetadata>())
        {
            if (DerivedActivityRelationship(package, relationship) is not { } derived) continue;
            var markedCustom = package.Sources?.GetValueOrDefault(Layers.LayerPayload.Key(ComponentType.EntityRelationship, relationship.SchemaName))
                ?.SourceXml.Element("IsCustomRelationship")?.Value.Trim().ToLowerInvariant() is "1" or "true";
            var renamed = !string.Equals(derived, relationship.SchemaName, StringComparison.OrdinalIgnoreCase);
            if (renamed || markedCustom)
                log.Warn(ImportJobLog.Relationships, relationship.SchemaName, relationship.SchemaName,
                    $"Relationship '{relationship.SchemaName}'{(markedCustom ? " (custom in the file)" : "")} is of the activity model: xrmverse derives it as '{derived}' and does not import the file's.");
        }
        foreach (var (entity, attribute, type) in package.SkippedAttributes)
            // The party lists and EntityName columns of an activity are derived (plan 2.11 A19): nothing is lost.
            if (!(IsActivityOfFile(package, entity) && ActivitySchema.IsGeneratedActivityAttribute(attribute)))
                log.Warn(ImportJobLog.Entities, entity, null,
                $"System attribute '{entity}.{attribute}' of type '{type}' is not created: xrmverse does not support the type.");

        foreach (var entity in package.Entities)
        {
            var created = ResultingAttributes(entity, package);
            var system = (entity.Attributes ?? []).Cast<AttributeMetadata>()
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName))
                .Where(a => a.IsCustomAttribute != true && !created.Contains(a.LogicalName));
            foreach (var attribute in system)
                log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                    $"System attribute '{entity.LogicalName}.{attribute.LogicalName}' is not created: xrmverse does not generate it.");
        }
    }

    /// <summary>
    /// The permissions each field security profile of the package gets (ruling F8), decided before any write: the System
    /// Administrator profile is not imported (a warning); a permission on a column that is not secured once the import's
    /// metadata is applied (or does not exist) is skipped with a warning, as Dataverse skips it; a <c>canreadunmasked</c>
    /// other than 0 is imported as 0 with a warning, because xrmverse does not support masking (ruling F5); a column listed
    /// twice in one profile keeps its first permission, with a warning.
    /// </summary>
    private Dictionary<Guid, IReadOnlyList<PackageFieldPermission>> PlanFieldPermissions(SolutionPackage package, ImportJobLog log)
    {
        var planned = new Dictionary<Guid, IReadOnlyList<PackageFieldPermission>>();
        foreach (var profile in package.FieldSecurityProfiles ?? [])
        {
            var id = profile.Id.ToString("D");
            if (profile.Id == FieldSecurityTables.SystemAdministratorProfileId)
            {
                log.Warn(ImportJobLog.FieldSecurityProfiles, id, profile.Name,
                    $"Field security profile '{profile.Name}' is the System Administrator profile, which is built in and read-only: it was not imported.");
                continue;
            }
            if (profile.UnwrappedPermissions > 0)
                log.Warn(ImportJobLog.FieldSecurityProfiles, id, profile.Name,
                    $"Field security profile '{profile.Name}' has {profile.UnwrappedPermissions} FieldPermission element(s) outside a FieldPermissions element: they were not imported.");
            var kept = new List<PackageFieldPermission>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in profile.Permissions)
            {
                var column = $"{permission.Entity}.{permission.Attribute}";
                if (!seen.Add(column))
                {
                    log.Warn(ImportJobLog.FieldSecurityProfiles, id, profile.Name,
                        $"Field security profile '{profile.Name}' lists column '{column}' more than once: only its first permission was imported.");
                    continue;
                }
                if (!SecuredAfterImport(package, permission.Entity, permission.Attribute))
                {
                    log.Warn(ImportJobLog.FieldSecurityProfiles, id, profile.Name,
                        $"The permission of field security profile '{profile.Name}' on column '{column}' was skipped: the column is not secured in this organization.");
                    continue;
                }
                if (permission.CanReadUnmasked != 0)
                    log.Warn(ImportJobLog.FieldSecurityProfiles, id, profile.Name,
                        $"canreadunmasked of field security profile '{profile.Name}' on column '{column}' is {permission.CanReadUnmasked}: imported as 0, because xrmverse does not support masking.");
                kept.Add(permission with { CanReadUnmasked = 0 });
            }
            planned[profile.Id] = kept;
        }
        return planned;
    }

    /// <summary>
    /// What an import may change in field security, checked before any write (final review I1). A package carrying field
    /// security profiles needs the create and write privileges on <c>fieldsecurityprofile</c> and <c>fieldpermission</c>
    /// (ruling F9: only the System Administrator role holds them, and it holds every privilege); otherwise the import fails
    /// with 403 PrivilegeDenied. A column the file turns from secured to not secured needs what <c>UpdateAttribute</c> needs
    /// (<see cref="SecurityGuard.DemandUnsecureAsync"/>, 403 0x8004F508).
    /// </summary>
    private async Task DemandFieldSecurityImportAsync(Caller caller, SolutionPackage package, CancellationToken ct)
    {
        if (package.FieldSecurityProfiles is { Count: > 0 })
            foreach (var table in new[] { FieldSecurityTables.Profile, FieldSecurityTables.Permission })
            {
                var definition = store.GetEntity(table) ?? throw new InvalidOperationException($"The table '{table}' does not exist.");
                foreach (var right in new[] { AccessRights.CreateAccess, AccessRights.WriteAccess })
                {
                    var privilege = PrivilegeNames.For(right, definition);
                    try
                    {
                        await guard.DemandPrivilegeAsync(caller, privilege, table, ct);
                    }
                    catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.PrivilegeDenied)
                    {
                        throw new XrmdFaultException(ErrorCodes.PrivilegeDenied,
                            $"The solution '{package.Solution.UniqueName}' carries field security profiles. Importing it takes the System Administrator role, or the create and write privileges on field security profiles and field permissions; {privilege} is missing.",
                            403);
                    }
                }
            }

        foreach (var packaged in package.Entities)
        {
            if (packaged.LogicalName is not { } entity || store.GetEntity(entity) is not { } published) continue;
            foreach (var attribute in packaged.Attributes ?? [])
                if (attribute is { IsSecured: false, LogicalName: { } name }
                    && published.Attributes.FirstOrDefault(a => a.LogicalName == name) is { IsSecured: true })
                    await guard.DemandUnsecureAsync(caller, entity, name, ct);
        }
    }

    /// <summary>
    /// True when <paramref name="entity"/>.<paramref name="attribute"/> exists and is secured once the import's metadata is
    /// applied: the file's <c>IsSecured</c> where the file gives the column one (applied only where the securable rule allows
    /// it, see <see cref="KeepUnsecuredIfNotSecurable"/>), else the organization's.
    /// </summary>
    private bool SecuredAfterImport(SolutionPackage package, string entity, string attribute)
    {
        var published = store.GetEntity(entity);
        var current = (published?.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute);
        if (package.Entities.FirstOrDefault(e => e.LogicalName == entity) is not { } packaged) return current?.IsSecured == true;
        if (!ResultingAttributes(packaged, package).Contains(attribute)) return false;
        var file = (packaged.Attributes ?? []).Cast<AttributeMetadata>()
            .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity))
            .FirstOrDefault(a => a.LogicalName == attribute);
        if (file?.IsSecured is not { } fileSecured) return current?.IsSecured == true;
        if (!fileSecured) return false;
        var table = published ?? Shape(entity, packaged.PrimaryNameAttribute, packaged.Attributes ?? [], packaged.PrimaryIdAttribute);
        return FieldSecurability.IsSecurable(table, current ?? file) || current?.IsSecured == true;
    }

    /// <summary>
    /// Import replaces the permission set of the profile (ruling F8): the permission of a column the file gives is created or
    /// updated, the others of the profile are deleted. Through the core operations, so the security cache follows the writes.
    /// The profile's users and teams are left as they are: they never travel in a solution.
    /// </summary>
    private async Task ApplyFieldPermissionsAsync(DataSession s, Caller caller, Guid profileId, IReadOnlyList<PackageFieldPermission> permissions,
        CancellationToken ct)
    {
        var existing = new Dictionary<(string, string), Guid>();
        var stale = new List<Guid>();
        await using (var cmd = s.Command(
                         $"SELECT fieldpermissionid, entityname, attributelogicalname FROM {s.DataTable(FieldSecurityTables.Permission)} " +
                         $"WHERE {s.Dialect.Quote(FieldSecurityTables.ProfileId)} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, profileId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var key = (rd.IsDBNull(1) ? "" : rd.GetString(1).ToLowerInvariant(), rd.IsDBNull(2) ? "" : rd.GetString(2).ToLowerInvariant());
                if (!existing.TryAdd(key, rd.GetGuid(0))) stale.Add(rd.GetGuid(0));
            }
        }

        var wanted = new HashSet<(string, string)>();
        foreach (var permission in permissions)
        {
            var key = (permission.Entity.ToLowerInvariant(), permission.Attribute.ToLowerInvariant());
            if (!wanted.Add(key)) continue; // the first permission of a column wins
            var target = existing.TryGetValue(key, out var id)
                ? new Entity(FieldSecurityTables.Permission, id)
                : new Entity(FieldSecurityTables.Permission)
                {
                    [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profileId),
                    [FieldSecurityTables.EntityName] = permission.Entity,
                    [FieldSecurityTables.AttributeLogicalName] = permission.Attribute,
                };
            target[FieldSecurityTables.CanRead] = new OptionSetValue(permission.CanRead);
            target[FieldSecurityTables.CanCreate] = new OptionSetValue(permission.CanCreate);
            target[FieldSecurityTables.CanUpdate] = new OptionSetValue(permission.CanUpdate);
            target[FieldSecurityTables.CanReadUnmasked] = new OptionSetValue(0);
            OrganizationRequest request = target.Id == Guid.Empty ? new CreateRequest { Target = target } : new UpdateRequest { Target = target };
            await core.ExecuteCoreAsync(request, caller, s, ct);
        }
        foreach (var id in existing.Where(e => !wanted.Contains(e.Key)).Select(e => e.Value).Concat(stale))
            await core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(FieldSecurityTables.Permission, id) }, caller, s, ct);
    }

    /// <summary>The attribute names the entity has after the import: the published ones (or the system attributes xrmverse
    /// generates) plus the custom attributes and lookups of the package, and the currency pair when the entity then has a money
    /// attribute (a money column brings it, ruling X13).</summary>
    private HashSet<string> ResultingAttributes(EntityMetadata entity, SolutionPackage package)
    {
        var custom = CustomAttributes(entity).Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName && l.IsCustomAttribute == true))
            .ToList();
        var published = store.GetEntity(entity.LogicalName);
        var names = published is not null
            ? (published.Attributes ?? []).Select(a => a.LogicalName)
            : NewTableAttributes(entity, custom.Where(a => a is not LookupAttributeMetadata).Select(MetadataSerializer.Clone)).Select(a => a.LogicalName);
        var result = names.Concat(custom.Select(a => a.LogicalName)).ToHashSet(StringComparer.Ordinal);
        if (published is not null && custom.Any(a => a is MoneyAttributeMetadata { IsBaseCurrency: not true }))
            result.UnionWith([CurrencySchema.CurrencyId, CurrencySchema.ExchangeRate]);
        return result;
    }

    /// <summary>
    /// The attributes the store gives a table the import creates from <paramref name="entity"/> with <paramref name="given"/> (its
    /// custom columns, as copies): the system columns, and for a custom activity (plan 2.11 A4) <c>activityid</c>, its primary name
    /// <c>subject</c> (a system column of the file included) and the columns an activity gets (A2, party lists, states).
    /// </summary>
    private static IReadOnlyList<AttributeMetadata> NewTableAttributes(EntityMetadata entity, IEnumerable<AttributeMetadata> given)
    {
        var shape = new EntityMetadata { LogicalName = entity.LogicalName, OwnershipType = entity.OwnershipType };
        var attributes = given.ToList();
        if (entity.IsActivity == true)
        {
            SdkProperty.Set(shape, nameof(EntityMetadata.PrimaryIdAttribute), ActivitySchema.ActivityId);
            if (attributes.All(a => a.LogicalName != entity.PrimaryNameAttribute) && ActivityPrimaryName(entity) is { } subject) attributes.Add(subject);
            attributes.AddRange(ActivitySchema.CustomActivityAttributes().Where(a => attributes.All(g => g.LogicalName != a.LogicalName)));
        }
        return SystemAttributes.Complete(shape, attributes);
    }

    /// <summary>
    /// The primary name of a custom activity of the file when the file has it as a system column (<c>IsCustomField</c> 0), as a
    /// copy the store can create; null for any other table, or when the file gives it as a custom column (the ordinary path).
    /// </summary>
    private static AttributeMetadata? ActivityPrimaryName(EntityMetadata entity)
    {
        if (entity.IsActivity != true || (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == entity.PrimaryNameAttribute) is not { } name
            || name.IsCustomAttribute == true) return null;
        var copy = MetadataSerializer.Clone(name);
        ServerOwnedProperties.Clear(copy);
        return copy;
    }

    /// <summary>
    /// The custom non-lookup attributes of a package entity that the import writes: not the primary key, not the generated
    /// ones (money <c>_base</c>, <c>AttributeOf</c>), not the reserved system names.
    /// </summary>
    private static IEnumerable<AttributeMetadata> CustomAttributes(EntityMetadata entity) =>
        (entity.Attributes ?? []).Where(a => a.IsCustomAttribute == true && a.IsPrimaryId != true
                                              && !TypeMapping.IsStoredByOtherAttribute(a)
                                              && !SystemAttributes.IsReservedName(entity.LogicalName, a.LogicalName));

    /// <summary>The row writes of the package in apply order, each a Create or an Update of the existing record.</summary>
    private async Task<List<RowWrite>> PlanRowsAsync(DataSession s, SolutionPackage package, Guid rootBusinessUnitId,
        List<RowWrite> customApiRows, string? prefix, CancellationToken ct)
    {
        var rows = new List<RowWrite>();
        var language = package.Solution.LanguageCode;

        foreach (var role in package.Roles)
        {
            if (StandardRoleIds.Contains(role.Id))
                throw XrmdFaultException.InvalidArgument($"Role '{role.Name}': the roleid {role.Id} is reserved for a standard role.");
            var exists = await ExistsAsync(s, "role", role.Id, ct);
            if (exists && await ScalarAsync(s, $"SELECT roletemplateid FROM {s.DataTable("role")} WHERE roleid = @id", ct, ("@id", role.Id)) is Guid)
                throw XrmdFaultException.InvalidArgument($"Role '{role.Name}': the role {role.Id} is a standard role and cannot be imported.");
            var target = new Entity("role", role.Id) { ["name"] = role.Name };
            if (!exists) target["businessunitid"] = new EntityReference("businessunit", rootBusinessUnitId);
            rows.Add(new(target, !exists, ImportJobLog.Roles, role.Id.ToString("D"), role.Name, ComponentType.Role, role.Id.ToString("D")));
        }

        // The System Administrator profile is never imported (PlanFieldPermissions logs it).
        foreach (var profile in (package.FieldSecurityProfiles ?? []).Where(p => p.Id != FieldSecurityTables.SystemAdministratorProfileId))
        {
            var target = new Entity(FieldSecurityTables.Profile, profile.Id) { ["name"] = profile.Name, ["description"] = profile.Description };
            rows.Add(new(target, !await ExistsAsync(s, FieldSecurityTables.Profile, profile.Id, ct), ImportJobLog.FieldSecurityProfiles,
                profile.Id.ToString("D"), profile.Name, ComponentType.FieldSecurityProfile, profile.Id.ToString("D")));
        }

        // The system charts wait for the web resources (a chart may name one in webresourceid).
        foreach (var opaque in package.Opaque.Where(o => o.Type != ComponentType.SavedQueryVisualization))
            rows.Add(await OpaqueRowAsync(s, opaque, language, ct));

        foreach (var resource in package.WebResources)
        {
            var target = new Entity("webresource", resource.Id)
            {
                ["name"] = resource.Name,
                ["displayname"] = resource.DisplayName,
                ["webresourcetype"] = new OptionSetValue(resource.WebResourceType),
                ["content"] = Convert.ToBase64String(resource.Content),
                ["description"] = resource.Description,
                ["xrmverse_componentxml"] = package.Unmodelled?.GetValueOrDefault(UnmodelledXml.WebResourceKey(resource.Id))
                    ?.ToString(SaveOptions.DisableFormatting),
            };
            rows.Add(new(target, !await ExistsAsync(s, "webresource", resource.Id, ct), ImportJobLog.WebResources,
                resource.Id.ToString("D"), resource.Name, ComponentType.WebResource, resource.Name));
        }

        // The system charts (plan 3.5 UI-1f, ruling F2): after the web resources, layered like the views.
        foreach (var opaque in package.Opaque.Where(o => o.Type == ComponentType.SavedQueryVisualization))
            rows.Add(await OpaqueRowAsync(s, opaque, language, ct));

        // The custom controls (plan 3.5 UI-1d, R3): after the web resources, before the plug-ins.
        rows.AddRange(await PlanCustomControlsAsync(s, package, ct));

        foreach (var assembly in package.Assemblies)
        {
            var exists = await ExistsAsync(s, "pluginassembly", assembly.Id, ct);
            // Issue #70: a plug-in assembly name is unique in the organization. An assembly of the file under another id than
            // the organization's assembly of that name is refused here, before any write, as Dataverse does not merge them.
            if (!exists && await AssemblyIdByNameAsync(s, assembly.Name, ct) is { } other)
                throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                    $"The plug-in assembly '{assembly.Name}' of the solution ({assembly.Id:D}) cannot be imported: the organization already has a plug-in assembly with that name under another id ({other:D}). Delete or rename that assembly, or import a solution that carries it under its id.",
                    412);
            var target = new Entity("pluginassembly", assembly.Id)
            {
                ["name"] = assembly.Name,
                ["content"] = Convert.ToBase64String(assembly.Content),
                ["isolationmode"] = new OptionSetValue(assembly.IsolationMode),
                ["sourcetype"] = new OptionSetValue(assembly.SourceType),
                ["xrmverse_componentxml"] = package.Unmodelled?.GetValueOrDefault(UnmodelledXml.AssemblyKey(assembly.Id))
                    ?.ToString(SaveOptions.DisableFormatting),
            };
            rows.Add(new(target, !exists, ImportJobLog.Assemblies,
                assembly.Id.ToString("D"), assembly.FullName, ComponentType.PluginAssembly, assembly.Id.ToString("D")));
        }
        foreach (var assembly in package.Assemblies)
        {
            foreach (var type in assembly.Types)
            {
                var target = new Entity("plugintype", type.Id)
                {
                    ["typename"] = type.TypeName,
                    ["name"] = type.TypeName,
                    ["friendlyname"] = type.FriendlyName,
                    ["assemblyname"] = assembly.Name,
                    ["pluginassemblyid"] = new EntityReference("pluginassembly", assembly.Id),
                };
                // Plan 2.14, W17: a type with a group name is a custom workflow activity (the validator checks its base type).
                if (type.WorkflowActivityGroupName is { } group)
                {
                    target["workflowactivitygroupname"] = group;
                    target["isworkflowactivity"] = true;
                }
                rows.Add(new(target, !await ExistsAsync(s, "plugintype", type.Id, ct), ImportJobLog.Assemblies,
                    type.Id.ToString("D"), type.TypeName, ComponentType.PluginType, type.Id.ToString("D")));
            }
        }

        // After the plug-in types they run, before the steps that may be registered on their messages.
        rows.AddRange(customApiRows);
        // The environment variables (#167, ruling E3): after the custom APIs, before the processes and steps that may read them.
        rows.AddRange(await PlanEnvironmentVariablesAsync(s, package, ct));
        var apiMessages = customApiRows.Where(r => r.Target.LogicalName == ApiMessage)
            .ToDictionary(r => r.Target.Id, r => (string?)r.LogName);
        var apiFilters = customApiRows.Where(r => r.ApiFilterEntity is not null)
            .ToDictionary(r => (r.Target.GetAttributeValue<EntityReference>("sdkmessageid").Id, r.ApiFilterEntity!), r => r.Target.Id);

        // The processes (plan 2.14, ruling W22): after the plug-in types (a custom activity step names one) and the custom APIs (an
        // action call may name one), before the steps.
        foreach (var workflow in package.Workflows)
        {
            var target = new Entity(WorkflowSchema.Table, workflow.Id)
            {
                [WorkflowSchema.Name] = workflow.Name,
                [SystemTableRules.ComponentXml] = workflow.Source.ToString(SaveOptions.DisableFormatting),
            };
            rows.Add(new(target, !await ExistsAsync(s, WorkflowSchema.Table, workflow.Id, ct), ImportJobLog.Processes, workflow.Id.ToString("D"),
                workflow.Name, ComponentType.Workflow, workflow.Id.ToString("D"), Workflow: workflow));
        }

        var actionMessages = await PlanActionMessagesAsync(s, package, prefix, ct);
        foreach (var step in package.Steps)
        {
            var action = actionMessages.GetValueOrDefault(step.SdkMessageId);
            var (messageId, messageName) = action is not null ? (action.Id, action.Name) : await ResolveMessageAsync(s, step, apiMessages, ct);
            var target = new Entity("sdkmessageprocessingstep", step.Id)
            {
                ["name"] = step.Name,
                ["description"] = step.Description,
                ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
                ["sdkmessagefilterid"] = step.PrimaryEntity is { } entity
                    ? new EntityReference("sdkmessagefilter", action is not null
                        ? await ActionFilterAsync(s, step, action, entity, ct)
                        : await ResolveFilterAsync(s, package, messageId, messageName, entity, apiFilters, ct))
                    // A step on a global custom API that names the API's own filter (primaryobjecttypecode none) keeps it.
                    : step.SdkMessageFilterId is { } fileFilter && apiFilters.TryGetValue((messageId, "none"), out var noneFilter) && noneFilter == fileFilter
                        ? new EntityReference("sdkmessagefilter", noneFilter)
                        : null,
                ["plugintypeid"] = new EntityReference("plugintype", step.PluginTypeId),
                ["eventhandler"] = new EntityReference("plugintype", step.PluginTypeId),
                ["stage"] = new OptionSetValue(step.Stage),
                ["mode"] = new OptionSetValue(step.Mode),
                ["rank"] = step.Rank,
                ["invocationsource"] = new OptionSetValue(step.InvocationSource),
                ["supporteddeployment"] = new OptionSetValue(step.SupportedDeployment),
                ["asyncautodelete"] = step.AsyncAutoDelete,
                ["filteringattributes"] = step.FilteringAttributes,
                ["configuration"] = step.Configuration,
                ["impersonatinguserid"] = step.ImpersonatingUserId is { } user ? new EntityReference("systemuser", user) : null,
                ["statecode"] = new OptionSetValue(step.Disabled ? 1 : 0),
                ["statuscode"] = new OptionSetValue(step.Disabled ? 2 : 1),
                ["xrmverse_componentxml"] = package.Unmodelled?.GetValueOrDefault(UnmodelledXml.StepKey(step.Id))
                    ?.ToString(SaveOptions.DisableFormatting),
            };
            CheckFilteringAttributes(package, step);
            rows.Add(new(target, !await ExistsAsync(s, "sdkmessageprocessingstep", step.Id, ct), ImportJobLog.Steps,
                step.Id.ToString("D"), step.Name, ComponentType.SdkMessageProcessingStep, step.Id.ToString("D")));
            foreach (var image in step.Images)
            {
                var imageTarget = new Entity("sdkmessageprocessingstepimage", image.Id)
                {
                    ["name"] = image.Name,
                    ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", step.Id),
                    ["imagetype"] = new OptionSetValue(image.ImageType),
                    ["entityalias"] = image.EntityAlias,
                    ["messagepropertyname"] = image.MessagePropertyName,
                    ["attributes"] = image.Attributes,
                };
                rows.Add(new(imageTarget, !await ExistsAsync(s, "sdkmessageprocessingstepimage", image.Id, ct), ImportJobLog.Steps,
                    image.Id.ToString("D"), image.Name, ComponentType.SdkMessageProcessingStepImage, image.Id.ToString("D")));
            }
        }
        return rows;
    }

    /// <summary>The message an action of the file has once written: its id, its name and its bound table (<c>none</c> when global).</summary>
    private sealed record PlannedActionMessage(Guid Id, string Name, string Entity);

    /// <summary>
    /// Plan 2.14 (W14): the messages of the actions (category 3) of the file that the organization does not have yet, by the
    /// <c>SdkMessageId</c> the file gives them, as <see cref="WriteWorkflowAsync"/> will create them (before the steps): the stored
    /// message of an existing definition, else the file's id. So a step of the file on an action's message resolves, as the
    /// messages of the file's custom APIs do. Nothing without a prefix (a restore: the messages exist already).
    /// </summary>
    private async Task<Dictionary<Guid, PlannedActionMessage>> PlanActionMessagesAsync(DataSession s, SolutionPackage package, string? prefix,
        CancellationToken ct)
    {
        var planned = new Dictionary<Guid, PlannedActionMessage>();
        if (prefix is null) return planned;
        foreach (var workflow in package.Workflows)
        {
            var row = WorkflowRowOf(workflow);
            if (row is not { Category: (int)WorkflowSchema.Category.Action, Type: (int)WorkflowSchema.Type.Definition, UniqueName.Length: > 0 }) continue;
            var fileMessage = workflow.Fields.FirstOrDefault(f => f.Key.Equals("SdkMessageId", StringComparison.OrdinalIgnoreCase)).Value;
            if (!Guid.TryParse(fileMessage?.Trim(), out var fileId) || await ExistsAsync(s, ApiMessage, fileId, ct)) continue;
            var id = (await _workflowStore.GetAsync(s, workflow.Id, ct))?.SdkMessageId is { } stored && await ExistsAsync(s, ApiMessage, stored, ct)
                ? stored
                : fileId;
            planned[fileId] = new(id, Xrmd.Workflows.Actions.ActionMessages.MessageName(row.UniqueName!, prefix),
                Xrmd.Workflows.Actions.ActionMessages.BoundEntityOf(row) ?? WorkflowSchema.NoPrimaryEntity);
        }
        return planned;
    }

    /// <summary>
    /// The filter of a step on a planned action message: the action's own filter (its bound table), the one the message has or the one
    /// <see cref="Xrmd.Workflows.Actions.ActionMessages.EnsureAsync"/> will create (its derived id); 400 for another table.
    /// </summary>
    private static async Task<Guid> ActionFilterAsync(DataSession s, PackageStep step, PlannedActionMessage action, string entity,
        CancellationToken ct)
    {
        if (!string.Equals(entity, action.Entity, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"Step '{step.Name}' is registered on the message '{action.Name}' for the table '{entity}'; the action is bound to '{action.Entity}'.");
        return await ScalarAsync(s,
                   s.Dialect.Limit($"SELECT sdkmessagefilterid FROM {s.DataTable("sdkmessagefilter")} WHERE sdkmessageid = @message AND primaryobjecttypecode = @entity", "1"),
                   ct, ("@message", action.Id), ("@entity", entity)) as Guid?
               ?? SdkMessageCatalog.FilterId(action.Name, action.Entity);
    }

    /// <summary>
    /// The xrmverse message of a step: a Dataverse well-known id (<see cref="DataverseSdkMessages"/>) maps by name to the
    /// xrmverse catalog row; the message of a custom API of the file is its own; any other id must be an <c>sdkmessage</c> row
    /// of the organization (a custom message, which keeps its id). 400 naming the id otherwise: so a step a Dataverse export
    /// registers on a custom API message, whose Dataverse id xrmverse cannot map, fails the import (plan 15, C4).
    /// </summary>
    private async Task<(Guid Id, string Name)> ResolveMessageAsync(DataSession s, PackageStep step,
        IReadOnlyDictionary<Guid, string?> apiMessages, CancellationToken ct)
    {
        // The message of a custom API this import writes (an xrmverse export carries xrmverse's id; plan 15, C4).
        if (apiMessages.TryGetValue(step.SdkMessageId, out var apiMessage) && apiMessage is not null)
            return (step.SdkMessageId, apiMessage);
        if (DataverseSdkMessages.NameOf(step.SdkMessageId) is { } known)
        {
            var id = SdkMessageCatalog.MessageId(known);
            if (await ScalarAsync(s, $"SELECT name FROM {s.DataTable("sdkmessage")} WHERE sdkmessageid = @id", ct, ("@id", id)) is string name)
                return (id, name);
        }
        else if (await ScalarAsync(s, $"SELECT name FROM {s.DataTable("sdkmessage")} WHERE sdkmessageid = @id", ct, ("@id", step.SdkMessageId))
                 is string custom)
        {
            return (step.SdkMessageId, custom);
        }
        throw XrmdFaultException.InvalidArgument(
            $"Step '{step.Name}' references the SDK message {step.SdkMessageId}, which xrmverse does not know.");
    }

    /// <summary>
    /// The filter of (message, entity): the catalog's derived id (a filter the catalog creates with a new entity, or one
    /// already in the organization), else a filter row of the organization; 400 when the message has none for the entity.
    /// </summary>
    private async Task<Guid> ResolveFilterAsync(DataSession s, SolutionPackage package, Guid messageId, string messageName, string entity,
        IReadOnlyDictionary<(Guid, string), Guid> apiFilters, CancellationToken ct)
    {
        // The filter of a custom API this import writes (planned with its message, before the steps).
        if (apiFilters.TryGetValue((messageId, entity.ToLowerInvariant()), out var apiFilter)) return apiFilter;
        var derived = SdkMessageCatalog.FilterId(messageName, entity);
        var isCatalogMessage = SdkMessageCatalog.EntityMessages.Contains(messageName, StringComparer.OrdinalIgnoreCase)
                               && messageId == SdkMessageCatalog.MessageId(messageName);
        if (isCatalogMessage && (await ExistsAsync(s, "sdkmessagefilter", derived, ct)
                                 || (store.GetEntity(entity) is null && package.Entities.Any(e => e.LogicalName == entity))))
            return derived;
        if (await ScalarAsync(s,
                s.Dialect.Limit($"SELECT sdkmessagefilterid FROM {s.DataTable("sdkmessagefilter")} WHERE sdkmessageid = @message AND primaryobjecttypecode = @entity", "1"),
                ct, ("@message", messageId), ("@entity", entity)) is Guid found)
            return found;
        throw XrmdFaultException.InvalidArgument($"The SDK message '{messageName}' has no filter for entity '{entity}'.");
    }

    /// <summary>
    /// The filtering attributes of a step on an entity of the package or of the organization must be attributes of that
    /// entity (the <see cref="RegistrationValidator"/> sees only the published metadata, not an entity created by the import).
    /// </summary>
    private void CheckFilteringAttributes(SolutionPackage package, PackageStep step)
    {
        if (step.PrimaryEntity is not { } entity || string.IsNullOrWhiteSpace(step.FilteringAttributes)) return;
        var known = package.Entities.FirstOrDefault(e => e.LogicalName == entity) is { } packaged
            ? ResultingAttributes(packaged, package)
            : (store.GetEntity(entity)?.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        foreach (var attribute in step.FilteringAttributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Contains(attribute.ToLowerInvariant()))
                throw XrmdFaultException.InvalidArgument($"Step '{step.Name}': attribute '{attribute}' not found on entity '{entity}'.");
        }
    }

    // ---- custom APIs (plan 15) --------------------------------------------------------------------------------------

    private const string ApiMessage = "sdkmessage";
    private const string ApiFilter = "sdkmessagefilter";

    /// <summary>True for a row of a custom API: its own tables, its message and its filter.</summary>
    private static bool IsCustomApiRow(RowWrite row) => row.Group == ImportJobLog.CustomApis;

    /// <summary>
    /// The custom APIs of the file, checked before any write (C1, C2, C5, C11) and planned as rows: for each API its
    /// <c>sdkmessage</c> (id <see cref="SdkMessageCatalog.MessageId"/> of its unique name, <c>categoryname</c> CustomAPI) and
    /// <c>sdkmessagefilter</c> (the bound table, or <c>none</c>; <c>iscustomprocessingstepallowed</c> unless the step type is
    /// 0), then its <c>customapi</c> row, its request parameters and response properties (each with what the file gives it
    /// that xrmverse does not model, in <c>xrmverse_componentxml</c>, C11). A re-import updates the API found by unique name
    /// (ignoring case) and keeps its ids; parameters and properties missing from the file stay (unmanaged import is additive).
    /// </summary>
    /// <param name="publisherPrefix">The prefix of the solution's publisher, which counts as existing (C2); null in a restore,
    /// which does not check the name again (<see cref="DemandCustomApiNameAsync"/>).</param>
    /// <exception cref="XrmdFaultException">400 naming the API for any refusal of C1, C2, C5 or C11.</exception>
    private async Task<List<RowWrite>> PlanCustomApisAsync(DataSession s, SolutionPackage package, string? publisherPrefix, CancellationToken ct)
    {
        var rows = new List<RowWrite>();
        if (package.CustomApis is not { Count: > 0 } apis) return rows;
        var packageTables = package.Entities.Select(e => e.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool TableExists(string table) => packageTables.Contains(table) || store.GetEntity(table.ToLowerInvariant()) is not null;
        var packageTypes = package.Assemblies.SelectMany(a => a.Types).Select(t => t.Id).ToHashSet();
        // The entity sets after the import (a table of the file keeps the set of the organization's table, or gets its own).
        var entitySets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in store.GetAllEntities())
            if (entity.EntitySetName is { } set) entitySets[set] = entity.LogicalName;
        foreach (var entity in package.Entities)
            entitySets.TryAdd(store.GetEntity(entity.LogicalName)?.EntitySetName ?? entity.EntitySetName ?? entity.LogicalName + "s", entity.LogicalName);

        foreach (var api in apis)
        {
            CustomApiImportRules.DemandSupported(api, TableExists);
            CustomApiImportRules.DemandNotAnEntitySet(api, entitySets);
            if (publisherPrefix is not null) await DemandCustomApiNameAsync(s, api, publisherPrefix, ct);
            await DemandMainOperationAsync(s, api, packageTypes, ct);
            var existing = await ExistingCustomApiAsync(s, api.UniqueName, ct);
            if (existing is not null) CustomApiImportRules.DemandUnchangedImmutables(api, existing);
            rows.AddRange(await CustomApiRowsAsync(s, package, api, existing, ct));
        }
        return rows;
    }

    /// <summary>
    /// C2, in one place, in this order: the unique name is no message the server handles itself
    /// (<see cref="PipelineExecutor.IsReservedMessageName"/>) and no <c>sdkmessage</c> row of the organization other than
    /// this API's own (a custom action, or a message of another kind), ignoring case; it holds only letters, digits and
    /// <c>_</c>; and it has the prefix of an existing publisher (of the organization, or the solution's).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the API.</exception>
    private async Task DemandCustomApiNameAsync(DataSession s, PackageCustomApi api, string publisherPrefix, CancellationToken ct)
    {
        var name = api.UniqueName;
        if (executor.IsReservedMessageName(name))
            throw CustomApiImportRules.Refused(name, $"'{name}' is the name of a message xrmverse handles itself");
        var d = s.Dialect;
        await s.KeyLockAsync("sdkmessage", name, ct);
        if (await ScalarAsync(s, d.Limit($"""
                SELECT m.name FROM {s.DataTable("sdkmessage")} AS m{d.LockHint(RowLock.UpdateRange)}
                WHERE {d.TextKey("m.name")} = {d.TextKey("@name")} AND NOT EXISTS (SELECT 1 FROM {s.DataTable(CustomApiTables.Api)} AS c
                    WHERE c.{CustomApiTables.SdkMessageId} = m.sdkmessageid AND {d.TextKey($"c.{CustomApiTables.UniqueName}")} = {d.TextKey("@name")})
                """, "1") + d.LockClause(RowLock.UpdateRange, "m"), ct, ("@name", name)) is string message)
            throw CustomApiImportRules.Refused(name, $"the organization already has the message '{message}' (a custom action or another message)");
        CustomApiImportRules.DemandNameCharacters(api);
        try
        {
            prefixes.Demand(name, "custom API", [publisherPrefix]);
        }
        catch (XrmdValidationException ex)
        {
            throw CustomApiImportRules.Refused(name, ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>
    /// C5: the plug-in type of the main operation (<c>plugintypeexportkey</c>, matched against <c>plugintypeid</c>) is a type of
    /// the file or of the organization, and not a workflow activity. No plug-in type is allowed (an empty main operation).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the API and the key.</exception>
    private async Task DemandMainOperationAsync(DataSession s, PackageCustomApi api, HashSet<Guid> packageTypes, CancellationToken ct)
    {
        if (api.PluginTypeKey is not { } key || packageTypes.Contains(key)) return;
        await using var cmd = s.Command($"SELECT isworkflowactivity FROM {s.DataTable("plugintype")} WHERE plugintypeid = @id");
        cmd.AddParameter("@id", DbType.Guid, key);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct))
            throw CustomApiImportRules.Refused(api.UniqueName,
                $"its plug-in type '{key}' (plugintypeexportkey) is neither in the solution nor in the organization");
        if (!rd.IsDBNull(0) && rd.GetBoolean(0))
            throw CustomApiImportRules.Refused(api.UniqueName, $"its plug-in type '{key}' is a workflow activity, not a plug-in");
    }

    /// <summary>The API with that unique name in the organization (ignoring case), with its parameters and properties; null when none.</summary>
    private static async Task<CustomApiImportRules.Existing?> ExistingCustomApiAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        var d = s.Dialect;
        await using var cmd = s.Command(d.Limit($"""
            SELECT customapiid, uniquename, sdkmessageid, bindingtype, boundentitylogicalname, isfunction, workflowsdkstepenabled,
                   allowedcustomprocessingsteptype
            FROM {s.DataTable(CustomApiTables.Api)} WHERE {d.TextKey("uniquename")} = {d.TextKey("@name")}
            """, "1") + ";\n" + $"""
            SELECT p.customapirequestparameterid, p.uniquename, p.type, p.isoptional, p.logicalentityname
            FROM {s.DataTable(CustomApiTables.RequestParameter)} AS p JOIN {s.DataTable(CustomApiTables.Api)} AS c ON c.customapiid = p.customapiid
            WHERE {d.TextKey("c.uniquename")} = {d.TextKey("@name")} AND p.uniquename IS NOT NULL;
            SELECT p.customapiresponsepropertyid, p.uniquename, p.type, CAST({d.BooleanLiteral(false)} AS {d.ColumnType(SqlColumnKind.Boolean)}), p.logicalentityname
            FROM {s.DataTable(CustomApiTables.ResponseProperty)} AS p JOIN {s.DataTable(CustomApiTables.Api)} AS c ON c.customapiid = p.customapiid
            WHERE {d.TextKey("c.uniquename")} = {d.TextKey("@name")} AND p.uniquename IS NOT NULL;
            """);
        cmd.AddParameter("@name", DbType.String, uniqueName, 128);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var (id, name, message) = (rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? (Guid?)null : rd.GetGuid(2));
        var (binding, bound) = (rd.IsDBNull(3) ? 0 : rd.GetInt32(3), rd.IsDBNull(4) ? null : rd.GetString(4));
        var (isFunction, workflow) = (!rd.IsDBNull(5) && rd.GetBoolean(5), !rd.IsDBNull(6) && rd.GetBoolean(6));
        var allowed = rd.IsDBNull(7) ? 0 : rd.GetInt32(7);

        async Task<Dictionary<string, CustomApiImportRules.ExistingField>> FieldsAsync()
        {
            await rd.NextResultAsync(ct);
            var fields = new Dictionary<string, CustomApiImportRules.ExistingField>(StringComparer.OrdinalIgnoreCase);
            while (await rd.ReadAsync(ct))
                fields.TryAdd(rd.GetString(1), new(rd.GetGuid(0), rd.IsDBNull(2) ? 0 : rd.GetInt32(2), !rd.IsDBNull(3) && rd.GetBoolean(3),
                    rd.IsDBNull(4) ? null : rd.GetString(4)));
            return fields;
        }

        var parameters = await FieldsAsync();
        var properties = await FieldsAsync();
        return new(id, name, message, binding, bound, isFunction, workflow, allowed, parameters, properties);
    }

    /// <summary>The rows of one API, in write order (see <see cref="PlanCustomApisAsync"/>).</summary>
    private async Task<List<RowWrite>> CustomApiRowsAsync(DataSession s, SolutionPackage package, PackageCustomApi api,
        CustomApiImportRules.Existing? existing, CancellationToken ct)
    {
        var rows = new List<RowWrite>();
        var name = existing?.UniqueName ?? api.UniqueName;
        var apiId = existing?.Id ?? DerivedId(CustomApiTables.Api, name);
        string? Kept(XElement? xml) => xml?.ToString(SaveOptions.DisableFormatting);

        var messageId = existing?.MessageId ?? SdkMessageCatalog.MessageId(name);
        var messageExists = await ExistsAsync(s, ApiMessage, messageId, ct);
        var message = new Entity(ApiMessage, messageId) { ["isprivate"] = api.IsPrivate };
        if (!messageExists)
        {
            message["name"] = name;
            message["categoryname"] = CustomApiTables.MessageCategory;
        }
        rows.Add(new(message, !messageExists, ImportJobLog.CustomApis, messageId.ToString("D"), name, 0, null, IsComponent: false));

        var entity = (api.BoundEntity ?? "none").ToLowerInvariant();
        var filterId = await ScalarAsync(s, s.Dialect.Limit($"SELECT sdkmessagefilterid FROM {s.DataTable(ApiFilter)} WHERE sdkmessageid = @message", "1"), ct,
            ("@message", messageId)) as Guid?;
        var filter = new Entity(ApiFilter, filterId ?? SdkMessageCatalog.FilterId(name, entity))
        {
            ["sdkmessageid"] = new EntityReference(ApiMessage, messageId),
            ["iscustomprocessingstepallowed"] = api.AllowedStepType != 0,
        };
        if (filterId is null)
        {
            filter["name"] = $"{name}:{entity}";
            filter["primaryobjecttypecode"] = entity;
            filter["secondaryobjecttypecode"] = "none";
            filter["isvisible"] = true;
        }
        rows.Add(new(filter, filterId is null, ImportJobLog.CustomApis, entity, name, 0, null, IsComponent: false, ApiFilterEntity: entity));

        var row = new Entity(CustomApiTables.Api, apiId)
        {
            [CustomApiTables.Name] = api.Name,
            [CustomApiTables.DisplayName] = Text(api.DisplayName),
            [CustomApiTables.Description] = Text(api.Description),
            [CustomApiTables.IsPrivate] = api.IsPrivate,
            [CustomApiTables.ExecutePrivilegeName] = api.ExecutePrivilegeName,
            [CustomApiTables.PluginTypeId] = api.PluginTypeKey is { } key ? new EntityReference("plugintype", key) : null,
            [SystemTableRules.ComponentXml] = Kept(api.Kept),
        };
        if (existing is null)
        {
            row[CustomApiTables.UniqueName] = name;
            row[CustomApiTables.BindingType] = new OptionSetValue(api.BindingType);
            row[CustomApiTables.BoundEntityLogicalName] = api.BoundEntity?.ToLowerInvariant();
            row[CustomApiTables.AllowedCustomProcessingStepType] = new OptionSetValue(api.AllowedStepType);
            row[CustomApiTables.IsFunction] = api.IsFunction;
            row[CustomApiTables.WorkflowSdkStepEnabled] = api.WorkflowEnabled;
            row[CustomApiTables.SdkMessageId] = new EntityReference(ApiMessage, messageId);
        }
        rows.Add(new(row, existing is null, ImportJobLog.CustomApis, apiId.ToString("D"), name, ComponentType.CustomApi, null));

        foreach (var (field, request) in api.RequestParameters.Select(p => (p, true)).Concat(api.ResponseProperties.Select(p => (p, false))))
        {
            var table = request ? CustomApiTables.RequestParameter : CustomApiTables.ResponseProperty;
            var stored = (request ? existing?.Parameters : existing?.Properties)?.GetValueOrDefault(field.UniqueName);
            var target = new Entity(table, stored?.Id ?? DerivedId(table, $"{name}.{field.UniqueName}"))
            {
                [CustomApiTables.Name] = field.Name,
                [CustomApiTables.DisplayName] = Text(field.DisplayName),
                [CustomApiTables.Description] = Text(field.Description),
                [SystemTableRules.ComponentXml] = Kept(field.Kept),
            };
            if (stored is null)
            {
                target[CustomApiTables.UniqueName] = field.UniqueName;
                target[CustomApiTables.ApiId] = new EntityReference(CustomApiTables.Api, apiId);
                target[CustomApiTables.Type] = new OptionSetValue(field.Type);
                target[CustomApiTables.LogicalEntityName] = field.LogicalEntityName?.ToLowerInvariant();
                if (request) target[CustomApiTables.IsOptional] = field.IsOptional == true;
            }
            rows.Add(new(target, stored is null, ImportJobLog.CustomApis, field.UniqueName, field.Name, 0, null, IsComponent: false));
        }
        return rows;
    }

    /// <summary>
    /// After an API's rows are written: an info line in the server log, and a warning when its <c>executeprivilegename</c>
    /// names no privilege of the organization (nobody holds it, so only System Administrators and internal calls pass).
    /// </summary>
    private async Task LogCustomApiAsync(DataSession s, RowWrite row, ImportJobLog log, CancellationToken ct)
    {
        logger.LogInformation("Solution import: custom API {CustomApi} {Action}.", row.LogName, row.Create ? "created" : "updated");
        if (row.Target.GetAttributeValue<string>(CustomApiTables.ExecutePrivilegeName) is not { Length: > 0 } privilege) return;
        if (await ScalarAsync(s, s.Dialect.Limit($"SELECT 1 FROM {s.DataTable("privilege")} WHERE {s.Dialect.TextKey("name")} = {s.Dialect.TextKey("@name")}", "1"), ct, ("@name", privilege)) is not null) return;
        log.Warn(ImportJobLog.CustomApis, row.LogId, row.LogName,
            $"The executeprivilegename '{privilege}' of custom API '{row.LogName}' names no privilege of the organization: no user holds it, so only System Administrators can call the API.");
    }

    // ---- custom controls (plan 3.5 UI-1d) ---------------------------------------------------------------------------

    /// <summary>
    /// The custom controls of the file as rows (ruling R3): for each its <c>customcontrol</c>, found by <c>name</c> ignoring case and
    /// updated (its <c>name</c> is not written again), else created with the id <see cref="DerivedId"/> gives the name; a component
    /// of type 66 keyed by its name, with its <c>CustomControl</c> element in <c>xrmverse_componentxml</c>. The files of its
    /// manifest's folder replace the control's files once the row is written.
    /// </summary>
    private static async Task<List<RowWrite>> PlanCustomControlsAsync(DataSession s, SolutionPackage package, CancellationToken ct)
    {
        var rows = new List<RowWrite>();
        foreach (var control in package.CustomControls)
        {
            Guid? existingId = null;
            string? storedName = null;
            await using (var cmd = s.Command(s.Dialect.Limit($"""
                             SELECT {s.Dialect.Quote(CustomControlSchema.Table + "id")}, {s.Dialect.Quote(CustomControlSchema.Name)} FROM {s.DataTable(CustomControlSchema.Table)}
                             WHERE LOWER({s.Dialect.Quote(CustomControlSchema.Name)}) = LOWER(@name)
                             """, "1")))
            {
                cmd.AddParameter("@name", DbType.String, control.Name, 200);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) (existingId, storedName) = (rd.GetGuid(0), rd.GetString(1));
            }

            var name = storedName ?? control.Name;
            var id = existingId ?? DerivedId(CustomControlSchema.Table, control.Name);
            var target = new Entity(CustomControlSchema.Table, id)
            {
                [CustomControlSchema.Manifest] = control.Manifest,
                [CustomControlSchema.Version] = control.Version,
                [CustomControlSchema.CompatibleDataTypes] = control.CompatibleDataTypes,
                [SystemTableRules.ComponentXml] = control.Source.ToString(SaveOptions.DisableFormatting),
            };
            if (existingId is null) target[CustomControlSchema.Name] = control.Name;
            rows.Add(new(target, existingId is null, ImportJobLog.CustomControls, id.ToString("D"), name, ComponentType.CustomControl, name,
                ControlFiles: control.Files));
        }
        return rows;
    }

    // ---- environment variables (#167) -------------------------------------------------------------------------------

    /// <summary>
    /// The environment variables of the file as rows (ruling E3): for each its <c>environmentvariabledefinition</c>, found by
    /// <c>schemaname</c> ignoring case and updated (its <c>schemaname</c> is not written again: it cannot change), else created
    /// with the id <see cref="DerivedId"/> gives the name, a component of type 380 keyed by its name (with what xrmverse does not
    /// model of its element, the label sets included, in <c>xrmverse_componentxml</c>); then, when its folder has a value file,
    /// its <c>environmentvariablevalue</c>: the definition's value row updated (the file wins), else created. Without a value file
    /// an existing value is left as it is. The base language's labels fill <c>displayname</c> and <c>description</c>.
    /// </summary>
    private async Task<List<RowWrite>> PlanEnvironmentVariablesAsync(DataSession s, SolutionPackage package, CancellationToken ct)
    {
        var rows = new List<RowWrite>();
        var language = package.Solution.LanguageCode;
        foreach (var variable in package.EnvironmentVariables)
        {
            Guid? existingId = null;
            string? storedName = null;
            await using (var cmd = s.Command(s.Dialect.Limit($"""
                             SELECT {s.Dialect.Quote(EnvironmentVariableSchema.Definition + "id")}, {s.Dialect.Quote(EnvironmentVariableSchema.SchemaName)}
                             FROM {s.DataTable(EnvironmentVariableSchema.Definition)}
                             WHERE LOWER({s.Dialect.Quote(EnvironmentVariableSchema.SchemaName)}) = LOWER(@name)
                             """, "1")))
            {
                cmd.AddParameter("@name", DbType.String, variable.SchemaName, 100);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) (existingId, storedName) = (rd.GetGuid(0), rd.GetString(1));
            }

            var name = storedName ?? variable.SchemaName;
            var id = existingId ?? DerivedId(EnvironmentVariableSchema.Definition, variable.SchemaName);
            var definition = new Entity(EnvironmentVariableSchema.Definition, id)
            {
                [EnvironmentVariableSchema.DisplayName] = EnvironmentVariableReader.BaseLabel(variable, EnvironmentVariableSchema.DisplayName, language),
                [EnvironmentVariableSchema.Description] = EnvironmentVariableReader.BaseLabel(variable, EnvironmentVariableSchema.Description, language),
                [EnvironmentVariableSchema.DefaultValue] = variable.DefaultValue,
                [EnvironmentVariableSchema.Type] = new OptionSetValue(variable.Type),
                [EnvironmentVariableSchema.IsRequired] = variable.IsRequired,
                [EnvironmentVariableSchema.SecretStore] = new OptionSetValue(variable.SecretStore),
                [EnvironmentVariableSchema.ValueSchema] = variable.ValueSchema,
                [EnvironmentVariableSchema.IntroducedVersion] = variable.IntroducedVersion,
                [EnvironmentVariableSchema.Hint] = variable.Hint,
                [SystemTableRules.ComponentXml] = variable.Source.ToString(SaveOptions.DisableFormatting),
            };
            if (existingId is null) definition[EnvironmentVariableSchema.SchemaName] = variable.SchemaName;
            rows.Add(new(definition, existingId is null, ImportJobLog.EnvironmentVariables, id.ToString("D"), name,
                ComponentType.EnvironmentVariableDefinition, name));

            if (!variable.HasValueFile) continue;
            var valueId = existingId is { } definitionId
                ? await ScalarAsync(s, s.Dialect.Limit($"""
                      SELECT {s.Dialect.Quote(EnvironmentVariableSchema.Value + "id")} FROM {s.DataTable(EnvironmentVariableSchema.Value)}
                      WHERE {s.Dialect.Quote(EnvironmentVariableSchema.DefinitionLookup)} = @definition
                      """, "1"), ct, ("@definition", definitionId)) as Guid?
                : null;
            var value = new Entity(EnvironmentVariableSchema.Value, valueId ?? DerivedId(EnvironmentVariableSchema.Value, variable.SchemaName))
            {
                [EnvironmentVariableSchema.ValueColumn] = variable.Value,
            };
            if (valueId is null)
            {
                value[EnvironmentVariableSchema.SchemaName] = name;
                value[EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, id);
            }
            rows.Add(new(value, valueId is null, ImportJobLog.EnvironmentVariables, id.ToString("D"), name, 0, null, IsComponent: false));
        }
        return rows;
    }

    /// <summary>
    /// After the rows are written: one warning for each definition of the file that has neither a value (none, or an empty one)
    /// nor a default value (ruling E3), naming the CLI command that sets one.
    /// </summary>
    private static async Task WarnEnvironmentVariablesWithoutValueAsync(DataSession s, Plan plan, ImportJobLog log, CancellationToken ct)
    {
        foreach (var row in plan.Rows.Where(r => r.ComponentType == ComponentType.EnvironmentVariableDefinition))
        {
            var d = s.Dialect;
            var definitionId = d.Quote(EnvironmentVariableSchema.Definition + "id");
            var missing = await ScalarAsync(s, d.Limit($"""
                SELECT 1 FROM {s.DataTable(EnvironmentVariableSchema.Definition)} AS d
                WHERE d.{definitionId} = @id
                  AND {d.IsNull(d.DataLength($"d.{d.Quote(EnvironmentVariableSchema.DefaultValue)}"), "0")} = 0
                  AND NOT EXISTS (SELECT 1 FROM {s.DataTable(EnvironmentVariableSchema.Value)} AS v
                      WHERE v.{d.Quote(EnvironmentVariableSchema.DefinitionLookup)} = d.{definitionId}
                        AND {d.IsNull(d.DataLength($"v.{d.Quote(EnvironmentVariableSchema.ValueColumn)}"), "0")} > 0)
                """, "1"), ct, ("@id", row.Target.Id));
            if (missing is not null)
                log.Warn(ImportJobLog.EnvironmentVariables, row.LogId, row.LogName,
                    $"Environment variable '{row.LogName}' has no value and no default value: set one with `xrmd envvar set`.");
        }
    }

    /// <summary>
    /// The 0/1 element <paramref name="name"/> of a form's or chart's export (<c>IsDefault</c>, <c>FormActivationState</c>,
    /// <c>isdefault</c>); null when it is missing or empty. <paramref name="noun"/> names the component in the error.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for any other value.</exception>
    private static bool? FormFlag(XElement form, string name, string formId, string noun = "Form") =>
        form.Element(name)?.Value.Trim() switch
        {
            null or "" => null,
            "1" => true,
            "0" => false,
            var other => throw XrmdFaultException.InvalidArgument($"{noun} {formId} has the value '{other}' for {name}: 0 or 1 is expected."),
        };

    /// <summary>The row of a view, form, system chart, site map or app module: its identity columns and its XML (plan ruling).</summary>
    private async Task<RowWrite> OpaqueRowAsync(DataSession s, OpaqueComponent opaque, int language, CancellationToken ct)
    {
        var xml = opaque.Xml;
        var name = LocalizedName(xml, language);
        switch (opaque.Type)
        {
            case ComponentType.SavedQuery:
            {
                var id = Guid.Parse(opaque.Id);
                var target = new Entity("savedquery", id)
                {
                    ["name"] = name ?? opaque.Id,
                    ["returnedtypecode"] = opaque.EntityLogicalName,
                    ["fetchxml"] = xml.Element("fetchxml")?.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting),
                    ["layoutxml"] = xml.Element("layoutxml")?.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting),
                    ["querytype"] = int.TryParse((string?)xml.Element("querytype"), out var queryType) ? queryType : 0,
                    ["isdefault"] = (string?)xml.Element("isdefault") == "1",
                    ["xrmverse_componentxml"] = xml.ToString(SaveOptions.DisableFormatting),
                };
                return new(target, !await ExistsAsync(s, "savedquery", id, ct), ImportJobLog.Nodes, opaque.Id, name, opaque.Type, opaque.Id);
            }
            case ComponentType.SystemForm:
            {
                var id = Guid.Parse(opaque.Id);
                if (opaque.FormType is not { } formType || !FormTypes.TryGetValue(formType, out var type))
                    throw XrmdFaultException.InvalidArgument(
                        opaque.EntityLogicalName is null
                            ? $"Dashboard {opaque.Id} has the form type '{opaque.FormType}', which xrmverse does not support."
                            : $"Form {opaque.Id} of entity '{opaque.EntityLogicalName}' has the type '{opaque.FormType}', which xrmverse does not support.");
                // A system dashboard (plan 3.5 UI-1f, ruling F1) is a root Dashboard element: a form of no table ("none"), its
                // <form> under FormXml/forms, with its description.
                var dashboard = opaque.EntityLogicalName is null;
                var target = new Entity("systemform", id)
                {
                    ["name"] = name ?? opaque.Id,
                    ["objecttypecode"] = opaque.EntityLogicalName ?? ComponentType.DashboardObjectTypeCode,
                    ["formxml"] = (dashboard ? xml.Element("FormXml")?.Element("forms")?.Element("form") : xml.Element("form"))
                        ?.ToString(SaveOptions.DisableFormatting),
                    ["type"] = new OptionSetValue(type),
                    // Learn's defaults (plan 3.5 UI-1b): not the default form, active, when the export has no element.
                    ["isdefault"] = FormFlag(xml, "IsDefault", opaque.Id) ?? false,
                    ["formactivationstate"] = new OptionSetValue(FormFlag(xml, "FormActivationState", opaque.Id) is { } active ? (active ? 1 : 0) : 1),
                    ["xrmverse_componentxml"] = xml.ToString(SaveOptions.DisableFormatting),
                };
                if (dashboard) target["description"] = LocalizedLabel(xml, "Descriptions", "Description", language);
                return new(target, !await ExistsAsync(s, "systemform", id, ct), ImportJobLog.Nodes, opaque.Id, name, opaque.Type, opaque.Id);
            }
            case ComponentType.SavedQueryVisualization:
            {
                var id = Guid.Parse(opaque.Id);
                var target = new Entity(ChartSchema.Table, id)
                {
                    [ChartSchema.Name] = name ?? opaque.Id,
                    [ChartSchema.PrimaryEntityTypeCode] = opaque.EntityLogicalName,
                    [ChartSchema.Description] = LocalizedLabel(xml, "Descriptions", "Description", language),
                    // The two XML documents are the single child of their element, stored as the export gives them.
                    [ChartSchema.DataDescription] = xml.Element(ChartSchema.DataDescription)?.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting),
                    [ChartSchema.PresentationDescription] =
                        xml.Element(ChartSchema.PresentationDescription)?.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting),
                    [ChartSchema.IsDefault] = FormFlag(xml, ChartSchema.IsDefault, opaque.Id, "Chart") ?? false,
                    // Learn's defaults: an ASP.NET chart (0) for data and interaction centric dashboards (0), when the export has no element.
                    [ChartSchema.ChartType] = new OptionSetValue(ChartChoice(xml, ChartSchema.ChartType, opaque.Id)),
                    [ChartSchema.Type] = new OptionSetValue(ChartChoice(xml, ChartSchema.Type, opaque.Id)),
                    [ChartSchema.IntroducedVersion] = ((string?)xml.Element("IntroducedVersion"))?.Trim() is { Length: > 0 } version ? version : null,
                    ["xrmverse_componentxml"] = xml.ToString(SaveOptions.DisableFormatting),
                };
                if (Guid.TryParse((string?)xml.Element(ChartSchema.WebResourceId), out var webResource) && webResource != Guid.Empty)
                    target[ChartSchema.WebResourceId] = new EntityReference("webresource", webResource);
                return new(target, !await ExistsAsync(s, ChartSchema.Table, id, ct), ImportJobLog.Nodes, opaque.Id, name, opaque.Type, opaque.Id);
            }
            case ComponentType.SiteMap:
            {
                var existing = await ScalarAsync(s, s.Dialect.Limit($"SELECT sitemapid FROM {s.DataTable("sitemap")} WHERE {s.Dialect.TextKey("sitemapnameunique")} = {s.Dialect.TextKey("@name")}", "1"), ct,
                    ("@name", opaque.Id)) as Guid?;
                var target = new Entity("sitemap", existing ?? DerivedId("sitemap", opaque.Id))
                {
                    ["sitemapname"] = name ?? opaque.Id,
                    ["sitemapnameunique"] = opaque.Id,
                    ["sitemapxml"] = xml.Element("SiteMap")?.ToString(SaveOptions.DisableFormatting),
                    ["xrmverse_componentxml"] = xml.ToString(SaveOptions.DisableFormatting),
                };
                return new(target, existing is null, ImportJobLog.Nodes, opaque.Id, name, opaque.Type, opaque.Id);
            }
            case ComponentType.AppModule:
            {
                var existing = await ScalarAsync(s, s.Dialect.Limit($"SELECT appmoduleid FROM {s.DataTable("appmodule")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1"), ct,
                    ("@name", opaque.Id)) as Guid?;
                var target = new Entity("appmodule", existing ?? DerivedId("appmodule", opaque.Id))
                {
                    ["name"] = name ?? opaque.Id,
                    ["uniquename"] = opaque.Id,
                    ["descriptor"] = xml.ToString(SaveOptions.DisableFormatting),
                };
                target["appmoduleidunique"] = target.Id;
                foreach (var (column, value) in AppModuleXml.Parse(xml).Columns)
                    // The choice columns are option set values; the rest (integers, booleans, ids, text) are plain.
                    target[column] = column is "navigationtype" or "statecode" or "statuscode" ? new OptionSetValue((int)value!) : value;
                return new(target, existing is null, ImportJobLog.Nodes, opaque.Id, name, opaque.Type, opaque.Id);
            }
            default:
                throw XrmdFaultException.InvalidArgument($"Component type {opaque.Type} is not supported by xrmverse.");
        }
    }

    /// <summary>The choice <paramref name="name"/> of a chart's export (<c>charttype</c>, <c>type</c>); 0 when it is missing or empty.</summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not 0 or 1.</exception>
    private static int ChartChoice(XElement chart, string name, string chartId) =>
        chart.Element(name)?.Value.Trim() switch
        {
            null or "" => 0,
            "0" => 0,
            "1" => 1,
            var other => throw XrmdFaultException.InvalidArgument($"Chart {chartId} has the value '{other}' for {name}: 0 or 1 is expected."),
        };

    /// <summary>The label of the base language (else the first one) of <c>container/element</c>; null when there is none.</summary>
    private static string? LocalizedLabel(XElement component, string container, string element, int language)
    {
        var labels = component.Element(container)?.Elements(element).ToList() ?? [];
        var chosen = labels.FirstOrDefault(n => (string?)n.Attribute("languagecode") == language.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     ?? labels.FirstOrDefault();
        return (string?)chosen?.Attribute("description") is { Length: > 0 } text ? text : null;
    }

    /// <summary>A stable id for a component the file names only by its unique name (site maps, app modules).</summary>
    private static Guid DerivedId(string table, string uniqueName) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"xrmd:{table}:{uniqueName.ToLowerInvariant()}")));

    /// <summary>The <c>LocalizedName</c> of the base language (else the first one); null when there is none.</summary>
    private static string? LocalizedName(XElement component, int language)
    {
        var names = component.Element("LocalizedNames")?.Elements("LocalizedName").ToList() ?? [];
        var chosen = names.FirstOrDefault(n => (string?)n.Attribute("languagecode") == language.ToString(System.Globalization.CultureInfo.InvariantCulture))
                     ?? names.FirstOrDefault();
        return (string?)chosen?.Attribute("description");
    }

    /// <summary>
    /// The rules of the system tables for every row write (reserved role ids, <c>roletemplateid</c>, steps impersonating
    /// another user), checked before the transaction: the impersonation rule loads security profiles on connections of its
    /// own, which must not wait for the import's locks.
    /// </summary>
    private async Task EnforceSystemTableRulesAsync(DataSession s, Caller caller, List<RowWrite> rows, CancellationToken ct)
    {
        var systemUserId = await securityCache.SystemUserIdAsync(ct);
        foreach (var row in rows.Where(r => r.Target.LogicalName != EnvironmentVariableSchema.Value))
            // A value row's definition may be one this import creates: the core checks it in the transaction (#167, E2).
            // The rows of a custom API are the import's own to write (plan 15, C10): its rule spares the internal caller only.
            // So are the processes (plan 2.14, ruling W3: no request authors them; the import writes them through the WorkflowStore).
            // So are the custom controls (plan 3.5 UI-1d, R1: the import writes them, no request does).
            await SystemTableRules.EnforceAsync(Request(row),
                IsCustomApiRow(row) || row.Workflow is not null || row.ComponentType == ComponentType.CustomControl ? Caller.Internal : caller,
                systemUserId, securityCache,
                store, s, ct);
    }

    /// <summary>The request of a row write, without the import-only columns (no request may set them: <see cref="WriteRowAsync"/>).</summary>
    private static OrganizationRequest Request(RowWrite row)
    {
        var target = WithoutImportOnlyColumns(row.Target);
        return row.Create ? new CreateRequest { Target = target } : new UpdateRequest { Target = target };
    }

    /// <summary><paramref name="target"/> without its import-only columns (<see cref="SystemTableRules.IsImportOnly"/>).</summary>
    private static Entity WithoutImportOnlyColumns(Entity target)
    {
        if (!target.Attributes.Keys.Any(name => SystemTableRules.IsImportOnly(target.LogicalName, name))) return target;
        var copy = new Entity(target.LogicalName, target.Id);
        foreach (var (name, value) in target.Attributes.Where(a => !SystemTableRules.IsImportOnly(target.LogicalName, a.Key))) copy[name] = value;
        return copy;
    }

    /// <summary>
    /// Writes the import-only columns of <paramref name="target"/> (the kept XML of a component, of the solution, an app
    /// module's descriptor) with SQL of its own, after the row's write: no request may set them (<see cref="SystemTableRules"/>).
    /// </summary>
    private static async Task WriteImportOnlyColumnsAsync(DataSession s, Entity target, CancellationToken ct)
    {
        foreach (var (column, value) in target.Attributes.Where(a => SystemTableRules.IsImportOnly(target.LogicalName, a.Key)))
        {
            // The column name is one of SystemTableRules' constants, never a caller's text.
            await using var cmd = s.Command(
                $"UPDATE {s.DataTable(target.LogicalName)} SET {s.Dialect.Quote(column)} = @value " +
                $"WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(target.LogicalName))} = @id");
            cmd.AddParameter("@value", DbType.String, (object?)(value as string) ?? DBNull.Value, -1);
            cmd.AddParameter("@id", DbType.Guid, target.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ---- apply (one transaction) ------------------------------------------------------------------------------------

    /// <summary>A component to record in <c>solutioncomponent</c>; <paramref name="Parent"/>: the entity of an attribute.</summary>
    /// <param name="Source">The key of its payload in <see cref="SolutionPackage.Sources"/> (plan 2.6, ruling M5): its own, or
    /// for a plug-in type its assembly's and for a step image its step's.</param>
    /// <param name="LayerOnly">A system column of the file (ruling R11): layered and marked, but not recorded in <c>solutioncomponent</c>
    /// (as with <c>CreateAttribute</c>, a table's system columns are not components of a solution).</param>
    private sealed record Tracked(int Type, Guid ObjectId, string? RootKey, Guid? Parent = null, (int Type, string Key)? Source = null,
        bool LayerOnly = false);

    /// <summary>The payload key of an attribute (<see cref="Layers.LayerPayload.Key"/>).</summary>
    private static (int, string) AttributeSource(string entity, string attribute) =>
        Layers.LayerPayload.Key(ComponentType.Attribute, $"{entity}.{attribute}");

    /// <summary>
    /// The rows of the solution <paramref name="name"/> and of its holding solution <c>{name}_Upgrade</c> as one comparable
    /// text (id, managed, version, publisher of each). With <paramref name="locked"/> (in the transaction, after the metadata
    /// lock), the rows read, and the range of each name, are locked until the commit: no other session can create, rename or
    /// change them meanwhile.
    /// </summary>
    private static async Task<string> SolutionStateAsync(DataSession s, string name, bool locked, CancellationToken ct)
    {
        var d = s.Dialect;
        if (locked)
        {
            // Plan 2.7b: the key range of each name (SQL Server's HOLDLOCK) is a key lock of its own on PostgreSQL.
            await s.KeyLockAsync("solution", name, ct);
            await s.KeyLockAsync("solution", name + Layers.ManagedImport.HoldingSuffix, ct);
        }
        await using var cmd = s.Command(
            $"SELECT solutionid, uniquename, ismanaged, version, publisherid FROM {s.DataTable("solution")}{(locked ? d.LockHint(RowLock.UpdateRange) : "")} " +
            $"WHERE {d.TextKey("uniquename")} IN ({d.TextKey("@name")}, {d.TextKey("@holding")}) ORDER BY uniquename, solutionid{(locked ? d.LockClause(RowLock.UpdateRange) : "")}");
        cmd.AddParameter("@name", DbType.String, name, SolutionComponentTracker.UniqueNameMaxLength);
        cmd.AddParameter("@holding", DbType.String, name + Layers.ManagedImport.HoldingSuffix, SolutionComponentTracker.UniqueNameMaxLength + Layers.ManagedImport.HoldingSuffix.Length);
        var state = new StringBuilder();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            state.Append(rd.GetGuid(0)).Append('|').Append(rd.GetString(1).ToUpperInvariant()).Append('|')
                .Append(!rd.IsDBNull(2) && rd.GetBoolean(2)).Append('|').Append(rd.IsDBNull(3) ? "" : rd.GetString(3)).Append('|')
                .Append(rd.IsDBNull(4) ? "" : rd.GetGuid(4).ToString()).Append(';');
        return state.ToString();
    }

    /// <summary>
    /// Under the metadata lock (final review I1): the solution rows the import was decided on are read again, locked, and
    /// the import classified again. A concurrent import, upgrade or uninstall that changed them meanwhile (another install of
    /// the same solution, a higher version, a pending upgrade) fails this import with a 409; nothing is changed.
    /// </summary>
    /// <exception cref="XrmdFaultException">409 when the solution changed since the import was prepared.</exception>
    private async Task DemandUnchangedAsync(DataSession s, Plan plan, ImportOptions options, CancellationToken ct)
    {
        if (plan.SolutionState is null) return;
        var name = plan.Package.Solution.UniqueName;
        var changed = await SolutionStateAsync(s, name, locked: true, ct) != plan.SolutionState;
        if (!changed)
        {
            try
            {
                changed = await managed.ClassifyAsync(s, plan.Package, options, ct) != plan.Kind;
            }
            catch (XrmdFaultException)
            {
                changed = true;
            }
        }
        if (changed)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The solution '{name}' changed while it was being imported (another import, upgrade or uninstall of it ran meanwhile). Nothing was changed; retry the import.", 409);
    }

    private async Task<ImportResult> ApplyAsync(DataSession s, Caller caller, Plan plan, ImportOptions options, Guid jobId, ImportJobLog log,
        CancellationToken ct)
    {
        var package = plan.Package;
        // A managed import writes its components and its solution row as the importing user: a layer operation, which the
        // managed properties and the managed-component rules do not apply to (plan 2.6, ruling R2).
        using var layerOperation = plan.Kind == Layers.ImportKind.Unmanaged ? null : Layers.ManagedProperties.LayerOperation();
        // The metadata write lock before anything is written; then the decision of PrepareAsync again (final review I1).
        await store.BeginWriteAsync(s, ct);
        await DemandUnchangedAsync(s, plan, options, ct);
        Layers.ManagedPlan? layered = null;
        if (plan.IsManaged)
        {
            // The layers are read before the file's metadata is written and written after it: the metadata locks come first.
            layered = await managed.PlanAsync(s, plan.Kind, plan.SolutionId, plan.SolutionName, ManagedComponents(plan),
                options.OverwriteUnmanagedCustomizations, log, ct);
            plan = plan with { Kept = layered.Kept };
        }
        var tracked = await ApplyFragmentsAsync(s, caller, plan, ApplyMode.Import, log, ct);

        await TrackAsync(s, plan, tracked, log, ct);
        if (layered is not null)
        {
            await managed.WriteLayersAsync(s, package, layered, tracked.Select(t => (t.Type, t.ObjectId, t.Source)).ToList(), ct);
            await MarkManagedAsync(s, tracked, ct);
        }
        else
        {
            // An unmanaged import that touches a layered component gives it its Active marker (plan 2.6, ruling M4), unless it
            // changes what the component's managed properties forbid (ruling M13): then the whole import is refused.
            foreach (var component in tracked)
            {
                await DemandManagedPropertiesAsync(s, component, package, ct);
                await tracker.MarkChangedAsync(s, component.Type, component.ObjectId, ct);
            }
        }

        var stopped = DateTime.UtcNow;
        await core.ExecuteCoreAsync(new CreateRequest { Target = ImportJob(jobId, package.Solution.UniqueName, plan.SolutionId, 100, log, stopped,
            log.ToXml(100, stopped)) }, caller, s, ct);
        return new ImportResult(jobId, plan.SolutionId, log.Warnings.ToList());
    }

    /// <summary>
    /// Restores components from a package of layer payloads (<see cref="Layers.LayerPayload.ToPackage"/>; plan 2.6, ruling M5)
    /// on <paramref name="s"/>, the caller's transaction: the import's checks and write path in <see cref="ApplyMode.Restore"/>,
    /// as <see cref="Caller.Internal"/>. The caches follow the commit of <paramref name="s"/>.
    /// </summary>
    /// <param name="s">The caller's session, in a transaction.</param>
    /// <param name="package">The merged payloads.</param>
    /// <param name="requested">The components to restore, by type (<see cref="ComponentType.Normalize"/>d) and id: only
    /// these are written; what else the package carries is there for the reader (ruling R9).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The warnings of the restore (what an import would log, the option values kept, the cascade configurations kept).</returns>
    /// <exception cref="XrmdFaultException">400 naming the component and property for a difference the store cannot apply
    /// (a shorter <c>MaxLength</c>, another attribute type, ...); the faults of the core operations.</exception>
    internal async Task<IReadOnlyList<string>> RestoreAsync(DataSession s, SolutionPackage package, IReadOnlySet<(int Type, Guid Id)> requested,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(requested);
        var log = new ImportJobLog(DateTime.UtcNow);
        try
        {
            var plan = await PrepareRestoreAsync(s, package, requested, log, ct);
            await ApplyFragmentsAsync(s, Caller.Internal, plan, ApplyMode.Restore, log, ct);
        }
        catch (Exception ex) when (AsFault(ex) is { } fault && !ReferenceEquals(fault, ex))
        {
            throw fault;
        }
        return log.Warnings.ToList();
    }

    /// <summary>
    /// Applies the components of <paramref name="plan"/>: metadata first (session rule: option sets, entities, relationships
    /// and lookups, system attributes, what xrmverse does not model), then (<see cref="ApplyMode.Import"/> only) the publisher and
    /// solution rows, then the rows in the §7 order. Registers the cache invalidation on the commit of <paramref name="s"/>.
    /// </summary>
    /// <returns>The components written, for <c>solutioncomponent</c> (<see cref="TrackAsync"/>, import only).</returns>
    private async Task<List<Tracked>> ApplyFragmentsAsync(DataSession s, Caller caller, Plan plan, ApplyMode mode, ImportJobLog log,
        CancellationToken ct)
    {
        var package = plan.Package;
        var tracked = new List<Tracked>();

        // Metadata first (session rule): option sets, entities (first pass), relationships and lookups (second pass), the
        // calculated (plan 2.12a, ruling K13) and rollup (plan 2.12b, ruling R14) columns last.
        if (mode == ApplyMode.Restore)
        {
            await RestoreMetadataAsync(s, package, plan.Targets!, log, ct);
        }
        else
        {
            await ApplyOptionSetsAsync(s, plan, log, tracked, ct);
            await ApplyEntitiesAsync(s, plan, log, tracked, ct);
            await ApplyRelationshipsAsync(s, plan, log, tracked, ct);
            await ApplySystemAttributesAsync(s, plan, log, tracked, ct);
            await ApplyComputedAttributesAsync(s, plan, log, tracked, ct);
            await KeepUnmodelledAsync(s, plan, log, ct);
        }

        // Then the rows, in the §7 order.
        if (mode == ApplyMode.Import) await ApplyPublisherAndSolutionAsync(s, caller, plan, ct);
        var keptRows = KeptRows(plan);
        foreach (var row in plan.Rows)
        {
            // An existing row a layer above keeps (plan 2.6, ruling M7) is not written, nor are its privileges or permissions.
            var keep = keptRows.Contains(row);
            // An app module belongs to the importing solution's publisher (known only after the rows are planned).
            if (row.ComponentType == ComponentType.AppModule && mode == ApplyMode.Import)
                row.Target["publisherid"] = new EntityReference("publisher", plan.PublisherId);
            if (!keep && row.Workflow is { } workflow) await WriteWorkflowAsync(s, caller, row, workflow, plan.Prefix, log, ct);
            else if (!keep) await WriteRowAsync(s, caller, row, ct);
            if (!keep && row.ControlFiles is { } controlFiles) await _controlFiles.ReplaceAsync(s, row.Target.Id, controlFiles, ct);
            if (!row.IsComponent) continue;
            if (row.ComponentType == ComponentType.Role && !keep)
                await ApplyRolePrivilegesAsync(s, package.Roles.Single(r => r.Id == row.Target.Id), log, ct);
            if (row.ComponentType == ComponentType.FieldSecurityProfile && !keep)
                await ApplyFieldPermissionsAsync(s, caller, row.Target.Id, plan.FieldPermissions[row.Target.Id], ct);
            log.Add(row.Group, row.LogId, row.LogName);
            if (row.ComponentType == ComponentType.CustomApi) await LogCustomApiAsync(s, row, log, ct);
            tracked.Add(new(row.ComponentType, row.Target.Id, row.RootKey, Source: RowSource(row)));
        }

        // The links of the app modules, after every row they name (roles, site maps) is written.
        foreach (var row in plan.Rows.Where(r => r.ComponentType == ComponentType.AppModule && !keptRows.Contains(r)))
            await ApplyAppModuleLinksAsync(s, row, log, ct);

        // The table and column maps (#143), once every table and column of the file exists.
        if (mode == ApplyMode.Import) await ApplyEntityMapsAsync(s, package, ct);

        // #167 (E3): a definition left with neither a value nor a default is named, so that an administrator sets one.
        if (mode == ApplyMode.Import) await WarnEnvironmentVariablesWithoutValueAsync(s, plan, log, ct);

        // The processes are activated once every row is written (a custom activity's plug-in type, a child workflow's process).
        foreach (var row in plan.Rows.Where(r => r.Workflow is not null && !keptRows.Contains(r)))
            await ActivateImportedAsync(s, row.Workflow!, plan.PublishWorkflows, plan.Prefix, log, ct);

        // After the store has published the metadata (it registered first): roles, privileges and steps were written.
        s.OnCommitted(() =>
        {
            securityCache.Invalidate();
            registry.Invalidate();
            customApis.Invalidate();
            prefixes.Invalidate(); // the import may have created the solution's publisher
        });
        return tracked;
    }

    /// <summary>
    /// The components of a managed import for <see cref="Layers.ManagedImport.PlanAsync"/> (plan 2.6, ruling M7): the global
    /// option sets, tables, columns and lookups (system columns of existing tables included, ruling R11), custom relationships
    /// and component rows of the file, each with its
    /// id in xrmverse before the import (null for one the import creates).
    /// </summary>
    private List<Layers.ManagedComponent> ManagedComponents(Plan plan)
    {
        var package = plan.Package;
        var components = new List<Layers.ManagedComponent>();
        foreach (var optionSet in package.OptionSets.Where(o => o.Name is not null))
        {
            var existing = store.GetOptionSet(optionSet.Name);
            components.Add(new(ComponentType.OptionSet, existing is null ? null : MetadataStore.OptionSetId(optionSet.Name),
                Layers.LayerPayload.Key(ComponentType.OptionSet, optionSet.Name), existing?.IsCustomOptionSet == false,
                ImportJobLog.OptionSets, optionSet.Name, optionSet.Name, $"option set '{optionSet.Name}'"));
        }
        foreach (var entity in package.Entities)
        {
            var published = store.GetEntity(entity.LogicalName);
            components.Add(new(ComponentType.Entity, published?.MetadataId, Layers.LayerPayload.Key(ComponentType.Entity, entity.LogicalName),
                published is not null && MetadataStore.IsSystemEntity(published), ImportJobLog.Entities, entity.LogicalName,
                entity.DisplayName?.UserLocalizedLabel?.Label, $"entity '{entity.LogicalName}'"));
            var attributes = CustomAttributes(entity)
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName && l.IsCustomAttribute == true));
            foreach (var attribute in attributes)
            {
                var current = (published?.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName);
                components.Add(new(ComponentType.Attribute, current?.MetadataId, AttributeSource(entity.LogicalName, attribute.LogicalName),
                    current is not null && !MetadataStore.IsCustomAttribute(published!, current), ImportJobLog.Entities, entity.LogicalName,
                    entity.DisplayName?.UserLocalizedLabel?.Label, $"attribute '{entity.LogicalName}.{attribute.LogicalName}'"));
            }
            // The file's system columns that exist in xrmverse (ruling R11): seeded on a seeded table (its System layer is
            // captured), on a custom table they follow the table.
            if (published is null) continue;
            foreach (var attribute in SystemAttributesOf(entity, package))
            {
                if ((published.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName) is not { } current) continue;
                components.Add(new(ComponentType.Attribute, current.MetadataId, AttributeSource(entity.LogicalName, attribute.LogicalName),
                    MetadataStore.IsSystemEntity(published), ImportJobLog.Entities, entity.LogicalName,
                    entity.DisplayName?.UserLocalizedLabel?.Label, $"attribute '{entity.LogicalName}.{attribute.LogicalName}'"));
            }
        }
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true))
            components.Add(new(ComponentType.EntityRelationship, store.GetRelationship(relationship.SchemaName)?.MetadataId,
                Layers.LayerPayload.Key(ComponentType.EntityRelationship, relationship.SchemaName), false, ImportJobLog.Relationships,
                relationship.SchemaName, relationship.SchemaName, $"relationship '{relationship.SchemaName}'"));
        foreach (var row in plan.Rows.Where(r => r.IsComponent))
            components.Add(new(row.ComponentType, row.Create ? null : row.Target.Id, RowSource(row), false, row.Group, row.LogId, row.LogName,
                $"{row.Target.LogicalName} '{row.LogName ?? row.LogId}'"));
        return components;
    }

    /// <summary>
    /// The managed properties of a component an unmanaged import wrote (plan 2.6, ruling M13), checked after the apply in the
    /// import's transaction (a refusal rolls everything back): <c>IsCustomizable</c> false refuses any write; for a table or a
    /// column, <c>IsRenameable</c> false refuses a new display name (per language) and, for a column,
    /// <c>CanModifyRequirementLevelSettings</c> false a new required level. The definition before the import is the published
    /// one, the session's view is the one after it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the component and the managed solution.</exception>
    private async Task DemandManagedPropertiesAsync(DataSession s, Tracked component, SolutionPackage package, CancellationToken ct)
    {
        if (component.Type == ComponentType.EntityRelationship && component.RootKey is { } schema)
            await DemandNewRelationshipAllowedAsync(s, schema, package, ct);
        if (await Layers.ManagedProperties.StateAsync(s, component.Type, component.ObjectId, ct) is not { } state) return;
        switch (component.Type)
        {
            case ComponentType.Entity when component.RootKey is { } name:
                var what = $"table '{name}'";
                if (!state.Flags.IsCustomizable) throw Layers.ManagedProperties.NotCustomizable(what, state);
                // A column the file adds to a table delivered with CanCreateAttributes false (final review I4).
                if (!state.Flags.CanCreateAttributes && store.GetEntity(name) is { } published && store.GetEntity(s, name) is { } current
                    && (current.Attributes ?? []).Select(a => a.LogicalName)
                        .Except((published.Attributes ?? []).Select(a => a.LogicalName), StringComparer.Ordinal).FirstOrDefault() is { } added)
                    throw Layers.ManagedProperties.TableFlagFalse($"attribute '{added}'", name, "CanCreateAttributes", state);
                if (!state.Flags.IsRenameable && store.GetEntity(name) is { } before && store.GetEntity(s, name) is { } after
                    && (Layers.ManagedProperties.Renamed(after.DisplayName, before.DisplayName)
                        || Layers.ManagedProperties.Renamed(after.DisplayCollectionName, before.DisplayCollectionName)))
                    throw Layers.ManagedProperties.NotRenameable(what, state);
                return;
            case ComponentType.Attribute when component.Source is { Key: var key } && key.IndexOf('.') is > 0 and var dot:
                var column = $"attribute '{key}'";
                if (!state.Flags.IsCustomizable) throw Layers.ManagedProperties.NotCustomizable(column, state);
                var (table, name2) = (key[..dot], key[(dot + 1)..]);
                if (store.GetEntity(table)?.Attributes?.FirstOrDefault(a => a.LogicalName == name2) is not { } old
                    || store.GetEntity(s, table)?.Attributes?.FirstOrDefault(a => a.LogicalName == name2) is not { } now) return;
                if (!state.Flags.IsRenameable && Layers.ManagedProperties.Renamed(now.DisplayName, old.DisplayName))
                    throw Layers.ManagedProperties.NotRenameable(column, state);
                if (!state.Flags.CanModifyRequirementLevel
                    && (now.RequiredLevel?.Value ?? AttributeRequiredLevel.None) != (old.RequiredLevel?.Value ?? AttributeRequiredLevel.None))
                    throw Layers.ManagedProperties.RequirementLevelFixed(column, state);
                return;
            default:
                if (!state.Flags.IsCustomizable)
                    throw Layers.ManagedProperties.NotCustomizable(
                        $"component {component.ObjectId} of type {component.Type}{(component.RootKey is { } root ? $" ('{root}')" : "")}", state);
                return;
        }
    }

    /// <summary>
    /// An unmanaged import that adds the relationship <paramref name="schema"/> (not published yet) to a table delivered with
    /// <c>CanCreateAttributes</c> false is refused (ruling M13; final review I4), as <c>CreateOneToMany</c> and
    /// <c>CreateManyToMany</c> are.
    /// </summary>
    private async Task DemandNewRelationshipAllowedAsync(DataSession s, string schema, SolutionPackage package, CancellationToken ct)
    {
        if (store.GetOneToManyRelationships().Any(r => string.Equals(r.SchemaName, schema, StringComparison.OrdinalIgnoreCase))
            || store.GetManyToManyRelationships().Any(r => string.Equals(r.SchemaName, schema, StringComparison.OrdinalIgnoreCase))) return;
        string?[] tables = package.Relationships.FirstOrDefault(r => string.Equals(r.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) switch
        {
            OneToManyRelationshipMetadata o => [o.ReferencingEntity, o.ReferencedEntity],
            ManyToManyRelationshipMetadata m => [m.Entity1LogicalName, m.Entity2LogicalName],
            _ => [],
        };
        foreach (var table in tables.OfType<string>().Distinct(StringComparer.Ordinal))
            if (store.GetEntity(table) is { MetadataId: { } id }
                && await Layers.ManagedProperties.StateAsync(s, ComponentType.Entity, id, ct) is { Flags.CanCreateAttributes: false } state)
                throw Layers.ManagedProperties.TableFlagFalse($"relationship '{schema}'", table, "CanCreateAttributes", state);
    }

    /// <summary>
    /// Marks every component of a managed import managed (plan 2.6, ruling M7 step 4): <c>IsManaged</c> on the tables, columns,
    /// global option sets and saved relationships (ruling M14), <c>ismanaged</c> on the component rows (an import-only column:
    /// no request may set it).
    /// </summary>
    private async Task MarkManagedAsync(DataSession s, List<Tracked> tracked, CancellationToken ct)
    {
        foreach (var component in tracked)
        {
            switch (component.Type)
            {
                case ComponentType.Entity when component.RootKey is { } entity:
                    await store.SetIsManagedAsync(s, entity, null, true, ct);
                    break;
                case ComponentType.Attribute when component.Source is { Key: var key } && key.IndexOf('.') is > 0 and var dot:
                    // A system column keeps its own (on a custom table it follows the table).
                    if (store.GetEntity(s, key[..dot]) is { } owner
                        && (owner.Attributes ?? []).FirstOrDefault(a => a.LogicalName == key[(dot + 1)..]) is { } column
                        && !MetadataStore.IsCustomAttribute(owner, column)) break;
                    await store.SetIsManagedAsync(s, key[..dot], key[(dot + 1)..], true, ct);
                    break;
                case ComponentType.OptionSet when component.RootKey is { } optionSet:
                    await store.SetOptionSetIsManagedAsync(s, optionSet, true, ct);
                    break;
                case ComponentType.EntityRelationship when component.RootKey is { } relationship:
                    await store.SetRelationshipIsManagedAsync(s, relationship, true, ct);
                    break;
                default:
                    if (ComponentType.TableOf(component.Type) is { } table && store.GetEntity(table) is { } entityMetadata)
                    {
                        await using var cmd = s.Command(
                            $"UPDATE {s.DataTable(table)} SET {s.Dialect.Quote(SystemTableRules.IsManaged)} = {s.Dialect.BooleanLiteral(true)} WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(entityMetadata))} = @id");
                        cmd.AddParameter("@id", DbType.Guid, component.ObjectId);
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The payload key (<see cref="Layers.LayerPayload.Key"/>) of a row component: its own; a plug-in type's is its assembly's
    /// and a step image's its step's (they travel in their owner's element).
    /// </summary>
    private static (int Type, string Key) RowSource(RowWrite row) => row.ComponentType switch
    {
        ComponentType.PluginType => Layers.LayerPayload.Key(ComponentType.PluginAssembly,
            row.Target.GetAttributeValue<EntityReference>("pluginassemblyid").Id.ToString("D")),
        ComponentType.SdkMessageProcessingStepImage => Layers.LayerPayload.Key(ComponentType.SdkMessageProcessingStep,
            row.Target.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid").Id.ToString("D")),
        ComponentType.CustomApi => Layers.LayerPayload.Key(ComponentType.CustomApi, row.LogName!),
        ComponentType.EnvironmentVariableDefinition => Layers.LayerPayload.Key(ComponentType.EnvironmentVariableDefinition, row.RootKey!),
        ComponentType.CustomControl => Layers.LayerPayload.Key(ComponentType.CustomControl, row.RootKey!),
        ComponentType.SavedQuery or ComponentType.SystemForm or ComponentType.SiteMap or ComponentType.AppModule
            or ComponentType.SavedQueryVisualization =>
            Layers.LayerPayload.Key(row.ComponentType, row.RootKey!),
        _ => Layers.LayerPayload.Key(row.ComponentType, row.Target.Id.ToString("D")),
    };

    /// <summary>
    /// The existing rows of a managed import that a layer above keeps (plan 2.6, ruling M7): the component rows whose payload
    /// key <see cref="Plan.Kept"/> holds, with the rows of a kept custom API (its message, filter, parameters and properties).
    /// A row the import creates is always written.
    /// </summary>
    private static HashSet<RowWrite> KeptRows(Plan plan)
    {
        var kept = new HashSet<RowWrite>(ReferenceEqualityComparer.Instance);
        if (plan.Kept is not { Count: > 0 }) return kept;
        // The rows of one custom API follow one another, its message first.
        List<RowWrite>? api = null;
        void FlushApi()
        {
            if (api?.FirstOrDefault(r => r.ComponentType == ComponentType.CustomApi) is { } apiRow && plan.Keeps(RowSource(apiRow)))
                foreach (var row in api.Where(r => !r.Create)) kept.Add(row);
            api = null;
        }
        foreach (var row in plan.Rows)
        {
            if (IsCustomApiRow(row))
            {
                if (row.Target.LogicalName == ApiMessage) FlushApi();
                (api ??= []).Add(row);
                continue;
            }
            FlushApi();
            if (row.IsComponent && !row.Create && plan.Keeps(RowSource(row))) kept.Add(row);
        }
        FlushApi();
        return kept;
    }

    /// <summary>
    /// The metadata of a restore (<see cref="ApplyMode.Restore"/>): each requested component (<paramref name="targets"/>; all
    /// exist) is updated to its layer, with its labels replaced (a missing one clears the current one, ruling R6), and gets
    /// what its layer has that xrmverse does not model. Options are added and relabelled, never removed: each current option
    /// value the layer does not have is kept with a warning (ruling R7). A relationship keeps its definition and lookup (its
    /// menu configuration is restored); a table keeps its columns, views and forms (each a component of its own).
    /// </summary>
    private async Task RestoreMetadataAsync(DataSession s, SolutionPackage package, RestoreTargets targets, ImportJobLog log, CancellationToken ct)
    {
        string? Xml((int Type, string Key) key) => package.Unmodelled?.GetValueOrDefault(key)?.ToString(SaveOptions.DisableFormatting);

        foreach (var optionSet in package.OptionSets.Where(o => o.Name is not null && targets.OptionSets.Contains(o.Name)))
        {
            var copy = MetadataSerializer.Clone(optionSet);
            copy.DisplayName ??= new Label();
            copy.Description ??= new Label();
            if (store.GetOptionSet(optionSet.Name!) is OptionSetMetadata { Options: { } currentOptions } && copy is OptionSetMetadata given)
                WarnKeptOptions(log, ImportJobLog.OptionSets, optionSet.Name!, currentOptions, given.Options);
            await store.UpdateOptionSetAsync(s, copy, replaceLabels: true, ct);
            await store.SetUnmodelledXmlAsync(s, ComponentType.OptionSet, optionSet.Name!, Xml(UnmodelledXml.OptionSetKey(optionSet.Name!)), ct);
        }

        foreach (var entity in package.Entities)
        {
            var published = store.GetEntity(entity.LogicalName)!;
            if (targets.Entities.Contains(entity.LogicalName))
            {
                var update = new EntityMetadata
                {
                    LogicalName = entity.LogicalName, DisplayName = entity.DisplayName ?? new Label(),
                    DisplayCollectionName = entity.DisplayCollectionName ?? new Label(), Description = entity.Description ?? new Label(),
                };
                if (entity.IsAuditEnabled is { } fileAudit && published.IsAuditEnabled is { } currentAudit && fileAudit.Value != currentAudit.Value)
                {
                    if (currentAudit.CanBeChanged) update.IsAuditEnabled = AuditDefaults.Managed(fileAudit.Value, currentAudit.CanBeChanged);
                    else
                        log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                            $"IsAuditEnabled of '{entity.LogicalName}' is {currentAudit.Value} in xrmverse and cannot change: the layer's {fileAudit.Value} is not applied.");
                }
                if (entity.AutoCreateAccessTeams is { } accessTeams && accessTeams != (published.AutoCreateAccessTeams == true))
                    update.AutoCreateAccessTeams = accessTeams;
                await store.UpdateEntityAsync(s, update, replaceLabels: true, ct);
                await store.SetUnmodelledXmlAsync(s, ComponentType.Entity, entity.LogicalName, Xml(UnmodelledXml.EntityKey(entity.LogicalName)), ct);
            }

            var current = CurrentAttributes(entity, package);
            var attributes = (entity.Attributes ?? []).Cast<AttributeMetadata>()
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName))
                .Where(a => targets.Attributes.Contains($"{entity.LogicalName}.{a.LogicalName}") && !TypeMapping.IsStoredByOtherAttribute(a));
            foreach (var attribute in attributes)
            {
                var existing = current[attribute.LogicalName];
                var name = $"{entity.LogicalName}.{attribute.LogicalName}";
                if (attribute.IsCustomAttribute != true)
                {
                    await ApplySystemAttributeAsync(s, entity, attribute, existing, current, log, restore: true, ct);
                }
                else if (attribute is LookupAttributeMetadata || CustomAttributes(entity).Contains(attribute))
                {
                    var copy = MetadataSerializer.Clone(attribute);
                    ServerOwnedProperties.Clear(copy);
                    if (copy is ImageAttributeMetadata { IsPrimaryImage: false } image) image.IsPrimaryImage = null;
                    if (copy is LookupAttributeMetadata lookup) lookup.Targets = null; // a lookup's targets do not change
                    if (copy.IsAuditEnabled is { } columnAudit && existing.IsAuditEnabled is { CanBeChanged: false } fixedAudit
                        && columnAudit.Value != fixedAudit.Value)
                    {
                        log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                            $"IsAuditEnabled of '{name}' is {fixedAudit.Value} in xrmverse and cannot change: the layer's {columnAudit.Value} is not applied.");
                        copy.IsAuditEnabled = null;
                    }
                    KeepUnsecuredIfNotSecurable(log, published, copy, existing);
                    copy.DisplayName ??= new Label();
                    copy.Description ??= new Label();
                    if (existing is EnumAttributeMetadata { OptionSet: { IsGlobal: not true } local } && copy is EnumAttributeMetadata { OptionSet: { } given })
                        WarnKeptOptions(log, ImportJobLog.Entities, name, local.Options, given.Options);
                    await store.UpdateAttributeAsync(s, entity.LogicalName, copy, replaceLabels: true, ct);
                    // R8, R14: a rollup restored from a lower layer is due at once, as an imported one.
                    if (RollupColumns.IsRollup(attribute)) store.NotifyRollupImported(s, entity.LogicalName, attribute.LogicalName);
                }
                else continue; // a column xrmverse generates (the primary key, a reserved name): nothing to restore
                await store.SetUnmodelledXmlAsync(s, ComponentType.Attribute, name, Xml(UnmodelledXml.AttributeKey(entity.LogicalName, attribute.LogicalName)), ct);
            }
        }

        foreach (var relationship in package.Relationships.Where(r => targets.Relationships.Contains(r.SchemaName)))
        {
            if (relationship.IsCustomRelationship == true)
            {
                var menu = package.RelationshipMenus.GetValueOrDefault(relationship.SchemaName)?.ToString(SaveOptions.DisableFormatting);
                if (menu != store.GetRelationshipMenuXml(relationship.SchemaName))
                    await store.SetRelationshipMenuXmlAsync(s, relationship.SchemaName, menu, ct);
            }
            await store.SetUnmodelledXmlAsync(s, ComponentType.EntityRelationship, relationship.SchemaName,
                Xml(UnmodelledXml.RelationshipKey(relationship.SchemaName)), ct);
        }
    }

    /// <summary>
    /// Ruling R7: a restore does not remove option values. Warns, one by one, of each value of <paramref name="current"/> that
    /// <paramref name="layer"/> does not have (it is kept).
    /// </summary>
    private static void WarnKeptOptions(ImportJobLog log, string group, string owner, IEnumerable<OptionMetadata>? current,
        IEnumerable<OptionMetadata>? layer)
    {
        var given = (layer ?? []).Select(o => o.Value).ToHashSet();
        foreach (var value in (current ?? []).Select(o => o.Value).Where(v => v is not null && !given.Contains(v)))
            log.Warn(group, owner, null, $"Option {value} of {owner} is kept: a restore does not remove option values.");
    }

    private async Task ApplyOptionSetsAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, CancellationToken ct)
    {
        foreach (var optionSet in plan.Package.OptionSets)
        {
            var copy = MetadataSerializer.Clone(optionSet);
            if (store.GetOptionSet(optionSet.Name) is null) await CreatingAsync(() => store.CreateOptionSetAsync(s, copy, ct));
            else if (!plan.Keeps(ComponentType.OptionSet, optionSet.Name)) await store.UpdateOptionSetAsync(s, copy, ct);
            log.Add(ImportJobLog.OptionSets, optionSet.Name, optionSet.Name);
            tracked.Add(new(ComponentType.OptionSet, MetadataStore.OptionSetId(optionSet.Name), optionSet.Name,
                Source: Layers.LayerPayload.Key(ComponentType.OptionSet, optionSet.Name)));
        }
    }

    /// <summary>
    /// First pass: each entity with its custom non-lookup attributes. A new entity is created with them; an existing one is
    /// relabelled, gets the attributes it lacks and has the others updated (labels, required level, a larger length, new
    /// options; never a removal).
    /// </summary>
    private async Task ApplyEntitiesAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, CancellationToken ct)
    {
        var package = plan.Package;
        foreach (var entity in package.Entities)
        {
            // Calculated, rollup and formula columns come last (ApplyComputedAttributesAsync), once every column, lookup and
            // relationship they may read exists.
            var attributes = CustomAttributes(entity).Where(a => !ComputedOrder.IsComputed(a)).Select(a =>
            {
                var copy = MetadataSerializer.Clone(a);
                ServerOwnedProperties.Clear(copy);
                // An image column that is not primary in the file: the first image column of a table is primary until a later
                // one in the file takes the flag (created in the file's order), and the flag can only move, never be dropped.
                if (copy is ImageAttributeMetadata { IsPrimaryImage: false } image) image.IsPrimaryImage = null;
                return copy;
            }).ToList();

            Guid entityId;
            if (store.GetEntity(entity.LogicalName) is not { } published)
            {
                var primaryName = attributes.FirstOrDefault(a => a.LogicalName == entity.PrimaryNameAttribute)
                                  ?? ActivityPrimaryName(entity)
                                  // A calculated primary name goes to the store, which refuses it naming the column (plan 2.12a, final review I3).
                                  ?? CalculatedPrimaryName(entity)
                                  ?? throw XrmdFaultException.InvalidArgument(
                                      $"Entity '{entity.LogicalName}' has no custom primary name attribute in the solution.");
                var copy = MetadataSerializer.Clone(entity);
                ServerOwnedProperties.Clear(copy);
                // A custom activity always has notes (plan 2.11 A4): a file's 0 is not applied, and the import says so.
                if (copy.IsActivity == true && copy.HasNotes == false)
                {
                    log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                        $"HasRelatedNotes of custom activity '{entity.LogicalName}' is 0 in the file, but an activity always has notes: it is created with notes.");
                    copy.HasNotes = null;
                }
                copy.MetadataId = entityId = Guid.NewGuid();
                foreach (var attribute in attributes) attribute.MetadataId = Guid.NewGuid();
                foreach (var attribute in attributes) WarnFixedAudit(log, entity.LogicalName, attribute);
                var shape = Shape(entity.LogicalName, primaryName.LogicalName, NewTableAttributes(entity, attributes), entity.PrimaryIdAttribute);
                foreach (var attribute in attributes) KeepUnsecuredIfNotSecurable(log, shape, attribute, current: null);
                copy.SetAttributes(attributes.Where(a => !ReferenceEquals(a, primaryName)));
                await CreatingAsync(() => store.CreateEntityAsync(s, copy, primaryName, ct));
                tracked.AddRange(attributes.Select(a => new Tracked(ComponentType.Attribute, a.MetadataId!.Value, null, entityId,
                    AttributeSource(entity.LogicalName, a.LogicalName))));
            }
            else
            {
                entityId = published.MetadataId ?? throw new InvalidOperationException($"Entity '{entity.LogicalName}' has no MetadataId.");
                var update = new EntityMetadata
                {
                    LogicalName = entity.LogicalName, DisplayName = entity.DisplayName, DisplayCollectionName = entity.DisplayCollectionName,
                    Description = entity.Description,
                };
                // The table's auditing is the file's when the file owns the table (a shell keeps what it has) and it can change.
                if (!IsShell(package, entity) && entity.IsAuditEnabled is { } fileAudit && published.IsAuditEnabled is { } currentAudit
                    && fileAudit.Value != currentAudit.Value)
                {
                    if (currentAudit.CanBeChanged) update.IsAuditEnabled = AuditDefaults.Managed(fileAudit.Value, currentAudit.CanBeChanged);
                    else
                        log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                            $"IsAuditEnabled of '{entity.LogicalName}' is {currentAudit.Value} in xrmverse and cannot change: the file's {fileAudit.Value} is not applied.");
                }
                // Access teams (plan 2.16): the file's setting when the file owns the table; the store applies the rules (400).
                if (!IsShell(package, entity) && entity.AutoCreateAccessTeams is { } fileAccessTeams
                    && fileAccessTeams != (published.AutoCreateAccessTeams == true))
                    update.AutoCreateAccessTeams = fileAccessTeams;
                // Change tracking (plan 2.13, ruling C13): a file's 1 switches the table on (the store applies the rules, 400); a
                // file's 0 never switches it off: the table stays tracked and the import says so.
                if (!IsShell(package, entity) && entity.ChangeTrackingEnabled is { } fileTracking
                    && fileTracking != (published.ChangeTrackingEnabled == true))
                {
                    if (fileTracking) update.ChangeTrackingEnabled = true;
                    else
                        log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                            $"ChangeTrackingEnabled of '{entity.LogicalName}' is on in xrmverse and cannot be switched off: the file's 0 is not applied.");
                }
                // HasActivities and HasNotes (plan 2.11 A5, A19), one-way as change tracking: a file's 1 switches the table on (the
                // store applies the rules, 400); a file's 0 never switches it off.
                if (!IsShell(package, entity))
                {
                    if (OneWayFlag(log, entity.LogicalName, "HasRelatedActivities", entity.HasActivities, published.HasActivities)) update.HasActivities = true;
                    if (OneWayFlag(log, entity.LogicalName, "HasRelatedNotes", entity.HasNotes, published.HasNotes)) update.HasNotes = true;
                }
                // Duplicate detection (plan 2.15, ruling D18): the file's flag is applied (the store applies the rules, 400).
                if (!IsShell(package, entity) && entity.IsDuplicateDetectionEnabled is { } fileDuplicates
                    && fileDuplicates.Value != (published.IsDuplicateDetectionEnabled?.Value == true))
                    update.IsDuplicateDetectionEnabled = new BooleanManagedProperty(fileDuplicates.Value);
                // A table a layer above keeps (plan 2.6, ruling M7) is not changed; its columns are decided one by one.
                if (!plan.Keeps(ComponentType.Entity, entity.LogicalName)) await store.UpdateEntityAsync(s, update, ct);
                foreach (var attribute in attributes)
                    await ApplyAttributeAsync(s, plan, log, tracked, published, entityId, attribute, ct);
            }
            await ApplyPrimaryImageAsync(s, entity, attributes, log, ct);
            log.Add(ImportJobLog.Entities, entity.LogicalName, entity.DisplayName?.UserLocalizedLabel?.Label);
            tracked.Add(new(ComponentType.Entity, entityId, entity.LogicalName, Source: Layers.LayerPayload.Key(ComponentType.Entity, entity.LogicalName)));
        }
    }

    /// <summary>
    /// A custom column of the file on a table that exists (<paramref name="table"/>, as it is before the column is written): a column
    /// a layer above keeps is left as it is, an existing one updated, a new one created; tracked with its <c>MetadataId</c>.
    /// </summary>
    private async Task ApplyAttributeAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, EntityMetadata table, Guid entityId,
        AttributeMetadata attribute, CancellationToken ct)
    {
        var entity = table.LogicalName;
        var current = (table.Attributes ?? []).FirstOrDefault(a => a.LogicalName == attribute.LogicalName);
        if (current is not null && plan.Keeps(AttributeSource(entity, attribute.LogicalName)))
        {
            attribute.MetadataId = current.MetadataId;
        }
        else if (current is not null)
        {
            if (attribute.IsAuditEnabled is { } columnAudit && current.IsAuditEnabled is { CanBeChanged: false } fixedAudit
                && columnAudit.Value != fixedAudit.Value)
            {
                log.Warn(ImportJobLog.Entities, entity, null,
                    $"IsAuditEnabled of '{entity}.{current.LogicalName}' is {fixedAudit.Value} in xrmverse and cannot change: the file's {columnAudit.Value} is not applied.");
                attribute.IsAuditEnabled = null;
            }
            KeepUnsecuredIfNotSecurable(log, table, attribute, current);
            await store.UpdateAttributeAsync(s, entity, attribute, ct);
            attribute.MetadataId = current.MetadataId;
        }
        else
        {
            attribute.MetadataId = Guid.NewGuid();
            WarnFixedAudit(log, entity, attribute);
            KeepUnsecuredIfNotSecurable(log, table, attribute, current: null);
            await CreatingAsync(() => store.AddAttributeAsync(s, entity, attribute, ct));
        }
        if (attribute.MetadataId is { } id)
            tracked.Add(new(ComponentType.Attribute, id, null, entityId, AttributeSource(entity, attribute.LogicalName)));
    }

    /// <summary>
    /// The final attribute pass (plan 2.12a ruling K13, plan 2.12b ruling R14, plan 2.12c ruling F3): the calculated, rollup and
    /// formula columns of the file, after every table, column and relationship of the solution, so that a definition reading a
    /// column, a lookup or a relationship the same import creates resolves; among them, a column after the ones of the file it
    /// reads (<see cref="ComputedOrder"/>: a rollup after the basic calculated columns it aggregates, a calculated column after the
    /// rollups it reads). A new one is created, an existing one updated (a re-import changes its <c>FormulaDefinition</c>); the
    /// store checks the definition (400 naming the column and the rule) and refuses a change of <c>SourceType</c>. Each rollup the
    /// import writes has its Mass Calculate Rollup Field job due at once after the import commits (ruling R8).
    /// </summary>
    private async Task ApplyComputedAttributesAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, CancellationToken ct)
    {
        var package = plan.Package;
        var files = package.Entities
            .SelectMany(e => CustomAttributes(e).Where(ComputedOrder.IsComputed).Select(a => (Entity: e.LogicalName, Attribute: a)))
            .ToList();
        if (files.Count > 0)
        {
            foreach (var (entity, attribute) in Ordered(s, package, files))
            {
                var table = store.GetEntity(s, entity)
                            ?? throw new InvalidOperationException($"Entity '{entity}' was not created by the import.");
                var entityId = table.MetadataId ?? throw new InvalidOperationException($"Entity '{entity}' has no MetadataId.");
                var copy = MetadataSerializer.Clone(attribute);
                ServerOwnedProperties.Clear(copy);
                var kept = plan.Keeps(AttributeSource(entity, attribute.LogicalName))
                           && (table.Attributes ?? []).Any(a => a.LogicalName == attribute.LogicalName);
                await ApplyAttributeAsync(s, plan, log, tracked, table, entityId, copy, ct);
                // R8, R14: the Mass job of an imported rollup is due at once, after the store's own notification of the change.
                if (!kept && RollupColumns.IsRollup(attribute)) store.NotifyRollupImported(s, entity, attribute.LogicalName);
            }
        }
        // The _base of each calculated or rollup money column, generated with it: as for any money _base, its labels are applied.
        foreach (var entity in package.Entities)
        {
            var bases = (entity.Attributes ?? []).Where(a => a.IsCustomAttribute == true && IsComputedBase(entity, a)).ToList();
            if (bases.Count == 0) continue;
            var current = CurrentAttributes(entity, package);
            foreach (var file in bases)
                await ApplyGeneratedAttributeAsync(s, plan, log, entity, file, current, ct);
        }
    }

    /// <summary>
    /// The computed columns of the file (<paramref name="files"/>) in the order <see cref="ComputedOrder"/> gives them, translated
    /// against the session's metadata with the file's computed columns added (none of them exists yet on a first import).
    /// </summary>
    private List<(string Entity, AttributeMetadata Attribute)> Ordered(DataSession s, SolutionPackage package,
        List<(string Entity, AttributeMetadata Attribute)> files)
    {
        var merged = new Dictionary<string, EntityMetadata>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => f.Entity, StringComparer.Ordinal))
        {
            if (store.GetEntity(s, group.Key) is not { } current) continue;
            var copy = MetadataSerializer.Clone(current);
            var names = group.Select(f => f.Attribute.LogicalName).ToHashSet(StringComparer.Ordinal);
            copy.SetAttributes((current.Attributes ?? []).Where(a => !names.Contains(a.LogicalName))
                .Concat(group.Select(f => MetadataSerializer.Clone(f.Attribute))));
            merged[group.Key] = copy;
        }
        EntityMetadata? Find(string name) => merged.GetValueOrDefault(name) ?? store.GetEntity(s, name);
        var fileRelationships = package.Relationships.OfType<OneToManyRelationshipMetadata>().Where(r => r.SchemaName is not null)
            .GroupBy(r => r.SchemaName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        OneToManyRelationshipMetadata? Relationship(string name) =>
            fileRelationships.GetValueOrDefault(name) ?? store.GetRelationship(name) as OneToManyRelationshipMetadata
            ?? store.GetOneToManyRelationships().FirstOrDefault(r => string.Equals(r.SchemaName, name, StringComparison.OrdinalIgnoreCase));
        var byKey = files.ToDictionary(f => (f.Entity, f.Attribute.LogicalName));
        return ComputedOrder.Order(files.Select(f => (f.Entity, f.Attribute.LogicalName)).ToList(), Find, Relationship, computed, rollups,
                readersLast: true)
            .Select(k => byKey[k])
            .ToList();
    }

    /// <summary>The file's primary name column of <paramref name="entity"/> when it is calculated or a rollup (a copy), which the store refuses.</summary>
    private static AttributeMetadata? CalculatedPrimaryName(EntityMetadata entity)
    {
        if (CustomAttributes(entity).FirstOrDefault(a => a.LogicalName == entity.PrimaryNameAttribute && ComputedOrder.IsComputed(a)) is not { } name) return null;
        var copy = MetadataSerializer.Clone(name);
        ServerOwnedProperties.Clear(copy);
        return copy;
    }

    /// <summary>
    /// True when the file's <paramref name="file"/> switches the setting <paramref name="element"/> on; a file's 0 on a table that
    /// has the setting is a warning (the setting stays on).
    /// </summary>
    private static bool OneWayFlag(ImportJobLog log, string entity, string element, bool? file, bool? current)
    {
        if (file is not { } value || value == (current == true)) return false;
        if (value) return true;
        log.Warn(ImportJobLog.Entities, entity, null,
            $"{element} of '{entity}' is on in xrmverse and cannot be switched off: the file's 0 is not applied.");
        return false;
    }

    /// <summary>
    /// The name xrmverse derives for a relationship of the activity model of the file (plan 2.11: regarding, notes, parties, party
    /// targets); null for any other relationship, or when a table it needs is neither in the file nor in the organization.
    /// </summary>
    private string? DerivedActivityRelationship(SolutionPackage package, OneToManyRelationshipMetadata relationship)
    {
        if (!ActivitySchema.IsManagedLookup(relationship.ReferencingEntity, relationship.ReferencingAttribute)) return null;
        EntityMetadata? Table(string? name) => name is null ? null : store.GetEntity(name) ?? package.Entities.FirstOrDefault(e => e.LogicalName == name);
        if (Table(relationship.ReferencedEntity) is not { } referenced) return null;
        return (relationship.ReferencingEntity, relationship.ReferencingAttribute) switch
        {
            (ActivitySchema.Annotation, ActivitySchema.ObjectId) => ActivitySchema.NotesRelationship(referenced).SchemaName,
            (ActivitySchema.Party, ActivitySchema.ActivityId) => ActivitySchema.PartiesRelationship(referenced).SchemaName,
            (ActivitySchema.Party, ActivitySchema.PartyId) => ActivitySchema.PartyTargetRelationship(referenced.LogicalName).SchemaName,
            (_, ActivitySchema.RegardingObjectId) when Table(relationship.ReferencingEntity) is { } activity
                                                        && (activity.LogicalName == ActivitySchema.Pointer || ActivitySchema.IsActivityTable(activity))
                => ActivitySchema.RegardingRelationship(referenced, activity).SchemaName,
            _ => null,
        };
    }

    /// <summary>True when the file gives <paramref name="entity"/> as an activity table (<c>IsActivity</c> 1).</summary>
    private static bool IsActivityOfFile(SolutionPackage package, string entity) =>
        package.Entities.Any(e => e.LogicalName == entity && e.IsActivity == true);

    /// <summary>
    /// Warns when the file gives a column that is created with a fixed <c>IsAuditEnabled</c> (a file or image column, ...) another
    /// value: the fixed one is applied, as on a re-import, and the file's is reported.
    /// </summary>
    private static void WarnFixedAudit(ImportJobLog log, string entity, AttributeMetadata attribute)
    {
        var rule = AuditDefaults.ForAttribute(attribute);
        if (attribute.IsAuditEnabled is { } given && !rule.CanBeChanged && given.Value != rule.Value)
            log.Warn(ImportJobLog.Entities, entity, null,
                $"IsAuditEnabled of '{entity}.{attribute.LogicalName}' is {rule.Value} in xrmverse and cannot change: the file's {given.Value} is not applied.");
    }

    /// <summary>
    /// The securable rule on import (plan 14, Global Constraints): a column the file marks secured that cannot be secured
    /// (a primary name, a lookup, a system column such as <c>createdon</c>, ...) stays unsecured, because real exports may
    /// carry the flag on such a column. <paramref name="current"/>: the column in xrmverse, null when the import creates it
    /// (it is created unsecured); an existing column keeps its flag. The warning is logged only when the file's value differs
    /// from xrmverse's, so a re-import of an unchanged file adds none.
    /// </summary>
    private static void KeepUnsecuredIfNotSecurable(ImportJobLog log, EntityMetadata entity, AttributeMetadata attribute, AttributeMetadata? current)
    {
        if (attribute.IsSecured != true || FieldSecurability.IsSecurable(entity, attribute)) return;
        if (current?.IsSecured != true)
            log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                $"Column '{entity.LogicalName}.{attribute.LogicalName}' cannot be secured with field security: the file's IsSecured is not applied.");
        attribute.IsSecured = current is null ? false : null;
    }

    /// <summary>A table as the securable rule sees it: its name, primary id and primary name, and its columns.</summary>
    private static EntityMetadata Shape(string logicalName, string? primaryName, IEnumerable<AttributeMetadata> attributes, string? primaryId = null)
    {
        var shape = new EntityMetadata { LogicalName = logicalName };
        SdkProperty.Set(shape, nameof(EntityMetadata.PrimaryIdAttribute), primaryId ?? SystemAttributes.PrimaryIdName(logicalName));
        SdkProperty.Set(shape, nameof(EntityMetadata.PrimaryNameAttribute), primaryName);
        shape.SetAttributes(attributes);
        return shape;
    }

    /// <summary>True when the file has <paramref name="entity"/> only as a shell (not a root component of the solution, or with behavior 2).</summary>
    private static bool IsShell(SolutionPackage package, EntityMetadata entity) =>
        !package.RootComponents.Any(r => ComponentType.Normalize(r.Type) == ComponentType.Entity
                                         && NormalizeKey(r.SchemaNameOrId) == entity.LogicalName && r.Behavior != 2);

    /// <summary>
    /// The file's primary image (<c>IsPrimaryImage</c> 1; the reader refuses two in a table). IsPrimaryImage true is ignored
    /// when an image column is created while another one exists (Learn, "Update image columns"), so the import makes it
    /// primary afterwards with an update, as Dataverse is told to. A primary image the import does not create (a system
    /// image column xrmverse does not generate, such as <c>entityimage</c>) is not carried over: a warning says so and names
    /// the column that is primary instead only because it is the table's first image column.
    /// </summary>
    private async Task ApplyPrimaryImageAsync(DataSession s, EntityMetadata entity, List<AttributeMetadata> written, ImportJobLog log,
        CancellationToken ct)
    {
        if ((entity.Attributes ?? []).OfType<ImageAttributeMetadata>().FirstOrDefault(i => i.IsPrimaryImage == true) is not { } primary) return;
        if (written.Any(a => a is ImageAttributeMetadata && a.LogicalName == primary.LogicalName))
        {
            await store.UpdateAttributeAsync(s, entity.LogicalName, new ImageAttributeMetadata { LogicalName = primary.LogicalName, IsPrimaryImage = true }, ct);
            return;
        }
        var current = (store.GetEntity(entity.LogicalName)?.Attributes ?? []).OfType<ImageAttributeMetadata>()
            .FirstOrDefault(i => i.IsPrimaryImage == true)?.LogicalName;
        var first = written.OfType<ImageAttributeMetadata>().FirstOrDefault()?.LogicalName;
        log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
            $"The primary image of '{entity.LogicalName}' in the file is '{primary.LogicalName}', which is not created: the primary image is not carried over."
            + (current is not null ? $" '{current}' stays the primary image."
                : first is not null ? $" '{first}' is the primary image only because it is the first image column of the table." : ""));
    }

    /// <summary>
    /// Second pass: the custom one-to-many relationships with their lookups and the custom many-to-many relationships; an
    /// existing one keeps its definition (its lookup is relabelled; any other difference was refused in the preparation). System relationships are skipped.
    /// </summary>
    private async Task ApplyRelationshipsAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, CancellationToken ct)
    {
        var package = plan.Package;
        // The relationships a Customer column's creation made (the second one is created with the first), by SchemaName.
        var createdWithLookup = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in package.Relationships.Where(r => r.IsCustomRelationship == true))
        {
            Guid id;
            switch (relationship)
            {
                case OneToManyRelationshipMetadata oneToMany:
                {
                    var lookup = MetadataSerializer.Clone(package.Lookups.Single(l =>
                        l.EntityLogicalName == oneToMany.ReferencingEntity && l.LogicalName == oneToMany.ReferencingAttribute));
                    ServerOwnedProperties.Clear(lookup);
                    // A lookup is never securable: its table's columns do not change the outcome.
                    KeepUnsecuredIfNotSecurable(log, Shape(oneToMany.ReferencingEntity!, null, [lookup]), lookup,
                        store.GetRelationship(oneToMany.SchemaName) is null ? null
                            : store.GetEntity(oneToMany.ReferencingEntity!)?.Attributes?.FirstOrDefault(a => a.LogicalName == lookup.LogicalName));
                    var entityId = (store.GetEntity(oneToMany.ReferencingEntity!)?.MetadataId)
                                   ?? tracked.First(t => t.Type == ComponentType.Entity && t.RootKey == oneToMany.ReferencingEntity).ObjectId;
                    if (createdWithLookup.TryGetValue(oneToMany.SchemaName, out var createdId))
                    {
                        id = createdId;
                    }
                    else if (store.GetRelationship(oneToMany.SchemaName) is { MetadataId: { } existing })
                    {
                        id = existing;
                        var current = store.GetEntity(oneToMany.ReferencingEntity!)?.Attributes?.FirstOrDefault(a => a.LogicalName == lookup.LogicalName);
                        lookup.Targets = null; // a lookup's targets do not change
                        var lookupSource = AttributeSource(oneToMany.ReferencingEntity!, lookup.LogicalName);
                        if (!plan.Keeps(lookupSource)) await store.UpdateAttributeAsync(s, oneToMany.ReferencingEntity!, lookup, ct);
                        // A Customer column's second relationship updates the same lookup again: tracked once.
                        if (current?.MetadataId is { } lookupId
                            && !tracked.Any(t => t.Type == ComponentType.Attribute && t.ObjectId == lookupId))
                            tracked.Add(new(ComponentType.Attribute, lookupId, null, entityId, lookupSource));
                    }
                    else if (CustomerColumns.IsCustomer(lookup))
                    {
                        var both = package.Relationships.OfType<OneToManyRelationshipMetadata>()
                            .Where(r => r.IsCustomRelationship == true && r.ReferencingEntity == oneToMany.ReferencingEntity
                                        && r.ReferencingAttribute == oneToMany.ReferencingAttribute)
                            .Select(MetadataSerializer.Clone).ToList();
                        foreach (var each in both) each.MetadataId = Guid.NewGuid();
                        lookup.MetadataId = Guid.NewGuid();
                        await CreatingAsync(() => store.CreateCustomerRelationshipsAsync(s, lookup, both, ct));
                        tracked.Add(new(ComponentType.Attribute, lookup.MetadataId.Value, null, entityId,
                            AttributeSource(oneToMany.ReferencingEntity!, lookup.LogicalName)));
                        foreach (var each in both) createdWithLookup[each.SchemaName] = each.MetadataId!.Value;
                        id = createdWithLookup[oneToMany.SchemaName];
                    }
                    else
                    {
                        var copy = MetadataSerializer.Clone(oneToMany);
                        copy.MetadataId = id = Guid.NewGuid();
                        lookup.MetadataId = Guid.NewGuid();
                        await CreatingAsync(() => store.CreateOneToManyAsync(s, copy, lookup, ct));
                        tracked.Add(new(ComponentType.Attribute, lookup.MetadataId.Value, null, entityId,
                            AttributeSource(oneToMany.ReferencingEntity!, lookup.LogicalName)));
                    }
                    break;
                }
                case ManyToManyRelationshipMetadata manyToMany:
                {
                    if (store.GetRelationship(manyToMany.SchemaName) is { MetadataId: { } existing }) id = existing;
                    else
                    {
                        var copy = MetadataSerializer.Clone(manyToMany);
                        copy.MetadataId = id = Guid.NewGuid();
                        await CreatingAsync(() => store.CreateManyToManyAsync(s, copy, ct));
                    }
                    break;
                }
                default:
                    continue;
            }
            // The associated menu configuration is kept opaque with the relationship (for the export): set as the file has it.
            var menu = package.RelationshipMenus.GetValueOrDefault(relationship.SchemaName)?.ToString(SaveOptions.DisableFormatting);
            var source = Layers.LayerPayload.Key(ComponentType.EntityRelationship, relationship.SchemaName);
            if (menu != store.GetRelationshipMenuXml(relationship.SchemaName) && !plan.Keeps(source))
                await store.SetRelationshipMenuXmlAsync(s, relationship.SchemaName, menu, ct);
            log.Add(ImportJobLog.Relationships, relationship.SchemaName, relationship.SchemaName);
            tracked.Add(new(ComponentType.EntityRelationship, id, relationship.SchemaName, Source: source));
        }
    }

    /// <summary>
    /// The system attributes of the file's entities that xrmverse has (it generates its own): their labels and required level
    /// are applied, and so are the options of <c>statecode</c> (labels: the states of a table are fixed) and <c>statuscode</c>
    /// (labels and new status reasons). Any other difference from the xrmverse attribute is a warning of the entity naming
    /// the attribute and the property (real exports differ in details from the generated attributes), and so is a required
    /// level xrmverse cannot take (SystemRequired is the platform's), a new state or a status reason of an unknown state.
    /// </summary>
    private async Task ApplySystemAttributesAsync(DataSession s, Plan plan, ImportJobLog log, List<Tracked> tracked, CancellationToken ct)
    {
        var package = plan.Package;
        foreach (var entity in package.Entities)
        {
            var current = CurrentAttributes(entity, package);
            // The attributes xrmverse generates with another one (money _base, AttributeOf): not applied, their differences are
            // warnings; but the labels of a money _base are applied (it is renameable in Dataverse, plan 2.10 task 2).
            // The _base of a calculated or rollup money column exists once the computed pass has created its money column: it is
            // applied there.
            foreach (var file in (entity.Attributes ?? []).Where(a => a.IsCustomAttribute == true && TypeMapping.IsStoredByOtherAttribute(a)
                                                                      && !IsComputedBase(entity, a)))
                await ApplyGeneratedAttributeAsync(s, plan, log, entity, file, current, ct);
            WarnDerivedActivityColumns(entity, package, current, log);
            foreach (var file in SystemAttributesOf(entity, package))
            {
                if (!current.TryGetValue(file.LogicalName, out var existing)) continue; // logged as not created
                var source = AttributeSource(entity.LogicalName, file.LogicalName);
                if (!plan.Keeps(source)) await ApplySystemAttributeAsync(s, entity, file, existing, current, log, restore: false, ct);
                // A component of the solution (ruling R11), with its id in the session (a table the import created included).
                if (store.GetEntity(s, entity.LogicalName) is { MetadataId: { } entityId } table
                    && (table.Attributes ?? []).FirstOrDefault(a => a.LogicalName == file.LogicalName)?.MetadataId is { } id)
                    tracked.Add(new(ComponentType.Attribute, id, null, entityId, source, LayerOnly: true));
            }
        }
    }

    /// <summary>
    /// The columns of the activity model in the file (<see cref="IsDerivedActivityColumn"/>) are not applied: the server derives
    /// them (plan 2.11 A19, ruling Z5). Each difference from xrmverse's column (labels, description, required level, auditing,
    /// IsSecured, type-specific settings) is a warning naming it, so that nothing the file gives them is dropped silently.
    /// </summary>
    private static void WarnDerivedActivityColumns(EntityMetadata entity, SolutionPackage package, Dictionary<string, AttributeMetadata> current,
        ImportJobLog log)
    {
        var columns = (entity.Attributes ?? []).Cast<AttributeMetadata>()
            .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName))
            .Where(a => a.IsCustomAttribute != true && IsDerivedActivityColumn(entity, a));
        foreach (var file in columns)
        {
            if (!current.TryGetValue(file.LogicalName, out var existing)) continue;
            var differences = MetadataDifferences.OfAttribute(existing, file, system: true).Select(d => d.ToString()).ToList();
            if (!MetadataDifferences.LabelsMatch(file.DisplayName, existing.DisplayName))
                differences.Add($"DisplayName (file: {Labels(file.DisplayName)}, xrmverse: {Labels(existing.DisplayName)})");
            if (!MetadataDifferences.LabelsMatch(file.Description, existing.Description))
                differences.Add($"Description (file: {Labels(file.Description)}, xrmverse: {Labels(existing.Description)})");
            var currentRequired = existing.RequiredLevel?.Value ?? AttributeRequiredLevel.None;
            if (file.RequiredLevel?.Value is { } required && required != currentRequired)
                differences.Add($"RequiredLevel (file: {required}, xrmverse: {currentRequired})");
            var currentAudit = (existing.IsAuditEnabled ?? AuditDefaults.ForAttribute(existing, SystemAttributes.PrimaryIdName(entity))).Value;
            if (file.IsAuditEnabled is { } audit && audit.Value != currentAudit)
                differences.Add($"IsAuditEnabled (file: {audit.Value}, xrmverse: {currentAudit})");
            if (file.IsSecured is { } secured && secured != (existing.IsSecured == true))
                differences.Add($"IsSecured (file: {secured}, xrmverse: {existing.IsSecured == true})");
            foreach (var difference in differences)
                log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                    $"System attribute '{entity.LogicalName}.{file.LogicalName}' differs from the xrmverse one in {difference}: not applied (a column of the activity model is the server's).");
        }
    }

    /// <summary>
    /// An attribute xrmverse generates with another one (<paramref name="file"/>, a money <c>_base</c> or an <c>AttributeOf</c>
    /// companion): not applied, its differences from the generated one are warnings; but the labels of a money <c>_base</c> are
    /// applied (it is renameable in Dataverse, plan 2.10 task 2).
    /// </summary>
    private async Task ApplyGeneratedAttributeAsync(DataSession s, Plan plan, ImportJobLog log, EntityMetadata entity, AttributeMetadata file,
        Dictionary<string, AttributeMetadata> current, CancellationToken ct)
    {
        if (!current.TryGetValue(file.LogicalName, out var generated)) return;
        var differences = MetadataDifferences.OfAttribute(generated, file, system: true).Select(d => d.ToString()).ToList();
        var relabel = file is MoneyAttributeMetadata { IsBaseCurrency: true } && generated.GetType() == file.GetType();
        if (!relabel && !MetadataDifferences.LabelsMatch(file.DisplayName, generated.DisplayName))
            differences.Add($"DisplayName (file: {Labels(file.DisplayName)}, xrmverse: {Labels(generated.DisplayName)})");
        if (!relabel && !MetadataDifferences.LabelsMatch(file.Description, generated.Description))
            differences.Add($"Description (file: {Labels(file.Description)}, xrmverse: {Labels(generated.Description)})");
        foreach (var difference in differences)
            log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                $"Attribute '{entity.LogicalName}.{file.LogicalName}' is generated by xrmverse and differs from the file in {difference}: not applied.");
        if (relabel) await ApplyBaseMoneyLabelsAsync(s, plan, entity.LogicalName, file, generated, current, ct);
    }

    /// <summary>
    /// The <c>_base</c> of a calculated or rollup money column of the file (the parity export writes one per calculated or rollup
    /// Currency column).
    /// </summary>
    private static bool IsComputedBase(EntityMetadata entity, AttributeMetadata attribute) =>
        attribute is MoneyAttributeMetadata { IsBaseCurrency: true }
        && attribute.LogicalName.EndsWith(TypeMapping.BaseColumnSuffix, StringComparison.Ordinal)
        && (entity.Attributes ?? []).Any(a => a is MoneyAttributeMetadata && ComputedOrder.IsComputed(a)
            && a.LogicalName + TypeMapping.BaseColumnSuffix == attribute.LogicalName);

    /// <summary>
    /// The labels of a money <c>_base</c> of the file (<paramref name="file"/>), applied to xrmverse's <paramref name="generated"/>
    /// when they differ. The <c>_base</c> is not a solution component of its own: it follows its money column. So nothing is applied
    /// when the plan keeps the layer of the <c>_base</c> or of its money column, and an unmanaged import is refused (as for the money
    /// column itself, ruling M13) when the money column is delivered by a managed solution as not customizable or not renameable.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the <c>_base</c> and the managed solution.</exception>
    private async Task ApplyBaseMoneyLabelsAsync(DataSession s, Plan plan, string entity, AttributeMetadata file, AttributeMetadata generated,
        Dictionary<string, AttributeMetadata> current, CancellationToken ct)
    {
        var money = file.LogicalName[..^TypeMapping.BaseColumnSuffix.Length];
        if (plan.Keeps(AttributeSource(entity, file.LogicalName)) || plan.Keeps(AttributeSource(entity, money))) return;
        var renamed = (file.DisplayName is { LocalizedLabels.Count: > 0 } && !MetadataDifferences.LabelsMatch(file.DisplayName, generated.DisplayName))
                      || (file.Description is { LocalizedLabels.Count: > 0 } && !MetadataDifferences.LabelsMatch(file.Description, generated.Description));
        if (!renamed) return;
        if (!plan.IsManaged && current.TryGetValue(money, out var column) && column.MetadataId is { } moneyId
            && await Layers.ManagedProperties.StateAsync(s, ComponentType.Attribute, moneyId, ct) is { } state)
        {
            var what = $"attribute '{entity}.{file.LogicalName}'";
            if (!state.Flags.IsCustomizable) throw Layers.ManagedProperties.NotCustomizable(what, state);
            if (!state.Flags.IsRenameable) throw Layers.ManagedProperties.NotRenameable(what, state);
        }
        var labels = (AttributeMetadata)Activator.CreateInstance(generated.GetType())!;
        labels.LogicalName = generated.LogicalName;
        labels.DisplayName = file.DisplayName;
        labels.Description = file.Description;
        await store.UpdateAttributeAsync(s, entity, labels, ct);
    }

    /// <summary>
    /// One system attribute of <see cref="ApplySystemAttributesAsync"/>: <paramref name="file"/> as the file has it,
    /// <paramref name="existing"/> as xrmverse has it, <paramref name="current"/> the table's columns. In a restore
    /// (<paramref name="restore"/>), the labels replace the current ones (a missing one clears it, ruling R6) and a status
    /// reason the layer does not have is kept with a warning (ruling R7).
    /// </summary>
    private async Task ApplySystemAttributeAsync(DataSession s, EntityMetadata entity, AttributeMetadata file, AttributeMetadata existing,
        Dictionary<string, AttributeMetadata> current, ImportJobLog log, bool restore, CancellationToken ct)
    {
        void Warn(string what) => log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
            $"System attribute '{entity.LogicalName}.{file.LogicalName}' differs from the xrmverse one in {what}: not applied.");

        foreach (var difference in MetadataDifferences.OfAttribute(existing, file, system: true)) Warn(difference.ToString());
        if (existing.GetType() != file.GetType()) return;

        var changes = (AttributeMetadata)Activator.CreateInstance(existing.GetType())!;
        changes.LogicalName = existing.LogicalName;
        if (file.IsAuditEnabled is { } fileAudit && existing.IsAuditEnabled is { } existingAudit && fileAudit.Value != existingAudit.Value)
        {
            if (existingAudit.CanBeChanged) changes.IsAuditEnabled = AuditDefaults.Managed(fileAudit.Value, existingAudit.CanBeChanged);
            else Warn($"IsAuditEnabled (file: {fileAudit.Value}, xrmverse: {existingAudit.Value}; it cannot change)");
        }
        if (file.IsSecured is { } fileSecured && fileSecured != (existing.IsSecured == true))
        {
            var table = store.GetEntity(entity.LogicalName)
                        ?? Shape(entity.LogicalName, entity.PrimaryNameAttribute, current.Values, entity.PrimaryIdAttribute);
            if (!fileSecured || FieldSecurability.IsSecurable(table, existing)) changes.IsSecured = fileSecured;
            else
                log.Warn(ImportJobLog.Entities, entity.LogicalName, null,
                    $"Column '{entity.LogicalName}.{file.LogicalName}' cannot be secured with field security: the file's IsSecured is not applied.");
        }
        changes.DisplayName = restore ? file.DisplayName ?? new Label() : file.DisplayName;
        changes.Description = restore ? file.Description ?? new Label() : file.Description;
        var required = file.RequiredLevel?.Value;
        var currentRequired = existing.RequiredLevel?.Value ?? AttributeRequiredLevel.None;
        if (required is { } level && level != currentRequired)
        {
            if (level == AttributeRequiredLevel.SystemRequired || currentRequired == AttributeRequiredLevel.SystemRequired)
                Warn($"RequiredLevel (file: {level}, xrmverse: {currentRequired})");
            else changes.RequiredLevel = new AttributeRequiredLevelManagedProperty(level);
        }
        if (existing is StateAttributeMetadata or StatusAttributeMetadata
            && ((EnumAttributeMetadata)file).OptionSet is { Options.Count: > 0 } fileOptions)
        {
            var known = ((EnumAttributeMetadata)existing).OptionSet?.Options.Select(o => o.Value).ToHashSet() ?? [];
            var states = current.Values.OfType<StateAttributeMetadata>().SingleOrDefault()?.OptionSet?.Options
                .Select(o => o.Value).ToHashSet() ?? [];
            var applied = new List<OptionMetadata>();
            foreach (var option in fileOptions.Options)
            {
                if (known.Contains(option.Value))
                {
                    applied.Add(option);
                    if (option is StatusOptionMetadata { State: { } fileState }
                        && ((EnumAttributeMetadata)existing).OptionSet!.Options.OfType<StatusOptionMetadata>()
                            .FirstOrDefault(o => o.Value == option.Value)?.State is { } currentState
                        && fileState != currentState)
                        Warn($"its options: the status reason {option.Value} has the state {fileState} in the file and {currentState} in xrmverse (the state is kept, the labels are applied)");
                }
                else if (existing is StateAttributeMetadata)
                    Warn($"its options: the state {option.Value} cannot be added (the states of a table are fixed)");
                else if (option is StatusOptionMetadata { State: { } state } && states.Contains(state)) applied.Add(option);
                else Warn($"its options: the status reason {option.Value} has no existing state");
            }
            if (applied.Count > 0)
                ((EnumAttributeMetadata)changes).OptionSet = new OptionSetMetadata(new OptionMetadataCollection(applied.Select(MetadataSerializer.Clone).ToList()))
                {
                    IsGlobal = false,
                };
        }
        if (restore && existing is StatusAttributeMetadata)
            WarnKeptOptions(log, ImportJobLog.Entities, $"{entity.LogicalName}.{file.LogicalName}", ((EnumAttributeMetadata)existing).OptionSet?.Options,
                ((EnumAttributeMetadata)file).OptionSet?.Options);
        await store.UpdateAttributeAsync(s, entity.LogicalName, changes, restore, ct);
    }

    /// <summary>
    /// Keeps with each entity, attribute, relationship and global option set of the file what xrmverse does not model
    /// (<see cref="SolutionPackage.Unmodelled"/>), as the file has it (a new import replaces it), for the export to write it
    /// again. Run after the metadata is applied: the components are the session's (a component this import creates
    /// included). One xrmverse has no row for keeps nothing, with a warning (but a system attribute it does not generate and a
    /// system relationship it derives, logged already or never exported). An entity the file has only as a shell (not a root
    /// component of the solution, or with behavior 2) belongs to another solution: it and its attributes keep what they have,
    /// get the file's only when they have nothing, and a file's that differs is logged as not applied.
    /// </summary>
    private async Task KeepUnmodelledAsync(DataSession s, Plan plan, ImportJobLog log, CancellationToken ct)
    {
        var package = plan.Package;
        string? Xml((int Type, string Key) key) => package.Unmodelled?.GetValueOrDefault(key)?.ToString(SaveOptions.DisableFormatting);
        async Task KeepAsync(int type, string storeKey, (int, string) packageKey, bool shell, string group, string component, bool warnMissing)
        {
            // A component a layer above keeps (plan 2.6, ruling M7) keeps what it has.
            if (plan.Keeps(type, storeKey)) return;
            var xml = Xml(packageKey);
            if (shell && store.GetUnmodelledXml(s, type, storeKey) is { } kept)
            {
                if (kept != xml)
                    log.Warn(group, storeKey, null, $"What the file gives {component} that xrmverse does not model is not applied: the file has " +
                                                    "the entity as a shell, and what another solution gave it is kept.");
                return;
            }
            if (!await store.SetUnmodelledXmlAsync(s, type, storeKey, xml, ct) && xml is not null && warnMissing)
                log.Warn(group, storeKey, null, $"What the file gives {component} that xrmverse does not model is not kept: " +
                                                "xrmverse has no definition of its own for it, and the export writes the defaults.");
        }

        foreach (var entity in package.Entities)
        {
            var shell = IsShell(package, entity);
            await KeepAsync(ComponentType.Entity, entity.LogicalName, UnmodelledXml.EntityKey(entity.LogicalName), shell,
                ImportJobLog.Entities, $"entity '{entity.LogicalName}'", warnMissing: true);
            var attributes = (entity.Attributes ?? []).Cast<AttributeMetadata>()
                .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName));
            foreach (var attribute in attributes)
                await KeepAsync(ComponentType.Attribute, $"{entity.LogicalName}.{attribute.LogicalName}",
                    UnmodelledXml.AttributeKey(entity.LogicalName, attribute.LogicalName), shell, ImportJobLog.Entities,
                    $"attribute '{entity.LogicalName}.{attribute.LogicalName}'", warnMissing: attribute.IsCustomAttribute == true);
        }
        foreach (var relationship in package.Relationships)
            await KeepAsync(ComponentType.EntityRelationship, relationship.SchemaName, UnmodelledXml.RelationshipKey(relationship.SchemaName),
                false, ImportJobLog.Relationships, $"relationship '{relationship.SchemaName}'", warnMissing: relationship.IsCustomRelationship == true);
        foreach (var optionSet in package.OptionSets.Where(o => o.Name is not null))
            await KeepAsync(ComponentType.OptionSet, optionSet.Name!, UnmodelledXml.OptionSetKey(optionSet.Name!), false,
                ImportJobLog.OptionSets, $"option set '{optionSet.Name}'", warnMissing: true);
    }

    /// <summary>The system attributes of an entity of the file (its lookups included), but those xrmverse generates with another one.</summary>
    private static IEnumerable<AttributeMetadata> SystemAttributesOf(EntityMetadata entity, SolutionPackage package) =>
        (entity.Attributes ?? []).Cast<AttributeMetadata>()
            .Concat(package.Lookups.Where(l => l.EntityLogicalName == entity.LogicalName))
            // The columns of the activity model (regardingobjectid, party lists, EntityName: plan 2.11) are the server's; their
            // differences are warnings (WarnDerivedActivityColumns), the store takes no change of them (A19).
            .Where(a => a.IsCustomAttribute != true && !TypeMapping.IsStoredByOtherAttribute(a) && !IsDerivedActivityColumn(entity, a));

    /// <summary>
    /// A column of the activity model the server derives and never changes on an import (plan 2.11 A19): a lookup whose targets
    /// it keeps (<see cref="ActivitySchema.IsManagedLookup(string?, string?)"/>), and on an activity table a party list or an
    /// EntityName column.
    /// </summary>
    private static bool IsDerivedActivityColumn(EntityMetadata entity, AttributeMetadata a) =>
        (a is LookupAttributeMetadata && ActivitySchema.IsManagedLookup(entity.LogicalName, a.LogicalName))
        || (entity.IsActivity == true && (ActivitySchema.IsPartyList(a) || a is EntityNameAttributeMetadata));

    /// <summary>The attributes an entity of the file has in xrmverse, by logical name: the published ones, or for a new
    /// entity the system attributes xrmverse generates.</summary>
    private Dictionary<string, AttributeMetadata> CurrentAttributes(EntityMetadata entity, SolutionPackage package)
    {
        var attributes = store.GetEntity(entity.LogicalName) is { } published
            ? published.Attributes ?? []
            : NewTableAttributes(entity, CustomAttributes(entity).Where(a => a is not LookupAttributeMetadata).Select(MetadataSerializer.Clone)).ToArray();
        // Not created yet: the setting each column will get (the store sets it on creation).
        foreach (var attribute in attributes)
            attribute.IsAuditEnabled ??= AuditDefaults.ForAttribute(attribute, SystemAttributes.PrimaryIdName(entity));
        var map = new Dictionary<string, AttributeMetadata>(StringComparer.Ordinal);
        foreach (var attribute in attributes) map.TryAdd(attribute.LogicalName, attribute);
        return map;
    }

    /// <summary>The publisher (created when missing; an existing one is kept as it is) and the solution row.</summary>
    private async Task ApplyPublisherAndSolutionAsync(DataSession s, Caller caller, Plan plan, CancellationToken ct)
    {
        var package = plan.Package;
        if (!plan.PublisherExists)
            await core.ExecuteCoreAsync(new CreateRequest
            {
                Target = new Entity("publisher", plan.PublisherId)
                {
                    ["uniquename"] = package.Publisher.UniqueName,
                    ["friendlyname"] = Text(package.Publisher.FriendlyName) ?? package.Publisher.UniqueName,
                    ["customizationprefix"] = package.Publisher.CustomizationPrefix,
                    ["customizationoptionvalueprefix"] = package.Publisher.CustomizationOptionValuePrefix,
                },
            }, caller, s, ct);

        var solution = new Entity("solution", plan.SolutionId)
        {
            ["friendlyname"] = Text(package.Solution.FriendlyName) ?? package.Solution.UniqueName,
            ["version"] = package.Solution.Version,
            ["ismanaged"] = plan.IsManaged,
            ["publisherid"] = new EntityReference("publisher", plan.PublisherId),
            ["description"] = Text(package.Solution.Description),
        };
        // The solution row's ismanaged is written by the import alone, as a layer operation (final review I2): an unmanaged
        // import writes false on an unmanaged row (ClassifyAsync refuses one over a managed row, checked again under the lock).
        using var layerOperation = plan.IsManaged ? null : Layers.ManagedProperties.LayerOperation();
        if (plan.SolutionExists)
        {
            await core.ExecuteCoreAsync(new UpdateRequest { Target = solution }, caller, s, ct);
        }
        else
        {
            solution["uniquename"] = plan.SolutionName;
            solution["installedon"] = DateTime.UtcNow;
            await core.ExecuteCoreAsync(new CreateRequest { Target = solution }, caller, s, ct);
        }
        // What the import keeps for the export: an import-only column, written by the import alone.
        await WriteImportOnlyColumnsAsync(s, new Entity("solution", plan.SolutionId) { [SystemTableRules.UnsupportedXml] = UnsupportedXml(package) }, ct);
    }

    /// <summary>
    /// What the import keeps without applying it, for the export to emit again (<see cref="KeptXml"/>): the root nodes of
    /// unsupported components under <c>ImportExportXml</c>, the entity-level ones under <c>Entity Name="…"</c>, and under
    /// <c>SolutionManifest</c> (with the file's <c>languagecode</c>) the solution's names and descriptions in every language
    /// and the file's <c>Publisher</c> (descriptions, e-mail, web site, addresses): the <c>solution</c> and <c>publisher</c>
    /// rows keep one name and one description, in the base language. Under <c>Files</c>, the archive entries no reader
    /// consumed (agents, environment variables, formulas, the files of unsupported components). The custom APIs are not kept
    /// here: they are rows, and the export writes their files from those (plan 15).
    /// </summary>
    private static string UnsupportedXml(SolutionPackage package) =>
        new XElement(KeptXml.Root,
            new XElement("ImportExportXml", package.Unsupported),
            package.EntityUnsupported.GroupBy(e => e.EntityLogicalName).Select(g =>
                new XElement("Entity", new XAttribute("Name", g.Key), g.Select(e => e.Xml))),
            new XElement("SolutionManifest", new XAttribute("languagecode", package.Solution.LanguageCode),
                KeptXml.Labels("LocalizedNames", "LocalizedName", package.Solution.FriendlyName),
                KeptXml.Labels("Descriptions", "Description", package.Solution.Description),
                package.Publisher.Xml),
            KeptXml.FilesElement(package.KeptFiles)).ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Writes the row through the core operation (after the <see cref="RegistrationValidator"/> for registrations), then its
    /// import-only columns (<c>xrmverse_componentxml</c>, what the file gave the component that xrmverse does not model; an
    /// app module's <c>descriptor</c>) with SQL of its own: no request may set them (<see cref="SystemTableRules"/>), only
    /// the import does.
    /// </summary>
    private async Task WriteRowAsync(DataSession s, Caller caller, RowWrite row, CancellationToken ct)
    {
        var request = Request(row);
        var target = (Entity)request.Parameters["Target"];
        if (StepRegistry.RegistrationEntities.Contains(target.LogicalName))
        {
            var existing = row.Create ? null : await core.RetrieveImageAsync(target.ToEntityReference(), null, s, ct);
            await validator.ValidateAsync(target.LogicalName, target, existing, s, ct);
        }
        await core.ExecuteCoreAsync(request, caller, s, ct);
        await WriteImportOnlyColumnsAsync(s, row.Target, ct);
    }

    // ---- processes (plan 2.14) ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a process of the file (ruling W22) through the <see cref="WorkflowStore"/>, as <see cref="Caller.Internal"/> (ruling W3):
    /// an existing activated definition is deactivated first (its activation goes, unless a job still runs it), then the row is
    /// written as a Draft definition with the file's values and its triggers, and its kept XML for the export. The activation
    /// follows once every row is written (<see cref="ActivateImportedAsync"/>). An action (category 3, W14) keeps the message it has, or
    /// gets its message now, whatever its state, with the file's <c>SdkMessageId</c> when that id is free (the export of a Draft action
    /// carries one); a message name that clashes with a message or custom API of the organization refuses the import (400).
    /// A new process is owned by the importing user (<paramref name="caller"/>; the owner of a queued import's job), so its
    /// background jobs, its <c>runas</c>-owner runs and its scope act as that user (Learn: background workflows run as their owner);
    /// an existing one keeps its owner. Only an import without a user (<see cref="Caller.Internal"/>, a restore) leaves it to SYSTEM.
    /// </summary>
    private async Task WriteWorkflowAsync(DataSession s, Caller caller, RowWrite row, PackageWorkflow workflow, string? prefix,
        ImportJobLog log, CancellationToken ct)
    {
        var existing = await _workflowStore.GetAsync(s, workflow.Id, ct);
        if (existing is { Type: (int)WorkflowSchema.Type.Definition, StateCode: WorkflowSchema.ActivatedState })
            await WorkflowActivation.DeactivateAsync(s, Caller.Internal, workflow.Id, ct);
        var written = WorkflowRowOf(workflow) with
        {
            SdkMessageId = existing?.SdkMessageId,
            OwnerId = existing?.OwnerId ?? (caller.UserId == Guid.Empty ? null : new EntityReference("systemuser", caller.UserId)),
        };
        await _workflowStore.SaveAsync(s, written, await ProcessTriggersAsync(s, workflow, log, ct), ct);
        if (written is { Category: (int)WorkflowSchema.Category.Action, Type: (int)WorkflowSchema.Type.Definition })
        {
            var fileMessage = workflow.Fields.FirstOrDefault(f => f.Key.Equals("SdkMessageId", StringComparison.OrdinalIgnoreCase)).Value;
            await ActionMessages.EnsureAsync(s, written, prefix ?? await Xrmd.Workflows.Actions.ActionMessages.PrefixAsync(s, workflow.Id, ct),
                Guid.TryParse(fileMessage?.Trim(), out var messageId) ? messageId : null, ct);
        }
        await WriteImportOnlyColumnsAsync(s, row.Target, ct);
    }

    /// <summary>
    /// The row of a process of the file, as a Draft definition (the activation decides its state). The numbers and flags were checked
    /// by the reader; an empty or missing <c>TriggerOnUpdateAttributeList</c> is no update trigger.
    /// </summary>
    private static WorkflowRow WorkflowRowOf(PackageWorkflow workflow)
    {
        string? Field(string name) =>
            workflow.Fields.FirstOrDefault(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { } value && value.Trim().Length > 0
                ? value.Trim()
                : null;
        int? Number(string name) => Field(name) is { } value ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        bool Flag(string name, bool fallback = false) => Field(name)?.ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => fallback,
        };
        var description = (string?)workflow.Source.Attribute("Description");
        return new WorkflowRow
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Category = workflow.Category,
            Mode = workflow.Mode,
            Scope = workflow.Scope,
            Type = Number("Type") ?? (int)WorkflowSchema.Type.Definition,
            UniqueName = Field("UniqueName"),
            PrimaryEntity = workflow.PrimaryEntity?.ToLowerInvariant(),
            Xaml = workflow.Xaml,
            TriggerOnCreate = Flag("TriggerOnCreate"),
            TriggerOnDelete = Flag("TriggerOnDelete"),
            TriggerOnUpdateAttributes = Field("TriggerOnUpdateAttributeList"),
            CreateStage = Number("CreateStage"),
            UpdateStage = Number("UpdateStage"),
            DeleteStage = Number("DeleteStage"),
            RunAs = Number("RunAs") ?? (int)WorkflowSchema.RunAs.CallingUser,
            ProcessTriggerScope = Number("ProcessTriggerScope"),
            Rank = Number("Rank") ?? 0,
            OnDemand = Flag("OnDemand"),
            Subprocess = Flag("Subprocess"),
            AsyncAutoDelete = Flag("AsyncAutodelete"),
            SyncWorkflowLogOnFailure = Flag("SyncWorkflowLogOnFailure"),
            IsTransacted = Flag("IsTransacted", true),
            InputParameters = Field("InputParameters"),
            IntroducedVersion = Field("IntroducedVersion"),
            IsCustomizable = Flag("IsCustomizable", true),
            StateCode = WorkflowSchema.DraftState,
        };
    }

    /// <summary>
    /// The <c>processtrigger</c> rows of a process's <c>ProcessTrigger</c> elements, by their id. The child element names are read
    /// ignoring case (not verified on a real export, checklist 1h). A <c>formid</c> naming a form the organization does not have is
    /// left out with a warning: the trigger is kept without it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not a number or an id where one is expected.</exception>
    private async Task<List<Entity>> ProcessTriggersAsync(DataSession s, PackageWorkflow workflow, ImportJobLog log, CancellationToken ct)
    {
        var triggers = new List<Entity>();
        foreach (var element in workflow.ProcessTriggers)
        {
            var id = CustomizationsReader.ProcessTriggerId(element)!.Value;
            string? Child(string name) =>
                element.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } child
                && !CustomizationsReader.IsNil(child) && child.Value.Trim().Length > 0
                    ? child.Value.Trim()
                    : null;
            OptionSetValue? Option(string name) => Child(name) is not { } value
                ? null
                : int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    ? new OptionSetValue(number)
                    : throw XrmdFaultException.InvalidArgument(
                        $"The trigger {id} of process '{workflow.Name}' has the {name} '{value}', which is not a number.");
            Guid? Id(string name) => Child(name) is not { } value
                ? null
                : Guid.TryParse(value, out var guid)
                    ? guid
                    : throw XrmdFaultException.InvalidArgument($"The trigger {id} of process '{workflow.Name}' has the {name} '{value}', which is not an id.");

            var trigger = new Entity(WorkflowSchema.TriggerTable, id)
            {
                [WorkflowSchema.Event] = Child(WorkflowSchema.Event),
                [WorkflowSchema.PrimaryEntityTypeCode] = Child(WorkflowSchema.PrimaryEntityTypeCode)?.ToLowerInvariant(),
                [WorkflowSchema.ControlName] = Child(WorkflowSchema.ControlName),
                [WorkflowSchema.ControlType] = Option(WorkflowSchema.ControlType),
                [WorkflowSchema.TriggerScope] = Option(WorkflowSchema.TriggerScope),
                [WorkflowSchema.PipelineStage] = Option(WorkflowSchema.PipelineStage),
                [WorkflowSchema.MethodId] = Id(WorkflowSchema.MethodId),
            };
            if (Id(WorkflowSchema.FormId) is { } form)
            {
                if (await ExistsAsync(s, "systemform", form, ct)) trigger[WorkflowSchema.FormId] = new EntityReference("systemform", form);
                else log.Warn(ImportJobLog.Processes, workflow.Id.ToString("D"), workflow.Name,
                    $"The trigger {id} of process '{workflow.Name}' names the form {form}, which this organization does not have: the trigger is imported without it.");
            }
            triggers.Add(trigger);
        }
        return triggers;
    }

    /// <summary>
    /// The state of an imported process (ruling W22): activated (<see cref="WorkflowActivation.ActivateAsync"/>, as
    /// <see cref="Caller.Internal"/>) when the file's <c>StateCode</c> is 1 or <paramref name="publish"/> is set, else left Draft. An
    /// activation the validation refuses (400) is a warning naming the process and the reason, and the process stays Draft (ruling
    /// W4); the steps xrmverse cannot run never block the import: they are a warning naming the process and each step (ruling W20). A
    /// file <c>StateCode</c> other than 0 or 1 is a warning and the process stays Draft.
    /// </summary>
    private async Task ActivateImportedAsync(DataSession s, PackageWorkflow workflow, bool publish, string? prefix, ImportJobLog log, CancellationToken ct)
    {
        var id = workflow.Id.ToString("D");
        WorkflowDefinition? definition = null;
        string? unreadable = null;
        try
        {
            definition = XamlWorkflowParser.Parse(workflow.Xaml, workflow.Category);
        }
        catch (XamlWorkflowException ex)
        {
            unreadable = ex.Message;
        }

        var activated = false;
        if (workflow.StateCode is not (WorkflowSchema.DraftState or WorkflowSchema.ActivatedState))
        {
            log.Warn(ImportJobLog.Processes, id, workflow.Name,
                $"Process '{workflow.Name}' has StateCode {workflow.StateCode} in the file, which is neither 0 (Draft) nor 1 (Activated): it is imported as Draft.");
        }
        else if (workflow.StateCode == WorkflowSchema.ActivatedState || publish)
        {
            WorkflowActivation.ValidatedActivation validated;
            try
            {
                // Only the checks, which write nothing, may be refused into a warning: the import's transaction is intact after one.
                // A 400 of the writes that follow fails the import, so no activation is ever left half written.
                validated = await WorkflowActivation.ValidateAsync(s, Caller.Internal, workflow.Id, ct, prefix);
            }
            catch (XrmdFaultException ex) when (ex.HttpStatus == 400)
            {
                log.Warn(ImportJobLog.Processes, id, workflow.Name, $"Process '{workflow.Name}' is imported as Draft: {ex.Message}");
                return;
            }
            await WorkflowActivation.ActivateAsync(s, validated, ct);
            activated = true;
        }
        else if (unreadable is not null)
        {
            log.Warn(ImportJobLog.Processes, id, workflow.Name,
                $"Process '{workflow.Name}' is imported as Draft and cannot be activated as it is: {unreadable}");
        }

        if (definition?.Unsupported is { Count: > 0 } steps)
            log.Warn(ImportJobLog.Processes, id, workflow.Name,
                $"Process '{workflow.Name}' has steps xrmverse cannot run: {string.Join("; ", steps.Select(u => $"{u.Label}: {u.Reason}"))}. " +
                (activated
                    ? "It is activated with them listed in xrmverse_unsupportedsteps; a run of it fails naming them."
                    : "Once activated, a run of it fails naming them."));
    }

    /// <summary>
    /// Replaces the components, the roles and the settings of an app module with those of its file, in the import's transaction.
    /// The rows are written with the internal caller (no request may write the first two: <see cref="AppModuleSchema.ImportOnly"/>).
    /// A role the organization does not have, a component whose table, site map or id cannot be resolved, and a setting whose
    /// definition the organization does not have (#129: only AsyncOnLoad and AsyncOnSave are seeded) is a warning in the log and
    /// is skipped (plan 3.5 UI-1a, ruling U3); the rest is imported.
    /// </summary>
    private async Task ApplyAppModuleLinksAsync(DataSession s, RowWrite row, ImportJobLog log, CancellationToken ct)
    {
        var definition = AppModuleXml.Parse(XElement.Parse(row.Target.GetAttributeValue<string>("descriptor")));
        var appId = row.Target.Id;
        void Warn(string text) => log.Warn(row.Group, row.LogId, row.LogName, text);

        foreach (var (table, column) in new[]
                 {
                     (AppModuleSchema.Component, "appmoduleidunique"), (AppModuleSchema.RolesIntersect, "appmoduleid"),
                     (AppModuleSchema.AppSetting, "parentappmoduleid"),
                 })
        {
            await using var delete = s.Command($"DELETE FROM {s.DataTable(table)} WHERE {s.Dialect.Quote(column)} = @id");
            delete.AddParameter("@id", DbType.Guid, appId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        var seen = new HashSet<(int, Guid)>();
        foreach (var component in definition.Components)
        {
            Guid? objectId = component.Type switch
            {
                AppModuleSchema.ComponentType.Entity when component.SchemaName is { } schema =>
                    store.GetEntity(s, schema.ToLowerInvariant())?.MetadataId,
                AppModuleSchema.ComponentType.SiteMap when component.SchemaName is { } schema =>
                    await ScalarAsync(s, s.Dialect.Limit($"SELECT sitemapid FROM {s.DataTable("sitemap")} WHERE {s.Dialect.TextKey("sitemapnameunique")} = {s.Dialect.TextKey("@name")}", "1"), ct, ("@name", schema)) as Guid?,
                _ => component.Id,
            };
            var what = component.SchemaName is { } n ? $"'{n}'" : component.Id?.ToString("D") ?? "(no id)";
            if (objectId is null)
            {
                Warn($"Component {what} of type {component.Type} of app module '{row.LogId}' does not exist in the organization and was skipped.");
                continue;
            }
            if (!seen.Add((component.Type, objectId.Value))) continue;
            // #110: a view, chart or form the organization does not have is linked anyway (a later import may bring it), with a
            // warning, since the app shows nothing for it.
            if (ComponentTable(component.Type) is { } componentTable
                && (store.GetEntity(s, componentTable) is null || !await ExistsAsync(s, componentTable, objectId.Value, ct)))
                Warn($"Component {what} of type {component.Type} of app module '{row.LogId}' has no {componentTable} row in the " +
                     "organization; it is linked, but the app does not show it.");
            var target = new Entity(AppModuleSchema.Component, DerivedId(AppModuleSchema.Component, $"{row.LogId}:{component.Type}:{objectId:D}"))
            {
                ["appmoduleidunique"] = new EntityReference("appmodule", appId),
                ["componenttype"] = new OptionSetValue(component.Type),
                ["objectid"] = objectId.Value,
                ["rootcomponentbehavior"] = new OptionSetValue(component.Behavior),
                ["ismetadata"] = component.Type == AppModuleSchema.ComponentType.Entity,
                ["exempt"] = false,
            };
            await core.ExecuteCoreAsync(new CreateRequest { Target = target }, Caller.Internal, s, ct);
        }

        foreach (var roleId in definition.RoleIds)
        {
            if (!await ExistsAsync(s, "role", roleId, ct))
            {
                Warn($"Role {roleId:D} of app module '{row.LogId}' does not exist in the organization and was not linked.");
                continue;
            }
            await using var insert = s.Command($"""
                INSERT INTO {s.DataTable(AppModuleSchema.RolesIntersect)} ({s.Dialect.Quote(SystemAttributes.PrimaryIdName(store.GetEntity(s, AppModuleSchema.RolesIntersect)!))}, {s.Dialect.Quote("appmoduleid")}, {s.Dialect.Quote("roleid")}, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                VALUES ({s.Dialect.NewGuid}, @app, @role, {s.Dialect.UtcNow}, @by, {s.Dialect.UtcNow}, @by, 0, 1)
                """);
            insert.AddParameter("@app", DbType.Guid, appId);
            insert.AddParameter("@role", DbType.Guid, roleId);
            insert.AddParameter("@by", DbType.Guid, Guid.Empty);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // The app settings (#129): one row per setting of the file, named by its definition; the file gives no id, no unique
        // name and no display name, and neither does the row (observed).
        foreach (var setting in definition.Settings)
        {
            if (await ScalarAsync(s, s.Dialect.Limit($"SELECT {AppModuleSchema.SettingDefinition}id FROM {s.DataTable(AppModuleSchema.SettingDefinition)} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1"),
                    ct, ("@name", setting.DefinitionUniqueName)) is not Guid definitionId)
            {
                Warn($"App setting '{setting.DefinitionUniqueName}' of app module '{row.LogId}' has no setting definition in the organization " +
                     "and was skipped.");
                continue;
            }
            var target = new Entity(AppModuleSchema.AppSetting, DerivedId(AppModuleSchema.AppSetting, $"{row.LogId}:{definitionId:D}"))
            {
                ["parentappmoduleid"] = new EntityReference("appmodule", appId),
                ["settingdefinitionid"] = new EntityReference(AppModuleSchema.SettingDefinition, definitionId),
                ["value"] = setting.Value,
            };
            await core.ExecuteCoreAsync(new CreateRequest { Target = target }, Caller.Internal, s, ct);
        }
    }

    /// <summary>
    /// Writes the user maps of the file (#143, ruling A4) as <see cref="Caller.Internal"/> (no request may write these tables:
    /// <see cref="AttributeMapSchema.ImportOnly"/>): the <c>entitymap</c> of each pair is reused or created, and each attribute map
    /// replaces the one of the same target column, else is added; maps the file does not name stay. Every map is checked first,
    /// against the metadata as this import sees it; one that Dataverse would refuse fails the import (400 naming it).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for a map that cannot be applied.</exception>
    private async Task ApplyEntityMapsAsync(DataSession s, SolutionPackage package, CancellationToken ct)
    {
        foreach (var map in package.EntityMaps) ValidateEntityMap(s, map);
        foreach (var map in package.EntityMaps)
        {
            var mapId = DerivedId(AttributeMapSchema.EntityMap, $"{map.Source}:{map.Target}");
            if (!await ExistsAsync(s, AttributeMapSchema.EntityMap, mapId, ct))
                await core.ExecuteCoreAsync(new CreateRequest
                {
                    Target = new Entity(AttributeMapSchema.EntityMap, mapId)
                    {
                        [AttributeMapSchema.SourceEntity] = map.Source, [AttributeMapSchema.TargetEntity] = map.Target,
                    },
                }, Caller.Internal, s, ct);
            foreach (var attribute in map.Attributes)
            {
                var row = new Entity(AttributeMapSchema.AttributeMap, DerivedId(AttributeMapSchema.AttributeMap, $"{map.Source}:{map.Target}:{attribute.Target}"))
                {
                    [AttributeMapSchema.EntityMapLookup] = new EntityReference(AttributeMapSchema.EntityMap, mapId),
                    [AttributeMapSchema.SourceAttribute] = attribute.Source,
                    [AttributeMapSchema.TargetAttribute] = attribute.Target,
                    [AttributeMapSchema.IsSystem] = false,
                };
                OrganizationRequest write = await ExistsAsync(s, AttributeMapSchema.AttributeMap, row.Id, ct)
                    ? new UpdateRequest { Target = row }
                    : new CreateRequest { Target = row };
                await core.ExecuteCoreAsync(write, Caller.Internal, s, ct);
            }
        }
    }

    /// <summary>
    /// The rules a map must meet (Learn, "Customize table and column mappings" and "Map table columns", paraphrased): both tables and
    /// columns exist; the target column can be written; the columns have the same type (a lookup's targets must all be targets of the target lookup); a text target is not
    /// shorter than its source; no party list; a target column is mapped once; and it is not the lookup to the source table, which the
    /// relationship's own map fills. Not checked: the text format and that two choices share their option values.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the map.</exception>
    private void ValidateEntityMap(DataSession s, PackageEntityMap map)
    {
        var where = $"Entity map from '{map.Source}' to '{map.Target}'";
        var source = store.GetEntity(s, map.Source) ?? throw XrmdFaultException.InvalidArgument($"{where}: table '{map.Source}' does not exist.");
        var target = store.GetEntity(s, map.Target) ?? throw XrmdFaultException.InvalidArgument($"{where}: table '{map.Target}' does not exist.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in map.Attributes)
        {
            var what = $"{where}, column '{attribute.Source}' to '{attribute.Target}'";
            var from = source.Attributes?.FirstOrDefault(a => a.LogicalName == attribute.Source)
                       ?? throw XrmdFaultException.InvalidArgument($"{what}: column '{map.Source}.{attribute.Source}' does not exist.");
            var to = target.Attributes?.FirstOrDefault(a => a.LogicalName == attribute.Target)
                     ?? throw XrmdFaultException.InvalidArgument($"{what}: column '{map.Target}.{attribute.Target}' does not exist.");
            if (to.IsValidForCreate == false && to.IsValidForUpdate == false)
                throw XrmdFaultException.InvalidArgument($"{what}: the target column cannot be written.");
            if (!seen.Add(attribute.Target))
                throw XrmdFaultException.InvalidArgument($"{what}: the target column is mapped more than once.");
            if (ActivitySchema.IsPartyList(from) || ActivitySchema.IsPartyList(to))
                throw XrmdFaultException.InvalidArgument($"{what}: a party list column cannot be mapped.");
            if (from.AttributeType != to.AttributeType)
                throw XrmdFaultException.InvalidArgument($"{what}: the columns have different types ({from.AttributeType} and {to.AttributeType}).");
            if (from is LookupAttributeMetadata fromLookup && to is LookupAttributeMetadata toLookup
                && (fromLookup.Targets ?? []).Except(toLookup.Targets ?? []).Any())
                throw XrmdFaultException.InvalidArgument($"{what}: the target lookup cannot hold every table the source lookup points to.");
            if (MaxLength(from) is { } fromLength && MaxLength(to) is { } toLength && toLength < fromLength)
                throw XrmdFaultException.InvalidArgument($"{what}: the target column is shorter ({toLength}) than the source column ({fromLength}).");
            if (to is LookupAttributeMetadata parent && (parent.Targets ?? []).Contains(map.Source)
                && !SystemAttributes.IsReservedName(map.Target, parent.LogicalName))
                throw XrmdFaultException.InvalidArgument(
                    $"{what}: the target column is the lookup to '{map.Source}', which the relationship's own map fills.");
        }

        static int? MaxLength(AttributeMetadata a) => a switch
        {
            StringAttributeMetadata text => text.MaxLength,
            MemoAttributeMetadata memo => memo.MaxLength,
            _ => null,
        };
    }

    /// <summary>The table of an app module component checked on import (#110): views, charts and forms.</summary>
    private static string? ComponentTable(int type) => type switch
    {
        AppModuleSchema.ComponentType.View => "savedquery",
        AppModuleSchema.ComponentType.Chart => "savedqueryvisualization",
        AppModuleSchema.ComponentType.Form => "systemform",
        _ => null,
    };

    /// <summary>
    /// The role gets exactly the privileges of the file that exist (resolved by name, after the entities); an unknown
    /// privilege is a warning and is skipped, as Dataverse does for privileges of absent features.
    /// </summary>
    private async Task ApplyRolePrivilegesAsync(DataSession s, PackageRole role, ImportJobLog log, CancellationToken ct)
    {
        var known = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = s.Command($"""
                         SELECT p.name, p.privilegeid FROM {s.DataTable("privilege")} AS p
                         WHERE p.name IN (SELECT j.name FROM {s.Dialect.JsonRecords("@names", [new JsonColumn("name", SqlColumnKind.String, 100, "$")])} AS j)
                         """))
        {
            cmd.AddParameter("@names", DbType.String, JsonSerializer.Serialize(role.Privileges.Select(p => p.Name)), -1);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) known[rd.GetString(0)] = rd.GetGuid(1);
        }

        var privileges = new List<(Guid, DepthMask)>();
        foreach (var (name, depth) in role.Privileges)
        {
            if (known.TryGetValue(name, out var id)) privileges.Add((id, depth));
            else log.Warn(ImportJobLog.Roles, role.Id.ToString("D"), role.Name,
                $"Privilege '{name}' of role '{role.Name}' does not exist in xrmverse and was skipped.");
        }
        await security.SetRolePrivilegesAsync(s, role.Id, privileges, replaceAll: true, ct);
    }

    /// <summary>
    /// The <c>solutioncomponent</c> rows: every component in <c>Default</c> and in the imported solution, the root
    /// components with their <c>behavior</c> (updated on a re-import), the attributes linked to their entity's row.
    /// </summary>
    private async Task TrackAsync(DataSession s, Plan plan, List<Tracked> tracked, ImportJobLog log, CancellationToken ct)
    {
        var roots = plan.Package.RootComponents
            .Select(r => (Type: ComponentType.Normalize(r.Type), Key: NormalizeKey(r.SchemaNameOrId), r.Behavior)).ToList();
        int BehaviorOf(Tracked t) =>
            t.RootKey is { } key && roots.FirstOrDefault(r => r.Type == t.Type && r.Key == NormalizeKey(key)) is { Key: not null } root
                ? root.Behavior
                : 0;

        var rows = new Dictionary<Guid, Guid>();
        foreach (var component in tracked.Where(t => !t.LayerOnly).OrderBy(t => t.Parent is null ? 0 : 1))
        {
            await tracker.AddAsync(s, SystemSeed.DefaultSolutionId, component.Type, component.ObjectId, null, ct);
            Guid? parentRow = component.Parent is { } parent && rows.TryGetValue(parent, out var p) ? p : null;
            rows[component.ObjectId] = await tracker.AddAsync(s, plan.SolutionId, component.Type, component.ObjectId, BehaviorOf(component),
                ct, parentRow);
        }

        foreach (var root in roots.Where(r => ComponentType.IsSupported(r.Type) && !tracked.Any(t => t.Type == r.Type && t.RootKey is { } key && NormalizeKey(key) == r.Key)))
            log.Warn(ImportJobLog.Manifest, plan.Package.Solution.UniqueName, null,
                $"Root component '{root.Key}' of type {root.Type} is not in the solution file and was not added.");
    }

    /// <summary>A root component key as compared: a GUID in <c>D</c> format, else the lower-cased name.</summary>
    private static string NormalizeKey(string key) =>
        Guid.TryParse(key, out var id) ? id.ToString("D") : key.ToLowerInvariant();

    // ---- import job -------------------------------------------------------------------------------------------------

    private static Entity ImportJob(Guid jobId, string? solutionName, Guid? solutionId, int progress, ImportJobLog log, DateTime stopped,
        string data) => new("importjob", jobId)
    {
        ["name"] = solutionName ?? "Import Solution",
        ["solutionname"] = solutionName,
        ["solutionid"] = solutionId,
        ["progress"] = (double)progress,
        ["startedon"] = log.StartedOn,
        ["completedon"] = stopped,
        ["data"] = data,
    };

    /// <summary>
    /// Writes the <c>importjob</c> of a failed import in a transaction of its own, after the rollback: <c>progress</c> 0 and
    /// the log with the error. A failure to write it does not hide the import's failure.
    /// </summary>
    private async Task WriteFailedJobAsync(Caller caller, Guid jobId, ImportJobLog log, int errorCode, string error)
    {
        var stopped = DateTime.UtcNow;
        try
        {
            await using var conn = await factory.OpenAsync(CancellationToken.None);
            if (await ExistsAsync(new DataSession(conn, null), "importjob", jobId, CancellationToken.None)) return; // not ours
            await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
            var session = new DataSession(conn, tx);
            try
            {
                await core.ExecuteCoreAsync(new CreateRequest
                {
                    Target = ImportJob(jobId, log.Solution?.UniqueName, null, 0, log, stopped, log.ToXml(0, stopped, errorCode, error)),
                }, caller, session, CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
            }
            catch
            {
                try
                {
                    await tx.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollback) when (rollback is DbException or InvalidOperationException)
                {
                    // The transaction is already gone: nothing is committed.
                }
                ReportRolledBack(session, jobId);
                throw;
            }
            session.Committed();
        }
        catch (Exception ex)
        {
            // The import's own failure is what the caller must see.
            logger.LogError(ex, "The import job {ImportJobId} of a failed solution import could not be written.", jobId);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The labels as <c>1033 'text', 1040 'testo'</c>; <c>(none)</c> without labels.</summary>
    private static string Labels(Label? label) => label?.LocalizedLabels is { Count: > 0 } labels
        ? string.Join(", ", labels.Select(l => $"{l.LanguageCode} '{l.Label}'"))
        : "(none)";

    private static string? Text(Label? label) =>
        label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels.FirstOrDefault()?.Label;

    /// <summary>True when the row of <paramref name="table"/> (a system table) with that primary key exists.</summary>
    private async Task<bool> ExistsAsync(DataSession s, string table, Guid id, CancellationToken ct)
    {
        var entity = store.GetEntity(table) ?? throw new InvalidOperationException($"The system table '{table}' is missing.");
        return await ScalarAsync(s,
            s.Dialect.Limit($"SELECT 1 FROM {s.DataTable(entity.LogicalName)} WHERE {s.Dialect.Quote(SystemAttributes.PrimaryIdName(entity))} = @id", "1"), ct, ("@id", id)) is not null;
    }

    /// <summary>The id of a <c>pluginassembly</c> named <paramref name="name"/> (ignoring case, whatever the collation); null when there is none.</summary>
    private static async Task<Guid?> AssemblyIdByNameAsync(DataSession s, string name, CancellationToken ct) =>
        await ScalarAsync(s, s.Dialect.Limit($"SELECT pluginassemblyid FROM {s.DataTable("pluginassembly")} WHERE LOWER(name) = LOWER(@name) ORDER BY createdon", "1"),
            ct, ("@name", name)) as Guid?;

    private static async Task<object?> ScalarAsync(DataSession s, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = s.Command(sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, DbValueTypes.Of(value), value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }
}
