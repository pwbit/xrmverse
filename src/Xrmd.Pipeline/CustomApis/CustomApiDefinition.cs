using Xrmd.Metadata;

namespace Xrmd.Pipeline.CustomApis;

/// <summary>A custom API as the pipeline sees it: a <c>customapi</c> row with its request parameters and response properties.</summary>
/// <param name="Id">The <c>customapiid</c>.</param>
/// <param name="UniqueName">The <c>uniquename</c>, also the name of its message.</param>
/// <param name="BindingType">0 global, 1 entity, 2 entity collection.</param>
/// <param name="BoundEntity">The bound table (<c>boundentitylogicalname</c>); null for a global API.</param>
/// <param name="IsFunction">True for a function (a Web API GET), false for an action.</param>
/// <param name="IsPrivate">True for a private API (left out of <c>$metadata</c>).</param>
/// <param name="AllowedStepType">The <c>allowedcustomprocessingsteptype</c>: 0 none, 1 asynchronous only, 2 synchronous and asynchronous.</param>
/// <param name="ExecutePrivilegeName">The privilege a caller needs (<c>executeprivilegename</c>); null when none.</param>
/// <param name="MainOperation">The plug-in type run at stage 30; null for an API without one.</param>
/// <param name="RequestParameters">The request parameters, by unique name.</param>
/// <param name="ResponseProperties">The response properties, by unique name.</param>
public sealed record CustomApiDefinition(Guid Id, string UniqueName, int BindingType, string? BoundEntity, bool IsFunction, bool IsPrivate,
    int AllowedStepType, string? ExecutePrivilegeName, CustomApiMainOperation? MainOperation, IReadOnlyList<CustomApiField> RequestParameters,
    IReadOnlyList<CustomApiField> ResponseProperties);

/// <summary>The plug-in type of an API's main operation, with its assembly (as <c>StepRegistry</c> reads a step's).</summary>
/// <param name="PluginTypeId">The <c>plugintypeid</c>.</param>
/// <param name="TypeName">The full type name in the assembly.</param>
/// <param name="AssemblyId">The <c>pluginassemblyid</c>.</param>
/// <param name="AssemblyVersion">The row version of the assembly (the loader's cache key).</param>
public sealed record CustomApiMainOperation(Guid PluginTypeId, string TypeName, Guid AssemblyId, long AssemblyVersion);

/// <summary>A request parameter or response property of a custom API.</summary>
/// <param name="UniqueName">The <c>uniquename</c>: the parameter's key in the request or response.</param>
/// <param name="Type">Its type.</param>
/// <param name="LogicalEntityName">The table an Entity, EntityReference or EntityCollection value must be of; null when any.</param>
/// <param name="IsOptional">For a request parameter, true when it may be left out; always false for a response property.</param>
public sealed record CustomApiField(string UniqueName, CustomApiType Type, string? LogicalEntityName, bool IsOptional);
