using Microsoft.AspNetCore.Mvc.Testing;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Integration.Tests;

[CollectionDefinition("xrmd")]
public class XrmdCollection : ICollectionFixture<XrmdFixture>;

/// <summary>
/// One database container + one in-memory host (TestServer, composed by Program/XrmdHost) per "xrmd" collection. The engine is the
/// run's (<see cref="TestProvider.Current"/>, <c>XRMD_TEST_PROVIDER</c>; plan 2.7a, Task 5) unless a derived fixture pins one.
/// </summary>
public class XrmdFixture : IAsyncLifetime
{
    private readonly TestDatabase _db;

    public XrmdFixture() : this(TestProvider.Current)
    {
    }

    /// <param name="provider">The engine of the container and of the host's <c>Storage:Provider</c>.</param>
    protected XrmdFixture(StorageProvider provider) => _db = new TestDatabase(provider);

    /// <summary>The engine of this fixture's database and host.</summary>
    public StorageProvider Provider => _db.Provider;

    public string ConnectionString => _db.ConnectionString;
    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid AdminObjectId { get; } = Guid.NewGuid();
    public const string OrgName = "Test Org";
    public const string AdminName = "Test Admin";
    public string BaseUrl { get; private set; } = "";

    /// <summary>
    /// The folder where the Admin API writes backups (<c>Admin:BackupDirectory</c>): the SQL Server container's on SQL Server; on
    /// PostgreSQL, where <c>pg_dump</c> runs on this machine (plan 2.7b, ruling Q7), <see cref="FilesBackupDirectory"/>.
    /// </summary>
    public string BackupDirectory => Provider == StorageProvider.PostgreSql ? FilesBackupDirectory : "/var/opt/mssql/data";

    /// <summary>Where this process sees the files archives of backups (<c>Admin:FilesBackupDirectory</c>): the SQL Server container's directory is not visible here.</summary>
    public string FilesBackupDirectory { get; } = Path.Combine(Path.GetTempPath(), "xrmd-backups-" + Guid.NewGuid().ToString("N"));

    /// <summary>The backup file's extension of the fixture's engine: <c>.bak</c>, or <c>.dump</c> of <c>pg_dump</c>.</summary>
    public string BackupExtension => Provider == StorageProvider.PostgreSql ? ".dump" : ".bak";

    /// <summary>The root of the host's file store (<c>Files:FileSystem:Root</c>), a folder of this fixture.</summary>
    public string FilesRoot { get; } = Path.Combine(Path.GetTempPath(), "xrmd-files-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // The root exists before any test runs, as on an installed server: the doctor's file store check (#72) reports a
        // missing root as a warning, so a test that runs before any file write would otherwise depend on test order.
        Directory.CreateDirectory(FilesRoot);
        await _db.StartAsync();

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(Configure);
        Client = App.CreateClient();
        BaseUrl = Client.BaseAddress!.ToString(); // http://localhost/ — equals Auth:Audience above
        await TestPublishers.EnsureAsync(ConnectionString); // the host has seeded the organization: the prefixes the tests' names carry
    }

    /// <summary>The settings of <see cref="App"/>, which a host of <see cref="WithOwnDatabase"/> starts from.</summary>
    private void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder b)
    {
        b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(Provider));
        b.UseSetting("ConnectionStrings:Org", ConnectionString);
        b.UseSetting("Auth:Mode", "Development");
        b.UseSetting("Auth:Audience", "http://localhost/");
        b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
        b.UseSetting("Seed:OrgName", OrgName);
        b.UseSetting("Seed:AdminObjectId", AdminObjectId.ToString());
        b.UseSetting("Seed:AdminName", AdminName);
        // Tests run async jobs with a worker of their own (AsyncOperationTests): the host's must not race with them.
        b.UseSetting("Pipeline:AsyncWorkerEnabled", "false");
        b.UseSetting("Admin:BackupDirectory", BackupDirectory);
        b.UseSetting("Files:FileSystem:Root", FilesRoot);
        b.UseSetting("Admin:FilesBackupDirectory", FilesBackupDirectory);
        // Plan 2.7b, Task 9: pg_dump and pg_restore of this machine (the image's path does not exist here).
        if (TestProvider.PgToolsPath is { } tools) b.UseSetting(Xrmd.Storage.PostgreSql.PgTools.ConfigurationKey, tools);
    }

    /// <summary>
    /// A second host like <see cref="App"/> with <paramref name="configure"/> applied, serving a new database of its own on the same
    /// SQL Server (created, migrated and seeded at its start, with the same administrator): one xrmd process per organization database
    /// (the instance lock), so a second host can never serve the fixture's database.
    /// </summary>
    /// <remarks>
    /// The database is dropped when the returned host is disposed (unless <paramref name="keepDatabase"/>: a test that serves it
    /// with another process afterwards drops it itself, <see cref="DropDatabaseAsync"/>), so that a long run does not keep every one
    /// of them on the SQL Server container's disk until the end (CI runners have little free disk); the fixture drops any left when
    /// it is disposed.
    /// </remarks>
    public WebApplicationFactory<Program> WithOwnDatabase(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure, bool keepDatabase = false)
    {
        var database = "derived_" + Guid.NewGuid().ToString("N")[..10];
        lock (_derivedDatabases) _derivedDatabases.Add(database);
        return new OwnDatabaseFactory(this, database, configure, keepDatabase);
    }

    /// <summary>A host of <see cref="WithOwnDatabase"/>: the fixture's settings, its own database, dropped when it is disposed (unless kept).</summary>
    private sealed class OwnDatabaseFactory(
        XrmdFixture fixture, string database, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configure, bool keepDatabase)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            fixture.Configure(builder);
            builder.UseSetting("ConnectionStrings:Org", fixture.ConnectionStringOf(database));
            configure(builder);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (!keepDatabase) await fixture.DropDatabaseAsync(database);
        }
    }

    private readonly List<string> _derivedDatabases = [];

    private async Task DropDerivedDatabasesAsync()
    {
        string[] databases;
        lock (_derivedDatabases) databases = [.. _derivedDatabases];
        foreach (var database in databases) await DropDatabaseAsync(database);
    }

    /// <summary>
    /// Drops <paramref name="database"/> on the fixture's server (with its files: the server deletes them), if it exists: a database
    /// of <see cref="WithOwnDatabase"/>, or one a test created for itself, when the test is done with it.
    /// </summary>
    public async Task DropDatabaseAsync(string database)
    {
        await TestProvider.DropDatabaseAsync(ConnectionString, database);
        lock (_derivedDatabases) _derivedDatabases.Remove(database);
    }

    /// <summary>The connection string of <paramref name="database"/> on the fixture's server.</summary>
    public string ConnectionStringOf(string database) => TestProvider.WithDatabase(Provider, ConnectionString, database);

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) await App.DisposeAsync();
        await DropDerivedDatabasesAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(FilesRoot)) Directory.Delete(FilesRoot, recursive: true);
        if (Directory.Exists(FilesBackupDirectory)) Directory.Delete(FilesBackupDirectory, recursive: true);
    }
}
