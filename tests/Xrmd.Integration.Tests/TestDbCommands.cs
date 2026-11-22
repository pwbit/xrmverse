using System.Data.Common;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Test-only shorthand for the tests' own SQL (plan 2.7a, Task 2): a parameter typed from its value with
/// <see cref="DbValueTypes.Of"/>. Production code passes the <see cref="System.Data.DbType"/> itself (ruling P3).
/// </summary>
internal static class TestDbCommands
{
    public static DbParameter AddParameter(this DbCommand cmd, string name, object? value) =>
        cmd.AddParameter(name, DbValueTypes.Of(value), value);
}
