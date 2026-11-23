using Xrmd.Abstractions.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xrmd.Host;

namespace Xrmd.Integration.Tests;

[CollectionDefinition("serviceclient")]
public class ServiceClientCollection : ICollectionFixture<ServiceClientFixture>;

/// <summary>
/// Its own SQL Server container + the xrmd host on Kestrel at <see cref="BaseUrl"/>: ServiceClient opens its own
/// connections and drops the port of the instance URI, so the host must listen on 127.0.0.1:80.
/// </summary>
public sealed class ServiceClientFixture : IAsyncLifetime
{
    public const string BaseUrl = "http://127.0.0.1/";
    public const string OrgName = "Test Org";
    private const string ListenUrl = "http://127.0.0.1:80";

    private readonly TestDatabase _db = new();
    private readonly RequestLogCollector _requestLog = new();

    public string ConnectionString => _db.ConnectionString;
    public WebApplication App { get; private set; } = null!;
    public Guid AdminObjectId { get; } = Guid.NewGuid();

    /// <summary>Returns and clears the host's request log lines (method, path, query, status) captured so far.</summary>
    public IReadOnlyList<string> DrainRequestLog() => _requestLog.Drain();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.WebHost.UseUrls(ListenUrl);
        builder.Configuration["ConnectionStrings:Org"] = ConnectionString;
        // The engine of the test database (plan 2.7b, Task 3: the acceptance classes of solutions run on PostgreSQL too).
        builder.Configuration[StorageSettings.ProviderKey] = TestProvider.Setting(TestProvider.Current);
        builder.Configuration["Auth:Mode"] = "Development";
        builder.Configuration["Auth:Audience"] = BaseUrl;
        builder.Configuration["Auth:DevSigningKey"] = DevTokenFactory.SigningKey;
        builder.Configuration["Seed:OrgName"] = OrgName;
        builder.Configuration["Seed:AdminObjectId"] = AdminObjectId.ToString();
        builder.Configuration["Seed:AdminName"] = "Test Admin";
        // Plan 4.1b: GreenMail and the fake SMTP server offer no STARTTLS (a test setting; production requires TLS).
        builder.Configuration["Mail:AllowInsecureSmtp"] = "true";
        // Plan 4.1c: GreenMail's IMAP has no STARTTLS either, and the mailboxes sync every 10 seconds (the shortest interval accepted).
        builder.Configuration["Mail:AllowInsecureImap"] = "true";
        builder.Configuration["Mail:IncomingSyncInterval"] = "00:00:10";
        builder.Logging.AddProvider(_requestLog);
        builder.Logging.AddFilter<RequestLogCollector>(null, LogLevel.None);
        builder.Logging.AddFilter<RequestLogCollector>(typeof(RequestLoggingMiddleware).FullName, LogLevel.Debug);
        builder.AddXrmd();

        App = builder.Build();
        await App.UseXrmdAsync();
        try
        {
            await App.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ServiceClient acceptance requires binding {ListenUrl} because ServiceClient drops the port from the instance URI " +
                $"(binding failed: {ex.Message}). Run as root / with CAP_NET_BIND_SERVICE and with port 80 free.", ex);
        }

        await TestPublishers.EnsureAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        await _db.DisposeAsync();
    }
}
