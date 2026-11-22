using System.Data;
using Microsoft.EntityFrameworkCore;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Host.Idp;

/// <summary>
/// First start of a Local-mode organization: <c>xrmd server init --auth local</c> writes the initial administrator's password to a
/// one-shot file (<c>Idp:InitialAdminPasswordFile</c>, never an environment value). When the file exists the administrator
/// (<c>Seed:AdminObjectId</c>) gets its credential from it, an existing credential is never overwritten, and the file is deleted.
/// </summary>
public static class IdpBootstrap
{
    public const string PasswordFileKey = "Idp:InitialAdminPasswordFile";

    public static async Task ApplyInitialAdminPasswordAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken ct)
    {
        if (configuration[PasswordFileKey] is not { Length: > 0 } path || !File.Exists(path)) return;
        if (!Guid.TryParse(configuration["Seed:AdminObjectId"], out var objectId))
            throw new InvalidOperationException("Seed:AdminObjectId must be configured with the administrator's object id (GUID).");

        if (Unsafe(path) is { } problem)
        {
            logger.LogWarning("The initial administrator password file {Path} is not used and is left in place: {Reason}.", path, problem);
            return;
        }

        string password;
        try
        {
            password = (await File.ReadAllTextAsync(path, ct)).Split('\n')[0].TrimEnd('\r');
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning("The initial administrator password file {Path} cannot be read ({Reason}); the administrator gets no local credential. " +
                              "Make it readable by the server's user, or set the password with `xrmd idp user set-password`.", path, ex.Message);
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var userId = await FindUserAsync(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), objectId, ct);
        if (userId is null)
        {
            logger.LogWarning("The initial administrator (object id {ObjectId}) does not exist; {Path} is left in place.", objectId, path);
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        if (await db.Credentials.AnyAsync(c => c.SystemUserId == userId, ct))
            logger.LogInformation("The initial administrator already has a local credential; {Path} is discarded unused.", path);
        else
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<CredentialService>().SetPasswordAsync(userId.Value, password, ct);
                logger.LogInformation("The initial administrator's local credential was created from {Path}.", path);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                logger.LogError("The initial administrator password in {Path} was not applied: {Reason}", path, ex.Message);
                return;
            }
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning("The initial administrator password file {Path} could not be deleted ({Reason}): delete it.", path, ex.Message);
        }
    }

    /// <summary>
    /// Why the file must not be trusted, or null: it is a symbolic link, has more than one hard link, is not a regular file, or its mode
    /// is wider than 0640 (a file anyone could have planted or read). Anyone who can put a password file here becomes the administrator.
    /// </summary>
    internal static string? Unsafe(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return "it is a symbolic link";
        if (OperatingSystem.IsWindows()) return null;
        const UnixFileMode allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        if ((info.UnixFileMode & ~allowed) != 0) return $"its mode {Convert.ToString((int)info.UnixFileMode, 8)} is wider than 640";
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var stat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat")
            {
                ArgumentList = { "-c", "%h", "--", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var links = stat.StandardOutput.ReadToEnd().Trim();
            stat.WaitForExit();
            return stat.ExitCode == 0 && links == "1" ? null : $"its link count could not be confirmed as 1 ({links})";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "its link count could not be checked (" + ex.Message + ")";
        }
    }

    private static async Task<Guid?> FindUserAsync(IDbConnectionFactory database, Guid objectId, CancellationToken ct)
    {
        await using var conn = await database.OpenAsync(ct);
        await using var cmd = conn.Command(database.Dialect.Limit("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", "1"));
        cmd.AddParameter("@oid", DbType.Guid, objectId);
        return await cmd.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }
}
