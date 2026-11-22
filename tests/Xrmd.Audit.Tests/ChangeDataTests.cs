using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Audit.Tests;

/// <summary>
/// The <c>changedata</c> codec (plan 13, ruling A3): every value type round-trips to the same SDK type without metadata, long
/// strings are capped, and the format carries a version that unknown readers refuse.
/// </summary>
public sealed class ChangeDataTests
{
    private static readonly Guid AccountId = Guid.Parse("0b3f9b7a-2a54-4f6e-8a1e-2b2e6d1c9a01");
    private static readonly Guid UserId = Guid.Parse("6f0e1c2d-3b4a-4c5d-8e9f-a0b1c2d3e4f5");

    private static AttributeChange RoundTrip(AttributeChange change) =>
        ChangeData.Deserialize(ChangeData.Serialize(change)).Should().BeOfType<AttributeChange>().Subject;

    [Fact]
    public void ChangeData_codec_round_trips_every_type()
    {
        var memo = new string('m', 10_000);
        var when = new DateTime(2026, 11, 10, 8, 30, 15, 123, DateTimeKind.Utc);
        var values = new Dictionary<string, object?>
        {
            ["name"] = "Contoso \"quoted\" é",
            ["description"] = memo,
            ["numberofemployees"] = 42,
            ["new_big"] = 9_000_000_000L,
            ["donotemail"] = true,
            ["industrycode"] = new OptionSetValue(7),
            ["new_colors"] = new OptionSetValueCollection([new OptionSetValue(1), new OptionSetValue(3)]),
            ["revenue"] = new Money(1234567.89m),
            ["new_rate"] = 0.1234567890m,
            ["new_ratio"] = 2.5e-7,
            ["new_when"] = when,
            ["new_key"] = AccountId,
            ["primarycontactid"] = new EntityReference("contact", UserId) { Name = "Ada Lovelace" },
            ["new_owner"] = new EntityReference("team", AccountId),
            ["fax"] = null,
        };

        var back = RoundTrip(new AttributeChange(new Dictionary<string, object?> { ["name"] = "Old" }, values));

        back.Old.Should().ContainKey("name").WhoseValue.Should().Be("Old");
        var n = back.New;
        n.Keys.Should().BeEquivalentTo(values.Keys);
        n["name"].Should().Be("Contoso \"quoted\" é");
        n["description"].Should().BeOfType<string>().Which.Should().Be(new string('m', 5000) + "…", "a value over 5000 characters is capped and marked");
        n["numberofemployees"].Should().BeOfType<int>().Which.Should().Be(42);
        n["new_big"].Should().BeOfType<long>().Which.Should().Be(9_000_000_000L);
        n["donotemail"].Should().Be(true);
        n["industrycode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(7);
        n["new_colors"].Should().BeOfType<OptionSetValueCollection>().Which.Select(o => o.Value).Should().Equal(1, 3);
        n["revenue"].Should().BeOfType<Money>().Which.Value.Should().Be(1234567.89m);
        n["new_rate"].Should().BeOfType<decimal>().Which.Should().Be(0.1234567890m);
        n["new_ratio"].Should().BeOfType<double>().Which.Should().Be(2.5e-7);
        var date = n["new_when"].Should().BeOfType<DateTime>().Subject;
        date.Should().Be(when);
        date.Kind.Should().Be(DateTimeKind.Utc);
        date.Millisecond.Should().Be(123);
        n["new_key"].Should().BeOfType<Guid>().Which.Should().Be(AccountId);
        var lookup = n["primarycontactid"].Should().BeOfType<EntityReference>().Subject;
        (lookup.LogicalName, lookup.Id, lookup.Name).Should().Be(("contact", UserId, "Ada Lovelace"));
        var polymorphic = n["new_owner"].Should().BeOfType<EntityReference>().Subject;
        (polymorphic.LogicalName, polymorphic.Id).Should().Be(("team", AccountId));
        polymorphic.Name.Should().BeNull();
        n["fax"].Should().BeNull();
    }

    [Fact]
    public void ChangeData_caps_a_long_value_without_splitting_a_surrogate_pair()
    {
        var text = new string('a', 4999) + "\U0001F600" + "tail";
        var back = RoundTrip(new AttributeChange(new Dictionary<string, object?>(), new Dictionary<string, object?> { ["x"] = text }));
        back.New["x"].Should().Be(new string('a', 4999) + "…");
        // At most 5000 characters are kept: exactly 5000 stays as it is.
        var exact = new string('b', 5000);
        RoundTrip(new AttributeChange(new Dictionary<string, object?>(), new Dictionary<string, object?> { ["x"] = exact })).New["x"].Should().Be(exact);
    }

    [Fact]
    public void ChangeData_writes_version_1_and_rejects_unknown_versions()
    {
        var json = ChangeData.Serialize(new AttributeChange(
            new Dictionary<string, object?> { ["name"] = "a" }, new Dictionary<string, object?> { ["name"] = "b" }));
        var node = JsonNode.Parse(json)!.AsObject();
        node["v"]!.GetValue<int>().Should().Be(1);
        node["old"]!["name"]!.GetValue<string>().Should().Be("a");
        node["new"]!["name"]!.GetValue<string>().Should().Be("b");
        node.ContainsKey("kind").Should().BeFalse("attribute events carry no kind (ruling A3)");

        var unknown = () => ChangeData.Deserialize("""{"v":2,"old":{},"new":{}}""");
        unknown.Should().Throw<FormatException>().WithMessage("*version 2*");
        var missing = () => ChangeData.Deserialize("""{"old":{},"new":{}}""");
        missing.Should().Throw<FormatException>();
        var kind = () => ChangeData.Deserialize("""{"v":1,"kind":"other"}""");
        kind.Should().Throw<FormatException>().WithMessage("*other*");
        var tag = () => ChangeData.Deserialize("""{"v":1,"old":{},"new":{"x":{"$unknown":1}}}""");
        tag.Should().Throw<FormatException>().WithMessage("*$unknown*");
    }

    [Fact]
    public void ChangeData_refuses_a_value_it_cannot_store()
    {
        var change = new AttributeChange(new Dictionary<string, object?>(), new Dictionary<string, object?> { ["image"] = new byte[] { 1, 2 } });
        var act = () => ChangeData.Serialize(change);
        act.Should().Throw<NotSupportedException>().WithMessage("*image*");
    }

    [Fact]
    public void ChangeData_round_trips_relationship_share_and_role_privilege_events()
    {
        var relationship = new RelationshipChange("new_account_new_tag",
            [new EntityReference("new_tag", AccountId) { Name = "Red" }, new EntityReference("new_tag", UserId)]);
        var json = ChangeData.Serialize(relationship);
        JsonNode.Parse(json)!["kind"]!.GetValue<string>().Should().Be("relationship");
        var r = ChangeData.Deserialize(json).Should().BeOfType<RelationshipChange>().Subject;
        r.Relationship.Should().Be("new_account_new_tag");
        r.Targets.Select(t => (t.LogicalName, t.Id, t.Name ?? "<null>")).Should().Equal(("new_tag", AccountId, "Red"), ("new_tag", UserId, "<null>"));

        var share = new ShareChange(new EntityReference("systemuser", UserId) { Name = "Bob" }, AccessRights.ReadAccess,
            AccessRights.ReadAccess | AccessRights.WriteAccess);
        json = ChangeData.Serialize(share);
        JsonNode.Parse(json)!["kind"]!.GetValue<string>().Should().Be("share");
        JsonNode.Parse(json)!["new"]!.GetValue<int>().Should().Be((int)(AccessRights.ReadAccess | AccessRights.WriteAccess));
        var s = ChangeData.Deserialize(json).Should().BeOfType<ShareChange>().Subject;
        (s.Principal.LogicalName, s.Principal.Id, s.Principal.Name).Should().Be(("systemuser", UserId, "Bob"));
        s.Old.Should().Be(AccessRights.ReadAccess);
        s.New.Should().Be(AccessRights.ReadAccess | AccessRights.WriteAccess);

        var bu = Guid.NewGuid();
        var privileges = new RolePrivilegesChange(
            [new RolePrivilege((int)PrivilegeDepth.Basic, AccountId, bu)],
            [new RolePrivilege((int)PrivilegeDepth.Global, AccountId, bu), new RolePrivilege((int)PrivilegeDepth.Local, UserId)]);
        json = ChangeData.Serialize(privileges);
        JsonNode.Parse(json)!["kind"]!.GetValue<string>().Should().Be("roleprivileges");
        var p = ChangeData.Deserialize(json).Should().BeOfType<RolePrivilegesChange>().Subject;
        p.Old.Select(x => (x.PrivilegeId, x.Depth, x.BusinessUnitId)).Should().Equal((AccountId, PrivilegeDepth.Basic, bu));
        p.New.Select(x => (x.PrivilegeId, x.Depth, x.BusinessUnitId))
            .Should().Equal((AccountId, PrivilegeDepth.Global, bu), (UserId, PrivilegeDepth.Local, Guid.Empty));
    }
}
