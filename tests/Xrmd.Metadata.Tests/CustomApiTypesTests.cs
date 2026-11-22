using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// The type table of the custom API parameters and response properties (plan 15, Global Constraints): the SDK value type, the
/// Edm type and the default of each value of <c>customapifieldtype</c>.
/// </summary>
public class CustomApiTypesTests
{
    public static TheoryData<CustomApiType, int, Type, string, object?> Cases => new()
    {
        { CustomApiType.Boolean, 0, typeof(bool), "Edm.Boolean", false },
        { CustomApiType.DateTime, 1, typeof(DateTime), "Edm.DateTimeOffset", DateTime.MinValue },
        { CustomApiType.Decimal, 2, typeof(decimal), "Edm.Decimal", 0m },
        { CustomApiType.Entity, 3, typeof(Entity), "mscrm.crmbaseentity", null },
        { CustomApiType.EntityCollection, 4, typeof(EntityCollection), "Collection(mscrm.crmbaseentity)", null },
        { CustomApiType.EntityReference, 5, typeof(EntityReference), "mscrm.crmbaseentity", null },
        { CustomApiType.Float, 6, typeof(double), "Edm.Double", 0d },
        { CustomApiType.Integer, 7, typeof(int), "Edm.Int32", 0 },
        { CustomApiType.Money, 8, typeof(Money), "Edm.Decimal", null },
        { CustomApiType.Picklist, 9, typeof(OptionSetValue), "Edm.Int32", null },
        { CustomApiType.String, 10, typeof(string), "Edm.String", null },
        { CustomApiType.StringArray, 11, typeof(string[]), "Collection(Edm.String)", null },
        { CustomApiType.Guid, 12, typeof(Guid), "Edm.Guid", Guid.Empty },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_type_has_its_value_sdk_type_edm_type_and_default(CustomApiType type, int value, Type sdk, string edm, object? defaultValue)
    {
        ((int)type).Should().Be(value);
        CustomApiTypes.SdkType(type).Should().Be(sdk);
        CustomApiTypes.EdmTypeName(type, null).Should().Be(edm);
        var actual = CustomApiTypes.DefaultValue(type);
        if (defaultValue is null) actual.Should().BeNull();
        else actual.Should().Be(defaultValue).And.BeOfType(sdk);
    }

    [Fact]
    public void The_enum_has_the_thirteen_values_of_the_choice() =>
        Enum.GetValues<CustomApiType>().Select(t => (int)t).Should().Equal(Enumerable.Range(0, 13));

    [Theory]
    [InlineData(CustomApiType.Entity, "mscrm.account")]
    [InlineData(CustomApiType.EntityReference, "mscrm.account")]
    [InlineData(CustomApiType.EntityCollection, "Collection(mscrm.crmbaseentity)")]
    [InlineData(CustomApiType.String, "Edm.String")]
    public void A_logical_entity_name_names_the_entity_type_of_entity_and_reference_only(CustomApiType type, string edm) =>
        CustomApiTypes.EdmTypeName(type, "account").Should().Be(edm);

    [Fact]
    public void An_out_of_range_type_is_refused()
    {
        var act = () => CustomApiTypes.SdkType((CustomApiType)13);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
