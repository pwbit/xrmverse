using FluentAssertions;
using Xrmd.Host.Idp;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, L9 and Review Focus 5: the scopes the local identity provider accepts.</summary>
public sealed class IdpScopesTests
{
    private const string Audience = "https://h/";

    [Theory]
    [InlineData("https://h/.default")]
    [InlineData("https://h//.default")]
    [InlineData("https://h/user_impersonation")]
    [InlineData("https://h//user_impersonation")]
    [InlineData("openid profile offline_access")]
    [InlineData("openid offline_access https://h/.default")]
    public void Accepted(string scopes)
    {
        var requested = scopes.Split(' ');

        IdpScopes.TryResolve(requested, Audience, out var granted).Should().BeTrue();

        granted.Should().Equal(requested);
    }

    [Theory]
    [InlineData("https://other/.default")]
    [InlineData("api://x/.default")]
    [InlineData("email")]
    [InlineData("https://h/.default email")]
    [InlineData("https://h/other")]
    [InlineData("https://h.evil/.default")]
    public void Rejected(string scopes)
    {
        IdpScopes.TryResolve(scopes.Split(' '), Audience, out var granted).Should().BeFalse();

        granted.Should().BeEmpty();
    }

    [Fact]
    public void Audience_without_trailing_slash_gives_the_same_rules()
    {
        IdpScopes.TryResolve(["https://h/.default"], "https://h", out _).Should().BeTrue();
        IdpScopes.TryResolve(["https://h.default"], "https://h", out _).Should().BeFalse();
    }
}
