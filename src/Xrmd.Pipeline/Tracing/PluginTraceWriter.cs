using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Pipeline.Tracing;

/// <summary>
/// Writes <c>plugintracelog</c> rows on a connection of its own, without a transaction: a trace survives the rollback
/// of the operation the plug-in ran in.
/// </summary>
public sealed class PluginTraceWriter(IDbConnectionFactory factory)
{
    /// <summary>Operation type "Plug-in" of <c>plugintracelog.operationtype</c>.</summary>
    public const int PluginOperation = 1;

    /// <summary>Operation type "Workflow Activity" of <c>plugintracelog.operationtype</c> (a custom workflow activity, plan 2.14 W17).</summary>
    public const int WorkflowActivityOperation = 2;

    /// <summary>Inserts one row for an execution of <paramref name="step"/>; <paramref name="messageBlock"/> is the trace text.</summary>
    public Task WriteAsync(PluginStep step, XrmdPluginExecutionContext context, string messageBlock, string? exceptionDetails,
        DateTime startUtc, int durationMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        return WriteAsync(step.TypeName, PluginOperation, step.Id, step.UnsecureConfiguration, context, messageBlock, exceptionDetails,
            startUtc, durationMs, ct);
    }

    /// <summary>
    /// Inserts one row for an execution of the type <paramref name="typeName"/> (<paramref name="operationType"/>: a plug-in or a
    /// workflow activity, which has no step); <paramref name="messageBlock"/> is the trace text.
    /// </summary>
    public async Task WriteAsync(string typeName, int operationType, Guid? stepId, string? configuration, XrmdPluginExecutionContext context,
        string messageBlock, string? exceptionDetails, DateTime startUtc, int durationMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(context);
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        // Plan 2.7a, Task 6: the Boolean literal of the engine (PostgreSQL's boolean takes no integer).
        var sql = $"""
            INSERT INTO data.plugintracelog (plugintracelogid, typename, messageblock, exceptiondetails, messagename, primaryentity,
                mode, operationtype, depth, correlationid, requestid, pluginstepid, configuration,
                performanceexecutionstarttime, performanceexecutionduration, issystemcreated,
                createdon, createdby, modifiedon, modifiedby, owningbusinessunit, statecode, statuscode)
            VALUES (@id, @typename, @messageblock, @exceptiondetails, @messagename, @primaryentity,
                @mode, @operationtype, @depth, @correlationid, @requestid, @pluginstepid, @configuration,
                @start, @duration, {SqlDialects.For(conn).BooleanLiteral(false)},
                @now, @user, @now, @user, @bu, 0, 1)
            """;
        // CancellationToken.None (the open above too): a trace of a cancelled or failed request must still be written.
        await using var cmd = conn.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, Guid.NewGuid());
        cmd.AddParameter("@typename", DbType.String, Truncate(typeName, 1024), 1024);
        cmd.AddParameter("@messageblock", DbType.String, Truncate(messageBlock, TracingService.MaxLength), -1);
        cmd.AddParameter("@exceptiondetails", DbType.String, (object?)exceptionDetails ?? DBNull.Value, -1);
        cmd.AddParameter("@messagename", DbType.String, context.MessageName, 1024);
        cmd.AddParameter("@primaryentity", DbType.String, context.PrimaryEntityName, 1024);
        cmd.AddParameter("@mode", DbType.Int32, context.Mode);
        cmd.AddParameter("@operationtype", DbType.Int32, operationType);
        cmd.AddParameter("@depth", DbType.Int32, context.Depth);
        cmd.AddParameter("@correlationid", DbType.Guid, context.CorrelationId);
        cmd.AddParameter("@requestid", DbType.Guid, (object?)context.RequestId ?? DBNull.Value);
        cmd.AddParameter("@pluginstepid", DbType.Guid, (object?)stepId ?? DBNull.Value);
        cmd.AddParameter("@configuration", DbType.String, (object?)configuration ?? DBNull.Value, -1);
        cmd.AddParameter("@start", DbType.DateTime2, startUtc);
        cmd.AddParameter("@duration", DbType.Int32, durationMs);
        cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
        cmd.AddParameter("@user", DbType.Guid, context.UserId);
        cmd.AddParameter("@bu", DbType.Guid, context.BusinessUnitId);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string Truncate(string? text, int max) =>
        text is null ? string.Empty : text.Length <= max ? text : text[..max];
}
