using System.Data.Common;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Abstractions;

/// <summary>
/// The database connection and transaction a unit of work runs on. It owns neither: whoever opened them closes them.
/// Data operations create every command through <see cref="Command"/> so that all of them share the transaction.
/// </summary>
/// <remarks>
/// Work that must follow the outcome of the transaction (publishing metadata changes, invalidating caches, releasing a
/// lock taken for the transaction) registers with <see cref="OnCommitted"/> / <see cref="OnRolledBack"/>; whoever owns the
/// transaction calls <see cref="Committed"/> after the commit or <see cref="RolledBack"/> after the rollback (or after a
/// failure that leaves nothing committed). Each call runs the callbacks of its outcome in registration order and forgets
/// those of both outcomes, so the session can carry a following transaction.
/// </remarks>
public sealed class DataSession(DbConnection connection, DbTransaction? transaction)
{
    private List<Action> _onCommitted = [];
    private List<Action> _onRolledBack = [];

    public DbConnection Connection { get; } = connection;

    /// <summary>The current transaction (null: auto-commit); the pipeline sets it after the pre-operation stages.</summary>
    /// <remarks>Each assignment starts a new <see cref="TransactionId"/>.</remarks>
    public DbTransaction? Transaction
    {
        get => _transaction;
        set
        {
            _transaction = value;
            TransactionId = Guid.NewGuid();
        }
    }

    private DbTransaction? _transaction = transaction;

    /// <summary>
    /// One id per transaction of this session (plan 2.7a, Task 8 fix round 2), e.g. the <c>transactionid</c> of the audit rows
    /// written in it: a new one each time <see cref="Transaction"/> is assigned and when the owner reports the outcome
    /// (<see cref="Committed"/>, <see cref="RolledBack"/>). It is ours, not the provider's transaction object: Npgsql hands out the
    /// same <c>NpgsqlTransaction</c> again for the next transaction of a connection.
    /// </summary>
    public Guid TransactionId { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// The SQL dialect of the engine <see cref="Connection"/> talks to (plan 2.7a, ruling P3): every engine-specific fragment of
    /// the statements run on this session comes from it.
    /// </summary>
    public ISqlDialect Dialect => _dialect ??= SqlDialects.For(Connection);

    private ISqlDialect? _dialect;

    /// <summary>The quoted identifier <paramref name="identifier"/> in <see cref="Dialect"/> (plan 2.7a, fix round 1).</summary>
    public string Quote(string identifier) => Dialect.Quote(identifier);

    /// <summary>A new command on <see cref="Connection"/> enlisted in <see cref="Transaction"/>.</summary>
    public DbCommand Command(string sql) => Connection.Command(sql, Transaction);

    /// <summary>Runs <paramref name="action"/> when the owner reports the commit (<see cref="Committed"/>).</summary>
    public void OnCommitted(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _onCommitted.Add(action);
    }

    /// <summary>Runs <paramref name="action"/> when the owner reports the rollback (<see cref="RolledBack"/>).</summary>
    public void OnRolledBack(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _onRolledBack.Add(action);
    }

    /// <summary>Called by the owner of the transaction after its commit: runs the <see cref="OnCommitted"/> callbacks.</summary>
    /// <exception cref="AggregateException">One or more callbacks failed; all of them ran anyway.</exception>
    public void Committed() => Run(TakeCallbacks().Committed);

    /// <summary>Called by the owner of the transaction after its rollback: runs the <see cref="OnRolledBack"/> callbacks.</summary>
    /// <exception cref="AggregateException">One or more callbacks failed; all of them ran anyway.</exception>
    public void RolledBack() => Run(TakeCallbacks().RolledBack);

    private (List<Action> Committed, List<Action> RolledBack) TakeCallbacks()
    {
        TransactionId = Guid.NewGuid(); // the transaction ended: a following one on this session has its own id
        var taken = (_onCommitted, _onRolledBack);
        _onCommitted = [];
        _onRolledBack = [];
        return taken;
    }

    private static void Run(List<Action> callbacks)
    {
        List<Exception>? failures = null;
        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }
        if (failures is not null) throw new AggregateException(failures);
    }
}
