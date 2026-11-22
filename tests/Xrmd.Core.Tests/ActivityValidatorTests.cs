using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Core.Validation;

namespace Xrmd.Core.Tests;

/// <summary>
/// Plan 2.11: the note body is refused until Task 5 (it has no SQL column); the party lists (A8, A9) and the EntityName columns
/// (A7) are checked before any database work.
/// </summary>
public sealed class ActivityValidatorTests
{
    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static IReadOnlyDictionary<string, AttributeMetadata> Attrs(string entity, params AttributeMetadata[] attributes)
    {
        foreach (var a in attributes) Set(a, nameof(AttributeMetadata.EntityLogicalName), entity);
        return attributes.ToDictionary(a => a.LogicalName);
    }

    public static TheoryData<object?, bool> Values => new() { { "aGVsbG8=", true }, { "aGVsbG8=", false }, { null, true }, { null, false } };

    /// <summary>
    /// Plan 2.11, task 5 (A16): the note body is checked as any String (the Core decodes and stores it in the file store), so
    /// base64 text and null pass the validator, and a value of another type is refused.
    /// </summary>
    [Theory]
    [MemberData(nameof(Values))]
    public void Annotation_documentbody_is_validated_as_a_string(object? value, bool isCreate)
    {
        var attrs = Attrs("annotation", new StringAttributeMetadata { LogicalName = "documentbody", MaxLength = 1073741823 });
        var validate = () => EntityValidator.Validate(new Entity("annotation") { ["documentbody"] = value },
            new EntityMetadata { LogicalName = "annotation" }, attrs, isCreate);
        validate.Should().NotThrow();

        var wrongType = () => EntityValidator.Validate(new Entity("annotation") { ["documentbody"] = new byte[] { 1, 2 } },
            new EntityMetadata { LogicalName = "annotation" }, attrs, isCreate);
        wrongType.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain("documentbody");
    }

    private static LookupAttributeMetadata PartyList(string name)
    {
        var a = new LookupAttributeMetadata { LogicalName = name, Targets = ["account", "contact", "systemuser"] };
        Set(a, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.PartyList);
        return a;
    }

    private static readonly EntityMetadata Email = new() { LogicalName = "email", IsActivity = true };
    private static readonly EntityMetadata PhoneCall = new() { LogicalName = "phonecall", IsActivity = true };

    private static IReadOnlyDictionary<string, AttributeMetadata> EmailAttrs(string entity = "email")
    {
        var regarding = new LookupAttributeMetadata { LogicalName = "regardingobjectid", Targets = ["account", "contact"] };
        var regardingType = new EntityNameAttributeMetadata { LogicalName = "regardingobjecttypecode" };
        Set(regardingType, nameof(AttributeMetadata.AttributeOf), "regardingobjectid");
        var typeCode = new EntityNameAttributeMetadata { LogicalName = "activitytypecode", IsValidForCreate = false, IsValidForUpdate = false };
        return Attrs(entity, PartyList("from"), PartyList("to"), regarding, regardingType, typeCode);
    }

    private static Entity Party(string table, Guid? id = null) => new("activityparty") { ["partyid"] = new EntityReference(table, id ?? Guid.NewGuid()) };

    private static XrmdFaultException Refused(Entity e, EntityMetadata m, bool isCreate = true)
    {
        var validate = () => EntityValidator.Validate(e, m, EmailAttrs(m.LogicalName), isCreate);
        var fault = validate.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        return fault;
    }

    private static void Accepted(Entity e, EntityMetadata m, bool isCreate = true) =>
        ((Action)(() => EntityValidator.Validate(e, m, EmailAttrs(m.LogicalName), isCreate))).Should().NotThrow();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Party_list_takes_an_entitycollection_of_activityparty(bool isCreate)
    {
        Accepted(new Entity("email") { ["to"] = new EntityCollection([Party("contact"), Party("account")]) }, Email, isCreate);
        Accepted(new Entity("email") { ["to"] = null }, Email, isCreate);
        Accepted(new Entity("email") { ["to"] = new EntityCollection() }, Email, isCreate);
        Refused(new Entity("email") { ["to"] = new EntityReference("contact", Guid.NewGuid()) }, Email, isCreate).Message.Should().Contain("'to'");
        Refused(new Entity("email") { ["to"] = new EntityCollection([new Entity("contact")]) }, Email, isCreate).Message.Should().Contain("activityparty");
    }

    [Fact]
    public void Party_rules()
    {
        Refused(new Entity("email") { ["to"] = new EntityCollection([Party("pb_project")]) }, Email).Message.Should().Contain("pb_project");
        Refused(new Entity("email") { ["from"] = new EntityCollection([Party("contact"), Party("contact")]) }, Email).Message.Should().Contain("'from'");
        foreach (var mask in new[] { 8, 9 })
            Refused(new Entity("email")
            {
                ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["participationtypemask"] = new OptionSetValue(mask) }]),
            }, Email).Message.Should().Contain("participationtypemask");
        // A mask other than the column's type.
        Refused(new Entity("email")
        {
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["participationtypemask"] = new OptionSetValue(3) }]),
        }, Email);
        // The column's own type is accepted.
        Accepted(new Entity("email")
        {
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["participationtypemask"] = new OptionSetValue(2) }]),
        }, Email);
        // Neither a party nor an address.
        Refused(new Entity("email") { ["to"] = new EntityCollection([new Entity("activityparty")]) }, Email);
        // An unresolved address: email only.
        var unresolved = new Entity("activityparty") { ["addressused"] = "someone@example.com" };
        Accepted(new Entity("email") { ["to"] = new EntityCollection([unresolved]) }, Email);
        Refused(new Entity("phonecall") { ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "someone@example.com" }]) }, PhoneCall)
            .Message.Should().Contain("addressused");
        Refused(new Entity("email") { ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = new string('a', 321) }]) }, Email);
        Refused(new Entity("email") { ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["nosuchcolumn"] = 1 }]) }, Email)
            .Message.Should().Contain("nosuchcolumn");
        // A7 inside a party: partyobjecttypecode, when sent, names the party's table.
        Accepted(new Entity("email")
        {
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["partyobjecttypecode"] = "contact" }]),
        }, Email);
        Refused(new Entity("email")
        {
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", Guid.NewGuid()), ["partyobjecttypecode"] = "account" }]),
        }, Email).Message.Should().Contain("partyobjecttypecode");
        Refused(new Entity("email")
        {
            ["to"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "a@example.com", ["partyobjecttypecode"] = "contact" }]),
        }, Email).Message.Should().Contain("partyobjecttypecode");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Entityname_companion_must_name_the_lookup_target(bool isCreate)
    {
        var account = new EntityReference("account", Guid.NewGuid());
        Accepted(new Entity("email") { ["regardingobjectid"] = account, ["regardingobjecttypecode"] = "account" }, Email, isCreate);
        Refused(new Entity("email") { ["regardingobjectid"] = account, ["regardingobjecttypecode"] = "contact" }, Email, isCreate)
            .Message.Should().Contain("regardingobjecttypecode");
        Refused(new Entity("email") { ["regardingobjectid"] = null, ["regardingobjecttypecode"] = "account" }, Email, isCreate);
        Accepted(new Entity("email") { ["regardingobjectid"] = null, ["regardingobjecttypecode"] = null }, Email, isCreate);
        Refused(new Entity("email") { ["regardingobjectid"] = account, ["regardingobjecttypecode"] = 1 }, Email, isCreate);
    }

    [Fact]
    public void Companion_without_its_lookup_on_create_must_be_null()
    {
        Refused(new Entity("email") { ["regardingobjecttypecode"] = "account" }, Email);
        Accepted(new Entity("email") { ["regardingobjecttypecode"] = null }, Email);
        // On update the stored target decides (the Core reads it).
        Accepted(new Entity("email") { ["regardingobjecttypecode"] = "account" }, Email, isCreate: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Activitytypecode_must_name_the_table(bool isCreate)
    {
        Accepted(new Entity("email") { ["activitytypecode"] = "email" }, Email, isCreate);
        Refused(new Entity("email") { ["activitytypecode"] = "task" }, Email, isCreate).Message.Should().Contain("activitytypecode");
    }
}
