namespace Xrmd.Metadata;

/// <summary>
/// The seeded custom API tables (<c>fixtures/seed/customapi.json</c>, <c>customapirequestparameter.json</c> and
/// <c>customapiresponseproperty.json</c>, plan 15): their names, object type codes, columns, relationships and choices.
/// </summary>
/// <remarks>
/// The object type codes are xrmverse's own. Dataverse gives these tables a code of 10000 or more that differs from one
/// environment to another, so no client can rely on a fixed value; these sit below the custom range, which
/// <c>MetadataStore</c> allocates from 10000. The relationship names, the choice <c>customapifieldtype</c> and the navigation
/// property names are from memory: the plan 15 checklist verifies them on a real environment.
/// </remarks>
public static class CustomApiTables
{
    /// <summary>A custom API: a message defined by a solution, run by a plug-in type as its main operation.</summary>
    public const string Api = "customapi";

    /// <summary>A request parameter of a custom API.</summary>
    public const string RequestParameter = "customapirequestparameter";

    /// <summary>A response property of a custom API.</summary>
    public const string ResponseProperty = "customapiresponseproperty";

    public const int ApiTypeCode = 9990;
    public const int RequestParameterTypeCode = 9991;
    public const int ResponsePropertyTypeCode = 9992;

    /// <summary>The primary id columns, also the lookup of a parameter or property to its API (<see cref="ApiId"/>).</summary>
    public const string ApiId = "customapiid", RequestParameterId = "customapirequestparameterid",
        ResponsePropertyId = "customapiresponsepropertyid";

    /// <summary>The columns the three tables share.</summary>
    public const string Name = "name", UniqueName = "uniquename", DisplayName = "displayname", Description = "description";

    /// <summary>The columns of <c>customapi</c>.</summary>
    public const string BindingType = "bindingtype", BoundEntityLogicalName = "boundentitylogicalname",
        AllowedCustomProcessingStepType = "allowedcustomprocessingsteptype", IsFunction = "isfunction", IsPrivate = "isprivate",
        WorkflowSdkStepEnabled = "workflowsdkstepenabled", ExecutePrivilegeName = "executeprivilegename",
        PluginTypeId = "plugintypeid", SdkMessageId = "sdkmessageid";

    /// <summary>The columns of the parameters and properties (<see cref="IsOptional"/>: parameters only).</summary>
    public const string Type = "type", IsOptional = "isoptional", LogicalEntityName = "logicalentityname";

    /// <summary>The relationships from an API to its parameters and properties (Delete: Cascade).</summary>
    public const string RequestParametersRelationship = "customapi_customapirequestparameter",
        ResponsePropertiesRelationship = "customapi_customapiresponseproperty";

    /// <summary>The relationships from a plug-in type and a message to the APIs (Delete: Restrict).</summary>
    public const string PluginTypeRelationship = "plugintype_customapi", SdkMessageRelationship = "sdkmessage_customapi";

    /// <summary>The navigation properties of <c>customapi</c> to its parameters and properties.</summary>
    public const string RequestParametersNavigation = "CustomAPIRequestParameters", ResponsePropertiesNavigation = "CustomAPIResponseProperties";

    /// <summary>The global choices of the three tables.</summary>
    public const string BindingTypeChoice = "customapi_bindingtype",
        AllowedCustomProcessingStepTypeChoice = "customapi_allowedcustomprocessingsteptype", FieldTypeChoice = "customapifieldtype";

    /// <summary>The <c>categoryname</c> of the <c>sdkmessage</c> row an API owns (from memory, checked by the checklist).</summary>
    public const string MessageCategory = "CustomAPI";

    /// <summary>The three tables.</summary>
    public static readonly IReadOnlyList<string> All = [Api, RequestParameter, ResponseProperty];
}

/// <summary>The values of the global choice <c>customapifieldtype</c>: the type of a request parameter or response property.</summary>
public enum CustomApiType
{
    Boolean = 0,
    DateTime = 1,
    Decimal = 2,
    Entity = 3,
    EntityCollection = 4,
    EntityReference = 5,
    Float = 6,
    Integer = 7,
    Money = 8,
    Picklist = 9,
    String = 10,
    StringArray = 11,
    Guid = 12,
}
