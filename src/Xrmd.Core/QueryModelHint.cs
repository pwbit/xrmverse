using System.Runtime.CompilerServices;
using Xrmd.Model.Messages;
using Xrmd.Query.Model;

namespace Xrmd.Core;

/// <summary>
/// The query a front end built as a <see cref="QueryModel"/> for a RetrieveMultiple, kept beside the model request (S.2e3b, Decision
/// 6): the adapter reads it from the request's SDK twin (<c>RetrieveMultipleModelRequest</c>, which stays SDK until S.2e3c) and
/// attaches it here, so the handler on the model finds it without the SDK type.
/// </summary>
internal static class QueryModelHint
{
    private static readonly ConditionalWeakTable<Request, QueryModel> Models = new();

    /// <summary>Attaches <paramref name="model"/> to <paramref name="request"/>.</summary>
    public static void Attach(Request request, QueryModel model)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        Models.AddOrUpdate(request, model);
    }

    /// <summary>The query attached to <paramref name="request"/>, or null.</summary>
    public static QueryModel? Of(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Models.TryGetValue(request, out var model) ? model : null;
    }
}
