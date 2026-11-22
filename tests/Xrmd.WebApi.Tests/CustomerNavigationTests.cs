using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.WebApi;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The Web API view of a Customer column: one single-valued navigation property per target (and one for any other
/// lookup, <c>ownerid</c> included) in <c>$metadata</c>, the annotation of a read, and the strict
/// <c>@odata.bind</c> (one navigation property, the right table, never the bare name).
/// </summary>
public class CustomerNavigationTests
{
    private const string BaseUrl = "https://org.example";
    private static readonly Guid AccountId = Guid.Parse("11111111-2a8e-4b8e-9d55-3c1a1b2c3d4e");
    private static readonly Guid ContactId = Guid.Parse("22222222-2a8e-4b8e-9d55-3c1a1b2c3d4e");

    private static EntityMetadata Entity(string logicalName, string setName, params AttributeMetadata[] attributes)
    {
        var e = new EntityMetadata { LogicalName = logicalName, EntitySetName = setName, OwnershipType = OwnershipTypes.UserOwned };
        e.SetAttributes(SystemAttributes.Complete(e, attributes));
        return e;
    }

    private static LookupAttributeMetadata Customer(string name)
    {
        var customer = new LookupAttributeMetadata { LogicalName = name, SchemaName = name, Targets = ["account", "contact"] };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(customer, AttributeTypeCode.Customer);
        return customer;
    }

    private static readonly EntityMetadata Account = Entity("account", "accounts", new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 });

    private static readonly EntityMetadata Contact = Entity("contact", "contacts",
        new StringAttributeMetadata { LogicalName = "lastname", MaxLength = 50 }, Customer("parentcustomerid"));

    private static readonly EntityMetadata Deal = Entity("pb_deal", "pb_deals",
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 100 }, Customer("pb_customerid"),
        new LookupAttributeMetadata { LogicalName = "pb_owner2", Targets = ["account", "contact"] });

    private static readonly EntityMetadata SystemUser = Entity("systemuser", "systemusers", new StringAttributeMetadata { LogicalName = "fullname", MaxLength = 200 });

    private static readonly FakeStore Store = new(Account, Contact, Deal, SystemUser);

    private static OneToManyRelationshipMetadata Relationship(string entity, string column, string target, string navigation) => new()
    {
        SchemaName = $"{entity}_{column}_{target}", ReferencingEntity = entity, ReferencingAttribute = column, ReferencedEntity = target,
        ReferencingEntityNavigationPropertyName = navigation,
    };

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    private static Entity FromJson(string json) => ODataJson.FromJson(Body(json), Contact, Store);

    private static string Rejected(string json) => ((Action)(() => FromJson(json))).Should().Throw<XrmdFaultException>().Which.Message;

    [Fact]
    public void Metadata_lists_one_nav_per_customer_target()
    {
        var model = new ODataEdmModelBuilder(Store, TestCustomApis.None()).Build();
        var contact = (IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.contact");

        var navigations = contact.NavigationProperties().Select(n => (n.Name, Target: n.ToEntityType().Name)).ToList();
        navigations.Where(n => n.Name.StartsWith("parentcustomerid")).Should().BeEquivalentTo(
            [("parentcustomerid_account", "account"), ("parentcustomerid_contact", "contact")]);
        contact.FindProperty("parentcustomerid").Should().BeNull();
        contact.StructuralProperties().Count(p => p.Name.StartsWith("_parentcustomerid")).Should().Be(1);
        // One navigation property for every other lookup, the owner included.
        navigations.Where(n => n.Name == "ownerid").Should().ContainSingle();
        var deal = (IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.pb_deal");
        deal.NavigationProperties().Select(n => n.Name).Where(n => n.Contains("customerid") || n.Contains("owner2"))
            .Should().BeEquivalentTo("pb_customerid_account", "pb_customerid_contact", "pb_owner2");
        // Each is bound to its table's entity set.
        var set = model.EntityContainer.FindEntitySet("contacts");
        set.NavigationPropertyBindings.Select(b => (b.NavigationProperty.Name, b.Target.Name)).Should().Contain(
            [("parentcustomerid_account", "accounts"), ("parentcustomerid_contact", "contacts")]);
    }

    [Fact]
    public void A_relationship_names_the_navigation_property_of_its_target()
    {
        var store = new FakeStore(Account, Contact, Deal, SystemUser);
        store.OneToMany.AddRange([
            Relationship("contact", "parentcustomerid", "account", "Customer_Account"),
            Relationship("contact", "parentcustomerid", "contact", "Customer_Contact")]);
        var model = new ODataEdmModelBuilder(store, TestCustomApis.None()).Build();

        var contact = (IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.contact");
        contact.NavigationProperties().Where(n => n.Name.StartsWith("Customer_")).Select(n => (n.Name, n.ToEntityType().Name))
            .Should().BeEquivalentTo([("Customer_Account", "account"), ("Customer_Contact", "contact")]);
        ODataJson.FromJson(Body($$"""{ "Customer_Contact@odata.bind": "/contacts({{ContactId}})" }"""), Contact, store)["parentcustomerid"]
            .Should().BeEquivalentTo(new EntityReference("contact", ContactId));
    }

    [Fact]
    public void Bind_accepts_one_nav_per_target()
    {
        var onAccount = FromJson($$"""{ "lastname": "A", "parentcustomerid_account@odata.bind": "/accounts({{AccountId}})" }""");
        onAccount["parentcustomerid"].Should().BeEquivalentTo(new EntityReference("account", AccountId));
        var onContact = FromJson($$"""{ "parentcustomerid_contact@odata.bind": "{{BaseUrl}}/api/data/v9.2/contacts({{ContactId}})" }""");
        onContact["parentcustomerid"].Should().BeEquivalentTo(new EntityReference("contact", ContactId));
        FromJson("""{ "parentcustomerid_account@odata.bind": null }""")["parentcustomerid"].Should().BeNull();
    }

    [Fact]
    public void Bind_rejects_both_navs_the_bare_name_and_the_wrong_table()
    {
        Rejected($$"""{ "parentcustomerid_account@odata.bind": "/accounts({{AccountId}})", "parentcustomerid_contact@odata.bind": "/contacts({{ContactId}})" }""")
            .Should().Contain("parentcustomerid_account").And.Contain("parentcustomerid_contact").And.Contain("both");
        Rejected($$"""{ "parentcustomerid_contact@odata.bind": null, "parentcustomerid_account@odata.bind": "/accounts({{AccountId}})" }""")
            .Should().Contain("both");
        Rejected($$"""{ "parentcustomerid@odata.bind": "/accounts({{AccountId}})" }""")
            .Should().Contain("'parentcustomerid_account@odata.bind'").And.Contain("'parentcustomerid_contact@odata.bind'");
        Rejected($$"""{ "parentcustomerid_account@odata.bind": "/contacts({{ContactId}})" }""")
            .Should().Contain("'account'").And.Contain("'contact'");
        Rejected($$"""{ "parentcustomerid_contact@odata.bind": "/accounts({{AccountId}})" }""")
            .Should().Contain("'contact'").And.Contain("'account'");
        Rejected($$"""{ "ParentCustomerId_Account@odata.bind": "/accounts({{AccountId}})" }""").Should().Contain("not a single-valued navigation property");
        Rejected($$"""{ "_parentcustomerid_value": "{{AccountId}}" }""").Should().Contain("parentcustomerid_account@odata.bind").And.Contain("parentcustomerid_contact@odata.bind");
        Rejected($$"""{ "parentcustomerid": "{{AccountId}}" }""").Should().Contain("parentcustomerid_account@odata.bind").And.Contain("parentcustomerid_contact@odata.bind");
    }

    [Fact]
    public void Other_lookups_keep_their_single_nav_and_message()
    {
        var onOwner = ODataJson.FromJson(Body($$"""{ "pb_owner2@odata.bind": "/contacts({{ContactId}})" }"""), Deal, Store);
        onOwner["pb_owner2"].Should().BeEquivalentTo(new EntityReference("contact", ContactId));
        var bare = ((Action)(() => ODataJson.FromJson(Body($$"""{ "pb_owner2": "{{ContactId}}" }"""), Deal, Store))).Should().Throw<XrmdFaultException>().Which.Message;
        bare.Should().Be("'pb_owner2' is a navigation property: set it with 'pb_owner2@odata.bind': \"/<entityset>(<id>)\".");
    }

    [Fact]
    public void A_read_names_the_navigation_property_of_the_stored_target()
    {
        foreach (var (target, id, nav) in new[] { ("account", AccountId, "parentcustomerid_account"), ("contact", ContactId, "parentcustomerid_contact") })
        {
            var record = new Entity("contact", Guid.NewGuid()) { ["parentcustomerid"] = new EntityReference(target, id) };
            var json = ODataJson.ToJson(record, Contact, BaseUrl);
            json["_parentcustomerid_value"]!.GetValue<string>().Should().Be(id.ToString());
            json["_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be(target);
            json["_parentcustomerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be(nav);
        }
    }
}
