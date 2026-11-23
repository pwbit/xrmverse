using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution;

namespace Xrmd.WebApi;

/// <summary>
/// The unbound solution actions and function of the Web API (mapped on the <c>/api/data/v9.x</c> group; the caller must map
/// to a <c>systemuser</c>), with the parameter names, types and response shapes of the Learn Web API reference:
/// <list type="bullet">
/// <item><c>POST ImportSolution</c>, <c>PublishXml</c>, <c>PublishAllXml</c>: 204.</item>
/// <item><c>POST ImportSolutionAsync</c> → <c>{ AsyncOperationId, ImportJobKey }</c>; <c>POST ExportSolution</c> →
/// <c>{ ExportSolutionFile }</c> (base64); <c>POST AddSolutionComponent</c> and <c>RemoveSolutionComponent</c> →
/// <c>{ id }</c>; <c>POST UninstallSolutionAsync</c> (<c>SolutionUniqueName</c>) → <c>{ AsyncOperationId }</c>;
/// <c>POST StageAndUpgrade</c> → <c>{ SolutionId }</c>, <c>POST StageAndUpgradeAsync</c> → <c>{ ImportJobKey, AsyncOperationId }</c>;
/// <c>POST DeleteAndPromote</c> (<c>UniqueName</c>) → <c>{ SolutionId }</c>, <c>POST DeleteAndPromoteAsync</c> →
/// <c>{ AsyncOperationId }</c>: 200.</item>
/// <item><c>GET RetrieveFormattedImportJobResults(ImportJobId=@p1)?@p1={guid}</c> (or the value inline) →
/// <c>{ FormattedResults }</c>: 200.</item>
/// <item><c>GET RetrieveExchangeRate(TransactionCurrencyId=@p1)?@p1={guid}</c> (or the value inline) → <c>{ ExchangeRate }</c>:
/// 200 (plan 2.10).</item>
/// <item>The file messages: <c>POST InitializeFileBlocksUpload</c> → <c>{ FileContinuationToken }</c>,
/// <c>POST CommitFileBlocksUpload</c> → <c>{ FileId, FileSizeInBytes }</c>, <c>POST InitializeFileBlocksDownload</c> →
/// <c>{ FileContinuationToken, FileName, FileSizeInBytes, IsChunkingSupported }</c>, <c>POST DownloadBlock</c> →
/// <c>{ Data }</c> (base64): 200; <c>POST UploadBlock</c>, <c>DeleteFile</c>: 204. <c>Target</c> is the entity reference
/// <c>{ "@odata.type": "Microsoft.Dynamics.CRM.{entity}", "{entity}id": "{guid}" }</c>; <c>Edm.Binary</c> is base64.</item>
/// <item>The option value messages (choice colours): <c>POST InsertOptionValue</c> → <c>{ NewOptionValue }</c>: 200;
/// <c>POST UpdateOptionValue</c>: 204. <c>Label</c> and <c>Description</c> are <c>Microsoft.Dynamics.CRM.Label</c> objects
/// (<c>{ "LocalizedLabels": [{ "Label": "x", "LanguageCode": 1033 }] }</c>); a <c>Color</c> of <c>null</c> in
/// UpdateOptionValue clears the colour.</item>
/// <item>The autonumber seed messages: <c>POST SetAutoNumberSeed</c> (<c>EntityName</c>, <c>AttributeName</c>, <c>Value</c>):
/// 204; <c>GET GetAutoNumberSeed(EntityName='…',AttributeName='…')</c> → <c>{ AutoNumberSeedValue }</c> and
/// <c>GET GetNextAutoNumberValue(…)</c> → <c>{ NextAutoNumberValue }</c>: 200.</item>
/// </list>
/// The body becomes the SDK request of the same name (<see cref="ToRequest"/>) executed by
/// <see cref="OrganizationService.ExecuteAsync"/>, so every privilege, check and refusal is the SDK path's, with its status
/// and message. A parameter the action does not have, a value of the wrong type and a <c>null</c> for a non-nullable
/// parameter are 400 naming the parameter. Each operation is declared in <c>$metadata</c> (<see cref="AddOperations"/>).
/// </summary>
public static partial class ActionController
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";

    private enum Kind
    {
        Boolean, Int32, Int64, Guid, String, Binary, StringCollection, EntityCollection, SolutionParameters, LayerDesiredOrder,
        ExportComponentsParams, SolutionComponent, EntityReference, Label, Int32Collection,
    }

    /// <param name="WebApiOnly">
    /// The Web API's parameter has no SDK counterpart: its default value is accepted, any other value is refused here with
    /// <paramref name="Refusal"/> (the SDK cannot send it, so there is no SDK refusal to reuse).
    /// </param>
    /// <param name="NullIsEmpty">An explicit <c>null</c> is passed on as an empty string (a clear: the <c>Color</c> of
    /// UpdateOptionValue) instead of leaving the parameter unset.</param>
    private sealed record Parameter(string Name, Kind Kind, bool Nullable, string? Refusal = null, bool NullIsEmpty = false)
    {
        public bool WebApiOnly => Refusal is not null;
    }

    private sealed record ResponseProperty(string Name, EdmPrimitiveTypeKind Type);

    private sealed record Operation(string Name, bool IsFunction, Parameter[] Parameters, string? Response, ResponseProperty[] Properties);

    /// <summary>The parameters of ImportSolution and ImportSolutionAsync (Learn), in Learn's order.</summary>
    private static Parameter[] ImportParameters(bool async) =>
    [
        new("OverwriteUnmanagedCustomizations", Kind.Boolean, false),
        new("PublishWorkflows", Kind.Boolean, false),
        new("CustomizationFile", Kind.Binary, async),
        new("ImportJobId", Kind.Guid, async),
        new("ConvertToManaged", Kind.Boolean, true),
        new("SkipProductUpdateDependencies", Kind.Boolean, true),
        new("HoldingSolution", Kind.Boolean, true),
        new("SkipQueueRibbonJob", Kind.Boolean, true),
        new("LayerDesiredOrder", Kind.LayerDesiredOrder, true),
        new("AsyncRibbonProcessing", Kind.Boolean, true),
        new("ComponentParameters", Kind.EntityCollection, false),
        new("IsTemplateMode", Kind.Boolean, true),
        new("TemplateSuffix", Kind.String, true),
        new("SolutionParameters", Kind.SolutionParameters, true),
        new("TemplateDisplayNamePrefix", Kind.String, true),
        new("StageAndUpgrade", Kind.Boolean, true, "StageAndUpgrade is not supported as a parameter of ImportSolution by xrmverse: call the StageAndUpgrade action instead."),
        new("SchemaUpdatesOnly", Kind.Boolean, true, "SchemaUpdatesOnly is not supported: xrmverse imports every component of the solution."),
    ];

    /// <summary>
    /// The parameters of InsertOptionValue and UpdateOptionValue (Learn), in Learn's order. <c>ParentValues</c>,
    /// <c>ExternalValue</c> and <c>IsHidden</c> reach the SDK path, which refuses them when set (<c>IsHidden</c>: when true;
    /// 400 naming them).
    /// </summary>
    private static Parameter[] OptionValueParameters(bool update) =>
    [
        new("Color", Kind.String, true, NullIsEmpty: update),
        new("OptionSetName", Kind.String, true),
        new("AttributeLogicalName", Kind.String, true),
        new("ExternalValue", Kind.String, true),
        new("EntityLogicalName", Kind.String, true),
        new("IsHidden", Kind.Boolean, true),
        new("Value", Kind.Int32, !update),
        new("Label", Kind.Label, true),
        new("Description", Kind.Label, true),
        .. update ? (Parameter[])[new("MergeLabels", Kind.Boolean, false)] : [],
        new("ParentValues", Kind.Int32Collection, false),
        new("SolutionUniqueName", Kind.String, true),
    ];

    /// <summary>The parameters of StageAndUpgrade and StageAndUpgradeAsync (Learn), in Learn's order.</summary>
    private static Parameter[] UpgradeParameters(bool async) =>
    [
        new("OverwriteUnmanagedCustomizations", Kind.Boolean, false),
        new("PublishWorkflows", Kind.Boolean, false),
        new("CustomizationFile", Kind.Binary, async),
        new("ImportJobId", Kind.Guid, async),
        new("ConvertToManaged", Kind.Boolean, true),
        new("SkipProductUpdateDependencies", Kind.Boolean, true),
        new("SkipQueueRibbonJob", Kind.Boolean, true),
        new("AsyncRibbonProcessing", Kind.Boolean, true),
        new("ComponentParameters", Kind.EntityCollection, false),
        new("SolutionParameters", Kind.SolutionParameters, true),
        new("LayerDesiredOrder", Kind.LayerDesiredOrder, true),
    ];

    private static readonly Operation[] Operations =
    [
        new("ImportSolution", false, ImportParameters(async: false), null, []),
        new("ImportSolutionAsync", false, ImportParameters(async: true), "ImportSolutionAsyncResponse",
            [new("AsyncOperationId", EdmPrimitiveTypeKind.Guid), new("ImportJobKey", EdmPrimitiveTypeKind.String)]),
        new("ExportSolution", false,
        [
            new("SolutionName", Kind.String, false),
            new("Managed", Kind.Boolean, false),
            new("TargetVersion", Kind.String, true),
            new("ExportAutoNumberingSettings", Kind.Boolean, true),
            new("ExportCalendarSettings", Kind.Boolean, true),
            new("ExportCustomizationSettings", Kind.Boolean, true),
            new("ExportEmailTrackingSettings", Kind.Boolean, true),
            new("ExportGeneralSettings", Kind.Boolean, true),
            new("ExportMarketingSettings", Kind.Boolean, true),
            new("ExportOutlookSynchronizationSettings", Kind.Boolean, true),
            new("ExportRelationshipRoles", Kind.Boolean, true),
            new("ExportIsvConfig", Kind.Boolean, true),
            new("ExportSales", Kind.Boolean, true),
            new("ExportExternalApplications", Kind.Boolean, true),
            new("ExportComponentsParams", Kind.ExportComponentsParams, true),
        ], "ExportSolutionResponse", [new("ExportSolutionFile", EdmPrimitiveTypeKind.Binary)]),
        new("AddSolutionComponent", false,
        [
            new("ComponentId", Kind.Guid, false),
            new("ComponentType", Kind.Int32, false),
            new("SolutionUniqueName", Kind.String, false),
            new("AddRequiredComponents", Kind.Boolean, false),
            new("DoNotIncludeSubcomponents", Kind.Boolean, true),
            new("IncludedComponentSettingsValues", Kind.StringCollection, false),
        ], "AddSolutionComponentResponse", [new("id", EdmPrimitiveTypeKind.Guid)]),
        new("RemoveSolutionComponent", false,
        [
            new("SolutionComponent", Kind.SolutionComponent, false),
            new("ComponentType", Kind.Int32, false),
            new("SolutionUniqueName", Kind.String, false),
        ], "RemoveSolutionComponentResponse", [new("id", EdmPrimitiveTypeKind.Guid)]),
        // The uninstall of a managed solution, queued as a system job (plan 2.6, ruling M9).
        new("UninstallSolutionAsync", false, [new("SolutionUniqueName", Kind.String, false)], "UninstallSolutionAsyncResponse",
            [new("AsyncOperationId", EdmPrimitiveTypeKind.Guid)]),
        // The upgrade of a managed solution and the promote of a holding solution (plan 2.6, ruling M11).
        new("StageAndUpgrade", false, UpgradeParameters(async: false), "StageAndUpgradeResponse", [new("SolutionId", EdmPrimitiveTypeKind.Guid)]),
        new("StageAndUpgradeAsync", false, UpgradeParameters(async: true), "StageAndUpgradeAsyncResponse",
            [new("ImportJobKey", EdmPrimitiveTypeKind.String), new("AsyncOperationId", EdmPrimitiveTypeKind.Guid)]),
        new("DeleteAndPromote", false, [new("UniqueName", Kind.String, false)], "DeleteAndPromoteResponse",
            [new("SolutionId", EdmPrimitiveTypeKind.Guid)]),
        new("DeleteAndPromoteAsync", false, [new("UniqueName", Kind.String, false)], "DeleteAndPromoteAsyncResponse",
            [new("AsyncOperationId", EdmPrimitiveTypeKind.Guid)]),
        new("PublishXml", false, [new("ParameterXml", Kind.String, false)], null, []),
        new("PublishAllXml", false, [], null, []),
        new("RetrieveFormattedImportJobResults", true, [new("ImportJobId", Kind.Guid, false)], "RetrieveFormattedImportJobResultsResponse",
            [new("FormattedResults", EdmPrimitiveTypeKind.String)]),
        // The file messages (Learn Web API reference; "Use file column data"), executed by the SDK path of the same name.
        new("InitializeFileBlocksUpload", false,
        [
            new("Target", Kind.EntityReference, false),
            new("FileName", Kind.String, false),
            new("FileAttributeName", Kind.String, false),
        ], "InitializeFileBlocksUploadResponse", [new("FileContinuationToken", EdmPrimitiveTypeKind.String)]),
        new("UploadBlock", false,
        [
            new("BlockId", Kind.String, false),
            new("BlockData", Kind.Binary, false),
            new("FileContinuationToken", Kind.String, false),
        ], null, []),
        new("CommitFileBlocksUpload", false,
        [
            new("FileName", Kind.String, false),
            new("MimeType", Kind.String, false),
            new("BlockList", Kind.StringCollection, false),
            new("FileContinuationToken", Kind.String, false),
        ], "CommitFileBlocksUploadResponse",
            [new("FileId", EdmPrimitiveTypeKind.Guid), new("FileSizeInBytes", EdmPrimitiveTypeKind.Int64)]),
        new("InitializeFileBlocksDownload", false,
        [
            new("Target", Kind.EntityReference, false),
            new("FileAttributeName", Kind.String, false),
        ], "InitializeFileBlocksDownloadResponse",
        [
            new("FileContinuationToken", EdmPrimitiveTypeKind.String), new("FileName", EdmPrimitiveTypeKind.String),
            new("FileSizeInBytes", EdmPrimitiveTypeKind.Int64), new("IsChunkingSupported", EdmPrimitiveTypeKind.Boolean),
        ]),
        new("DownloadBlock", false,
        [
            new("Offset", Kind.Int64, false),
            new("BlockLength", Kind.Int64, false),
            new("FileContinuationToken", Kind.String, false),
        ], "DownloadBlockResponse", [new("Data", EdmPrimitiveTypeKind.Binary)]),
        new("DeleteFile", false, [new("FileId", Kind.Guid, false)], null, []),
        // The autonumber seed messages (Learn, "Create autonumber columns"; autonumber plan, ruling N6).
        new("SetAutoNumberSeed", false,
        [
            new("EntityName", Kind.String, false),
            new("AttributeName", Kind.String, false),
            new("Value", Kind.Int64, false),
        ], null, []),
        new("GetAutoNumberSeed", true, AutoNumberColumn, "GetAutoNumberSeedResponse",
            [new("AutoNumberSeedValue", EdmPrimitiveTypeKind.Int64)]),
        new("GetNextAutoNumberValue", true, AutoNumberColumn, "GetNextAutoNumberValueResponse",
            [new("NextAutoNumberValue", EdmPrimitiveTypeKind.Int64)]),
        // The option value messages (Learn; choice colours): the SDK path's InsertOptionValue and UpdateOptionValue.
        new("InsertOptionValue", false, OptionValueParameters(update: false), "InsertOptionValueResponse",
            [new("NewOptionValue", EdmPrimitiveTypeKind.Int32)]),
        new("UpdateOptionValue", false, OptionValueParameters(update: true), null, []),
        // The rate of a currency (plan 2.10, ruling X12).
        new("RetrieveExchangeRate", true, [new("TransactionCurrencyId", Kind.Guid, false)], "RetrieveExchangeRateResponse",
            [new("ExchangeRate", EdmPrimitiveTypeKind.Decimal)]),
        // The next tracking token of the organization (plan 4.1b, task 7; Learn: an unbound action with an optional Subject).
        new("GetTrackingTokenEmail", false, [new("Subject", Kind.String, true)], "GetTrackingTokenEmailResponse",
            [new("TrackingToken", EdmPrimitiveTypeKind.String)]),
    ];

    /// <summary>The parameters of the two autonumber functions: the table and the column, both strings.</summary>
    private static Parameter[] AutoNumberColumn => [new("EntityName", Kind.String, false), new("AttributeName", Kind.String, false)];

    /// <summary>The solution patch actions (Learn), refused until xrmverse supports patches (plan 2.6, ruling M12).</summary>
    private static readonly string[] PatchActions = ["CloneAsPatch", "CloneAsSolution"];

    /// <summary>
    /// The names of the unbound actions and functions this controller serves, the refused patch actions and those of
    /// <see cref="DuplicateDetectionActions"/>, <see cref="AccessActions"/> and <see cref="RollupActions"/> (all reserved as entity set names).
    /// </summary>
    public static IReadOnlyList<string> OperationNames { get; } =
        [.. Operations.Select(o => o.Name), .. PatchActions, .. DuplicateDetectionActions.OperationNames, .. AccessActions.OperationNames,
         .. RollupActions.OperationNames];

    public static IEndpointRouteBuilder MapActionEndpoints(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        foreach (var operation in Operations.Where(o => !o.IsFunction))
        {
            var route = routes.MapPost("/" + operation.Name,
                (HttpContext context, OrganizationService service, IMetadataStore store) => PostAsync(context, service, store, operation))
                .InChangeSet(); // the messages that commit on their own are refused by the OrganizationService
            // A solution file, or a file block (4 MB, about 5.6 MB once base64 encoded): Dataverse's 128 MB request limit.
            if (operation.Name is "ImportSolution" or "ImportSolutionAsync" or "StageAndUpgrade" or "StageAndUpgradeAsync" or "UploadBlock")
                SolutionFileSizeLimit.ApplyTo(route);
        }
        // Solution patches are deferred (plan 2.6, ruling M12): the two patch actions are refused by the SDK path, with the reason.
        foreach (var name in PatchActions)
            routes.MapPost("/" + name, async (HttpContext context, OrganizationService service) =>
            {
                await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
                await service.ExecuteAsync(new OrganizationRequest(name), context.RequestAborted); // always refused (400)
                return Results.NoContent();
            });
        routes.MapGet("/RetrieveFormattedImportJobResults({parameters})", RetrieveFormattedImportJobResultsAsync);
        routes.MapGet("/RetrieveExchangeRate({parameters})", RetrieveExchangeRateAsync);
        // Elastic tables: the basic Cosmos SQL subset (plan #78).
        parent.MapCosmosQueryFunction();
        foreach (var name in (string[])["GetAutoNumberSeed", "GetNextAutoNumberValue"])
            routes.MapGet("/" + name + "({parameters})", (HttpContext context, OrganizationService service, string parameters) =>
                StringFunctionAsync(context, service, Find(name), parameters));
        return parent;
    }

    private static async Task<IResult> PostAsync(HttpContext context, OrganizationService service, IMetadataStore store, Operation operation)
    {
        var body = await ODataJson.ReadBodyAsync(context.Request, allowEmpty: true);
        var response = await service.ExecuteAsync(ToRequest(operation, body, store, IsIeee754Compatible(context.Request.ContentType)),
            context.RequestAborted);
        return Respond(context, operation, response);
    }

    private static async Task<IResult> RetrieveFormattedImportJobResultsAsync(HttpContext context, OrganizationService service, string parameters)
    {
        var operation = Find("RetrieveFormattedImportJobResults");
        var id = ParseImportJobId(parameters, alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null);
        var request = new OrganizationRequest(operation.Name) { ["ImportJobId"] = id };
        return Respond(context, operation, await service.ExecuteAsync(request, context.RequestAborted));
    }

    private static async Task<IResult> RetrieveExchangeRateAsync(HttpContext context, OrganizationService service, string parameters)
    {
        var operation = Find("RetrieveExchangeRate");
        var id = ParseGuidParameter(operation.Name, "TransactionCurrencyId", parameters,
            alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null);
        var request = new OrganizationRequest(operation.Name) { ["TransactionCurrencyId"] = id };
        return Respond(context, operation, await service.ExecuteAsync(request, context.RequestAborted));
    }

    private static async Task<IResult> StringFunctionAsync(HttpContext context, OrganizationService service, Operation operation, string parameters)
    {
        var request = ToStringFunctionRequest(operation, parameters, alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null);
        return Respond(context, operation, await service.ExecuteAsync(request, context.RequestAborted));
    }

    /// <summary>
    /// The SDK request of a function whose parameters are all strings (<c>Name='text'</c> pairs, each value inline or an
    /// <c>@alias</c> that <paramref name="alias"/> resolves from the query string, where it may be quoted or not).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, repeated or missing parameter, an alias without a value, or a
    /// value that is not a quoted string.</exception>
    private static OrganizationRequest ToStringFunctionRequest(Operation operation, string parameters, Func<string, string?> alias)
    {
        var request = new OrganizationRequest(operation.Name);
        foreach (var pair in SplitParameters(parameters))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || operation.Parameters.All(p => p.Name != name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{operation.Name}'.");
            if (request.Parameters.ContainsKey(name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{operation.Name}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            var aliased = text.StartsWith('@');
            if (aliased)
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{operation.Name}' has no value.");
            if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') text = text[1..^1].Replace("''", "'", StringComparison.Ordinal);
            else if (!aliased)
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{operation.Name}' must be an Edm.String ('text').");
            request[name] = text;
        }
        foreach (var required in operation.Parameters.Where(p => !p.Nullable))
            if (!request.Parameters.ContainsKey(required.Name))
                throw XrmdFaultException.InvalidArgument($"Required parameter '{required.Name}' of function '{operation.Name}' is missing.");
        return request;
    }

    /// <summary>The <c>Name=value</c> pairs of a parameter list, split on the commas outside quotes.</summary>
    private static IEnumerable<string> SplitParameters(string parameters)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i] == '\'') quoted = !quoted;
            else if (parameters[i] == ',' && !quoted)
            {
                yield return parameters[start..i];
                start = i + 1;
            }
        }
        if (parameters[start..].Trim().Length > 0) yield return parameters[start..];
    }

    private static IResult Respond(HttpContext context, Operation operation, OrganizationResponse response)
    {
        if (operation.Response is null)
        {
            context.Response.Headers["OData-Version"] = "4.0";
            return Results.NoContent();
        }
        var json = new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{Ns}.{operation.Response}",
        };
        foreach (var property in operation.Properties)
        {
            var value = response.Results.TryGetValue(property.Name, out var v) ? v : null;
            json[property.Name] = value switch
            {
                null => null,
                byte[] bytes => Convert.ToBase64String(bytes),
                Guid guid => guid,
                int number => number,
                long number => number,
                decimal number => number,
                bool flag => flag,
                string text => text,
                _ => throw new InvalidOperationException(
                    $"Response property '{property.Name}' of '{operation.Name}' has an unexpected type {value.GetType().Name}."),
            };
        }
        return ODataJson.Result(context, json);
    }

    private static Operation Find(string name) => Operations.First(o => o.Name == name);

    /// <summary>
    /// The SDK request of action <paramref name="action"/> for its JSON <paramref name="body"/>: each parameter under the
    /// SDK's name with the SDK's type (<c>Edm.Binary</c> base64 → <c>byte[]</c>, <c>ComponentParameters</c> →
    /// <see cref="EntityCollection"/>, <c>SolutionParameters</c> → <see cref="SolutionParameters"/>, the
    /// <c>SolutionComponent</c> of RemoveSolutionComponent → its key as <c>ComponentId</c>). A <c>null</c> of a nullable
    /// parameter leaves it unset.
    /// </summary>
    /// <param name="store">The entities an entity reference parameter (<c>Target</c>) may name.</param>
    /// <exception cref="XrmdFaultException">400 naming a parameter the action does not have, a value of the wrong type, a
    /// <c>null</c> of a non-nullable parameter, or a Web API-only option set to a value xrmverse cannot honor.</exception>
    /// <param name="ieee754Compatible">The request's content type has <c>IEEE754Compatible=true</c> (OData JSON): an
    /// <c>Edm.Int64</c> may then be sent as a string.</param>
    public static OrganizationRequest ToRequest(string action, JsonObject body, IMetadataStore store, bool ieee754Compatible = false) =>
        ToRequest(Operations.FirstOrDefault(o => o.Name == action && !o.IsFunction)
                  ?? throw new ArgumentException($"'{action}' is not an action of this controller.", nameof(action)), body, store, ieee754Compatible);

    /// <summary>True when the content type carries the OData JSON parameter <c>IEEE754Compatible=true</c> (any case).</summary>
    public static bool IsIeee754Compatible(string? contentType) =>
        Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var media)
        && media.Parameters.Any(p => string.Equals(p.Name.Value, "IEEE754Compatible", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(p.Value.Value?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase));

    private static OrganizationRequest ToRequest(Operation operation, JsonObject body, IMetadataStore store, bool ieee754Compatible)
    {
        ArgumentNullException.ThrowIfNull(store);
        var request = new OrganizationRequest(operation.Name);
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@')) continue; // instance annotations
            var parameter = operation.Parameters.FirstOrDefault(p => p.Name == name)
                            ?? throw XrmdFaultException.InvalidArgument(
                                $"The parameter '{name}' in the request payload is not a valid parameter for the operation '{operation.Name}'.");
            if (node is null)
            {
                if (!parameter.Nullable) throw Invalid(operation, parameter, "must not be null");
                if (parameter.NullIsEmpty) request[name] = "";
                continue;
            }
            if (parameter.WebApiOnly)
            {
                if (ReadBoolean(operation, parameter, node)) throw XrmdFaultException.InvalidArgument(parameter.Refusal!);
                continue;
            }
            if (parameter.Kind == Kind.SolutionComponent)
                request["ComponentId"] = SolutionComponentId(operation, parameter, node);
            else if (parameter.Kind == Kind.EntityReference)
                request[name] = ReadEntityReference(operation, parameter, node, store);
            else
                request[name] = ReadValue(operation, parameter, node, ieee754Compatible);
        }
        return request;
    }

    private static object ReadValue(Operation operation, Parameter parameter, JsonNode node, bool ieee754Compatible) => parameter.Kind switch
    {
        Kind.Boolean => ReadBoolean(operation, parameter, node),
        Kind.Int32 => node is JsonValue v && v.TryGetValue<int>(out var number) ? number : throw Invalid(operation, parameter, "must be an Edm.Int32"),
        Kind.Int64 => ReadInt64(operation, parameter, node, ieee754Compatible),
        Kind.Guid => ReadGuid(operation, parameter, node),
        Kind.String => ReadText(operation, parameter, node, "must be an Edm.String"),
        Kind.Binary => ReadBinary(operation, parameter, node),
        Kind.StringCollection => node is JsonArray strings
            ? strings.Select(s => s is JsonValue sv && sv.TryGetValue<string>(out var text) ? text
                : throw Invalid(operation, parameter, "must be a Collection(Edm.String)")).ToArray()
            : throw Invalid(operation, parameter, "must be a Collection(Edm.String)"),
        Kind.EntityCollection => ReadEntityCollection(operation, parameter, node),
        Kind.SolutionParameters => ReadSolutionParameters(operation, parameter, node),
        Kind.LayerDesiredOrder => node is JsonObject
            ? new LayerDesiredOrder()
            : throw Invalid(operation, parameter, $"must be a {Ns}.LayerDesiredOrder object"),
        Kind.ExportComponentsParams => node is JsonObject
            ? new ExportComponentsParams()
            : throw Invalid(operation, parameter, $"must be a {Ns}.ExportComponentsParams object"),
        Kind.Label => ReadLabel(operation, parameter, node),
        Kind.Int32Collection => node is JsonArray numbers
            ? numbers.Select(n => n is JsonValue nv && nv.TryGetValue<int>(out var item) ? item
                : throw Invalid(operation, parameter, "must be a Collection(Edm.Int32)")).ToArray()
            : throw Invalid(operation, parameter, "must be a Collection(Edm.Int32)"),
        _ => throw new InvalidOperationException($"Unhandled parameter kind {parameter.Kind}."),
    };

    /// <summary>
    /// A <c>Microsoft.Dynamics.CRM.Label</c> (<c>{ "LocalizedLabels": [{ "Label": "x", "LanguageCode": 1033 }] }</c>), read as
    /// the metadata endpoints read labels (<see cref="MetadataSerializer.Options"/>); every localized label needs its text and
    /// language.
    /// </summary>
    private static Label ReadLabel(Operation operation, Parameter parameter, JsonNode node)
    {
        var expected = $"must be a {Ns}.Label object ({{ \"LocalizedLabels\": [{{ \"Label\": \"…\", \"LanguageCode\": 1033 }}] }})";
        if (node is not JsonObject) throw Invalid(operation, parameter, expected);
        Label? label;
        try
        {
            label = System.Text.Json.JsonSerializer.Deserialize<Label>(node, MetadataSerializer.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            throw Invalid(operation, parameter, expected);
        }
        if (label is null || label.LocalizedLabels.Any(l => l is null || l.Label is null || l.LanguageCode <= 0))
            throw Invalid(operation, parameter, expected);
        return label;
    }

    /// <summary>An <c>Edm.Int64</c>: a JSON number, or with <c>IEEE754Compatible=true</c> also a string of one (OData JSON 4.01).</summary>
    private static long ReadInt64(Operation operation, Parameter parameter, JsonNode node, bool ieee754Compatible)
    {
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<long>(out var number)) return number;
            if (ieee754Compatible && v.TryGetValue<string>(out var text)
                && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        throw Invalid(operation, parameter, ieee754Compatible
            ? "must be an Edm.Int64 (a number, or a string of one with IEEE754Compatible=true)"
            : "must be an Edm.Int64 (a JSON number; send IEEE754Compatible=true in the content type to send it as a string)");
    }

    private static bool ReadBoolean(Operation operation, Parameter parameter, JsonNode node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : throw Invalid(operation, parameter, "must be an Edm.Boolean");

    private static Guid ReadGuid(Operation operation, Parameter parameter, JsonNode node) =>
        System.Guid.TryParseExact(ReadText(operation, parameter, node, "must be an Edm.Guid"), "D", out var id)
            ? id
            : throw Invalid(operation, parameter, "must be an Edm.Guid");

    private static string ReadText(Operation operation, Parameter parameter, JsonNode node, string expected) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw Invalid(operation, parameter, expected);

    private static byte[] ReadBinary(Operation operation, Parameter parameter, JsonNode node)
    {
        try
        {
            return Convert.FromBase64String(ReadText(operation, parameter, node, "must be an Edm.Binary (base64)"));
        }
        catch (FormatException)
        {
            throw Invalid(operation, parameter, "must be an Edm.Binary (base64)");
        }
    }

    /// <summary>
    /// <c>Collection(crmbaseentity)</c>: one <see cref="Entity"/> per object, of the entity its <c>@odata.type</c> names. Its
    /// columns are not read: the SDK path refuses any non-empty <c>ComponentParameters</c>.
    /// </summary>
    private static EntityCollection ReadEntityCollection(Operation operation, Parameter parameter, JsonNode node)
    {
        if (node is not JsonArray items) throw Invalid(operation, parameter, $"must be a Collection({Ns}.crmbaseentity)");
        var collection = new EntityCollection();
        foreach (var item in items)
        {
            if (item is not JsonObject entity) throw Invalid(operation, parameter, $"must be a Collection({Ns}.crmbaseentity)");
            var type = entity[ODataType] is JsonValue t && t.TryGetValue<string>(out var typeName) ? TypeName(typeName) : null;
            collection.Entities.Add(type is null ? new Entity() : new Entity(type));
        }
        return collection;
    }

    /// <summary>
    /// <c>SolutionParameters</c> (Learn: <c>DeployMissingPackagesBeforeSolutionImport</c>, <c>SolutionComponentOptions</c>,
    /// <c>StageSolutionUploadId</c>, <c>PackageHistoryReference</c>) as the SDK's <see cref="Microsoft.Xrm.Sdk.SolutionParameters"/>,
    /// whose refusal of a set value is the SDK path's. The two properties the SDK type does not have are refused here with
    /// the same message when set.
    /// </summary>
    private static SolutionParameters ReadSolutionParameters(Operation operation, Parameter parameter, JsonNode node)
    {
        if (node is not JsonObject properties) throw Invalid(operation, parameter, $"must be a {Ns}.SolutionParameters object");
        var result = new SolutionParameters();
        foreach (var (name, value) in properties)
        {
            switch (name)
            {
                case ODataType:
                    break;
                case "StageSolutionUploadId":
                    if (value is not null) result.StageSolutionUploadId = ReadGuid(operation, parameter with { Name = $"{parameter.Name}/{name}" }, value);
                    break;
                case "SolutionComponentOptions":
                    if (value is JsonArray options)
                        result.SolutionComponentOptions = options.Select(_ => new SolutionComponentOption()).ToList();
                    else if (value is not null)
                        throw Invalid(operation, parameter with { Name = $"{parameter.Name}/{name}" }, $"must be a Collection({Ns}.SolutionComponentOption)");
                    break;
                case "DeployMissingPackagesBeforeSolutionImport":
                    if (value is not null && ReadBoolean(operation, parameter with { Name = $"{parameter.Name}/{name}" }, value))
                        throw XrmdFaultException.InvalidArgument(SolutionImporter.SolutionParametersNotSupported);
                    break;
                case "PackageHistoryReference":
                    if (value is not null) throw XrmdFaultException.InvalidArgument(SolutionImporter.SolutionParametersNotSupported);
                    break;
                default:
                    throw XrmdFaultException.InvalidArgument(
                        $"The property '{name}' of parameter '{parameter.Name}' is not a property of {Ns}.SolutionParameters.");
            }
        }
        return result;
    }

    /// <summary>
    /// The <c>SolutionComponent</c> of RemoveSolutionComponent, <c>{ "@odata.type": "Microsoft.Dynamics.CRM.solutioncomponent",
    /// "solutioncomponentid": "{guid}" }</c>: its key is the SDK request's <c>ComponentId</c> (the SDK message has no entity
    /// parameter; the id is read as the SDK path reads <c>ComponentId</c>).
    /// </summary>
    private static Guid SolutionComponentId(Operation operation, Parameter parameter, JsonNode node)
    {
        const string key = "solutioncomponentid";
        if (node is not JsonObject entity) throw Invalid(operation, parameter, $"must be a {Ns}.solutioncomponent object");
        foreach (var (name, value) in entity)
        {
            if (name == ODataType)
            {
                if (value is not JsonValue t || !t.TryGetValue<string>(out var type) || TypeName(type) != "solutioncomponent")
                    throw Invalid(operation, parameter, $"must be a {Ns}.solutioncomponent object");
            }
            else if (name != key)
                throw XrmdFaultException.InvalidArgument(
                    $"The property '{name}' of parameter '{parameter.Name}' is not supported: only '{key}' identifies the solution component.");
        }
        return entity[key] is { } id
            ? ReadGuid(operation, parameter with { Name = $"{parameter.Name}/{key}" }, id)
            : throw Invalid(operation, parameter, $"must carry '{key}'");
    }

    /// <summary>
    /// An entity reference parameter (<c>crmbaseentity</c>), as Learn's examples send <c>Target</c>:
    /// <c>{ "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "{guid}" }</c>. The type names the entity and the
    /// primary key the record; any other property (an alternate key, a column value) is refused, naming it.
    /// </summary>
    private static EntityReference ReadEntityReference(Operation operation, Parameter parameter, JsonNode node, IMetadataStore store)
    {
        if (node is not JsonObject entity) throw Invalid(operation, parameter, $"must be an entity ({Ns}.crmbaseentity) object");
        var type = entity[ODataType] is JsonValue t && t.TryGetValue<string>(out var typeName) ? TypeName(typeName) : null;
        if (type is null) throw Invalid(operation, parameter, $"must carry '{ODataType}' naming its entity type");
        var m = store.GetEntity(type) ?? throw Invalid(operation, parameter, $"names the entity type '{type}', which does not exist");
        var key = SystemAttributes.PrimaryIdName(m);
        foreach (var (name, _) in entity)
            if (name != ODataType && name != key)
                throw Invalid(operation, parameter with { Name = $"{parameter.Name}/{name}" },
                    $"is not supported: only '{key}' identifies the {m.LogicalName} record");
        return entity[key] is { } id
            ? new EntityReference(m.LogicalName, ReadGuid(operation, parameter with { Name = $"{parameter.Name}/{key}" }, id))
            : throw Invalid(operation, parameter, $"must carry its key '{key}'");
    }

    /// <summary>
    /// An entity reference parameter <paramref name="parameterName"/> of operation <paramref name="operationName"/>, read as
    /// <see cref="ReadEntityReference"/> reads <c>Target</c> (for the operations of other controllers).
    /// </summary>
    internal static EntityReference ReadEntityReference(string operationName, string parameterName, JsonNode node, IMetadataStore store) =>
        ReadEntityReference(new Operation(operationName, false, [], null, []), new Parameter(parameterName, Kind.EntityReference, false), node, store);

    /// <summary>
    /// An entity parameter (<c>crmbaseentity</c>) with its columns: <c>{ "@odata.type": "Microsoft.Dynamics.CRM.{table}",
    /// "{column}": value, … }</c>, the columns read as a record body (<see cref="ODataJson.FromJson"/>), the primary key, when
    /// present, also the record's <see cref="Entity.Id"/>. Without <c>@odata.type</c>, the record is of
    /// <paramref name="defaultTable"/> (a custom API parameter's <c>logicalentityname</c>); with neither, it is refused.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the parameter: not an object, no or an unknown table, or a column the
    /// table does not have or of the wrong type.</exception>
    internal static Entity ReadEntity(string operationName, string parameterName, JsonNode node, IMetadataStore store, string? defaultTable)
    {
        var operation = new Operation(operationName, false, [], null, []);
        var parameter = new Parameter(parameterName, Kind.EntityCollection, false);
        if (node is not JsonObject record) throw Invalid(operation, parameter, $"must be an entity ({Ns}.crmbaseentity) object");
        var type = record[ODataType] is JsonValue t && t.TryGetValue<string>(out var typeName) ? TypeName(typeName) : defaultTable;
        if (type is null) throw Invalid(operation, parameter, $"must carry '{ODataType}' naming its entity type");
        var m = store.GetEntity(type) ?? throw Invalid(operation, parameter, $"names the entity type '{type}', which does not exist");
        Entity entity;
        try
        {
            entity = ODataJson.FromJson(record, m, store);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.InvalidArgument)
        {
            throw Invalid(operation, parameter, $"is not a valid '{m.LogicalName}' record: {ex.Message.TrimEnd('.')}");
        }
        if (entity.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(m), out var id) && id is Guid key) entity.Id = key;
        return entity;
    }

    /// <summary>
    /// A <c>Collection(crmbaseentity)</c> parameter with the columns of each record, each read as <see cref="ReadEntity"/>
    /// reads one.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the parameter: not an array, or a record <see cref="ReadEntity"/> refuses.</exception>
    internal static EntityCollection ReadEntities(string operationName, string parameterName, JsonNode node, IMetadataStore store, string? defaultTable)
    {
        if (node is not JsonArray items)
            throw Invalid(new Operation(operationName, false, [], null, []), new Parameter(parameterName, Kind.EntityCollection, false),
                $"must be a Collection({Ns}.crmbaseentity)");
        var collection = new EntityCollection();
        foreach (var item in items)
            collection.Entities.Add(item is null
                ? throw Invalid(new Operation(operationName, false, [], null, []), new Parameter(parameterName, Kind.EntityCollection, false),
                    "must not hold a null record")
                : ReadEntity(operationName, parameterName, item, store, defaultTable));
        if (collection.Entities.Count > 0 && collection.Entities.Select(e => e.LogicalName).Distinct(StringComparer.Ordinal).Count() == 1)
            collection.EntityName = collection.Entities[0].LogicalName;
        return collection;
    }

    /// <summary>The unqualified name of an <c>@odata.type</c> (with or without <c>#</c> and the namespace).</summary>
    private static string TypeName(string type)
    {
        type = type.TrimStart('#');
        return type.StartsWith(Ns + ".", StringComparison.Ordinal) ? type[(Ns.Length + 1)..] : type;
    }

    private static XrmdFaultException Invalid(Operation operation, Parameter parameter, string problem) =>
        XrmdFaultException.InvalidArgument($"The parameter '{parameter.Name}' of operation '{operation.Name}' {problem}.");

    /// <summary>
    /// The <c>ImportJobId</c> of <c>RetrieveFormattedImportJobResults(ImportJobId=…)</c>: a GUID inline or a <c>@alias</c>
    /// whose value <paramref name="alias"/> returns (the query string).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for any other parameter, a missing alias value or a value that is not a GUID.</exception>
    public static Guid ParseImportJobId(string parameters, Func<string, string?> alias) =>
        ParseGuidParameter("RetrieveFormattedImportJobResults", "ImportJobId", parameters, alias);

    /// <summary>
    /// The one <c>Edm.Guid</c> parameter <paramref name="name"/> of function <paramref name="function"/>
    /// (<c>{name}={guid}</c>): a GUID inline or a <c>@alias</c> whose value <paramref name="alias"/> returns (the query string).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for any other parameter, a missing alias value or a value that is not a GUID.</exception>
    public static Guid ParseGuidParameter(string function, string name, string parameters, Func<string, string?> alias)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alias);
        var match = SingleParameter().Match(parameters);
        if (!match.Success || match.Groups[1].Value != name)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid parameters '{parameters}' for function {function}: expected {name}=<guid>.");
        var value = match.Groups[2].Value;
        if (value.StartsWith('@'))
            value = alias(value) ?? throw XrmdFaultException.InvalidArgument(
                $"Parameter alias '{value}' of function {function} has no value.");
        return System.Guid.TryParseExact(value, "D", out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"Invalid {name} '{value}' for function {function}: a GUID is expected.");
    }

    [GeneratedRegex(@"\A\s*(\w+)\s*=\s*(\S+?)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SingleParameter();

    // ---- $metadata ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Declares the operations in <paramref name="model"/>: an unbound <c>Action</c> or <c>Function</c> with its Learn parameters
    /// and an <c>ActionImport</c>/<c>FunctionImport</c> each, the response complex types, and the parameter types:
    /// <c>crmbaseentity</c> (abstract, open), <c>SolutionParameters</c> (its primitive properties and
    /// <c>PackageHistoryReference</c>), and <c>SolutionComponentOption</c>, <c>LayerDesiredOrder</c>,
    /// <c>ExportComponentsParams</c> as open complex types whose properties are not declared (internal-use types), and
    /// <c>Label</c> with <c>LocalizedLabel</c> (<see cref="LabelType"/>).
    /// </summary>
    /// <param name="entityTypes">The entity types of the model by logical name (for <c>solutioncomponent</c>).</param>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> entityTypes)
    {
        var core = EdmCoreModel.Instance;
        var crmBase = new EdmEntityType(Ns, "crmbaseentity", null, isAbstract: true, isOpen: true);
        model.AddElement(crmBase);

        EdmComplexType Open(string name)
        {
            var type = new EdmComplexType(Ns, name, null, isAbstract: false, isOpen: true);
            model.AddElement(type);
            return type;
        }
        var option = Open("SolutionComponentOption");
        var layer = Open("LayerDesiredOrder");
        var exportParams = Open("ExportComponentsParams");
        var solutionParameters = new EdmComplexType(Ns, "SolutionParameters");
        solutionParameters.AddStructuralProperty("DeployMissingPackagesBeforeSolutionImport", EdmPrimitiveTypeKind.Boolean);
        solutionParameters.AddStructuralProperty("SolutionComponentOptions",
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(option, false))));
        solutionParameters.AddStructuralProperty("StageSolutionUploadId", EdmPrimitiveTypeKind.Guid);
        solutionParameters.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo
        {
            Name = "PackageHistoryReference", Target = crmBase, TargetMultiplicity = EdmMultiplicity.ZeroOrOne,
        });
        model.AddElement(solutionParameters);

        var solutionComponent = entityTypes.TryGetValue("solutioncomponent", out var sc) ? sc : crmBase;
        var label = LabelType(model);

        IEdmTypeReference TypeOf(Parameter p) => p.Kind switch
        {
            Kind.Boolean => core.GetBoolean(p.Nullable),
            Kind.Int32 => core.GetInt32(p.Nullable),
            Kind.Int64 => core.GetInt64(p.Nullable),
            Kind.Guid => core.GetGuid(p.Nullable),
            Kind.String => core.GetString(p.Nullable),
            Kind.Binary => core.GetBinary(p.Nullable),
            Kind.StringCollection => new EdmCollectionTypeReference(new EdmCollectionType(core.GetString(false))),
            Kind.EntityCollection => new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(crmBase, false))),
            Kind.SolutionParameters => new EdmComplexTypeReference(solutionParameters, p.Nullable),
            Kind.LayerDesiredOrder => new EdmComplexTypeReference(layer, p.Nullable),
            Kind.ExportComponentsParams => new EdmComplexTypeReference(exportParams, p.Nullable),
            Kind.SolutionComponent => new EdmEntityTypeReference(solutionComponent, p.Nullable),
            Kind.EntityReference => new EdmEntityTypeReference(crmBase, p.Nullable),
            Kind.Label => new EdmComplexTypeReference(label, p.Nullable),
            Kind.Int32Collection => new EdmCollectionTypeReference(new EdmCollectionType(core.GetInt32(false))),
            _ => throw new InvalidOperationException($"Unhandled parameter kind {p.Kind}."),
        };

        foreach (var operation in Operations)
        {
            IEdmTypeReference? returnType = null;
            if (operation.Response is { } responseName)
            {
                var response = new EdmComplexType(Ns, responseName);
                foreach (var property in operation.Properties) response.AddStructuralProperty(property.Name, property.Type, isNullable: true);
                model.AddElement(response);
                returnType = new EdmComplexTypeReference(response, false);
            }

            EdmOperation declared = operation.IsFunction
                ? new EdmFunction(Ns, operation.Name, returnType!, isBound: false, entitySetPathExpression: null, isComposable: false)
                : new EdmAction(Ns, operation.Name, returnType, isBound: false, entitySetPathExpression: null);
            foreach (var parameter in operation.Parameters) declared.AddParameter(parameter.Name, TypeOf(parameter));
            model.AddElement(declared);
            if (declared is EdmFunction function) container.AddFunctionImport(operation.Name, function, null, includeInServiceDocument: false);
            else container.AddActionImport((EdmAction)declared);
        }
    }
    /// <summary>
    /// The complex type <c>Microsoft.Dynamics.CRM.Label</c> (<c>LocalizedLabels</c>, <c>UserLocalizedLabel</c>) and its
    /// <c>LocalizedLabel</c> (<c>Label</c>, <c>LanguageCode</c>, <c>IsManaged</c>, <c>MetadataId</c>,
    /// <c>HasChanged</c>), as Learn declares them; declared here unless the model has them already.
    /// </summary>
    private static IEdmComplexType LabelType(EdmModel model)
    {
        if (model.FindDeclaredType($"{Ns}.Label") is IEdmComplexType declared) return declared;
        if (model.FindDeclaredType($"{Ns}.LocalizedLabel") is not IEdmComplexType localized)
        {
            var type = new EdmComplexType(Ns, "LocalizedLabel");
            type.AddStructuralProperty("Label", EdmPrimitiveTypeKind.String, isNullable: true);
            type.AddStructuralProperty("LanguageCode", EdmPrimitiveTypeKind.Int32, isNullable: false);
            type.AddStructuralProperty("IsManaged", EdmPrimitiveTypeKind.Boolean, isNullable: true);
            type.AddStructuralProperty("MetadataId", EdmPrimitiveTypeKind.Guid, isNullable: true);
            type.AddStructuralProperty("HasChanged", EdmPrimitiveTypeKind.Boolean, isNullable: true);
            model.AddElement(type);
            localized = type;
        }
        var label = new EdmComplexType(Ns, "Label");
        label.AddStructuralProperty("LocalizedLabels",
            new EdmCollectionTypeReference(new EdmCollectionType(new EdmComplexTypeReference(localized, false))));
        label.AddStructuralProperty("UserLocalizedLabel", new EdmComplexTypeReference(localized, true));
        model.AddElement(label);
        return label;
    }
}
