using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Workflows.Triggers;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// Plan 2.14, Task 5 (W5, W6, W15): which activated processes a message triggers. The business units of the scope cases form the
/// tree root → child → grandchild, with a sibling of child under root.
/// </summary>
public class WorkflowTriggerMatcherTests
{
    private static readonly Guid Root = Guid.NewGuid(), Child = Guid.NewGuid(), Grandchild = Guid.NewGuid(), Sibling = Guid.NewGuid();
    private static readonly Guid WorkflowOwner = Guid.NewGuid();

    /// <summary>Users map to their unit, units to their parent (the root to <see cref="Guid.Empty"/>).</summary>
    private static readonly Dictionary<Guid, Guid> Units = new()
    {
        [WorkflowOwner] = Child,
        [Child] = Root, [Grandchild] = Child, [Sibling] = Root, [Root] = Guid.Empty,
    };

    private static Guid BusinessUnitOf(Guid id) => Units.GetValueOrDefault(id);

    private static WorkflowRow Realtime(string name, Func<WorkflowRow, WorkflowRow>? configure = null)
    {
        var row = new WorkflowRow
        {
            Id = Guid.NewGuid(), Name = name, Category = 0, Mode = 1, Type = 2, Scope = 4, StateCode = 1, PrimaryEntity = "account",
            OwnerId = new EntityReference("systemuser", WorkflowOwner),
        };
        return configure?.Invoke(row) ?? row;
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<WorkflowRow> rows) => rows.Select(r => r.Name!).ToList();

    private static IReadOnlyList<string> Match(IReadOnlyList<WorkflowRow> activated, string message, int? stage, int mode = 1,
        IReadOnlyCollection<string>? columns = null, Guid? recordOwner = null) =>
        Names(WorkflowTriggerMatcher.Match(activated, message, "account", columns ?? [], recordOwner ?? WorkflowOwner, BusinessUnitOf, stage, mode));

    [Fact]
    public void Create_trigger()
    {
        var post = Realtime("post", r => r with { TriggerOnCreate = true, CreateStage = 40 });
        var pre = Realtime("pre", r => r with { TriggerOnCreate = true, CreateStage = 20 });
        var defaulted = Realtime("default stage", r => r with { TriggerOnCreate = true });
        var notOnCreate = Realtime("update only", r => r with { TriggerOnUpdateAttributes = "name", UpdateStage = 40 });
        var background = Realtime("background", r => r with { TriggerOnCreate = true, Mode = 0 });
        var other = Realtime("contact", r => r with { TriggerOnCreate = true, CreateStage = 40, PrimaryEntity = "contact" });
        IReadOnlyList<WorkflowRow> all = [post, pre, defaulted, notOnCreate, background, other];

        Match(all, "Create", 40).Should().BeEquivalentTo("post", "default stage");
        Match(all, "Create", 20).Should().BeEquivalentTo("pre");
        Match(all, "Create", null, mode: 0).Should().BeEquivalentTo("background");
        Match(all, "Update", 40, columns: ["description"]).Should().BeEmpty();
    }

    [Fact]
    public void Update_intersects_attribute_list()
    {
        var name = Realtime("name", r => r with { TriggerOnUpdateAttributes = "name,telephone1", UpdateStage = 40 });
        var spaced = Realtime("spaced", r => r with { TriggerOnUpdateAttributes = " Telephone1 , fax", UpdateStage = 40 });
        var pre = Realtime("pre", r => r with { TriggerOnUpdateAttributes = "name", UpdateStage = 20 });
        IReadOnlyList<WorkflowRow> all = [name, spaced, pre];

        Match(all, "Update", 40, columns: ["name"]).Should().BeEquivalentTo("name");
        Match(all, "Update", 40, columns: ["telephone1", "description"]).Should().BeEquivalentTo("name", "spaced");
        Match(all, "Update", 40, columns: ["description"]).Should().BeEmpty();
        Match(all, "Update", 20, columns: ["name"]).Should().BeEquivalentTo("pre");
    }

    [Fact]
    public void Assign_matches_ownerid()
    {
        var owner = Realtime("owner", r => r with { TriggerOnUpdateAttributes = "ownerid", UpdateStage = 40 });
        var name = Realtime("name", r => r with { TriggerOnUpdateAttributes = "name", UpdateStage = 40 });

        // Assign sends only the owner, whatever columns the caller passes.
        Match([owner, name], "Assign", 40, columns: ["name"]).Should().BeEquivalentTo("owner");
        Match([owner, name], "Assign", 40).Should().BeEquivalentTo("owner");
    }

    [Fact]
    public void SetState_matches_statecode()
    {
        var state = Realtime("state", r => r with { TriggerOnUpdateAttributes = "statecode", UpdateStage = 40 });
        var status = Realtime("status", r => r with { TriggerOnUpdateAttributes = "statuscode", UpdateStage = 40 });
        var name = Realtime("name", r => r with { TriggerOnUpdateAttributes = "name", UpdateStage = 40 });

        Match([state, status, name], "SetState", 40, columns: ["statecode", "statuscode"]).Should().BeEquivalentTo("state", "status");
        // A SetState with Status -1 sends only the state.
        Match([state, status, name], "SetState", 40, columns: ["statecode"]).Should().BeEquivalentTo("state");
    }

    [Fact]
    public void Delete_trigger()
    {
        var delete = Realtime("delete", r => r with { TriggerOnDelete = true, DeleteStage = 20 });
        var defaulted = Realtime("default stage", r => r with { TriggerOnDelete = true });
        var create = Realtime("create", r => r with { TriggerOnCreate = true, CreateStage = 20 });

        Match([delete, defaulted, create], "Delete", 20).Should().BeEquivalentTo("delete", "default stage");
        Match([delete, defaulted, create], "Delete", 40).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, "same user", true)]
    [InlineData(1, "other user in the unit", false)]
    [InlineData(2, "other user in the unit", true)]
    [InlineData(2, "user in a child unit", false)]
    [InlineData(3, "user in a child unit", true)]
    [InlineData(3, "other user in the unit", true)]
    [InlineData(3, "user in a parent unit", false)]
    [InlineData(3, "user in a sibling unit", false)]
    [InlineData(4, "user in a sibling unit", true)]
    [InlineData(4, "user in a parent unit", true)]
    public void Scope_user_bu_parent_org(int scope, string owner, bool matches)
    {
        var sameUnit = Guid.NewGuid();
        var childUnit = Guid.NewGuid();
        var parentUnit = Guid.NewGuid();
        var siblingUnit = Guid.NewGuid();
        var units = new Dictionary<Guid, Guid>(Units)
        {
            [sameUnit] = Child, [childUnit] = Grandchild, [parentUnit] = Root, [siblingUnit] = Sibling,
        };
        var recordOwner = owner switch
        {
            "same user" => WorkflowOwner,
            "other user in the unit" => sameUnit,
            "user in a child unit" => childUnit,
            "user in a parent unit" => parentUnit,
            _ => siblingUnit,
        };
        var row = Realtime("scoped", r => r with { TriggerOnCreate = true, CreateStage = 40, Scope = scope });

        var matched = WorkflowTriggerMatcher.Match([row], "Create", "account", [], recordOwner, id => units.GetValueOrDefault(id), 40, 1);

        matched.Should().HaveCount(matches ? 1 : 0);
    }

    [Fact]
    public void Scope_is_ignored_without_a_record_owner()
    {
        // An organization-owned table has no owner: no scope can filter it.
        var row = Realtime("user scope", r => r with { TriggerOnCreate = true, CreateStage = 40, Scope = 1 });

        Match([row], "Create", 40, recordOwner: Guid.Empty).Should().BeEquivalentTo("user scope");
    }

    [Fact]
    public void Draft_workflows_never_match()
    {
        var draft = Realtime("draft", r => r with { TriggerOnCreate = true, CreateStage = 40, StateCode = 0 });
        var rule = Realtime("draft rule", r => r with { Category = 2, StateCode = 0, EntityScopeRule = true });

        Match([draft, rule], "Create", 40).Should().BeEmpty();
        Match([draft, rule], "Create", 20).Should().BeEmpty();
    }

    [Fact]
    public void Entity_scope_business_rules_match_stage_20_of_create_and_update_only()
    {
        // W15 (amended): a rule runs server-side when its processtriggerscope or a trigger of it is Entity; whatever columns change.
        var rule = Realtime("rule", r => r with { Category = 2, EntityScopeRule = true, Scope = 1 });
        var formRule = Realtime("form rule", r => r with { Category = 2, EntityScopeRule = false });
        IReadOnlyList<WorkflowRow> all = [rule, formRule];

        Match(all, "Create", 20).Should().BeEquivalentTo("rule");
        Match(all, "Update", 20, columns: ["anything"]).Should().BeEquivalentTo("rule");
        Match(all, "Create", 40).Should().BeEmpty();
        Match(all, "Assign", 20).Should().BeEmpty();
        Match(all, "SetState", 20, columns: ["statecode"]).Should().BeEmpty();
        Match(all, "Delete", 20).Should().BeEmpty();
        Match(all, "Create", null, mode: 0).Should().BeEmpty();
        // Scope does not filter business rules.
        Match(all, "Create", 20, recordOwner: Guid.NewGuid()).Should().BeEquivalentTo("rule");
    }

    [Fact]
    public void Actions_and_other_categories_never_match()
    {
        var action = Realtime("action", r => r with { Category = 3, TriggerOnCreate = true, CreateStage = 40 });
        var flow = Realtime("flow", r => r with { Category = 5, TriggerOnCreate = true, CreateStage = 40 });

        Match([action, flow], "Create", 40).Should().BeEmpty();
    }

    [Fact]
    public void Matches_are_ordered_by_rank_then_id()
    {
        var rows = Enumerable.Range(0, 4)
            .Select(i => Realtime("w" + i, r => r with { TriggerOnCreate = true, CreateStage = 40, Rank = i % 2 }))
            .ToList();

        var matched = WorkflowTriggerMatcher.Match(rows, "Create", "account", [], WorkflowOwner, BusinessUnitOf, 40, 1);

        matched.Should().BeInAscendingOrder(r => r.Rank).And.HaveCount(4);
        matched.Where(r => r.Rank == 0).Should().BeInAscendingOrder(r => r.Id);
    }
}
