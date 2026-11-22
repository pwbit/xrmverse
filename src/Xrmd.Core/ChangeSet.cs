using System.Data.Common;
using Xrmd.Abstractions;
using Xrmd.Pipeline;

namespace Xrmd.Core;

/// <summary>
/// The transaction of a Web API change set (#130; Learn: a change set is atomic, the Web API form of <c>ExecuteTransaction</c>).
/// Scoped: while it is open, every pipeline execution of the request's <see cref="OrganizationService"/> runs on its session and
/// joins its transaction, and the messages that write outside the pipeline, in a transaction of their own, are refused.
/// </summary>
public sealed class ChangeSet(IDbConnectionFactory factory, PipelineExecutor executor) : IAsyncDisposable, IDisposable
{
    private DbConnection? _connection;

    /// <summary>The open change set's session; null when none is open.</summary>
    public DataSession? Session { get; private set; }

    /// <summary>Opens the change set's connection and transaction.</summary>
    /// <exception cref="InvalidOperationException">A change set is already open.</exception>
    public async Task BeginAsync(CancellationToken ct)
    {
        if (Session is not null) throw new InvalidOperationException("A change set is already open.");
        _connection = await factory.OpenAsync(ct);
        var session = new DataSession(_connection, null);
        try
        {
            await executor.BeginTransactionAsync(session, ct);
        }
        catch
        {
            await CloseAsync();
            throw;
        }
        Session = session;
    }

    /// <summary>Commits the change set and closes it.</summary>
    public async Task CommitAsync()
    {
        var session = Session ?? throw new InvalidOperationException("No change set is open.");
        try
        {
            await executor.CommitTransactionAsync(session);
        }
        finally
        {
            await CloseAsync();
        }
    }

    /// <summary>Rolls the change set back and closes it; nothing happens when none is open.</summary>
    public async Task RollbackAsync()
    {
        if (Session is not { } session) return;
        try
        {
            await executor.RollbackTransactionAsync(session);
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task CloseAsync()
    {
        Session = null;
        if (_connection is not null) await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await RollbackAsync();

    /// <summary>A scope disposed synchronously still rolls back a change set left open.</summary>
    public void Dispose() => RollbackAsync().GetAwaiter().GetResult();
}
