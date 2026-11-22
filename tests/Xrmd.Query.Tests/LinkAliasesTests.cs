using FluentAssertions;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests;

/// <summary>Final review M4: the one rule for the result alias of a link-entity, shared by the compiler and the note body read.</summary>
public class LinkAliasesTests
{
    [Fact]
    public void Generated_aliases_count_per_entity_and_skip_the_explicit_ones()
    {
        var aliases = new LinkAliases(["contact2", "mine"]);

        aliases.For("contact", null).Should().Be("contact1");
        aliases.For("account", "mine").Should().Be("mine");
        aliases.For("contact", null).Should().Be("contact3", "contact2 is the caller's");
        aliases.For("account", null).Should().Be("account1");
        aliases.For("contact", "contact2").Should().Be("contact2");
        aliases.For("contact", null).Should().Be("contact4");
    }
}
