using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Idp;
using Xrmd.Security;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>A host in <c>Auth:Mode=Local</c> on its own organization database, and local users for it.</summary>
internal static class LocalIdpTestHost
{
    public const string Host = "xrm.local.test";
    public const string Audience = "https://" + Host + "/";

    public static WebApplicationFactory<Program> Create(XrmdFixture fx) => fx.WithOwnDatabase(b =>
    {
        b.UseSetting("Auth:Mode", "Local");
        b.UseSetting("Auth:Audience", Audience);
    });

    /// <summary>A System Administrator <c>systemuser</c> named <paramref name="userName"/> (its domainname) with a new object id.</summary>
    public static async Task<(Guid UserId, Guid ObjectId)> SystemUserAsync(WebApplicationFactory<Program> app, string userName)
    {
        var oid = Guid.NewGuid();
        var userId = await app.Services.GetRequiredService<SecurityBootstrap>().EnsureSystemAdministratorAsync(oid, userName, default);
        return (userId, oid);
    }

    /// <summary>A System Administrator with a local credential.</summary>
    public static async Task<(Guid UserId, Guid ObjectId)> LocalUserAsync(WebApplicationFactory<Program> app, string userName, string password)
    {
        var user = await SystemUserAsync(app, userName);
        await WithCredentialsAsync(app, c => c.SetPasswordAsync(user.UserId, password, default));
        return user;
    }

    public static async Task WithCredentialsAsync(WebApplicationFactory<Program> app, Func<CredentialService, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<CredentialService>());
    }

    public static async Task<T> WithCredentialsAsync<T>(WebApplicationFactory<Program> app, Func<CredentialService, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CredentialService>());
    }

    public static async Task ExecAsync(WebApplicationFactory<Program> app, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(WebApplicationFactory<Program> app, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>A clock the test moves.</summary>
    public sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
