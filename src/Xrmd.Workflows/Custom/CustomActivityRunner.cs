using System.Activities;
using System.Data;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Data.Common;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Pipeline.Tracing;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Custom;

/// <summary>
/// Runs the custom workflow activity of a <see cref="CustomActivityStep"/> (plan 2.14, W17) through CoreWF: the activity type of the
/// registered <c>plugintype</c> (<c>isworkflowactivity</c>) is loaded with the plug-in assembly loader (the same process-wide collectible
/// copy as the plug-ins, <c>System.Activities</c> and the workflow SDK bound to the host's), its input arguments are set, and
/// <see cref="WorkflowInvoker"/> runs it with three extensions: the <c>IWorkflowContext</c> (<see cref="WorkflowContextProxy"/>), an
/// <see cref="IOrganizationServiceFactory"/> acting as the run's user (<see cref="WorkflowActivityServices"/>) and an
/// <see cref="ITracingService"/>.
/// </summary>
/// <remarks>
/// <para>The arguments are read by attribute full name (the workflow SDK is the customer's, never named at compile time):
/// an input is a public <c>InArgument&lt;T&gt;</c> property with <c>[Input]</c>, an output a public <c>OutArgument&lt;T&gt;</c> property
/// with <c>[Output]</c>; the step names them by property name, as the designer's XAML does. Nothing is converted (Task 8b rulings): an
/// input value must already be a <c>T</c>, an empty value is accepted only for a type that can be empty, an <c>EntityReference</c> must
/// be of the <c>[ReferenceTarget]</c> table; a required argument (<c>[RequiredArgument]</c>, with or without <c>[Input]</c>) the step
/// does not set, an input with a <c>[Default]</c> the step does not set, and an argument the activity does not have fail the step with
/// a 400 <see cref="ErrorCodes.InvalidArgument"/> naming it.</para>
/// <para>The activity runs as a plug-in does: at most <see cref="PipelineOptions.PluginTimeout"/> (then a 500 time-out, the calls of
/// its organization service revoked and the call in flight waited for), its exceptions mapped by <see cref="PluginRunner.Map"/>
/// (<c>InvalidPluginExecutionException</c> a 400 with its message, anything else an unexpected 500, logged with its detail and trace),
/// its trace written to <c>plugintracelog</c> (operation type 2) per the organization's trace setting. A <c>StopWorkflowException</c>
/// stops the workflow (<see cref="Stopped"/>).</para>
/// <para>Known limits: a new instance runs each time (Dataverse caches instances, which a stateless activity cannot tell); .NET
/// Framework-only APIs fail as in a plug-in.</para>
/// </remarks>
public sealed class CustomActivityRunner(PluginAssemblyLoader loader, StepRegistry registry, IDbConnectionFactory factory, IPipelineCore core,
    IOptions<PipelineOptions> options, PluginTraceWriter traces, ILogger<CustomActivityRunner> logger)
{
    private const string StopWorkflowException = "Microsoft.Xrm.Sdk.Workflow.StopWorkflowException";
    private const int TraceException = 1;
    private const int TraceAll = 2;
    private static readonly TimeSpan RevocationDrainTimeout = TimeSpan.FromSeconds(30);

    private const string InputAttribute = "Microsoft.Xrm.Sdk.Workflow.InputAttribute";
    private const string OutputAttribute = "Microsoft.Xrm.Sdk.Workflow.OutputAttribute";
    private const string ReferenceTargetAttribute = "Microsoft.Xrm.Sdk.Workflow.ReferenceTargetAttribute";
    private const string DefaultAttribute = "Microsoft.Xrm.Sdk.Workflow.DefaultAttribute";

    /// <summary>
    /// Runs <paramref name="step"/> with <paramref name="inputs"/> (its input values, already evaluated, by argument name) on
    /// <paramref name="run"/>; its requests run through <paramref name="executor"/> (the engine's: never resolved from the container).
    /// </summary>
    /// <returns>The values of the step's output arguments, by argument name, or how a <c>StopWorkflowException</c> ended the run.</returns>
    /// <exception cref="XrmdFaultException">400 for an argument the step cannot pass faithfully, an unregistered activity or a missing
    /// workflow SDK; the activity's own fault (400 abort, 500 unexpected or timed out).</exception>
    public async Task<CustomActivityResult> RunAsync(CustomActivityStep step, IReadOnlyDictionary<string, object?> inputs,
        WorkflowRun run, PipelineExecutor executor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executor);

        var contextInterface = WorkflowSdk.Assembly?.GetType(WorkflowContextProxy.InterfaceName, throwOnError: false)
                               ?? throw Fault(run, step, XamlWorkflowParser.WorkflowSdkMissingReason);
        var type = await ActivityTypeAsync(step, run, ct);
        var arguments = Arguments(type);
        var invokerInputs = Inputs(step, inputs, arguments, run, type);
        foreach (var output in step.Outputs.Keys)
            if (!arguments.TryGetValue(output, out var argument) || !argument.IsOutput)
                throw Fault(run, step, $"the custom workflow activity '{type.FullName}' has no output argument '{output}'.");

        var organization = await WithSessionAsync(run, (s, c) => core.OrganizationAsync(s, c), ct);
        // Read before the activity runs (a failing activity may doom the transaction), as for a plug-in step.
        var setting = await WithSessionAsync(run, (s, c) => core.PluginTraceLogSettingAsync(s, c), ct);
        var typeName = type.FullName ?? type.Name;
        var tracing = new TracingService();
        // The activity's organization service works only while the step runs: revoked when the activity returns, times out (its
        // thread may live on) or the request is cancelled, and the call in flight is waited for before the runner returns.
        using var revocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var services = new WorkflowActivityServices(executor, core, factory, run, revocation.Token);

        var start = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();
        PluginExecutionException? failure = null;
        CustomActivityResult? result = null;
        try
        {
            var activity = Create(type, step, run);
            var invoker = new WorkflowInvoker(activity);
            invoker.Extensions.Add(WorkflowContextProxy.Create(contextInterface, run, organization));
            invoker.Extensions.Add(services);
            invoker.Extensions.Add(tracing);
            result = await InvokeAsync(invoker, invokerInputs, typeName, step, ct);
        }
        catch (PluginExecutionException ex)
        {
            failure = ex;
        }
        finally
        {
            await revocation.CancelAsync();
            if (!await services.RevokeAsync(RevocationDrainTimeout))
                logger.LogWarning("A call of the custom workflow activity {Type} (workflow {Workflow}) was still running {Seconds} s after it ended.",
                    typeName, run.Workflow.Name, RevocationDrainTimeout.TotalSeconds);
        }

        watch.Stop();
        if (failure is { Unexpected: true })
            logger.LogError("The custom workflow activity {Type} (workflow {Workflow}, step {Step}) failed: {Detail}{NewLine}Trace:{NewLine}{Trace}",
                typeName, run.Workflow.Name, step.Label, failure.Detail ?? failure.Message, Environment.NewLine, Environment.NewLine, tracing.Text);
        if (setting == TraceAll || (failure is not null && (setting == TraceException || failure.Unexpected)))
        {
            try
            {
                await traces.WriteAsync(typeName, PluginTraceWriter.WorkflowActivityOperation, null, null,
                    WorkflowContextProxy.Values(run, organization), tracing.Text, failure is null ? null : failure.Detail ?? failure.Message,
                    start, (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), ct);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                logger.LogError(ex, "Could not write the trace of the custom workflow activity {Type} (workflow {Workflow}).", typeName, run.Workflow.Name);
            }
        }

        if (failure is not null) throw failure.Fault;
        return result!;
    }

    /// <summary>
    /// A new instance of the activity (M2): its public parameterless constructor, as CoreWF needs; none is a 400, and a constructor that
    /// throws is mapped as a plug-in's (<see cref="PluginRunner.Map"/>).
    /// </summary>
    private static Activity Create(Type type, CustomActivityStep step, WorkflowRun run)
    {
        var constructor = type.GetConstructor(Type.EmptyTypes)
                          ?? throw new PluginExecutionException(Fault(run, step, $"Type '{type.FullName}' has no supported constructor."), false);
        try
        {
            return (Activity)constructor.Invoke(null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw PluginRunner.Map(ex.InnerException, type.FullName ?? type.Name);
        }
    }

    /// <summary>
    /// Runs the activity on a pool thread, at most <see cref="PipelineOptions.PluginTimeout"/> as a plug-in
    /// (<see cref="PluginRunner.RunAsync"/>): CoreWF's own timeout cannot interrupt <c>CodeActivity.Execute</c>.
    /// </summary>
    /// <exception cref="PluginExecutionException">The activity failed or timed out.</exception>
    private async Task<CustomActivityResult> InvokeAsync(WorkflowInvoker invoker, Dictionary<string, object> inputs, string typeName,
        CustomActivityStep step, CancellationToken ct)
    {
        var work = Task.Run(() => invoker.Invoke(inputs), CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(options.Value.PluginTimeout, timeoutCts.Token);
        if (await Task.WhenAny(work, delay) != work)
        {
            // The activity's thread cannot be aborted; make sure a late failure is observed.
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ct.ThrowIfCancellationRequested();
            throw new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                "The plug-in execution failed because the operation has timed-out at the Sandbox Host.", 500), true);
        }

        await timeoutCts.CancelAsync();
        try
        {
            var results = await work;
            return new CustomActivityResult(
                step.Outputs.Keys.ToDictionary(o => o, o => results.TryGetValue(o, out var value) ? value : null, StringComparer.Ordinal), null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var thrown = Unwrap(ex);
            if (Stopped(thrown) is { } stop) return new CustomActivityResult(new Dictionary<string, object?>(), stop);
            throw PluginRunner.Map(thrown, typeName);
        }
    }

    /// <summary>
    /// The outcome of a <c>StopWorkflowException</c> (I1), recognised by full name, its <c>Status</c> read by name: <c>Succeeded</c> ends
    /// the run as succeeded and <c>Canceled</c> as canceled with the exception's message, as a stop step does (W11); <c>Failed</c> fails
    /// the step as an abort (400 ISVAborted with the message, a background job 3/31); <c>Retry</c> and <c>Suspended</c> fail explicitly.
    /// Null for any other exception.
    /// </summary>
    /// <exception cref="PluginExecutionException">A <c>Failed</c>, <c>Retry</c> or <c>Suspended</c> status.</exception>
    internal static WorkflowOutcome? Stopped(Exception ex)
    {
        var type = ex.GetType();
        while (type is not null && type.FullName != StopWorkflowException) type = type.BaseType;
        if (type is null) return null;
        var status = type.GetProperty("Status")?.GetValue(ex)?.ToString();
        return status switch
        {
            "Succeeded" => new WorkflowOutcome.Completed(),
            "Canceled" => new WorkflowOutcome.Canceled(ex.Message),
            "Failed" => throw new PluginExecutionException(PluginRunner.Aborted(ex.Message), false, ex.ToString()),
            _ => throw new PluginExecutionException(XrmdFaultException.InvalidArgument(
                $"A custom workflow activity stopped the workflow with the status '{status}' (StopWorkflowException), which xrmverse does not support: " +
                "only Succeeded, Canceled and Failed are."), false, ex.ToString()),
        };
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException or AggregateException && ex.InnerException is not null) ex = ex.InnerException!;
        return ex;
    }

    /// <summary>One argument of the activity: its <c>Argument&lt;T&gt;</c>'s <c>T</c> and what its attributes say.</summary>
    internal sealed record ActivityArgument(string Name, Type ValueType, bool IsInput, bool IsOutput, bool Required, bool HasDefault, string? ReferenceTarget);

    internal static Dictionary<string, ActivityArgument> Arguments(Type type)
    {
        var arguments = new Dictionary<string, ActivityArgument>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = property.PropertyType;
            if (!t.IsGenericType) continue;
            var definition = t.GetGenericTypeDefinition();
            var isIn = definition == typeof(InArgument<>);
            var isOut = definition == typeof(OutArgument<>);
            if (!isIn && !isOut) continue;
            var attributes = property.GetCustomAttributesData();
            bool Has(string name) => attributes.Any(a => a.AttributeType.FullName == name);
            var target = attributes.FirstOrDefault(a => a.AttributeType.FullName == ReferenceTargetAttribute)?.ConstructorArguments
                .Select(a => a.Value as string).FirstOrDefault();
            arguments[property.Name] = new ActivityArgument(property.Name, t.GetGenericArguments()[0],
                isIn && Has(InputAttribute), isOut && Has(OutputAttribute),
                isIn && property.GetCustomAttribute<RequiredArgumentAttribute>() is not null, Has(DefaultAttribute), target);
        }

        return arguments;
    }

    internal static Dictionary<string, object> Inputs(CustomActivityStep step, IReadOnlyDictionary<string, object?> values,
        Dictionary<string, ActivityArgument> arguments, WorkflowRun run, Type type)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!arguments.TryGetValue(name, out var argument) || !argument.IsInput)
                throw Fault(run, step, $"the custom workflow activity '{type.FullName}' has no input argument '{name}'.");
            var target = argument.ValueType;
            if (value is null)
            {
                if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                    throw Fault(run, step, $"the value of the input argument '{name}' of the custom workflow activity '{type.FullName}' is empty, " +
                                           $"and its type {target.FullName} cannot be empty. xrmverse converts no value.");
                result[name] = null!;
                continue;
            }

            if (!target.IsInstanceOfType(value))
                throw Fault(run, step, $"the input argument '{name}' of the custom workflow activity '{type.FullName}' is of type {target.FullName}, " +
                                       $"but its value is of type {value.GetType().FullName}. xrmverse converts no value.");
            if (argument.ReferenceTarget is { Length: > 0 } table && value is EntityReference reference
                && !string.Equals(reference.LogicalName, table, StringComparison.OrdinalIgnoreCase))
                throw Fault(run, step, $"the input argument '{name}' of the custom workflow activity '{type.FullName}' takes a reference to " +
                                       $"'{table}', not to '{reference.LogicalName}'.");
            result[name] = value;
        }

        // Every required InArgument, [Input] or not (M6): CoreWF would refuse it at Invoke with an ArgumentException.
        foreach (var argument in arguments.Values.Where(a => (a.IsInput || a.Required) && !values.ContainsKey(a.Name)))
        {
            if (argument.Required)
                throw Fault(run, step, $"the step does not set the required input argument '{argument.Name}' of the custom workflow activity '{type.FullName}'" +
                                       (argument.IsInput ? "." : ", which has no [Input] attribute: no step can set it."));
            if (argument.HasDefault)
                throw Fault(run, step, $"the step does not set the input argument '{argument.Name}' of the custom workflow activity '{type.FullName}', " +
                                       "and xrmverse does not apply the [Default] value of an argument.");
        }

        return result;
    }

    /// <summary>
    /// The activity type of the step: the <c>plugintype</c> marked <c>isworkflowactivity</c> with the step's type name, in the
    /// <c>pluginassembly</c> of the step's assembly name (and version, when the XAML names one and several are registered).
    /// </summary>
    private async Task<Type> ActivityTypeAsync(CustomActivityStep step, WorkflowRun run, CancellationToken ct)
    {
        var name = new AssemblyName(step.AssemblyName);
        var rows = await WithSessionAsync(run, async (s, c) =>
        {
            await using var cmd = s.Command($"""
                SELECT a.pluginassemblyid, CAST(a.versionnumber AS bigint), a.version
                FROM data.plugintype t JOIN data.pluginassembly a ON a.pluginassemblyid = t.pluginassemblyid
                WHERE t.typename = @type AND a.name = @assembly AND t.isworkflowactivity = {s.Dialect.BooleanLiteral(true)}
                """);
            cmd.AddParameter("@type", DbType.String, step.TypeName, 400);
            cmd.AddParameter("@assembly", DbType.String, name.Name ?? "", 400);
            var list = new List<(Guid Id, long Version, string? AssemblyVersion)>();
            await using var rd = await cmd.ExecuteReaderAsync(c);
            while (await rd.ReadAsync(c)) list.Add((rd.GetGuid(0), rd.IsDBNull(1) ? 0 : rd.GetInt64(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
            return list;
        }, ct);

        if (rows.Count > 1 && name.Version is { } version)
            rows = rows.Where(r => r.AssemblyVersion == version.ToString()).ToList();
        if (rows.Count != 1)
            throw Fault(run, step, rows.Count == 0
                ? $"the custom workflow activity '{step.TypeName}' of the assembly '{name.Name}' is not registered."
                : $"the custom workflow activity '{step.TypeName}' is registered in several assemblies named '{name.Name}'.");
        var (id, rowVersion, _) = rows[0];

        try
        {
            return loader.TryResolveWorkflowActivityType(id, rowVersion, null, step.TypeName)
                   ?? loader.TryResolveWorkflowActivityType(id, rowVersion, await registry.GetAssemblyContentAsync(id, ct), step.TypeName)!;
        }
        catch (XrmdFaultException ex)
        {
            throw new XrmdFaultException(ex.ErrorCode, $"Workflow '{run.Workflow.Name}': the step '{step.Label}': {ex.Message}", ex.HttpStatus);
        }
    }

    private async Task<T> WithSessionAsync<T>(WorkflowRun run, Func<DataSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (run.Invocation.Session is { } session) return await operation(session, ct);
        await using var conn = await factory.OpenAsync(ct);
        return await operation(new DataSession(conn, null), ct);
    }

    internal static XrmdFaultException Fault(WorkflowRun run, CustomActivityStep step, string what) =>
        XrmdFaultException.InvalidArgument($"Workflow '{run.Workflow.Name}': the step '{step.Label}': {what}");
}

/// <summary>What a custom workflow activity step gave: its outputs by argument name, or how a <c>StopWorkflowException</c> ended the run.</summary>
/// <param name="Outputs">The values of the step's output arguments (empty when <paramref name="Stop"/> is set).</param>
/// <param name="Stop">Null when the activity returned; <see cref="WorkflowOutcome.Completed"/> or <see cref="WorkflowOutcome.Canceled"/>
/// when it stopped the workflow.</param>
public sealed record CustomActivityResult(IReadOnlyDictionary<string, object?> Outputs, WorkflowOutcome? Stop);
