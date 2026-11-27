using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Model.Security;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Security.Tests;

/// <summary>
/// S.2e3c2a Decision 2: a text that names a value's type keeps the SDK name it shows today, so <see cref="SdkTypeNames.Of"/> gives each
/// model value type the simple name of its SDK counterpart (checked against the SDK type itself), and any other type its own name.
/// </summary>
public class SdkTypeNamesTests
{
    public static TheoryData<Type, Type> Pairs => new()
    {
        { typeof(Record), typeof(Entity) },
        { typeof(RecordReference), typeof(EntityReference) },
        { typeof(RecordCollection), typeof(EntityCollection) },
        { typeof(RecordReferenceCollection), typeof(EntityReferenceCollection) },
        { typeof(ChoiceValue), typeof(OptionSetValue) },
        { typeof(ChoiceValues), typeof(OptionSetValueCollection) },
        { typeof(MoneyValue), typeof(Money) },
        { typeof(ColumnSelection), typeof(ColumnSet) },
        { typeof(RecordQuery), typeof(QueryExpression) },
        { typeof(AttributeQuery), typeof(QueryByAttribute) },
        { typeof(FetchQuery), typeof(FetchExpression) },
        { typeof(RelationshipName), typeof(Relationship) },
        { typeof(AliasedColumnValue), typeof(AliasedValue) },
        { typeof(AccessMask), typeof(AccessRights) },
        { typeof(PrincipalAccessValue), typeof(PrincipalAccess) },
        { typeof(PrivilegeGrant), typeof(RolePrivilege) },
        { typeof(ParameterBag), typeof(ParameterCollection) },
        { typeof(Record[]), typeof(Entity[]) },
        { typeof(RecordReference[]), typeof(EntityReference[]) },
        { typeof(ChoiceValue[]), typeof(OptionSetValue[]) },
        { typeof(MoneyValue[]), typeof(Money[]) },
        { typeof(PrincipalAccessValue[]), typeof(PrincipalAccess[]) },
        { typeof(PrivilegeGrant[]), typeof(RolePrivilege[]) },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Model_types_map_to_the_sdk_names(Type model, Type sdk) => Assert.Equal(sdk.Name, SdkTypeNames.Of(model));

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(object[]))]
    [InlineData(typeof(string[]))]
    public void Other_types_keep_their_own_name(Type type) => Assert.Equal(type.Name, SdkTypeNames.Of(type));

    /// <summary>SDK values a caller can put in a row or a request, each with what its <see cref="object.ToString"/> prints.</summary>
    public static TheoryData<object> SdkValues => new()
    {
        new OptionSetValue(1), new Money(2.5m), new EntityReference("account", Guid.NewGuid()), new Entity("account"),
        new EntityCollection(), new EntityReferenceCollection(), new OptionSetValueCollection(), new AliasedValue("account", "name", "x"),
        new ColumnSet("name"), new QueryExpression("account"), new QueryByAttribute("account"), new FetchExpression("<fetch/>"),
        new Relationship("account_primary_contact"), new Relationship("account_parent_account") { PrimaryEntityRole = EntityRole.Referenced },
        new PrincipalAccess { AccessMask = AccessRights.ReadAccess },
        new RolePrivilege(1, Guid.NewGuid()), AccessRights.ReadAccess | AccessRights.WriteAccess,
        new[] { new OptionSetValue(1) }, new[] { new EntityReference("account", Guid.NewGuid()) }, new object[] { 1 },
        1, 2L, 3.5m, "text", true, Guid.Empty,
    };

    /// <summary>
    /// S.2e3c2a Task 4 (ruling 3a): a text that prints a value the caller sent prints it as today, the SDK object's <see cref="object.ToString"/>,
    /// when the rule reads the model value the mirror gives for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(SdkValues))]
    public void Model_values_print_as_the_sdk_value_the_caller_sent(object sdk) =>
        Assert.Equal(sdk.ToString(), SdkTypeNames.Text(new SdkMirror().In(sdk)!));

    // ---- Fix round 1 (ruling 4b): the cast error texts ----

    /// <summary>
    /// <see cref="SdkTypeNames.SdkValue{T}"/> throws, for a stored value of the wrong type, the text the SDK's
    /// <c>GetAttributeValue&lt;T&gt;</c> throws for the SDK twin of that value (observed here on the SDK itself), with every type the
    /// Security reads ask for (a reference, a choice, a nullable id or flag, a string).
    /// </summary>
    [Fact]
    public void A_wrong_typed_value_throws_the_sdks_cast_text()
    {
        var reference = new EntityReference("account", Guid.NewGuid());
        var choice = new OptionSetValue(1);
        var mirror = new SdkMirror();
        object[] values = [reference, choice, new Money(1m), new EntityCollection(), new[] { choice }, "text", 1, Guid.NewGuid(), true];
        foreach (var value in values)
        {
            var model = mirror.In(value)!;
            Check<EntityReference, RecordReference>(value, model);
            Check<OptionSetValue, ChoiceValue>(value, model);
            Check<Guid?, Guid?>(value, model);
            Check<bool?, bool?>(value, model);
            Check<string, string>(value, model);
        }
    }

    /// <summary>An absent or null value reads as default, and a value of the type asked for reads as itself.</summary>
    [Fact]
    public void An_absent_null_or_right_typed_value_reads_as_the_sdk_read()
    {
        var reference = new RecordReference { LogicalName = "account", Id = Guid.NewGuid() };
        var row = new Record("x") { ["n"] = null, ["r"] = reference };
        row.SdkValue<RecordReference>("absent").Should().BeNull();
        row.SdkValue<RecordReference>("n").Should().BeNull();
        row.SdkValue<Guid?>("n").Should().BeNull();
        row.SdkValue<RecordReference>("r").Should().BeSameAs(reference);
    }

    /// <summary>
    /// The SDK read of <paramref name="sdk"/> as a <typeparamref name="TSdk"/> and the model read of its twin <paramref name="model"/>
    /// as the matching <typeparamref name="TModel"/> either both pass or both throw the same text.
    /// </summary>
    private static void Check<TSdk, TModel>(object sdk, object model)
    {
        string? expected = null, actual = null;
        try { new Entity("x") { ["a"] = sdk }.GetAttributeValue<TSdk>("a"); }
        catch (InvalidCastException ex) { expected = ex.Message; }
        try { new Record("x") { ["a"] = model }.SdkValue<TModel>("a"); }
        catch (InvalidCastException ex) { actual = ex.Message; }
        actual.Should().Be(expected, "{0} read as {1}", sdk.GetType(), typeof(TSdk));
    }
}
