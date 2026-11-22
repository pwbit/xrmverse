using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// What the workflow compiler needs to know about the organization (plan 2.21, A11): the table metadata, the names of records a
/// lookup literal names, the process definitions a child workflow step names and the custom workflow activities the host can run.
/// The server implements it over <c>IMetadataStore</c> and a data session (Task 6); the tests over fixed metadata.
/// </summary>
public interface IAuthoringEnvironment
{
    /// <summary>The metadata of a table, or null when the organization has no table of that logical name.</summary>
    EntityMetadata? GetEntity(string logicalName);

    /// <summary>The primary name of a record, or null when the record does not exist (or, on the server, the caller cannot read it).</summary>
    Task<string?> PrimaryNameAsync(string entity, Guid id, CancellationToken ct);

    /// <summary>The process definitions (<c>type</c> 1) whose name is <paramref name="nameOrId"/>, or whose id it is.</summary>
    Task<IReadOnlyList<(Guid Id, string Name, bool Subprocess, string? Entity)>> FindDefinitionsAsync(string nameOrId, CancellationToken ct);

    /// <summary>The customization prefix of the publisher of the workflow's solution (an action's message name, Task 3).</summary>
    Task<string> PublisherPrefixAsync(Guid workflowId, string? solution, CancellationToken ct);

    /// <summary>Whether the host found the Microsoft workflow SDK (custom workflow activities, plan 2.14 W17).</summary>
    bool WorkflowSdkAvailable { get; }

    /// <summary>The version of the solution named <paramref name="solution"/> (its unique name); null when there is none.</summary>
    Task<string?> SolutionVersionAsync(string solution, CancellationToken ct);

    /// <summary>Whether a registered plug-in assembly holds <paramref name="typeName"/> as a custom workflow activity.</summary>
    bool IsWorkflowActivity(string typeName, string assemblyName);

    /// <summary>
    /// The request parameters (inputs) and response properties (outputs) of the message <paramref name="message"/> a perform action
    /// step calls, with their CLR types (plan 2.21, Task 3, ruling R6): an action's arguments or a custom API's request parameters
    /// and response properties, the bound <c>Target</c> left out. Null when the organization has no such message.
    /// </summary>
    Task<IReadOnlyList<CalleeArgument>?> MessageArgumentsAsync(string message, CancellationToken ct);

    /// <summary>
    /// The arguments of the custom workflow activity <paramref name="typeName"/> of <paramref name="assemblyName"/> (ruling R6): its
    /// public <c>InArgument&lt;T&gt;</c> properties marked <c>[Input]</c> (inputs) and <c>OutArgument&lt;T&gt;</c> properties marked
    /// <c>[Output]</c> (outputs), each with the full name of its <c>T</c>, as the 2.14 runner reads them
    /// (<c>CustomActivityRunner.Arguments</c>). Null when they cannot be read: the workflow SDK is not installed or no registered
    /// plug-in assembly holds the activity.
    /// </summary>
    IReadOnlyList<CalleeArgument>? WorkflowActivityArguments(string typeName, string assemblyName);
}

/// <summary>
/// An input or output of what a step calls (a message of a perform action step, a custom workflow activity): its name, the full name
/// of its CLR type (<c>System.Int32</c>, <c>Microsoft.Xrm.Sdk.EntityReference</c>, ...) and its direction.
/// </summary>
public sealed record CalleeArgument(string Name, string ClrType, bool IsInput);
