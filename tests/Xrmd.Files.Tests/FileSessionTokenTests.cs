using FluentAssertions;
using Xrmd.Files;

namespace Xrmd.Files.Tests;

/// <summary>The continuation token of a file session (ruling F6): the session id plus an HMAC over (id, caller).</summary>
public sealed class FileSessionTokenTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
    private static readonly Guid Organization = Guid.Parse("6f8d2c1e-0000-4000-8000-000000000001");

    private static FileSessionTokens Tokens() => FileSessionTokens.Derive(Secret, Organization);

    [Fact]
    public void Issued_token_reads_back_the_session_for_its_caller()
    {
        var session = Guid.NewGuid();
        var caller = Guid.NewGuid();

        var token = Tokens().Issue(session, caller);

        Tokens().TryRead(token, caller, out var read).Should().BeTrue();
        read.Should().Be(session);
        token.Should().NotContain(session.ToString()).And.NotContain(caller.ToString());
        token.Should().MatchRegex("^[A-Za-z0-9_-]+$", "the token travels in a URL query (sessiontoken)");
    }

    [Fact]
    public void Derivation_is_deterministic_so_tokens_survive_a_restart()
    {
        var session = Guid.NewGuid();
        var caller = Guid.NewGuid();
        FileSessionTokens.Derive(Secret, Organization).Issue(session, caller)
            .Should().Be(FileSessionTokens.Derive(Secret, Organization).Issue(session, caller));
    }

    [Fact]
    public void Foreign_caller_is_refused()
    {
        var token = Tokens().Issue(Guid.NewGuid(), Guid.NewGuid());
        Tokens().TryRead(token, Guid.NewGuid(), out _).Should().BeFalse();
    }

    [Fact]
    public void Tampered_token_is_refused()
    {
        var caller = Guid.NewGuid();
        var token = Tokens().Issue(Guid.NewGuid(), caller);
        for (var i = 0; i < token.Length; i++)
        {
            var chars = token.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            Tokens().TryRead(new string(chars), caller, out _).Should().BeFalse($"a change at position {i} must be detected");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("AAAA")]
    [InlineData("%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%")]
    public void Malformed_token_is_refused(string? token) =>
        Tokens().TryRead(token, Guid.NewGuid(), out _).Should().BeFalse();

    [Fact]
    public void Another_organization_or_secret_does_not_accept_the_token()
    {
        var session = Guid.NewGuid();
        var caller = Guid.NewGuid();
        var token = Tokens().Issue(session, caller);

        FileSessionTokens.Derive(Secret, Guid.NewGuid()).TryRead(token, caller, out _).Should().BeFalse();
        FileSessionTokens.Derive([.. Enumerable.Reverse(Secret)], Organization).TryRead(token, caller, out _).Should().BeFalse();
    }

    [Fact]
    public void A_short_secret_is_refused() =>
        FluentActions.Invoking(() => FileSessionTokens.Derive(new byte[8], Organization)).Should().Throw<ArgumentException>();
}
