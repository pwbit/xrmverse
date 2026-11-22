using System.Data;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The publishers the tests' custom names need (every custom name starts with the prefix of an existing publisher): <c>pb</c>, <c>xv</c> and
/// <c>ya</c>, next to the default publisher (<c>new</c>) that every organization has.
/// </summary>
public static class TestPublishers
{
    public static readonly (string UniqueName, string Prefix, int OptionValuePrefix)[] All =
    [
        ("testpublisherpb", "pb", 10001),
        ("testpublisherxv", "xv", 10002),
        ("testpublisherya", "ya", 10003), // the schema of CliApplyMcpTests
        ("testpublisherzm", "zm", 10004), // the schema of McpTests
        ("testpublisherzq", "zq", 10005), // the schema of AdminSchemaTests
    ];

    /// <summary>Creates the publishers that the organization database at <paramref name="connectionString"/> lacks (it must be seeded).</summary>
    public static async Task EnsureAsync(string connectionString)
    {
        // Engine-neutral SQL (plan 2.7a, Task 5): the fixture runs it on either provider.
        await using var conn = await TestProvider.OpenAsync(connectionString);
        foreach (var (uniqueName, prefix, optionValuePrefix) in All)
        {
            await using var cmd = TestSql.Command(conn, """
                INSERT INTO data.publisher (publisherid, uniquename, friendlyname, customizationprefix, customizationoptionvalueprefix,
                    owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
                SELECT @id, @uniquename, @uniquename, @prefix, @value, owningbusinessunit, @now, createdby, @now, createdby, 0, 1
                FROM data.publisher
                WHERE publisherid = @default AND NOT EXISTS (SELECT 1 FROM data.publisher WHERE customizationprefix = @prefix)
                """);
            cmd.AddParameter("@id", DbType.Guid, Guid.NewGuid());
            cmd.AddParameter("@prefix", DbType.String, prefix);
            cmd.AddParameter("@uniquename", DbType.String, uniqueName);
            cmd.AddParameter("@value", DbType.Int32, optionValuePrefix);
            cmd.AddParameter("@now", DbType.DateTime2, DateTime.UtcNow);
            cmd.AddParameter("@default", DbType.Guid, Xrmd.Metadata.SystemSeed.DefaultPublisherId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
