using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Query.Model;

namespace Xrmd.Core;

/// <summary>
/// A RetrieveMultiple whose query a front end (the Web API) already built as a <see cref="QueryModel"/>. Plug-ins see
/// <c>Query</c> as a <see cref="QueryExpression"/> naming only the entity; the core runs <see cref="Model"/> unless a
/// plug-in replaced <c>Query</c>.
/// </summary>
/// <remarks>
/// Only replacing <c>Query</c> is honoured: in-place edits of the placeholder (criteria, columns, orders...) are ignored,
/// since the placeholder does not carry the Web API's filters and falling back to it would drop them.
/// </remarks>
internal sealed class RetrieveMultipleModelRequest : OrganizationRequest
{
    private readonly QueryExpression _placeholder;

    public RetrieveMultipleModelRequest(QueryModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        RequestName = "RetrieveMultiple";
        _placeholder = new QueryExpression(model.Root.EntityName) { ColumnSet = new ColumnSet(model.Root.AllColumns) };
        Parameters["Query"] = _placeholder;
    }

    /// <summary>The query to run.</summary>
    public QueryModel Model { get; }

    /// <summary>True while <c>Query</c> is still the placeholder (no plug-in replaced it).</summary>
    public bool UsesModel => Parameters.TryGetValue("Query", out var query) && ReferenceEquals(query, _placeholder);

    /// <summary>
    /// S.2e3c3b1 (Decision 5; S.2e3b Decision 6, moved from the core's entry adapter): attaches the query model a front end built
    /// (<see cref="QueryModelHint.Attach"/>) to the model RetrieveMultiple <paramref name="request"/> while its SDK twin in the request's
    /// mirror (<see cref="SdkMirror.For"/>) is one of these still holding its placeholder (<see cref="UsesModel"/>); nothing otherwise.
    /// </summary>
    internal static void AttachHint(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (SdkMirror.For(request) is { } mirror && mirror.TryGetSdk(request, out var twin)
            && twin is RetrieveMultipleModelRequest { UsesModel: true } withModel)
            QueryModelHint.Attach(request, withModel.Model);
    }
}
