using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// The table of the parity tests of plan 2.7a, Task 6 (<see cref="CaseInsensitivityParityTests"/>, <see cref="ConcurrencyParityTests"/>):
/// <c>pb_paritykey</c> (organization-owned, primary name <c>pb_name</c>) with a unique index on the text key of <c>pb_name</c>
/// (<see cref="DdlGenerator.KeyColumns"/>, plan 2.7b), the alternate key of the
/// plan. xrmverse defines no alternate keys through its metadata API (standard tables have none), so the key is the unique index the
/// engine enforces, created here through the run's dialect: the write path answers its violation like Dataverse answers a duplicate
/// alternate key value (<c>0x80040237</c>). A table of its own, not <c>account.name</c>: alternate-key metadata exists in xrmverse
/// only for elastic tables (P10, ported in 2.7b), and <c>account</c> is shared by other classes of the collection, whose rows
/// (another <c>Acme</c>) a unique index on <c>account.name</c> would refuse.
/// </summary>
internal static class ParityTestKit
{
    public const string KeyTable = "pb_paritykey", KeyName = "pb_name";
    private const string Api = "/api/data/v9.2/";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>Creates <see cref="KeyTable"/> and its unique index on <see cref="KeyName"/> unless they exist.</summary>
    public static async Task EnsureKeyTableAsync(XrmdFixture fx)
    {
        await Lock.WaitAsync();
        try
        {
            var store = fx.App.Services.GetRequiredService<IMetadataStore>();
            if (store.GetEntity(KeyTable) is null)
            {
                var entity = new EntityMetadata
                {
                    LogicalName = KeyTable, SchemaName = "pb_ParityKey", OwnershipType = OwnershipTypes.OrganizationOwned,
                };
                entity.SetAttributes([]);
                await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = KeyName, SchemaName = "pb_Name", MaxLength = 100 });
            }

            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            var d = SqlDialects.For(conn);
            // Plan 2.7b, ruling Q1: an alternate key on a text column is an index on its text key (the column on SQL Server).
            await using var cmd = conn.Command(d.CreateIndexIfMissing("data", KeyTable, "UX_pb_paritykey_name",
                DdlGenerator.KeyColumns(d, [(KeyName, true)]), unique: true));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The entity set name of <see cref="KeyTable"/>.</summary>
    public static string SetOf(XrmdFixture fx) =>
        fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(KeyTable)!.EntitySetName!;

    /// <summary>A Web API request as the fixture's administrator.</summary>
    public static Task<HttpResponseMessage> SendAsync(XrmdFixture fx, HttpMethod method, string path, string? json = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        configure?.Invoke(req);
        return fx.Client.SendAsync(req);
    }
}
