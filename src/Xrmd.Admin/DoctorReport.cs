using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Files;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Workflows.Custom;

namespace Xrmd.Admin;

/// <summary>One line of the doctor report; <c>Status</c> is <c>ok</c>, <c>warning</c> or <c>error</c>.</summary>
public sealed record DoctorCheck(string Name, string Status, string Detail);

/// <summary>
/// The checks of <c>GET /admin/doctor</c>: database, migrations and auth (from <see cref="HealthReport"/>), the async worker,
/// the Microsoft SDK, the optional workflow SDK and, for each registered plug-in assembly, whether every assembly it references can be resolved.
/// </summary>
public sealed class DoctorReport(HealthReport health, IServiceProvider services, IOptions<PipelineOptions> pipeline, IFileStore fileStore, FileStoreOptions fileOptions)
{
    /// <summary>How long the file store may take to answer before the check is an error.</summary>
    public static readonly TimeSpan DefaultFileStoreTimeout = TimeSpan.FromSeconds(10);

    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Error = "error";

    /// <summary>The time the file store check may take; settable so a test need not wait the default.</summary>
    public TimeSpan FileStoreTimeout { get; set; } = DefaultFileStoreTimeout;

    public async Task<IReadOnlyList<DoctorCheck>> GetAsync(CancellationToken ct)
    {
        var status = await health.GetAsync(ct);
        var checks = new List<DoctorCheck>();
        var reachable = status.Status == HealthReport.Healthy;
        checks.Add(reachable
            ? new("database", Ok, $"Connected to '{status.Database}' (organization '{status.Organization}').")
            : new("database", Error, $"The database '{status.Database}' is unreachable or not set up."));
        checks.Add(!reachable
            ? new("migrations", Error, "Cannot read the schema version: the database is unavailable.")
            : status.PendingMigrations == 0
                ? new("migrations", Ok, $"Schema version {status.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, no pending migrations.")
                : new("migrations", Warning, $"Schema version {status.SchemaVersion.ToString(CultureInfo.InvariantCulture)}, {status.PendingMigrations.ToString(CultureInfo.InvariantCulture)} pending migration(s)."));
        checks.Add(status.AuthMode switch
        {
            "Entra" => new("auth", Ok, "Auth:Mode is Entra."),
            "Development" => new("auth", Warning, "Auth:Mode is Development: locally signed tokens are accepted; do not use it in production."),
            var other => new("auth", Error, $"Auth:Mode is '{other}': no token validation is configured."),
        });
        // Plan 2.7b, ruling Q3 (#299): a 2.7a PostgreSQL organization's key whose rows must be fixed before it compares text keys.
        foreach (var conflict in status.TextKeyConflicts)
            checks.Add(new($"textKey:{conflict.Table}.{conflict.Key}", Warning, conflict.Message));
        checks.Add(pipeline.Value.AsyncWorkerEnabled
            ? new("asyncWorker", Ok, "The asynchronous job worker is enabled.")
            : new("asyncWorker", Warning, "Pipeline:AsyncWorkerEnabled is false: asynchronous steps are queued but never run."));
        checks.Add(SdkCheck());
        checks.Add(WorkflowSdkCheck());
        checks.Add(await FileStoreCheckAsync(ct));
        if (reachable) checks.AddRange(await PluginAssembliesAsync(ct));
        return checks;
    }

    /// <summary>The configured file store (<c>Files:Store</c>, local directory by default): reachable, without writing a blob.</summary>
    private async Task<DoctorCheck> FileStoreCheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FileStoreTimeout);
        try
        {
            var result = await fileStore.CheckAsync(timeout.Token).WaitAsync(timeout.Token);
            return new("fileStore", result.Status switch
            {
                FileStoreStatus.Ok => Ok,
                FileStoreStatus.Warning => Warning,
                _ => Error,
            }, result.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new("fileStore", Error, $"The {fileOptions.Store} file store did not answer within {FileStoreTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} seconds.");
        }
        catch (Exception ex)
        {
            // The exception type only: the message of a storage SDK exception can quote a request URL or a credential.
            return new("fileStore", Error, $"The {fileOptions.Store} file store check failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>The Microsoft SDK the host loaded: ok with its version and folder, error when it is not the pinned one.</summary>
    private static DoctorCheck SdkCheck()
    {
#pragma warning disable IL3000 // the host is never a single-file app
        var location = HealthReport.SdkAssembly.Location;
#pragma warning restore IL3000
        var folder = string.IsNullOrEmpty(location) ? "(unknown folder)" : Path.GetDirectoryName(location);
        return HealthReport.SdkVersion == SdkPackage.XrmSdkFileVersion
            ? new("sdk", Ok, $"Microsoft.Xrm.Sdk {HealthReport.SdkVersion} ({SdkPackage.Id} {SdkPackage.Version}) from {folder}.")
            : new("sdk", Error, $"Microsoft.Xrm.Sdk {HealthReport.SdkVersion} from {folder} is not the one of {SdkPackage.Id} {SdkPackage.Version} ({SdkPackage.XrmSdkFileVersion}). Run 'xrmd sdk install'.");
    }

    /// <summary>
    /// The optional workflow SDK (plan 2.14, W18): ok with its version and folder, a warning when the host runs without it
    /// (custom workflow activity steps are then unsupported steps) or with one that is not the pinned package.
    /// </summary>
    private static DoctorCheck WorkflowSdkCheck()
    {
        if (WorkflowSdk.Assembly is { } assembly)
        {
#pragma warning disable IL3000 // the host is never a single-file app
            var location = assembly.Location;
#pragma warning restore IL3000
            var from = string.IsNullOrEmpty(location) ? "(unknown folder)" : Path.GetDirectoryName(location);
            if (WorkflowSdk.Mismatch is { } mismatch)
                return new("sdk-workflow", Warning,
                    $"The workflow SDK from {from} is {mismatch}, not {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version} " +
                    $"({SdkPackage.Workflow.AssemblyName} {SdkPackage.Workflow.FileVersion}). Run 'xrmd sdk install' to reinstall it.");
            return new("sdk-workflow", Ok, $"{SdkPackage.Workflow.AssemblyName} {WorkflowSdk.Version} ({SdkPackage.Workflow.Id}) from {from}.");
        }

        var folder = SdkAssemblyResolver.Folder ?? SdkFolder.Resolve(null);
        return new("sdk-workflow", Warning,
            $"{SdkPackage.Workflow.AssemblyFileName} is not installed in {folder}: custom workflow activities cannot run. " +
            $"Run 'xrmd sdk install' to install {SdkPackage.Workflow.Id} {SdkPackage.Workflow.Version} with the SDK.");
    }

    private async Task<List<DoctorCheck>> PluginAssembliesAsync(CancellationToken ct)
    {
        var assemblies = new List<(Guid Id, string Name)>();
        try
        {
            await using (var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct))
            await using (var cmd = conn.Command("SELECT pluginassemblyid, name FROM data.pluginassembly ORDER BY name, pluginassemblyid"))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) assemblies.Add((rd.GetGuid(0), rd.GetString(1)));
        }
        catch (DbException ex)
        {
            return [new("pluginAssemblies", Error, "Cannot list the plug-in assemblies: " + ex.Message)];
        }

        var registry = services.GetRequiredService<StepRegistry>();
        var checks = new List<DoctorCheck>();
        // Issue #70: a name is unique since registrations refuse a duplicate; rows written before that may still share one.
        var duplicates = assemblies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' ({string.Join(", ", g.Select(a => a.Id))})").ToList();
        if (duplicates.Count > 0)
            checks.Add(new("pluginAssemblies", Warning,
                "Plug-in assemblies share a name; if they are the same assembly identity, their builds replace each other's loaded copy on every execution. Keep one row per name: "
                + string.Join("; ", duplicates)));
        foreach (var (id, name) in assemblies)
        {
            var check = "pluginAssembly:" + name;
            try
            {
                var missing = PluginAssemblyLoader.MissingReferences(await registry.GetAssemblyContentAsync(id, ct));
                checks.Add(missing.Count == 0
                    ? new(check, Ok, "")
                    : new(check, Error, string.Join(", ", missing)));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ArgumentException or InvalidOperationException or FormatException)
            {
                checks.Add(new(check, Error, "The assembly cannot be loaded: " + ex.Message));
            }
        }

        return checks;
    }
}
