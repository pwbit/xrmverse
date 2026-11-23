using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;

namespace Xrmd.Admin.Migration;

/// <summary>
/// The keys of an uploaded migration package in the file store (migration plan M3, R5), under the organization database's folder as
/// every other blob of the organization: <c>{database}/migrate/packages/{id}/package.xrmpkg</c> and its upload chunks.
/// </summary>
public static class MigrationPackageKeys
{
    public static string Folder(string database, Guid packageId) => $"{database}/migrate/packages/{packageId:N}/";

    public static string Package(string database, Guid packageId) => Folder(database, packageId) + "package.xrmpkg";

    public static string Chunk(string database, Guid packageId, int n) =>
        Folder(database, packageId) + "chunks/" + n.ToString("D5", CultureInfo.InvariantCulture);
}

/// <summary>
/// The chunked upload of a migration package (migration spec §7.1, plan M3 task 3), under the Admin API (System Administrator only):
/// create, put chunks (at most <see cref="MaxChunkBytes"/>, a resend replaces), read the status, complete (concatenate and check the
/// SHA-256), delete.
/// </summary>
public static class MigrationPackageEndpoints
{
    /// <summary>8 MiB.</summary>
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    /// <summary>The highest chunk number.</summary>
    public const int MaxChunkNumber = 99999;

    /// <summary>Lets a chunk through Kestrel's default limit, so that the handler answers 413 itself past <see cref="MaxChunkBytes"/>.</summary>
    private sealed class ChunkSizeLimit : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => MaxChunkBytes + 1;
    }

    public static RouteGroupBuilder MapMigrationPackageEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapPost("/migrate/packages", (Func<HttpContext, Task<IResult>>)CreateAsync);
        admin.MapPut("/migrate/packages/{id}/chunks/{n}", (Func<HttpContext, Task<IResult>>)PutChunkAsync).WithMetadata(new ChunkSizeLimit());
        admin.MapGet("/migrate/packages/{id}", (Func<HttpContext, Task<IResult>>)GetAsync);
        admin.MapPost("/migrate/packages/{id}/complete", (Func<HttpContext, Task<IResult>>)CompleteAsync);
        admin.MapDelete("/migrate/packages/{id}", (Func<HttpContext, Task<IResult>>)DeleteAsync);
        return admin;
    }

    /// <summary>
    /// Refuses (400 <c>FeatureNotAvailable</c>) a migration from Dataverse on an SDK-free server (plan S.2a): a package's records are
    /// read with the Microsoft SDK. Called before anything is written: at the creation of a package and at the start of a run.
    /// </summary>
    internal static void DemandNotSdkFree(IServiceProvider services)
    {
        if (services.GetService<SdkModeInfo>()?.IsSdkFree == true)
            throw new XrmdFaultException(ErrorCodes.FeatureNotAvailable, SdkFreeMessages.Migration, StatusCodes.Status400BadRequest);
    }

    internal static XrmdFaultException BadRequest(string message) => XrmdFaultException.InvalidArgument(message);

    internal static XrmdFaultException Conflict(string message) => new(ErrorCodes.InvalidArgument, message, StatusCodes.Status409Conflict);

    internal static XrmdFaultException NotFound(string message) => new(ErrorCodes.ObjectDoesNotExist, message, StatusCodes.Status404NotFound);

    /// <summary>The organization database's name: the folder of its blobs.</summary>
    internal static Task<string> DatabaseAsync(HttpContext context) => DatabaseAsync(context.RequestServices, context.RequestAborted);

    /// <summary>The organization database's name: the folder of its blobs.</summary>
    internal static async Task<string> DatabaseAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        return conn.Database;
    }

    internal static Guid RouteId(HttpContext context) =>
        Guid.TryParse(context.Request.RouteValues["id"] as string, out var id) ? id : throw BadRequest("The id is not a GUID.");

    /// <summary>The package <paramref name="id"/>; 404 when there is none.</summary>
    internal static Task<MigrationPackageRow> PackageAsync(HttpContext context, Guid id) => PackageAsync(context.RequestServices, id, context.RequestAborted);

    /// <summary>The package <paramref name="id"/>; 404 when there is none.</summary>
    internal static async Task<MigrationPackageRow> PackageAsync(IServiceProvider services, Guid id, CancellationToken ct) =>
        await services.GetRequiredService<MigrationRunStore>().GetPackageAsync(id, ct)
        ?? throw NotFound($"No migration package with id {id}.");

    private static async Task<IResult> CreateAsync(HttpContext context)
    {
        DemandNotSdkFree(context.RequestServices);
        var caller = await context.RequestServices.GetRequiredService<CallerResolver>().ResolveAsync(context.RequestAborted);
        var id = await context.RequestServices.GetRequiredService<MigrationRunStore>().CreatePackageAsync(caller.UserId, context.RequestAborted);
        return Results.Json(new JsonObject { ["packageId"] = id.ToString() }, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PutChunkAsync(HttpContext context)
    {
        var id = RouteId(context);
        if (!int.TryParse(context.Request.RouteValues["n"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > MaxChunkNumber)
            throw BadRequest($"The chunk number must be between 0 and {MaxChunkNumber}.");
        var package = await PackageAsync(context, id);
        if (package.State != MigrationPackageStates.Uploading) throw Conflict($"The migration package {id} is complete: it takes no more chunks.");

        // Read up to one byte more than the limit: one more is the 413 (the limit holds whatever the server in front lets through).
        var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            body.Write(buffer, 0, read);
            if (body.Length > MaxChunkBytes)
                throw new XrmdFaultException(ErrorCodes.InvalidArgument, $"A chunk is at most {MaxChunkBytes} bytes.", StatusCodes.Status413PayloadTooLarge);
        }
        body.Position = 0;
        var files = context.RequestServices.GetRequiredService<IFileStore>();
        await files.WriteAsync(MigrationPackageKeys.Chunk(await DatabaseAsync(context), id, n), body, context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> GetAsync(HttpContext context)
    {
        var id = RouteId(context);
        var package = await PackageAsync(context, id);
        var chunks = new JsonArray();
        if (package.State == MigrationPackageStates.Uploading)
            foreach (var n in await ChunkNumbersAsync(context, id)) chunks.Add(n);
        return Results.Json(new JsonObject
        {
            ["packageId"] = id.ToString(), ["state"] = package.State, ["chunks"] = chunks, ["bytes"] = package.Bytes,
            ["sha256"] = package.Sha256,
        });
    }

    /// <summary>The numbers of the chunks uploaded so far, ascending.</summary>
    private static async Task<List<int>> ChunkNumbersAsync(HttpContext context, Guid id)
    {
        var files = context.RequestServices.GetRequiredService<IFileStore>();
        var prefix = MigrationPackageKeys.Folder(await DatabaseAsync(context), id) + "chunks/";
        var numbers = new List<int>();
        await foreach (var key in files.ListAsync(prefix, context.RequestAborted))
            if (int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) numbers.Add(n);
        numbers.Sort();
        return numbers;
    }

    private static async Task<IResult> CompleteAsync(HttpContext context)
    {
        var id = RouteId(context);
        var package = await PackageAsync(context, id);
        if (package.State != MigrationPackageStates.Uploading) throw Conflict($"The migration package {id} is already complete.");
        var body = await ReadBodyAsync(context);
        var count = body["chunks"] is JsonValue c && c.TryGetValue<int>(out var chunks) && chunks is > 0 and <= MaxChunkNumber + 1
            ? chunks : throw BadRequest("'chunks' must be the number of chunks (1 or more).");
        var expected = body["sha256"] is JsonValue h && h.TryGetValue<string>(out var hex) && hex.Length == 64 && hex.All(Uri.IsHexDigit)
            ? hex.ToLowerInvariant() : throw BadRequest("'sha256' must be the package's SHA-256 in hexadecimal (64 digits).");

        var present = (await ChunkNumbersAsync(context, id)).ToHashSet();
        var missing = Enumerable.Range(0, count).Where(n => !present.Contains(n)).ToList();
        if (missing.Count > 0) throw BadRequest($"Missing chunks: {string.Join(", ", missing.Take(50))}.");

        var database = await DatabaseAsync(context);
        var files = context.RequestServices.GetRequiredService<IFileStore>();
        var target = MigrationPackageKeys.Package(database, id);
        await files.ConcatAsync(Enumerable.Range(0, count).Select(n => MigrationPackageKeys.Chunk(database, id, n)).ToList(), target, context.RequestAborted);
        string actual;
        long bytes;
        await using (var stored = await files.OpenReadAsync(target, 0, null, context.RequestAborted))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            bytes = 0;
            int read;
            while ((read = await stored.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                bytes += read;
            }
            actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        if (actual != expected)
        {
            await files.DeleteAsync(target, context.RequestAborted);
            throw BadRequest("The package checksum does not match.");
        }
        await context.RequestServices.GetRequiredService<MigrationRunStore>().CompletePackageAsync(id, bytes, actual, context.RequestAborted);
        await files.DeletePrefixAsync(MigrationPackageKeys.Folder(database, id) + "chunks/", context.RequestAborted);
        return Results.Json(new JsonObject { ["packageId"] = id.ToString(), ["bytes"] = bytes, ["sha256"] = actual });
    }

    private static async Task<IResult> DeleteAsync(HttpContext context)
    {
        var id = RouteId(context);
        await PackageAsync(context, id);
        var store = context.RequestServices.GetRequiredService<MigrationRunStore>();
        if (await store.GetOpenRunAsync(context.RequestAborted) is { PackageId: { } used } open && used == id)
            throw Conflict($"The migration package {id} is used by the run {open.RunId}, which is not finalized.");
        await DeletePackageAsync(context.RequestServices, await DatabaseAsync(context), id, context.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>Deletes the package's blobs (package and chunks) and its row.</summary>
    internal static async Task DeletePackageAsync(IServiceProvider services, string database, Guid id, CancellationToken ct)
    {
        await services.GetRequiredService<IFileStore>().DeletePrefixAsync(MigrationPackageKeys.Folder(database, id), ct);
        await services.GetRequiredService<MigrationRunStore>().DeletePackageAsync(id, ct);
    }

    /// <summary>The JSON object of the request body; an empty body is an empty object, anything else is a 400.</summary>
    internal static async Task<JsonObject> ReadBodyAsync(HttpContext context)
    {
        // No body at all (a POST with nothing to say) is an empty object; a body without a JSON content type is a 400 below.
        if (context.Request.ContentType is null && context.Request.ContentLength is null or 0) return [];
        try
        {
            return await context.Request.ReadFromJsonAsync<JsonObject>(context.RequestAborted) ?? [];
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw BadRequest("The request body must be a JSON object: " + ex.Message);
        }
        catch (InvalidOperationException)
        {
            throw BadRequest("The request body must be application/json.");
        }
    }
}
