using FluentAssertions;
using Xrmd.Security;

namespace Xrmd.Security.Tests;

public sealed class BusinessUnitTreeTests
{
    private static readonly Guid R = Guid.NewGuid(), A = Guid.NewGuid(), A1 = Guid.NewGuid(), B = Guid.NewGuid();

    private static readonly BusinessUnitTree Tree = new(new Dictionary<Guid, Guid?> { [R] = null, [A] = R, [A1] = A, [B] = R });

    [Fact]
    public void Subtree_includes_the_unit_and_its_descendants()
    {
        Tree.SubtreeOf(A).Should().BeEquivalentTo([A, A1]);
        Tree.SubtreeOf(R).Should().BeEquivalentTo([R, A, A1, B]);
        Tree.SubtreeOf(A1).Should().BeEquivalentTo([A1]);
    }

    [Fact]
    public void Ancestors_go_from_the_parent_to_the_root()
    {
        Tree.AncestorsOf(A1).Should().Equal(A, R);
        Tree.AncestorsOf(R).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_unit_has_empty_subtree_and_no_ancestors()
    {
        Tree.SubtreeOf(Guid.NewGuid()).Should().BeEmpty();
        Tree.AncestorsOf(Guid.NewGuid()).Should().BeEmpty();
    }
}
