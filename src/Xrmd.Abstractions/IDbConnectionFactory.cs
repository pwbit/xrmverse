using System.Data.Common;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Abstractions;

/// <summary>Opens connections to the organization database of the configured storage provider (plan 2.7a, ruling P3).</summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// The SQL dialect of the engine the connections talk to (plan 2.7a, fix round 1): the one source of the server's
    /// <see cref="ISqlDialect"/>, equal to <see cref="DataSession.Dialect"/> of a session on these connections.
    /// </summary>
    ISqlDialect Dialect { get; }

    /// <summary>
    /// The type of the connections <see cref="OpenAsync"/> returns (plan 2.7a, Task 3, carry-over N2): <see cref="SqlDialects.Of"/>
    /// checks that <see cref="Dialect"/> is the one registered for it, which <see cref="DataSession.Dialect"/> uses.
    /// </summary>
    Type ConnectionType { get; }

    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}
