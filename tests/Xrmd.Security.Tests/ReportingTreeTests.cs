using FluentAssertions;
using Xrmd.Security;

namespace Xrmd.Security.Tests;

/// <summary>Plan 2.5, task 3: the manager hierarchy walk (<c>systemuser.parentsystemuserid</c>).</summary>
public sealed class ReportingTreeTests
{
    private static readonly Guid Bu = Guid.NewGuid();
    private static readonly Guid M = Guid.NewGuid(), R1 = Guid.NewGuid(), R2 = Guid.NewGuid(), R11 = Guid.NewGuid(),
        R111 = Guid.NewGuid(), Other = Guid.NewGuid(), T1 = Guid.NewGuid(), T2 = Guid.NewGuid();

    // M ← R1 ← R11 ← R111; M ← R2; Other has no manager.
    private static ReportingTree Tree() => new(
        new Dictionary<Guid, (Guid? Manager, Guid BusinessUnit)>
        {
            [M] = (null, Bu), [R1] = (M, Bu), [R2] = (M, Bu), [R11] = (R1, Bu), [R111] = (R11, Bu), [Other] = (null, Bu),
        },
        new Dictionary<Guid, IReadOnlySet<Guid>> { [R1] = new HashSet<Guid> { T1, T2 } });

    [Fact]
    public void Reports_have_levels_and_stop_at_depth()
    {
        var tree = Tree();
        tree.ReportsOf(M, 3).Should().BeEquivalentTo(new[] { (R1, 1), (R2, 1), (R11, 2), (R111, 3) });
        tree.ReportsOf(M, 2).Should().BeEquivalentTo(new[] { (R1, 1), (R2, 1), (R11, 2) });
        tree.ReportsOf(M, 1).Should().BeEquivalentTo(new[] { (R1, 1), (R2, 1) });
        tree.ReportsOf(M, 0).Should().BeEmpty();
        tree.ReportsOf(R1, 5).Should().BeEquivalentTo(new[] { (R11, 1), (R111, 2) });
        tree.ReportsOf(Other, 5).Should().BeEmpty();
        // Breadth first: every level-1 report comes before any level-2 report.
        tree.ReportsOf(M, 3).Select(r => r.Level).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Reporting_tree_survives_cycles()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), self = Guid.NewGuid();
        // a ← b ← c ← a (written directly in SQL by an older version); self is its own manager.
        var tree = new ReportingTree(
            new Dictionary<Guid, (Guid? Manager, Guid BusinessUnit)> { [a] = (c, Bu), [b] = (a, Bu), [c] = (b, Bu), [self] = (self, Bu) },
            new Dictionary<Guid, IReadOnlySet<Guid>>());

        tree.ReportsOf(a, 100).Should().BeEquivalentTo(new[] { (b, 1), (c, 2) }, "each user is visited once and the manager is never its own report");
        tree.ReportsOf(self, 100).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_manager_has_no_reports_and_teams_are_per_user()
    {
        var tree = Tree();
        tree.ReportsOf(Guid.NewGuid(), 3).Should().BeEmpty();
        tree.TeamsOf(R1).Should().BeEquivalentTo([T1, T2]);
        tree.TeamsOf(R2).Should().BeEmpty();
        tree.TeamsOf(Guid.NewGuid()).Should().BeEmpty();
        tree.BusinessUnitOf(R1).Should().Be(Bu);
        tree.BusinessUnitOf(Guid.NewGuid()).Should().BeNull();
    }
}
