using System.Diagnostics;
using System.Globalization;
using Meziantou.Framework.Tds.Handler;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Security;
using Xrmd.Tds.Translation;

namespace Xrmd.Tds.Execution;

/// <summary>
/// Answers a SQL endpoint request (plan 3.3, Task 3): reads the SQL of the batch, or the statement and parameters of an
/// <c>sp_executesql</c> call, translates it (<see cref="SqlTranslator"/>) and runs it as the caller, resolved from the login's
/// principal like every front end does (<see cref="CallerResolver"/>), through the <see cref="QueryExecutor"/>: the caller's read
/// security and field security apply, and no plug-in runs (ruling T10). The rows are read page by page (paging cookie) up to the
/// <c>TOP</c> of the query; more than <see cref="TdsOptions.MaxRows"/> rows fail the query, with no partial result (ruling T8).
/// Resolved in a new DI scope for each request; the listener cancels it at <see cref="TdsOptions.Timeout"/>.
/// <para>
/// Errors: a refused batch keeps the number of its <see cref="TdsSqlException"/>; a missing privilege or an unreadable secured
/// column in a condition is 229; an invalid query 40000 with the executor's message; a caller that is not a usable user of the
/// organization 18456; a page that hits SqlClient's 30 s command timeout (error -2) 40000 with the time-limit message. Anything else is the listener's: a generic message, never internals.
/// </para>
/// <para>Each batch is logged at Information with the user id, the duration and the row count, never the SQL text or the parameter values.</para>
/// </summary>
public sealed class TdsQueryHandler(IMetadataStore store, IDbConnectionFactory factory, SecurityCache security, FieldSecurity fields,
    QueryExecutor executor, IOptions<TdsOptions> options, IConfiguration configuration, ILogger<TdsQueryHandler> logger) : ITdsQueryHandler
{
    /// <summary>A permission was denied on an object.</summary>
    public const int PermissionDenied = 229;

    /// <summary>The largest page the query engine reads at once (and the largest <c>TOP</c> it accepts).</summary>
    public const int PageSize = SqlQueryCompiler.MaxPageSize;

    private const string ExecuteSql = "sp_executesql";

    /// <summary>The message of a query that returns more rows than <paramref name="maxRows"/> (ruling T8).</summary>
    public static string TooManyRowsMessage(int maxRows) =>
        $"The query returns more than {maxRows.ToString(CultureInfo.InvariantCulture)} rows. Add TOP or a filter.";

    public async ValueTask<TdsQueryResult> HandleAsync(TdsQueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var watch = Stopwatch.StartNew();
        Guid? userId = null;
        try
        {
            var (sql, parameters) = Batch(context);
            var batch = new SqlTranslator(store).Translate(sql, parameters);
            var result = new TdsQueryResult();
            if (batch is not NoOp)
            {
                var caller = await new CallerResolver(factory, new TdsCurrentUser(context.UserContext), security).ResolveAsync(cancellationToken);
                userId = caller.UserId;
                result.ResultSets.Add(batch switch
                {
                    SystemQuery system => await new SystemQueries(store, security, fields).AnswerAsync(system, caller, Database, cancellationToken),
                    TranslatedQuery query => await RunAsync(query, caller, cancellationToken),
                    _ => throw new InvalidOperationException($"Unknown translated batch {batch.GetType().Name}."),
                });
            }

            var rows = result.ResultSets.Sum(s => s.Rows.Count);
            logger.LogInformation("SQL endpoint batch of user {UserId}: {RowCount} rows in {ElapsedMs} ms.", userId, rows, watch.ElapsedMilliseconds);
            return result;
        }
        catch (TdsSqlException ex)
        {
            return Refused(userId, watch, (uint)ex.Number, ex.Message);
        }
        catch (XrmdFaultException ex) when (Map(ex) is { } error)
        {
            return Refused(userId, watch, error.Number, error.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // SqlClient reports a cancelled command as a SqlException: the listener answers a cancellation (the time limit).
            throw new OperationCanceledException("The SQL endpoint request was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex) when (IsCommandTimeout(ex))
        {
            // Each page the executor reads is one SqlCommand with SqlClient's default CommandTimeout (30 s): a slow page fails
            // with SQL error -2 before the batch limit. The client gets the same time-limit error as for the batch limit.
            return Refused(userId, watch, TdsErrors.Unsupported, TdsErrors.TimeoutMessage(options.Value.Timeout));
        }
    }

    /// <summary>The engine's command timeout (SQL error -2; ruling P9), thrown as is or wrapped once.</summary>
    public static bool IsCommandTimeout(Exception exception) => IsTimeout(exception) || IsTimeout(exception.InnerException);

    private static bool IsTimeout(Exception? exception) => exception is System.Data.Common.DbException db && SqlDialects.Classify(db) == DbErrorKind.Timeout;

    private TdsQueryResult Refused(Guid? userId, Stopwatch watch, uint number, string message)
    {
        logger.LogInformation("SQL endpoint batch of user {UserId} refused with error {ErrorNumber} in {ElapsedMs} ms.", userId, number,
            watch.ElapsedMilliseconds);
        return TdsErrors.Error(number, message);
    }

    /// <summary>The SQL error of a fault the client may see; null for the others (left to the listener).</summary>
    private static (uint Number, string Message)? Map(XrmdFaultException fault) => fault.ErrorCode switch
    {
        ErrorCodes.PrivilegeDenied or ErrorCodes.FieldReadDenied => (PermissionDenied, fault.Message),
        ErrorCodes.InvalidArgument or ErrorCodes.AggregateQueryRecordLimitExceeded => (TdsErrors.Unsupported, fault.Message),
        ErrorCodes.NotMemberOfOrganization or ErrorCodes.UserDisabled or ErrorCodes.UserNotAssignedRoles =>
            (TdsErrors.LoginFailed, TdsErrors.LoginFailedMessage),
        _ => null,
    };

    /// <summary>The name <c>DB_NAME()</c> reports: the organization database's.</summary>
    private string Database =>
        StorageConnectionStrings.DatabaseName(configuration.GetConnectionString("Org")) is { Length: > 0 } name ? name : "xrmverse";

    /// <summary>The SQL text and parameters of the request: a SQL batch, or an <c>sp_executesql</c> call.</summary>
    /// <exception cref="TdsSqlException">40000 for another procedure or a parameter the TDS server could not decode.</exception>
    private static (string Sql, IReadOnlyDictionary<string, object?> Parameters) Batch(TdsQueryContext context)
    {
        if (context.RequestType != TdsQueryRequestType.Rpc) return (context.CommandText ?? "", new Dictionary<string, object?>());

        var procedure = context.ProcedureName ?? "";
        var dot = procedure.LastIndexOf('.');
        if (!string.Equals(dot < 0 ? procedure : procedure[(dot + 1)..], ExecuteSql, StringComparison.OrdinalIgnoreCase))
            throw TdsSqlErrors.NotSupported("EXEC statements");
        if (!context.HasCompleteParameters) throw TdsSqlErrors.NotSupported("parameters of this data type");
        var list = context.Parameters;
        if (list.Count == 0 || list[0].IsNull || list[0].Value is not string statement)
            throw TdsSqlErrors.NotSupported("sp_executesql without a statement");
        // sp_executesql @stmt, @params (the declarations), then the values by name.
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in list.Skip(2))
        {
            if (string.IsNullOrEmpty(parameter.Name)) throw TdsSqlErrors.NotSupported("sp_executesql parameters without a name");
            parameters[parameter.Name] = parameter.IsNull ? null : parameter.Value;
        }
        return (statement, parameters);
    }

    private async Task<TdsResultSet> RunAsync(TranslatedQuery query, Caller caller, CancellationToken ct)
    {
        var columns = await ReadableColumnsAsync(query, caller, ct);
        var rows = await RowsAsync(query.Model, caller, ct);
        return ResultSetBuilder.Build(query.Model, columns, rows, store);
    }

    /// <summary>The columns of the result set: those of the query, less the <c>*</c> columns the caller cannot read (ruling T7).</summary>
    private async Task<IReadOnlyList<OutputColumn>> ReadableColumnsAsync(TranslatedQuery query, Caller caller, CancellationToken ct)
    {
        if (!query.Columns.Any(c => c.FromStar) || security.IsUnrestricted(caller, await security.SystemUserIdAsync(ct))) return query.Columns;
        var unreadable = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var table in query.Columns.Where(c => c.FromStar).Select(c => ResultSetBuilder.EntityName(query.Model, c.EntityAlias)).Distinct())
        {
            var set = await fields.ForAsync(caller, table, ct);
            if (ReferenceEquals(set, FieldPermissionSet.Unrestricted)) return query.Columns; // every principal bypasses: on every table
            unreadable[table] = set.UnreadableSecured;
        }
        return ResultSetBuilder.WithoutUnreadableStarColumns(query.Model, query.Columns, table => unreadable[table]);
    }

    /// <summary>
    /// The rows of <paramref name="model"/>, read page by page with the paging cookie up to its <c>TOP</c> (the query engine reads
    /// at most <see cref="PageSize"/> rows at once). An aggregate query is read once: the engine does not page aggregates.
    /// </summary>
    /// <exception cref="TdsSqlException">40000 when the query returns more than <see cref="TdsOptions.MaxRows"/> rows.</exception>
    private async Task<List<Entity>> RowsAsync(QueryModel model, Caller caller, CancellationToken ct)
    {
        var max = options.Value.MaxRows;
        var top = model.Top;
        if (top == 0)
        {
            // TOP 0: no row, but the query still runs once (one row, dropped), so that the caller's privileges are checked.
            model.Top = 1;
            await executor.ExecuteAsync(model, caller, ct);
            return [];
        }

        if (model.Aggregate)
        {
            if (top > PageSize) model.Top = null;
            var result = await executor.ExecuteAsync(model, caller, ct);
            var groups = top is { } t ? result.Entities.Take(t).ToList() : result.Entities.ToList();
            if (groups.Count > max) throw TooManyRows(max);
            if (result.MoreRecords && (top is null || top > groups.Count))
                throw max <= PageSize
                    ? TooManyRows(max)
                    : new TdsSqlException(TdsSqlErrors.Unsupported,
                        $"The aggregate query returns more than {PageSize.ToString(CultureInfo.InvariantCulture)} rows. Add TOP or a filter.");
            return groups;
        }

        if (top is { } small && small <= PageSize)
        {
            var result = await executor.ExecuteAsync(model, caller, ct);
            if (result.Entities.Count > max) throw TooManyRows(max);
            return [.. result.Entities];
        }

        // No TOP, or one above the engine's page limit: pages of at most max + 1 rows, so that a query over the cap fails on its
        // first page when the cap is small, and never reads more than one page past it.
        var limit = top ?? int.MaxValue;
        model.Top = null;
        model.PageSize = (int)Math.Min(Math.Clamp(options.Value.PageSize, 1, PageSize), Math.Min(limit, (long)max + 1));
        model.PageNumber = 1;
        model.PagingCookie = null;
        AddLinkKeyOrders(model, store);
        var rows = new List<Entity>();
        while (true)
        {
            var page = await executor.ExecuteAsync(model, caller, ct);
            rows.AddRange(page.Entities);
            if (rows.Count >= limit)
            {
                rows.RemoveRange(limit, rows.Count - limit);
                if (rows.Count > max) throw TooManyRows(max);
                return rows;
            }
            if (rows.Count > max) throw TooManyRows(max);
            if (!page.MoreRecords) return rows;
            model.PageNumber++;
            model.PagingCookie = page.PagingCookie;
        }
    }

    /// <summary>
    /// Gives every row of a paged join a fixed position: each linked table (at any depth) is ordered by its primary key, ascending,
    /// after the query's own orders. The engine already ends the order with the root primary key, which repeats on every row of a
    /// 1:N link; without the link keys, rows tied on it have no defined order and OFFSET pages could overlap or leave gaps.
    /// A distinct query is already ordered by every selected column; a query without links needs nothing.
    /// </summary>
    public static void AddLinkKeyOrders(QueryModel model, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        if (model.Distinct || model.Aggregate) return;

        void Walk(EntityNode node)
        {
            foreach (var link in node.Links)
            {
                var entity = store.GetEntity(link.EntityName)
                             ?? throw new InvalidOperationException($"The table '{link.EntityName}' of the query has no metadata.");
                var key = SystemAttributes.PrimaryIdName(entity);
                if (!link.Orders.Any(o => o.Alias is null && string.Equals(o.Attribute, key, StringComparison.Ordinal)))
                    link.Orders.Add(new OrderNode(key, Descending: false));
                Walk(link);
            }
        }
        Walk(model.Root);
    }

    private static TdsSqlException TooManyRows(int max) => new(TdsSqlErrors.Unsupported, TooManyRowsMessage(max));
}
