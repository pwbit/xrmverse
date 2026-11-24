using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Query.Cosmos;

/// <summary>
/// The SDK twin of <see cref="CosmosQueryTranslator.Translate(CosmosQuery, Xrmd.Model.Metadata.TableDefinition, IReadOnlyDictionary{string, object?}, int?, string?)"/>
/// (S.2c, R1, R2): the table becomes its model (a view of the store is looked up, any other object converted afresh). Transitional:
/// removed with the SDK view (S.2i).
/// </summary>
public static partial class CosmosQueryTranslator
{
    /// <param name="parameters">Normalized: keys with the leading <c>@</c>, values of a ruling J6 type.</param>
    /// <exception cref="XrmdFaultException">400 for every construct, value or cookie that cannot be translated.</exception>
    public static CosmosQueryPlan Translate(CosmosQuery query, EntityMetadata table, IReadOnlyDictionary<string, object?> parameters,
        int? pageSize, string? pagingCookie) =>
        Translate(query, table?.ToModel()!, parameters, pageSize, pagingCookie);
}
