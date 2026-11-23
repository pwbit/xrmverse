using System.Data;
using System.Runtime.Serialization;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Pipeline.Async;

/// <summary>
/// Queue of system jobs (<c>asyncoperation</c> rows): asynchronous (mode 1) post-operation steps, one row (System Event) per
/// step execution, written in the operation's transaction, so a rolled-back operation queues nothing; queued solution
/// imports (<see cref="EnqueueImportAsync"/>) and the other queued solution operations (<see cref="EnqueueJobAsync"/>); and
/// other system jobs (<see cref="EnqueueSystemJobAsync"/>, e.g. a duplicate rule publish). <see cref="AsyncOperationWorker"/>
/// runs the rows; <see cref="Signal"/> wakes it
/// after a commit.
/// </summary>
public sealed class AsyncOperationQueue
{
    /// <summary><c>asyncoperation.operationtype</c> "System Event".</summary>
    public const int SystemEvent = 1;

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Import Solution Async Operation" (Learn, asyncoperation table reference,
    /// OperationType choices: 203).
    /// </summary>
    public const int ImportSolution = 203;

    /// <summary><c>asyncoperation.operationtype</c> "Duplicate Detection Rule Publish" (Learn, OperationType choices: 7).</summary>
    public const int DuplicateRulePublish = 7;

    /// <summary><c>asyncoperation.operationtype</c> "Bulk Duplicate Detection" (Learn, OperationType choices: 8).</summary>
    public const int BulkDuplicateDetection = 8;

    /// <summary><c>asyncoperation.operationtype</c> "Workflow" (Learn, OperationType choices: 10): a background workflow job (plan 2.14).</summary>
    public const int Workflow = 10;

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Calculate Rollup Field" (Learn, OperationType choices: 57): the recurring incremental
    /// calculation of the rollup columns of one table (plan 2.12b, ruling R8).
    /// </summary>
    public const int CalculateRollupField = 57;

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Mass Calculate Rollup Field" (Learn, OperationType choices: 58): the calculation of one
    /// rollup column on every row of its table (plan 2.12b, ruling R8).
    /// </summary>
    public const int MassCalculateRollupField = 58;

    /// <summary>The <c>name</c> and <c>messagename</c> of a queued solution import.</summary>
    public const string ImportSolutionMessage = "ImportSolution";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Uninstall Solution" (Learn, asyncoperation table reference, OperationType choices:
    /// 208; plan 2.6, ruling M9).
    /// </summary>
    public const int UninstallSolution = 208;

    /// <summary>The <c>name</c> and <c>messagename</c> of a queued solution uninstall.</summary>
    public const string UninstallSolutionMessage = "UninstallSolution";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "DeleteAndPromote" (Learn, asyncoperation table reference, OperationType choices:
    /// 207; plan 2.6, ruling M11).
    /// </summary>
    public const int DeleteAndPromote = 207;

    /// <summary>The <c>name</c> and <c>messagename</c> of a queued promote of a holding solution.</summary>
    public const string DeleteAndPromoteMessage = "DeleteAndPromote";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "StageAndUpgrade" (Learn, asyncoperation table reference, OperationType choices:
    /// 211; plan 2.6, ruling M11).
    /// </summary>
    public const int StageAndUpgrade = 211;

    /// <summary>The <c>name</c> and <c>messagename</c> of a queued <c>StageAndUpgrade</c>.</summary>
    public const string StageAndUpgradeMessage = "StageAndUpgrade";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Migration Import": an import of a package migrated from Dataverse (migration plan M3,
    /// ruling R1). Dataverse has no such type; the value is xrmverse's own, chosen far from Dataverse's values.
    /// </summary>
    public const int MigrationImport = 99010;

    /// <summary>The <c>name</c> and <c>messagename</c> of a migration import job.</summary>
    public const string MigrationImportMessage = "MigrationImport";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Email Send": the send of one email through the mailbox of its sender (plan 4.1b), queued by
    /// SendEmail with <c>IssueSend</c> true. Dataverse has no such type; the value is xrmverse's own, next to
    /// <see cref="MigrationImport"/> (99012 is the mailbox test of plan 4.1b).
    /// </summary>
    public const int EmailSend = 99011;

    /// <summary>The <c>name</c> and <c>messagename</c> of an email send job.</summary>
    public const string EmailSendMessage = "SendEmail";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Mailbox Test": the test of one mailbox's outgoing configuration (plan 4.1b), queued when
    /// <c>mailbox.testemailconfigurationscheduled</c> is set to true. Dataverse has no such type; the value is xrmverse's own, next to
    /// <see cref="EmailSend"/>.
    /// </summary>
    public const int MailboxTest = 99012;

    /// <summary>The <c>name</c> and <c>messagename</c> of a mailbox test job.</summary>
    public const string MailboxTestMessage = "TestMailbox";

    /// <summary>
    /// <c>asyncoperation.operationtype</c> "Mailbox Sync": the synchronization of one mailbox's incoming email (plan 4.1c). Dataverse has
    /// no such type; the value is xrmverse's own, next to <see cref="MailboxTest"/>.
    /// </summary>
    public const int MailboxSync = 99013;

    /// <summary>Object type code of <c>systemuser</c> (the type column of the polymorphic <c>ownerid</c>).</summary>
    private const int SystemUserTypeCode = 8;

    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// Inserts the job of <paramref name="step"/> in <paramref name="session"/>'s transaction: name = step name, operationtype
    /// 1, messagename, primaryentitytype, owningextensionid = step, correlationid, depth, regardingobjectid = primary entity
    /// id, owner = the context's <c>UserId</c> (the identity the job runs as), creator (<c>createdby</c>/<c>modifiedby</c>) =
    /// the context's <c>InitiatingUserId</c> (the job's <c>InitiatingUserId</c>), statecode 0 / statuscode 0, and data = the context serialized now (a
    /// snapshot: later changes to the shared parameter collections do not reach the job). The job's id becomes the
    /// context's <c>OperationId</c>, as in Dataverse.
    /// </summary>
    /// <exception cref="XrmdFaultException">500 when the context cannot be serialized (e.g. a shared variable of a type that
    /// is not a known data contract or is outside the <see cref="Serialization.SdkTypeAllowlist"/>).</exception>
    public async Task EnqueueAsync(DataSession session, PluginStep step, XrmdPluginExecutionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        var id = Guid.NewGuid();
        context.OperationId = id;
        string data;
        try
        {
            data = AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(context));
        }
        catch (Exception ex) when (ex is SerializationException or InvalidDataContractException)
        {
            throw new XrmdFaultException(ErrorCodes.Unexpected,
                $"The execution context of asynchronous step '{step.Name}' cannot be serialized: {ex.Message}", 500);
        }

        const string sql = """
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, messagename, primaryentitytype, data,
                correlationid, depth, retrycount, owningextensionid, regardingobjectid, statecode, statuscode,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            VALUES (@id, @name, @operationtype, @messagename, @primaryentitytype, @data,
                @correlationid, @depth, 0, @step, @regarding, 0, 0,
                @now, @initiating, @now, @initiating, @user, @usertype, @user, @bu)
            """;
        await using var cmd = session.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@name", DbType.String, Truncate(step.Name, 256), 256);
        cmd.AddParameter("@operationtype", DbType.Int32, SystemEvent);
        cmd.AddParameter("@messagename", DbType.String, Truncate(context.MessageName, 256), 256);
        cmd.AddParameter("@primaryentitytype", DbType.String, Truncate(context.PrimaryEntityName, 64), 64);
        cmd.AddParameter("@data", DbType.String, data, -1);
        cmd.AddParameter("@correlationid", DbType.Guid, context.CorrelationId);
        cmd.AddParameter("@depth", DbType.Int32, context.Depth);
        cmd.AddParameter("@step", DbType.Guid, step.Id);
        cmd.AddParameter("@regarding", DbType.Guid, context.PrimaryEntityId == Guid.Empty ? DBNull.Value : context.PrimaryEntityId);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@user", DbType.Guid, context.UserId);
        cmd.AddParameter("@initiating", DbType.Guid, context.InitiatingUserId == Guid.Empty ? context.UserId : context.InitiatingUserId);
        cmd.AddParameter("@usertype", DbType.Int32, SystemUserTypeCode);
        cmd.AddParameter("@bu", DbType.Guid, context.BusinessUnitId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Inserts the job of a queued solution import (ImportSolutionAsync, ExecuteAsync of ImportSolution) in
    /// <paramref name="session"/>: operationtype <see cref="ImportSolution"/>, name and messagename <c>ImportSolution</c>,
    /// data = <paramref name="file"/> as base64, <c>xrmverse_importjobid</c> = <paramref name="importJobId"/> (the
    /// <c>importjob</c> the run writes), owner, creator and owning business unit = <paramref name="caller"/> (the job runs as its
    /// owner), a new correlationid, depth 1, statecode 0 / statuscode 0. The caller wakes the worker (<see cref="Signal"/>)
    /// once the row is committed.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    public Task<Guid> EnqueueImportAsync(DataSession session, Caller caller, byte[] file, Guid importJobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        return EnqueueImportAsync(session, caller, Convert.ToBase64String(file), importJobId, ct);
    }

    /// <summary>
    /// <see cref="EnqueueImportAsync(DataSession, Caller, byte[], Guid, CancellationToken)"/> with the <c>data</c> as the
    /// importer writes it (the file as base64, possibly with the options the run needs ahead of it).
    /// </summary>
    public Task<Guid> EnqueueImportAsync(DataSession session, Caller caller, string data, Guid importJobId, CancellationToken ct) =>
        EnqueueHandlerJobAsync(session, caller, ImportSolution, ImportSolutionMessage, data, importJobId, ct);

    /// <summary>
    /// <see cref="EnqueueImportAsync(DataSession, Caller, string, Guid, CancellationToken)"/> for another solution operation
    /// that writes an <c>importjob</c> (e.g. <see cref="StageAndUpgrade"/>): operationtype <paramref name="operationType"/>, name
    /// and messagename <paramref name="messageName"/>.
    /// </summary>
    public Task<Guid> EnqueueImportAsync(DataSession session, Caller caller, int operationType, string messageName, string data,
        Guid importJobId, CancellationToken ct) =>
        EnqueueHandlerJobAsync(session, caller, operationType, messageName, data, importJobId, ct);

    /// <summary>
    /// Inserts the job of a solution operation other than an import (e.g. <see cref="UninstallSolution"/>) in
    /// <paramref name="session"/>, as <see cref="EnqueueImportAsync(DataSession, Caller, string, Guid, CancellationToken)"/> does
    /// but without an <c>importjob</c>: operationtype <paramref name="operationType"/>, name and messagename
    /// <paramref name="messageName"/>, the run's input in <paramref name="data"/>, owned by <paramref name="caller"/> (the job runs
    /// as its owner). The caller wakes the worker (<see cref="Signal"/>) once the row is committed.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    public Task<Guid> EnqueueJobAsync(DataSession session, Caller caller, int operationType, string messageName, string data,
        CancellationToken ct) =>
        EnqueueHandlerJobAsync(session, caller, operationType, messageName, data, null, ct);

    private static async Task<Guid> EnqueueHandlerJobAsync(DataSession session, Caller caller, int operationType, string messageName,
        string data, Guid? importJobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(messageName);
        ArgumentNullException.ThrowIfNull(data);
        const string sql = """
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, messagename, data, xrmverse_importjobid,
                correlationid, depth, retrycount, statecode, statuscode,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            VALUES (@id, @name, @operationtype, @name, @data, @importjob,
                @correlationid, 1, 0, 0, 0,
                @now, @user, @now, @user, @user, @usertype, @user, @bu)
            """;
        var id = Guid.NewGuid();
        await using var cmd = session.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@name", DbType.String, messageName, 256);
        cmd.AddParameter("@operationtype", DbType.Int32, operationType);
        cmd.AddParameter("@data", DbType.String, data, -1);
        cmd.AddParameter("@importjob", DbType.Guid, importJobId is { } job ? job : DBNull.Value);
        cmd.AddParameter("@correlationid", DbType.Guid, Guid.NewGuid());
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@user", DbType.Guid, caller.UserId);
        cmd.AddParameter("@usertype", DbType.Int32, SystemUserTypeCode);
        cmd.AddParameter("@bu", DbType.Guid, caller.BusinessUnitId);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>
    /// Inserts a system job run by the <see cref="IAsyncOperationHandler"/> of <paramref name="operationType"/> in
    /// <paramref name="session"/>: <paramref name="name"/> (at most 256 characters), messagename, primaryentitytype,
    /// regardingobjectid and data as given, owner, creator and owning business unit = <paramref name="caller"/> (the job runs as
    /// its owner), a new correlationid, depth 1, statecode 0 / statuscode 0. The caller wakes the worker (<see cref="Signal"/>)
    /// once the row is committed.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    public async Task<Guid> EnqueueSystemJobAsync(DataSession session, Caller caller, int operationType, string name, string messageName,
        string? primaryEntityType, Guid? regardingObjectId, string? data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(messageName);

        const string sql = """
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, messagename, primaryentitytype, regardingobjectid, data,
                correlationid, depth, retrycount, statecode, statuscode,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            VALUES (@id, @name, @operationtype, @messagename, @primaryentitytype, @regarding, @data,
                @correlationid, 1, 0, 0, 0,
                @now, @user, @now, @user, @user, @usertype, @user, @bu)
            """;
        var id = Guid.NewGuid();
        await using var cmd = session.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@name", DbType.String, Truncate(name, 256), 256);
        cmd.AddParameter("@operationtype", DbType.Int32, operationType);
        cmd.AddParameter("@messagename", DbType.String, Truncate(messageName, 256), 256);
        cmd.AddParameter("@primaryentitytype", DbType.String, primaryEntityType is null ? DBNull.Value : Truncate(primaryEntityType, 64), 64);
        cmd.AddParameter("@regarding", DbType.Guid, (object?)regardingObjectId ?? DBNull.Value);
        cmd.AddParameter("@data", DbType.String, (object?)data ?? DBNull.Value, -1);
        cmd.AddParameter("@correlationid", DbType.Guid, Guid.NewGuid());
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@user", DbType.Guid, caller.UserId);
        cmd.AddParameter("@usertype", DbType.Int32, SystemUserTypeCode);
        cmd.AddParameter("@bu", DbType.Guid, caller.BusinessUnitId);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>
    /// Inserts the job of a background workflow (plan 2.14, ruling W9) in <paramref name="session"/>'s transaction (a rolled-back
    /// operation queues nothing): operationtype <see cref="Workflow"/>, <c>name</c> = the workflow's name, <c>messagename</c> = the
    /// triggering message, <c>primaryentitytype</c> and <c>regardingobjectid</c> = the record, <c>workflowactivationid</c> = the
    /// activation, owner and owning business unit = the workflow's owner (the job runs as its owner, W7), <c>createdby</c> = the
    /// caller, <c>depth</c> as given, <c>data</c> = the run's state, a new correlationid, statecode 0 / statuscode 0. The caller wakes
    /// the worker (<see cref="Signal"/>) once the row is committed.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    public async Task<Guid> EnqueueWorkflowAsync(DataSession s, WorkflowJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Primary);
        const string sql = """
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, messagename, primaryentitytype, regardingobjectid,
                workflowactivationid, data, correlationid, depth, retrycount, statecode, statuscode,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            SELECT @id, @name, @operationtype, @messagename, @primaryentitytype, @regarding,
                @activation, @data, @correlationid, @depth, 0, 0, 0,
                @now, @creator, @now, @creator, u.systemuserid, @usertype, u.systemuserid, u.businessunitid
            FROM data.systemuser u WHERE u.systemuserid = @owner
            """;
        var id = Guid.NewGuid();
        await using var cmd = s.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        cmd.AddParameter("@name", DbType.String, Truncate(request.Name, 256), 256);
        cmd.AddParameter("@operationtype", DbType.Int32, Workflow);
        cmd.AddParameter("@messagename", DbType.String, Truncate(request.MessageName, 256), 256);
        cmd.AddParameter("@primaryentitytype", DbType.String, Truncate(request.Primary.LogicalName, 64), 64);
        cmd.AddParameter("@regarding", DbType.Guid, request.Primary.Id);
        cmd.AddParameter("@activation", DbType.Guid, request.ActivationId);
        cmd.AddParameter("@data", DbType.String, request.Data, -1);
        cmd.AddParameter("@correlationid", DbType.Guid, Guid.NewGuid());
        cmd.AddParameter("@depth", DbType.Int32, request.Depth);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@creator", DbType.Guid, request.CreatedBy);
        cmd.AddParameter("@owner", DbType.Guid, request.OwnerId);
        cmd.AddParameter("@usertype", DbType.Int32, SystemUserTypeCode);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw XrmdFaultException.InvalidArgument(
                $"Workflow '{request.Name}' cannot be queued: its owner {request.OwnerId} is not a user; xrmverse runs a background workflow only as a user.");
        return id;
    }

    /// <summary>The <c>regardingobjectid</c> of the job <paramref name="asyncOperationId"/>; null when it has none.</summary>
    public static async Task<Guid?> RegardingObjectIdAsync(DataSession session, Guid asyncOperationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var cmd = session.Command("SELECT regardingobjectid FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.AddParameter("@id", DbType.Guid, asyncOperationId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>True when a job queued for the <c>importjob</c> <paramref name="importJobId"/> exists (in any state).</summary>
    public static async Task<bool> ImportQueuedAsync(DataSession session, Guid importJobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var cmd = session.Command(session.Dialect.Limit("SELECT 1 FROM data.asyncoperation WHERE xrmverse_importjobid = @importjob", "1"));
        cmd.AddParameter("@importjob", DbType.Guid, importJobId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>The <c>xrmverse_importjobid</c> of the queued import <paramref name="asyncOperationId"/>; null when it has none.</summary>
    public static async Task<Guid?> ImportJobIdAsync(DataSession session, Guid asyncOperationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var cmd = session.Command("SELECT xrmverse_importjobid FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.AddParameter("@id", DbType.Guid, asyncOperationId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Wakes the worker after a commit that enqueued work (signals coalesce; no-op when one is pending).</summary>
    public void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another signal is already pending.
        }
    }

    /// <summary>Waits for a <see cref="Signal"/> or <paramref name="timeout"/>, whichever comes first.</summary>
    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);

    private static string Truncate(string? text, int max) =>
        text is null ? string.Empty : text.Length <= max ? text : text[..max];
}

/// <summary>
/// A background workflow job to queue (<see cref="AsyncOperationQueue.EnqueueWorkflowAsync"/>, plan 2.14, ruling W9).
/// </summary>
/// <param name="ActivationId">The activation (<c>workflow</c> of type 2) the job runs.</param>
/// <param name="Name">The workflow's name (the job's <c>name</c>).</param>
/// <param name="MessageName">The message that triggered it (<c>Create</c>, <c>Update</c>, ...; a child job its parent's).</param>
/// <param name="Primary">The record it runs on.</param>
/// <param name="OwnerId">The workflow's owner, a user: the job's owner, whom it runs as (W7).</param>
/// <param name="CreatedBy">The user whose request queued it.</param>
/// <param name="Depth">The triggering depth + 1.</param>
/// <param name="Data">The run's state (<c>WorkflowJobState</c> JSON).</param>
public sealed record WorkflowJobRequest(Guid ActivationId, string Name, string MessageName, EntityReference Primary, Guid OwnerId,
    Guid CreatedBy, int Depth, string Data);
