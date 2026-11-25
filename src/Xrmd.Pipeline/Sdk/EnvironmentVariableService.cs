using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Metadata;

namespace Xrmd.Pipeline;

/// <summary>
/// The <see cref="IEnvironmentVariableService"/> handed to plug-ins (#167, ruling E5). It reads with the organization service
/// of the SYSTEM user, so a plug-in gets the variable whatever its caller may read: the current value
/// (<c>environmentvariablevalue.value</c>), else the definition's <c>defaultvalue</c>, else null.
/// </summary>
/// <remarks>
/// A secret variable (type Secret) lives in Azure Key Vault on Dataverse, which xrmverse does not replicate: reading one fails
/// explicitly instead of returning its stored text. The 1.2.27 SDK reference declares only
/// <see cref="IEnvironmentVariableService.RetrieveEnvironmentVariableValue"/>, so that is where the refusal happens.
/// </remarks>
internal sealed class EnvironmentVariableService(IOrganizationService system) : IEnvironmentVariableService
{
    private const string ValueAlias = "v";

    /// <summary>The message of the failure for a secret variable.</summary>
    internal const string SecretMessage = "Secret environment variables need Azure Key Vault, which xrmverse does not support.";

    /// <inheritdoc />
    /// <exception cref="InvalidPluginExecutionException">No definition has that schema name, or the variable is a secret.</exception>
    public string? RetrieveEnvironmentVariableValue(string environmentVariableSchemaName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableSchemaName)) throw NotFound(environmentVariableSchemaName);

        // One query: the definition by schema name, with its value (if any) through an outer link.
        var query = new QueryExpression(EnvironmentVariableSchema.Definition)
        {
            ColumnSet = new ColumnSet(EnvironmentVariableSchema.DefaultValue, EnvironmentVariableSchema.Type),
            TopCount = 1,
        };
        query.Criteria.AddCondition(EnvironmentVariableSchema.SchemaName, ConditionOperator.Equal, environmentVariableSchemaName);
        var link = query.AddLink(EnvironmentVariableSchema.Value, EnvironmentVariableSchema.Definition + "id",
            EnvironmentVariableSchema.DefinitionLookup, JoinOperator.LeftOuter);
        link.EntityAlias = ValueAlias;
        link.Columns = new ColumnSet(EnvironmentVariableSchema.ValueColumn);

        var definition = system.RetrieveMultiple(query).Entities.FirstOrDefault()
            ?? throw NotFound(environmentVariableSchemaName);
        if (definition.GetAttributeValue<OptionSetValue>(EnvironmentVariableSchema.Type)?.Value == (int)VariableType.Secret)
            throw new InvalidPluginExecutionException(SecretMessage);

        var value = definition.GetAttributeValue<AliasedValue>(ValueAlias + "." + EnvironmentVariableSchema.ValueColumn)?.Value as string;
        return value ?? definition.GetAttributeValue<string>(EnvironmentVariableSchema.DefaultValue);
    }

    private static InvalidPluginExecutionException NotFound(string? name) =>
        new($"Environment variable '{name}' was not found.");
}
