using System.Data;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Workflows.Actions;
using Xrmd.Workflows.Custom;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// The organization as the workflow compiler sees it on the server (plan 2.21, A11, Task 6): the published metadata
/// (<see cref="IMetadataStore"/>), the records a lookup literal names, retrieved as the caller through the pipeline (a record the
/// caller cannot read is reported as a missing one: fix round 1), the process definitions a child workflow step names, found in a
/// data session and kept when the caller may read them (final review I1), and the solutions and actions read in a data session without
/// any security check (the caller holds <c>prvReadWorkflow</c>, demanded before every compile, and the writes that
/// follow a compile are the caller's requests, checked by the pipeline). Built per apply or export by
/// <see cref="CreateAsync"/>, which reads the registered custom workflow activities once: their arguments are read with the BYO workflow SDK
/// and the plug-in assembly loader of the 2.14 runner (<see cref="CustomActivityRunner"/>).
/// </summary>
/// <remarks>
/// The messages a perform action step may call (ruling R6, carried to Task 6) are those a 2.14 <c>ActionStep</c> executes through the
/// pipeline: a custom API (its request parameters and response properties), a XAML action (a definition of category 3, Draft or
/// Activated, by the message name 2.14 gives it: <see cref="ActionMessages.MessageName"/>), a message of the core (its SDK request and
/// response classes, read by reflection; <c>SetState</c>, which the executor answers as an Update, likewise) and a custom
/// <c>sdkmessage</c> with plug-in steps only (no declared argument).
/// </remarks>
public sealed class AuthoringEnvironment : IAuthoringEnvironment
{
    private const string Target = "Target";
    private const string SetState = "SetState";

    /// <summary>The members of every request and response class, not parameters of a message.</summary>
    private static readonly HashSet<string> BaseMembers = new(StringComparer.Ordinal)
    {
        nameof(OrganizationRequest.RequestName), nameof(OrganizationRequest.RequestId), nameof(OrganizationRequest.Parameters),
        nameof(OrganizationRequest.ExtensionData), nameof(OrganizationResponse.ResponseName), nameof(OrganizationResponse.Results), "Item",
    };

    /// <summary>The assemblies of the SDK request classes: <c>Microsoft.Xrm.Sdk</c> and <c>Microsoft.Crm.Sdk.Proxy</c>.</summary>
    private static readonly Assembly[] SdkAssemblies =
        [typeof(Microsoft.Xrm.Sdk.Messages.CreateRequest).Assembly, typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly];

    private readonly DataSession _s;
    private readonly Caller _caller;
    private readonly PipelineExecutor _executor;
    private readonly IMetadataStore _metadata;
    private readonly IPipelineCore _core;
    private readonly CustomApiRegistry _apis;
    private readonly StepRegistry _steps;
    private readonly IReadOnlyDictionary<(string Type, string Assembly), IReadOnlyList<CalleeArgument>?> _activities;

    private AuthoringEnvironment(DataSession s, Caller caller, PipelineExecutor executor, IMetadataStore metadata, IPipelineCore core,
        CustomApiRegistry apis, StepRegistry steps, IReadOnlyDictionary<(string Type, string Assembly), IReadOnlyList<CalleeArgument>?> activities)
    {
        _s = s;
        _caller = caller;
        _executor = executor;
        _metadata = metadata;
        _core = core;
        _apis = apis;
        _steps = steps;
        _activities = activities;
    }

    /// <summary>
    /// The environment of <paramref name="s"/> for <paramref name="caller"/> (the records a lookup names are retrieved as the caller,
    /// through <paramref name="executor"/>): reads the registered custom workflow activities (<c>plugintype.isworkflowactivity</c>)
    /// and, when the workflow SDK is installed, the arguments of each (an activity the loader refuses has none).
    /// </summary>
    public static async Task<AuthoringEnvironment> CreateAsync(DataSession s, Caller caller, PipelineExecutor executor, IMetadataStore metadata,
        IPipelineCore core, CustomApiRegistry apis, StepRegistry steps, PluginAssemblyLoader loader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(executor);
        var rows = new List<(string Type, string Assembly, Guid Id, long Version)>();
        await using (var cmd = s.Command($"""
                         SELECT t.typename, a.name, a.pluginassemblyid, CAST(a.versionnumber AS bigint)
                         FROM {s.DataTable("plugintype")} t JOIN {s.DataTable("pluginassembly")} a ON a.pluginassemblyid = t.pluginassemblyid
                         WHERE t.isworkflowactivity = {s.Dialect.BooleanLiteral(true)}
                         ORDER BY a.name, t.typename, a.pluginassemblyid
                         """))
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                if (!rd.IsDBNull(0) && !rd.IsDBNull(1))
                    rows.Add((rd.GetString(0), rd.GetString(1), rd.GetGuid(2), rd.IsDBNull(3) ? 0 : rd.GetInt64(3)));
        }

        var activities = new Dictionary<(string, string), IReadOnlyList<CalleeArgument>?>();
        foreach (var (type, assembly, id, version) in rows)
        {
            if (activities.ContainsKey((type, assembly))) continue;
            activities[(type, assembly)] = WorkflowSdk.IsAvailable ? await ActivityArgumentsAsync(loader, steps, id, version, type, ct) : null;
        }
        return new AuthoringEnvironment(s, caller, executor, metadata, core, apis, steps, activities);
    }

    /// <summary>The arguments of a registered activity as the 2.14 runner reads them; null when the loader refuses the type.</summary>
    private static async Task<IReadOnlyList<CalleeArgument>?> ActivityArgumentsAsync(PluginAssemblyLoader loader, StepRegistry steps, Guid id,
        long version, string typeName, CancellationToken ct)
    {
        try
        {
            var type = loader.TryResolveWorkflowActivityType(id, version, null, typeName)
                       ?? loader.TryResolveWorkflowActivityType(id, version, await steps.GetAssemblyContentAsync(id, ct), typeName);
            if (type is null) return null;
            return CustomActivityRunner.Arguments(type).Values
                .Where(a => a.IsInput || a.IsOutput)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => new CalleeArgument(a.Name, a.ValueType.FullName ?? a.ValueType.Name, a.IsInput))
                .ToList();
        }
        catch (XrmdFaultException)
        {
            return null;
        }
    }

    public bool WorkflowSdkAvailable => WorkflowSdk.IsAvailable;

    public EntityMetadata? GetEntity(string logicalName) => _metadata.GetEntity(logicalName);

    /// <summary>
    /// The primary name of the record, retrieved as the caller through the pipeline (Read on the record, and on its primary name column
    /// when it is secured). A record the caller cannot read (403) is null, as a missing one (404): the compile's error is the same
    /// text, so it cannot tell which records exist, and no name the caller cannot read reaches the XAML (fix round 1).
    /// </summary>
    public async Task<string?> PrimaryNameAsync(string entity, Guid id, CancellationToken ct)
    {
        if (_metadata.GetEntity(entity) is not { } table) return null;
        var name = table.PrimaryNameAttribute;
        Entity record;
        try
        {
            var response = await _executor.ExecuteAsync(new RetrieveRequest
            {
                Target = new EntityReference(table.LogicalName, id),
                ColumnSet = name is null ? new ColumnSet(false) : new ColumnSet(name),
            }, new InvocationContext(_caller, _caller.UserId, 1, null, _s, Guid.NewGuid()), ct);
            record = (Entity)response.Results["Entity"];
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus is 403 or 404)
        {
            return null;
        }
        return name is not null && record.Attributes.TryGetValue(name, out var value) && value is string text ? text : "";
    }

    /// <summary>
    /// The definitions of that id, or of exactly that name, that the caller may read: each row found is retrieved as the caller through
    /// the pipeline, and one the caller cannot read is left out, as a missing one (final review I1), so neither its name nor its id,
    /// table or child flag reaches an error or the XAML.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string Name, bool Subprocess, string? Entity)>> FindDefinitionsAsync(string nameOrId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nameOrId);
        var byId = Guid.TryParse(nameOrId, out var id);
        var found = new List<(Guid Id, string Name, bool Subprocess, string? Entity)>();
        await using (var cmd = _s.Command($"""
                         SELECT {_s.Quote(S.WorkflowId)}, {_s.Quote(S.Name)}, {_s.Quote(S.Subprocess)}, {_s.Quote(S.PrimaryEntity)} FROM {_s.DataTable(S.Table)}
                         WHERE {_s.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND {(byId ? $"{_s.Quote(S.WorkflowId)} = @id" : $"{_s.Quote(S.Name)} = @name")}
                         ORDER BY {_s.Quote(S.WorkflowId)}
                         """))
        {
            if (byId) cmd.AddParameter("@id", DbType.Guid, id);
            else cmd.AddParameter("@name", DbType.String, nameOrId, 4000);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var name = rd.IsDBNull(1) ? "" : rd.GetString(1);
                // The column's collation ignores case; a name is matched exactly, as the compiler writes it.
                if (!byId && !string.Equals(name, nameOrId, StringComparison.Ordinal)) continue;
                found.Add((rd.GetGuid(0), name, !rd.IsDBNull(2) && rd.GetBoolean(2), rd.IsDBNull(3) ? null : rd.GetString(3)));
            }
        }
        var readable = new List<(Guid, string, bool, string?)>(found.Count);
        foreach (var definition in found)
            if (await CanReadProcessAsync(_executor, _caller, _s, definition.Id, ct)) readable.Add(definition);
        return readable;
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may read the process <paramref name="id"/>: a Retrieve of it as the caller through the pipeline
    /// (Read access, no column), in <paramref name="s"/>. False for a 403 (and a 404: a process that is gone).
    /// </summary>
    internal static async Task<bool> CanReadProcessAsync(PipelineExecutor executor, Caller caller, DataSession s, Guid id, CancellationToken ct)
    {
        try
        {
            await executor.ExecuteAsync(new RetrieveRequest { Target = new EntityReference(S.Table, id), ColumnSet = new ColumnSet(false) },
                new InvocationContext(caller, caller.UserId, 1, null, s, Guid.NewGuid()), ct);
            return true;
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus is 403 or 404)
        {
            return false;
        }
    }

    /// <summary>
    /// The prefix of the publisher of <paramref name="solution"/> when it names one; else 2.14's (<see cref="ActionMessages.PrefixAsync"/>):
    /// the publisher of the solution that holds the process, else the Default solution's publisher (A15).
    /// </summary>
    public async Task<string> PublisherPrefixAsync(Guid workflowId, string? solution, CancellationToken ct)
    {
        if (solution is not null)
        {
            await using var cmd = _s.Command($"""
                SELECT p.customizationprefix FROM {_s.DataTable("solution")} so JOIN {_s.DataTable("publisher")} p ON p.publisherid = so.publisherid
                WHERE {_s.Dialect.TextKey("so.uniquename")} = {_s.Dialect.TextKey("@name")}
                """);
            cmd.AddParameter("@name", DbType.String, solution, 256);
            if (await cmd.ExecuteScalarAsync(ct) is string { Length: > 0 } prefix) return prefix;
        }
        return await ActionMessages.PrefixAsync(_s, workflowId, ct);
    }

    public async Task<string?> SolutionVersionAsync(string solution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(solution);
        await using var cmd = _s.Command($"SELECT version FROM {_s.DataTable("solution")} WHERE {_s.Dialect.TextKey("uniquename")} = {_s.Dialect.TextKey("@name")}");
        cmd.AddParameter("@name", DbType.String, solution, 256);
        return await cmd.ExecuteScalarAsync(ct) is string version ? version : null;
    }

    public bool IsWorkflowActivity(string typeName, string assemblyName) => _activities.ContainsKey((typeName, SimpleName(assemblyName)));

    public IReadOnlyList<CalleeArgument>? WorkflowActivityArguments(string typeName, string assemblyName) =>
        _activities.GetValueOrDefault((typeName, SimpleName(assemblyName)));

    /// <summary>The simple name of an assembly name (<c>Name, Version=…</c> → <c>Name</c>), as the 2.14 runner matches it.</summary>
    private static string SimpleName(string assemblyName)
    {
        try
        {
            return new AssemblyName(assemblyName).Name ?? assemblyName;
        }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException)
        {
            return assemblyName.Split(',')[0].Trim();
        }
    }

    public async Task<IReadOnlyList<CalleeArgument>?> MessageArgumentsAsync(string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await _apis.FindAsync(message, ct) is { } api)
            return api.RequestParameters.Where(p => p.UniqueName != Target).Select(p => new CalleeArgument(p.UniqueName, ClrOf(p.Type), true))
                .Concat(api.ResponseProperties.Select(p => new CalleeArgument(p.UniqueName, ClrOf(p.Type), false)))
                .ToList();
        if (await ActionArgumentsAsync(message, ct) is { } action) return action;
        // SetState is answered by the executor itself (as an Update), not by the core: its SDK classes still give its parameters.
        if ((_core.IsCoreMessage(message) && !_core.IsNonPipelineMessage(message)) || string.Equals(message, SetState, StringComparison.OrdinalIgnoreCase))
            return CoreMessageArguments(message);
        if (await _steps.IsCustomMessageAsync(message, ct)) return [];
        return null;
    }

    /// <summary>
    /// The arguments of the XAML action whose message is <paramref name="message"/> (ignoring case, as the pipeline matches messages):
    /// the arguments its XAML declares, the bound <c>Target</c> left out; null when no definition of category 3 answers it.
    /// </summary>
    private async Task<IReadOnlyList<CalleeArgument>?> ActionArgumentsAsync(string message, CancellationToken ct)
    {
        var actions = new List<(Guid Id, string UniqueName, string? Xaml)>();
        await using (var cmd = _s.Command($"""
                         SELECT {_s.Quote(S.WorkflowId)}, {_s.Quote(S.UniqueName)}, {_s.Quote(S.Xaml)} FROM {_s.DataTable(S.Table)}
                         WHERE {_s.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND {_s.Quote(S.CategoryColumn)} = {(int)S.Category.Action}
                           AND {_s.Quote(S.UniqueName)} IS NOT NULL
                         ORDER BY {_s.Quote(S.WorkflowId)}
                         """))
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                actions.Add((rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        }
        foreach (var (id, uniqueName, xaml) in actions)
        {
            // Only a uniquename the message ends with can name it: the prefix is read for those alone.
            if (!message.EndsWith(uniqueName, StringComparison.OrdinalIgnoreCase) || uniqueName.Length == 0) continue;
            string name;
            try
            {
                name = ActionMessages.MessageName(uniqueName, await ActionMessages.PrefixAsync(_s, id, ct));
            }
            catch (XrmdFaultException)
            {
                continue;
            }
            if (!string.Equals(name, message, StringComparison.OrdinalIgnoreCase) || xaml is null) continue;
            try
            {
                return XamlWorkflowParser.Parse(xaml, (int)S.Category.Action, WorkflowSdkAvailable).Arguments
                    .Where(a => !a.IsTarget)
                    .Select(a => new CalleeArgument(a.Name, a.ClrType, a.IsInput))
                    .ToList();
            }
            catch (XamlWorkflowException)
            {
                return [];
            }
        }
        return null;
    }

    /// <summary>
    /// The parameters of a core message: the public properties of its SDK request class (<c>{message}Request</c>, the <c>Target</c> left
    /// out) and of its response class, each with the full name of its type; none when the SDK has no class of that name.
    /// </summary>
    private static IReadOnlyList<CalleeArgument> CoreMessageArguments(string message)
    {
        Type? Find(string suffix) => SdkAssemblies
            .SelectMany(a => new[] { $"Microsoft.Xrm.Sdk.Messages.{message}{suffix}", $"Microsoft.Crm.Sdk.Messages.{message}{suffix}" }
                .Select(n => a.GetType(n, throwOnError: false, ignoreCase: true)))
            .FirstOrDefault(t => t is not null);

        static IEnumerable<CalleeArgument> Of(Type? type, bool input) => type is null
            ? []
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => !BaseMembers.Contains(p.Name) && p.Name != Target && p.GetIndexParameters().Length == 0 && (!input || p.CanWrite))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new CalleeArgument(p.Name, p.PropertyType.FullName ?? p.PropertyType.Name, input));

        return Of(Find("Request"), true).Concat(Of(Find("Response"), false)).ToList();
    }

    /// <summary>The CLR type of a custom API parameter's type (the inverse of <see cref="ActionMessages.TypeOf"/>).</summary>
    private static string ClrOf(CustomApiType type) => type switch
    {
        CustomApiType.Boolean => "System.Boolean",
        CustomApiType.DateTime => "System.DateTime",
        CustomApiType.Decimal => "System.Decimal",
        CustomApiType.Entity => "Microsoft.Xrm.Sdk.Entity",
        CustomApiType.EntityCollection => "Microsoft.Xrm.Sdk.EntityCollection",
        CustomApiType.EntityReference => "Microsoft.Xrm.Sdk.EntityReference",
        CustomApiType.Float => "System.Double",
        CustomApiType.Integer => "System.Int32",
        CustomApiType.Money => "Microsoft.Xrm.Sdk.Money",
        CustomApiType.Picklist => "Microsoft.Xrm.Sdk.OptionSetValue",
        CustomApiType.String => "System.String",
        CustomApiType.StringArray => "System.String[]",
        CustomApiType.Guid => "System.Guid",
        _ => "System.Object",
    };
}
