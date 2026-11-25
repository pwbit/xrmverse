namespace Xrmd.Model.Serialization;

/// <summary>
/// The names, namespaces and type hints of the contract XML of the remote context and of queries, as the SDK's data-contract
/// serializer writes them (learned from its output, see tests/fixtures/remote-context-golden). Shared by the writer and the
/// reader.
/// </summary>
internal static class ContractNames
{
    /// <summary>The default namespace of the payload: the namespace of every contract type of the context and of the queries.</summary>
    public const string Contracts = "http://schemas.microsoft.com/xrm/2011/Contracts";

    /// <summary>The namespace of the collection types that carry a key attribute list (<c>KeyAttributeCollection</c>).</summary>
    public const string KeyAttributes = "http://schemas.microsoft.com/xrm/7.1/Contracts";

    /// <summary>The namespace of <c>OptionSetValueCollection</c>.</summary>
    public const string Choices = "http://schemas.microsoft.com/xrm/9.0/Contracts";

    public const string XmlSchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The XML Schema namespace of the primitive type hints (<c>string</c>, <c>int</c>, <c>dateTime</c>, ...).</summary>
    public const string XmlSchema = "http://www.w3.org/2001/XMLSchema";

    /// <summary>The data-contract namespace of <c>guid</c>.</summary>
    public const string DataContractSerialization = "http://schemas.microsoft.com/2003/10/Serialization/";

    /// <summary>The data-contract namespace of the primitive arrays (<c>ArrayOfstring</c>, the <c>string</c> items, <c>anyType</c> values).</summary>
    public const string Arrays = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    /// <summary>The namespace of the <c>key</c> and <c>value</c> members of a dictionary entry.</summary>
    public const string Generic = "http://schemas.datacontract.org/2004/07/System.Collections.Generic";

    /// <summary>The namespace the query serializer gives the collection types it does not know (CLR namespace of the SDK).</summary>
    public const string ClrNamespace = "Microsoft.Xrm.Sdk";

    // Type hints of values (i:type) in the default namespace.
    public const string EntityType = "Entity";
    public const string EntityReferenceType = "EntityReference";
    public const string EntityCollectionType = "EntityCollection";
    public const string EntityReferenceCollectionType = "EntityReferenceCollection";
    public const string OptionSetValueType = "OptionSetValue";
    public const string MoneyType = "Money";
    public const string AliasedValueType = "AliasedValue";
    public const string ColumnSetType = "ColumnSet";
    public const string RelationshipType = "Relationship";
    public const string QueryExpressionType = "QueryExpression";
    public const string QueryByAttributeType = "QueryByAttribute";
    public const string FetchExpressionType = "FetchExpression";
    public const string ArrayOfEntityType = "ArrayOfEntity";
    public const string ArrayOfEntityReferenceType = "ArrayOfEntityReference";
    public const string ArrayOfOptionSetValueType = "ArrayOfOptionSetValue";
    public const string ArrayOfMoneyType = "ArrayOfMoney";

    // Dictionary entry element names (the item name the data contract derives from the key and value types).
    public const string ParameterEntry = "KeyValuePairOfstringanyType";
    public const string FormattedValueEntry = "KeyValuePairOfstringstring";
    public const string ImageEntry = "KeyValuePairOfstringEntitygUGIFE1S";
    public const string RelatedEntitiesEntry = "KeyValuePairOfRelationshipEntityCollectionX_PsK4FkN";
}
