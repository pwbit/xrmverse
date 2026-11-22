using System.Data;
using System.Data.Common;

namespace Xrmd.Abstractions.Storage;

/// <summary>
/// Provider-neutral command and parameter helpers (plan 2.7a, ruling P3): code outside the SQL Server classes builds its
/// commands on <see cref="DbConnection"/> and types its parameters with <see cref="DbType"/>, so the same code runs on every
/// storage provider.
/// </summary>
public static class DbCommandExtensions
{
    /// <summary>
    /// Adds a parameter to <paramref name="cmd"/>. A null <paramref name="value"/> is sent as <see cref="DBNull.Value"/>;
    /// <paramref name="size"/>, <paramref name="precision"/> and <paramref name="scale"/> are set only when not 0 (0: the
    /// provider infers them from the value; size -1: the unbounded type, <c>nvarchar(max)</c> / <c>varbinary(max)</c>).
    /// </summary>
    /// <returns>The added parameter (for an output or return-value direction).</returns>
    public static DbParameter AddParameter(this DbCommand cmd, string name, DbType type, object? value,
        int size = 0, byte precision = 0, byte scale = 0)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        if (size != 0) p.Size = size;
        if (precision != 0) p.Precision = precision;
        if (scale != 0) p.Scale = scale;
        p.Value = value ?? DBNull.Value;
        // Plan 2.7a, Task 3: the engine's own adjustments (UTC timestamptz dates on PostgreSQL); a command without a
        // connection yet is adjusted by nobody.
        if (cmd.Connection is { } connection && SqlDialects.TryFor(connection) is { } dialect) dialect.ConfigureParameter(p);
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>
    /// Adds a copy of each of <paramref name="parameters"/> (usually <see cref="DetachedParameter"/>s built before the
    /// command existed, such as a compiled query's) to <paramref name="cmd"/>, as the provider's own parameters.
    /// </summary>
    public static void AddParameters(this DbCommand cmd, IEnumerable<DbParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var source in parameters)
        {
            var p = cmd.AddParameter(source.ParameterName, source.DbType, source.Value, source.Size, source.Precision, source.Scale);
            p.Direction = source.Direction;
        }
    }

    /// <summary>
    /// A new command on <paramref name="connection"/> with <paramref name="sql"/>, enlisted in <paramref name="transaction"/>
    /// when given. Work that has a <see cref="DataSession"/> uses <see cref="DataSession.Command"/> instead.
    /// </summary>
    public static DbCommand Command(this DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }
}
