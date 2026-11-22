using Microsoft.OData.Edm;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.WebApi;

/// <summary>
/// Declares the custom APIs (plan 15, C9) in <c>$metadata</c>. Every API that is not private and is not a function without response
/// properties becomes a non-composable <c>Action</c> or <c>Function</c> of <c>Microsoft.Dynamics.CRM</c>:
/// <list type="bullet">
/// <item>bound to its table with a first parameter <c>entity</c> (the table's entity type) or <c>entityset</c> (a collection of it),
/// or unbound with an <c>ActionImport</c> or <c>FunctionImport</c>;</item>
/// <item>with one parameter per request parameter, nullable when optional;</item>
/// <item>returning nothing, the entity or the collection of <c>crmbaseentity</c> of a single Entity or EntityCollection response
/// property (as <see cref="CustomApiJson"/> writes it), or the complex type <c>{name}Response</c> with one property per response property.</item>
/// </list>
/// An Entity or EntityReference value is typed as the entity type of its <c>logicalentityname</c> when the model has it, otherwise
/// as <c>crmbaseentity</c>; an EntityCollection is always a collection of <c>crmbaseentity</c>. Inside a <c>{name}Response</c> an entity
/// or collection property is a navigation property, because a complex type has no entity-typed structural property.
/// An API bound to a table the model does not have is left out: no entity set could reach it. So is an API whose name (or response type
/// name) is already a type or operation of the model, a global API whose name is already an entity set (or another member) of the
/// container, or an API that has a request parameter named like its binding parameter: the model would be invalid. Each of these is logged as a warning naming the API; an unknown <c>logicalentityname</c> is logged too and typed
/// as <c>crmbaseentity</c>. Deviation from C9: an optional parameter of a collection type (StringArray, EntityCollection) is written
/// <c>Nullable="false"</c>, because the CSDL <c>Nullable</c> of a collection describes its elements and cannot say "optional".
/// A single Entity return is nullable, since the API answers 204 when the plug-in leaves it unset.
/// </summary>
internal static class CustomApiOperations
{
    private const string Ns = ODataEdmModelBuilder.Namespace;

    /// <param name="types">The entity types of the model by logical name.</param>
    public static void AddOperations(EdmModel model, EdmEntityContainer container, IReadOnlyDictionary<string, EdmEntityType> types,
        IReadOnlyList<CustomApiDefinition> apis, Action<string> warn)
    {
        var crmBase = (IEdmEntityType)model.FindDeclaredType($"{Ns}.crmbaseentity");

        IEdmEntityType EntityType(CustomApiDefinition api, CustomApiField field)
        {
            if (string.IsNullOrEmpty(field.LogicalEntityName)) return crmBase;
            if (types.TryGetValue(field.LogicalEntityName, out var type)) return type;
            warn($"Custom API '{api.UniqueName}': '{field.UniqueName}' names table '{field.LogicalEntityName}', which is not in the model; "
                + "it is typed as crmbaseentity in $metadata.");
            return crmBase;
        }

        IEdmTypeReference TypeOf(CustomApiDefinition api, CustomApiField field, bool nullable) => field.Type switch
        {
            CustomApiType.Boolean => EdmCoreModel.Instance.GetBoolean(nullable),
            CustomApiType.DateTime => EdmCoreModel.Instance.GetDateTimeOffset(nullable),
            CustomApiType.Decimal or CustomApiType.Money => EdmCoreModel.Instance.GetDecimal(nullable),
            CustomApiType.Entity or CustomApiType.EntityReference => new EdmEntityTypeReference(EntityType(api, field), nullable),
            CustomApiType.EntityCollection => new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(crmBase, false))),
            CustomApiType.Float => EdmCoreModel.Instance.GetDouble(nullable),
            CustomApiType.Integer or CustomApiType.Picklist => EdmCoreModel.Instance.GetInt32(nullable),
            CustomApiType.String => EdmCoreModel.Instance.GetString(nullable),
            CustomApiType.StringArray => new EdmCollectionTypeReference(new EdmCollectionType(EdmCoreModel.Instance.GetString(false))),
            CustomApiType.Guid => EdmCoreModel.Instance.GetGuid(nullable),
            _ => throw new InvalidOperationException($"Unhandled custom API type {field.Type}."),
        };

        bool Taken(string name) => model.FindDeclaredType($"{Ns}.{name}") is not null || model.FindDeclaredOperations($"{Ns}.{name}").Any();

        foreach (var api in apis)
        {
            if (api.IsPrivate || (api.IsFunction && api.ResponseProperties.Count == 0)) continue;
            var bound = api.BindingType is 1 or 2;
            if (bound && (api.BoundEntity is null || !types.ContainsKey(api.BoundEntity)))
            {
                warn($"Custom API '{api.UniqueName}' is bound to table '{api.BoundEntity}', which is not in the model; it is not in $metadata.");
                continue;
            }
            if (Taken(api.UniqueName))
            {
                warn($"Custom API '{api.UniqueName}' has the name of a type or operation of the model; it is not in $metadata.");
                continue;
            }
            // An unbound API adds an import to the container, whose members share one namespace with the entity sets.
            if (!bound && (container.FindEntitySet(api.UniqueName) is not null || container.FindSingleton(api.UniqueName) is not null
                           || container.FindOperationImports(api.UniqueName).Any()))
            {
                warn($"Custom API '{api.UniqueName}' is global and has the name of an entity set of the model; it is not in $metadata.");
                continue;
            }
            var bindingParameter = api.BindingType == 1 ? "entity" : "entityset";
            if (bound && api.RequestParameters.FirstOrDefault(p => string.Equals(p.UniqueName, bindingParameter, StringComparison.OrdinalIgnoreCase)) is { } clash)
            {
                warn($"Custom API '{api.UniqueName}' has a request parameter '{clash.UniqueName}', the name of its binding parameter '{bindingParameter}'; "
                    + "it is not in $metadata.");
                continue;
            }
            var responseName = api.UniqueName + "Response";
            var properties = api.ResponseProperties;
            var needsResponseType = properties.Count > 0 && properties is not [{ Type: CustomApiType.Entity or CustomApiType.EntityCollection }];
            if (needsResponseType && Taken(responseName))
            {
                warn($"Custom API '{api.UniqueName}': its response type name '{responseName}' is taken by a type of the model; it is not in $metadata.");
                continue;
            }

            IEdmTypeReference? returnType = null;
            if (properties is [{ Type: CustomApiType.Entity or CustomApiType.EntityCollection } single])
                returnType = TypeOf(api, single, true); // nullable: an unset property is answered 204
            else if (properties.Count > 0)
            {
                var response = new EdmComplexType(Ns, responseName);
                foreach (var property in properties)
                {
                    if (property.Type is CustomApiType.Entity or CustomApiType.EntityReference or CustomApiType.EntityCollection)
                        response.AddUnidirectionalNavigation(new EdmNavigationPropertyInfo
                        {
                            Name = property.UniqueName,
                            Target = property.Type == CustomApiType.EntityCollection ? crmBase : EntityType(api, property),
                            TargetMultiplicity = property.Type == CustomApiType.EntityCollection ? EdmMultiplicity.Many : EdmMultiplicity.ZeroOrOne,
                        });
                    else response.AddStructuralProperty(property.UniqueName, TypeOf(api, property, true));
                }
                model.AddElement(response);
                returnType = new EdmComplexTypeReference(response, false);
            }

            EdmOperation operation = api.IsFunction
                ? new EdmFunction(Ns, api.UniqueName, returnType!, isBound: bound, entitySetPathExpression: null, isComposable: false)
                : new EdmAction(Ns, api.UniqueName, returnType, isBound: bound, entitySetPathExpression: null);
            if (bound)
            {
                var table = types[api.BoundEntity!];
                operation.AddParameter(bindingParameter, api.BindingType == 1
                    ? new EdmEntityTypeReference(table, false)
                    : new EdmCollectionTypeReference(new EdmCollectionType(new EdmEntityTypeReference(table, false))));
            }
            foreach (var parameter in api.RequestParameters) operation.AddParameter(parameter.UniqueName, TypeOf(api, parameter, parameter.IsOptional));
            model.AddElement(operation);
            if (!bound)
            {
                if (operation is EdmFunction function) container.AddFunctionImport(api.UniqueName, function, null, includeInServiceDocument: false);
                else container.AddActionImport((EdmAction)operation);
            }
        }
    }
}
