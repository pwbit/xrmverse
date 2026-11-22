using Xrmd.Abstractions.Storage;
using System.Data.Common;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The helpers of the authoring tests (plan 2.21, Task 6): the parity export imported once per database, users, solutions of the
/// export's publisher and the YAML fixtures (<c>tests/fixtures/workflows/yaml</c>).
/// </summary>
public static class AuthoringTestHelper
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly HashSet<string> Imported = [];

    /// <summary>The YAML fixture <c>yaml/{name}</c>, copied next to the test assembly (LF line ends).</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "workflows", "yaml", name));

    /// <summary>Imports the parity export <c>xrmdWfTest_1_0_0_2.zip</c> into the fixture's database once (its processes stay Draft).</summary>
    public static async Task EnsureParityAsync(XrmdFixture fx)
    {
        await Lock.WaitAsync();
        try
        {
            if (!Imported.Add(fx.ConnectionString)) return;
            await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
            var zip = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmdWfTest_1_0_0_2.zip"));
            await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        }
        catch
        {
            Imported.Remove(fx.ConnectionString);
            throw;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static async Task ExecAsync(XrmdFixture fx, string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(XrmdFixture fx, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>A new user of the root business unit with the role <paramref name="role"/>, resolved as a caller.</summary>
    public static async Task<Caller> UserWithRoleAsync(XrmdFixture fx, Guid role) => (await UserWithRoleAndObjectIdAsync(fx, role)).Caller;

    /// <summary>A new user of the root business unit with the role <paramref name="role"/>: its caller and its Entra object id.</summary>
    public static async Task<(Caller Caller, Guid ObjectId)> UserWithRoleAndObjectIdAsync(XrmdFixture fx, Guid role)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, role);
        return (await TestServices.CallerAsync(fx.App.Services, objectId), objectId);
    }

    /// <summary>A new unmanaged solution of the publisher whose prefix is <paramref name="prefix"/> (by default the parity export's).</summary>
    public static async Task<string> SolutionAsync(XrmdFixture fx, string uniqueName, string version = "1.0.0.0", string prefix = "xrmv")
    {
        var publisher = (Guid)(await ScalarAsync(fx, "SELECT TOP 1 publisherid FROM data.publisher WHERE customizationprefix = @p ORDER BY createdon",
            ("@p", prefix)))!;
        await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("solution")
        {
            ["uniquename"] = uniqueName, ["friendlyname"] = uniqueName, ["version"] = version,
            ["publisherid"] = new EntityReference("publisher", publisher),
        });
        return uniqueName;
    }

    /// <summary>The plugintype <paramref name="typeName"/> of the assembly, registered when there is none yet.</summary>
    public static async Task<Guid> EnsureTypeAsync(XrmdFixture fx, Xrmd.Core.OrganizationService admin, Guid assembly, string typeName) =>
        await ScalarAsync(fx, "SELECT plugintypeid FROM data.plugintype WHERE pluginassemblyid = @a AND typename = @t", ("@a", assembly), ("@t", typeName))
            is Guid id
            ? id
            : await PluginTestHelper.RegisterTypeAsync(admin, assembly, typeName);
}
