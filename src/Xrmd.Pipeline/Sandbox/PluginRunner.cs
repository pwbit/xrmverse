using System.Reflection;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Pipeline.Sandbox;

/// <summary>A plug-in failure already mapped to a Dataverse fault; <see cref="Unexpected"/> failures must be trace-logged.</summary>
/// <remarks>It holds no exception object coming from the plug-in (whose type may live in the plug-in's collectible
/// load context): <see cref="Detail"/> keeps its full text instead.</remarks>
public sealed class PluginExecutionException(XrmdFaultException fault, bool unexpected, string? detail = null)
    : Exception(fault.Message, fault)
{
    /// <summary>The full text of the plug-in's exception (type, message, stack, inner exceptions), for <c>plugintracelog.exceptiondetails</c>.</summary>
    public string? Detail { get; } = detail;

    /// <summary>The mapped fault (code, message, HTTP status).</summary>
    public XrmdFaultException Fault { get; } = fault;

    /// <summary>True when the plug-in crashed or timed out (anything but an intentional abort); a trace-log row is then mandatory.</summary>
    public bool Unexpected { get; } = unexpected;
}

/// <summary>Instantiates plug-ins and runs them on the thread pool with a timeout, mapping exceptions to faults.</summary>
public sealed class PluginRunner(PipelineOptions options)
{
    private const int ManagedApplicationHResult = unchecked((int)0x80131500);

    /// <summary>Creates the plug-in with the constructor <c>(string, string)</c>, else <c>(string)</c>, else the default one.</summary>
    /// <exception cref="XrmdFaultException">400 when the type has no supported constructor.</exception>
    /// <exception cref="PluginExecutionException">The constructor threw (mapped as an exception from <c>Execute</c>).</exception>
    public IPlugin CreateInstance(Type type, string? unsecureConfig, string? secureConfig)
    {
        object? instance;
        try
        {
            var two = type.GetConstructor([typeof(string), typeof(string)]);
            if (two is not null) instance = two.Invoke([unsecureConfig, secureConfig]);
            else
            {
                var one = type.GetConstructor([typeof(string)]);
                if (one is not null) instance = one.Invoke([unsecureConfig]);
                else
                {
                    var none = type.GetConstructor(Type.EmptyTypes)
                        ?? throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                            $"Type '{type.FullName}' has no supported constructor.", 400);
                    instance = none.Invoke(null);
                }
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw Map(ex.InnerException, type.FullName ?? type.Name);
        }

        return (IPlugin)instance;
    }

    /// <summary>Runs <c>plugin.Execute(provider)</c> on a pool thread; throws <see cref="PluginExecutionException"/> on failure or timeout.</summary>
    public async Task RunAsync(IPlugin plugin, IServiceProvider provider, string typeName, CancellationToken ct)
    {
        var work = Task.Run(() => plugin.Execute(provider), CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(options.PluginTimeout, timeoutCts.Token);

        var first = await Task.WhenAny(work, delay).ConfigureAwait(false);
        if (first != work)
        {
            ct.ThrowIfCancellationRequested();
            // The plug-in thread cannot be aborted; make sure a late failure is observed.
            _ = work.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                "The plug-in execution failed because the operation has timed-out at the Sandbox Host.", 500), true);
        }

        timeoutCts.Cancel();
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Map(ex, typeName);
        }
    }

    /// <summary>
    /// The fault of an intentional abort (<see cref="InvalidPluginExecutionException"/> semantics): 400 with <paramref name="code"/>
    /// (ISVAborted by default) and <paramref name="message"/>. Also the fault of a real-time workflow's canceling stop and of a
    /// business rule's error (plan 2.14, rulings W11 and W15).
    /// </summary>
    public static XrmdFaultException Aborted(string message, int code = ErrorCodes.ISVAborted) => new(code, message, 400);

    /// <summary>
    /// Maps an exception thrown by plug-in code: <see cref="InvalidPluginExecutionException"/> → 400 (its code, else
    /// ISVAborted) and <see cref="FaultException{OrganizationServiceFault}"/> → 400 with the fault's code, both expected;
    /// anything else → unexpected 500 <c>0x80040224</c>.
    /// </summary>
    public static PluginExecutionException Map(Exception ex, string typeName)
    {
        var detail = ex.ToString();
        switch (ex)
        {
            case InvalidPluginExecutionException invalid:
                // Without an explicit code ErrorCode is 0 or the default exception HResult (COR_E_APPLICATION).
                var code = invalid.ErrorCode is 0 or ManagedApplicationHResult ? ErrorCodes.ISVAborted : invalid.ErrorCode;
                return new PluginExecutionException(Aborted(invalid.Message, code), false, detail);
            case FaultException<OrganizationServiceFault> fault:
                return new PluginExecutionException(new XrmdFaultException(fault.Detail.ErrorCode, fault.Detail.Message, 400), false, detail);
            default:
                return new PluginExecutionException(new XrmdFaultException(ErrorCodes.UnexpectedPluginException,
                    $"Unexpected exception from plug-in (Execute): {typeName}: {ex.GetType().FullName}: {ex.Message}", 500), true, detail);
        }
    }
}
