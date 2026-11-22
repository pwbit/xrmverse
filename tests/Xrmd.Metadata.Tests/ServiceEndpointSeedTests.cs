using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 3.2, task 1: the embedded seed definition of <c>serviceendpoint</c>, the polymorphic <c>sdkmessageprocessingstep.eventhandler</c>
/// and the schema constants (no database).
/// </summary>
public class ServiceEndpointSeedTests
{
    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static AttributeMetadata Column(EntityMetadata e, string name) => e.Attributes.Single(a => a.LogicalName == name);

    private static IEnumerable<(int, string)> Options(EntityMetadata e, string name)
    {
        var options = ((EnumAttributeMetadata)Column(e, name)).OptionSet!;
        options.IsGlobal.Should().BeTrue(name);
        options.Name.Should().Be($"serviceendpoint_{name}");
        return options.Options.Select(o => (o.Value!.Value, o.Label.UserLocalizedLabel.Label));
    }

    [Fact]
    public void Seed_creates_serviceendpoint()
    {
        var endpoint = Seed(ServiceEndpointSchema.Table);
        (endpoint.LogicalName, endpoint.EntitySetName, endpoint.ObjectTypeCode, endpoint.OwnershipType, endpoint.PrimaryNameAttribute)
            .Should().Be(("serviceendpoint", "serviceendpoints", 4618, OwnershipTypes.OrganizationOwned, "name"));
        ServiceEndpointSchema.ObjectTypeCode.Should().Be(4618);
        (endpoint.IsAuditEnabled.Value, endpoint.IsAuditEnabled.CanBeChanged).Should().Be((false, false));

        Options(endpoint, "contract").Should().Equal(
            (1, "OneWay"), (2, "Queue"), (3, "Rest"), (4, "TwoWay"), (5, "Topic"), (6, "Queue (Persistent)"), (7, "Event Hub"),
            (8, "Webhook"), (9, "Event Grid"), (10, "Managed Data Lake"), (11, "Container Storage"));
        Options(endpoint, "authtype").Should().Equal(
            (0, "Not Specified"), (1, "ACS"), (2, "SAS Key"), (3, "SAS Token"), (4, "Webhook Key"), (5, "Http Header"),
            (6, "Http Query String"), (7, "Connection String"), (8, "Access Key"), (9, "Managed Identity"));
        Options(endpoint, "messageformat").Should().Equal((1, "Binary XML"), (2, "Json"), (3, "Text XML"));
        Options(endpoint, "messagecharset").Should().Equal((0, "Default"), (1, "UTF8"), (2, "Windows1252"));
        Options(endpoint, "connectionmode").Should().Equal((1, "Normal"), (2, "Federated"));
        Options(endpoint, "userclaim").Should().Equal((1, "None"), (2, "UserId"), (3, "UserInfo"));

        var authValue = (StringAttributeMetadata)Column(endpoint, ServiceEndpointSchema.AuthValue);
        (authValue.MaxLength, authValue.IsValidForRead).Should().Be((2048, false));
        var isSet = (BooleanAttributeMetadata)Column(endpoint, ServiceEndpointSchema.IsAuthValueSet);
        (isSet.IsValidForCreate, isSet.IsValidForUpdate, isSet.DefaultValue).Should().Be((false, false, false));

        ((StringAttributeMetadata)Column(endpoint, "name")).MaxLength.Should().Be(256);
        Column(endpoint, "name").RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        ((MemoAttributeMetadata)Column(endpoint, "description")).MaxLength.Should().Be(2000);
        foreach (var (name, length) in new[] { ("url", 2000), ("namespaceaddress", 255), ("path", 160), ("solutionnamespace", 160), ("saskeyname", 256) })
            ((StringAttributeMetadata)Column(endpoint, name)).MaxLength.Should().Be(length, name);
        foreach (var unmodelled in new[] { "saskey", "sastoken", "keyvaultreferenceid", "managedidentityid", "usekeyvaultconfiguration",
                     "schematype", "runtimeintegrationproperties", "introducedversion", "iscustomizable" })
            endpoint.Attributes.Should().NotContain(a => a.LogicalName == unmodelled, unmodelled);
    }

    [Fact]
    public void Eventhandler_is_polymorphic()
    {
        var step = Seed("sdkmessageprocessingstep");
        var eventHandler = (LookupAttributeMetadata)Column(step, "eventhandler");
        eventHandler.Targets.Should().Equal("plugintype", "serviceendpoint");

        var relationships = step.ManyToOneRelationships.ToDictionary(r => r.SchemaName);
        var type = relationships["plugintype_sdkmessageprocessingstep"];
        (type.ReferencedEntity, type.ReferencingAttribute, type.ReferencingEntityNavigationPropertyName)
            .Should().Be(("plugintype", "eventhandler", "eventhandler_plugintype"));
        var endpoint = relationships["serviceendpoint_sdkmessageprocessingstep"];
        (endpoint.ReferencedEntity, endpoint.ReferencingAttribute, endpoint.ReferencingEntityNavigationPropertyName)
            .Should().Be(("serviceendpoint", "eventhandler", "eventhandler_serviceendpoint"));
        endpoint.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
    }

    [Fact]
    public void Schema_constants_are_the_plan_values()
    {
        (ServiceEndpointSchema.Table, ServiceEndpointSchema.ObjectTypeCode).Should().Be(("serviceendpoint", 4618));
        ServiceEndpointSchema.WebhookContract.Should().Be(8);
        (ServiceEndpointSchema.WebhookKey, ServiceEndpointSchema.HttpHeader, ServiceEndpointSchema.HttpQueryString).Should().Be((4, 5, 6));
        (ServiceEndpointSchema.AuthValue, ServiceEndpointSchema.IsAuthValueSet).Should().Be(("authvalue", "isauthvalueset"));
    }
}
