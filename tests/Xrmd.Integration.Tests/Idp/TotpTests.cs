using System.Text;
using FluentAssertions;
using Xrmd.Host.Idp;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 4: TOTP of L7 (RFC 6238, SHA-1, 6 digits, 30 s, ±1 step, a used step refused).</summary>
public sealed class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 seed; the 8-digit codes truncated to their last 6 digits.
    private static readonly byte[] Seed = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Rfc6238_vectors(long unixTime, string code)
    {
        Totp.Compute(Seed, unixTime / 30).Should().Be(code);

        Totp.Verify(Seed, code, DateTimeOffset.FromUnixTimeSeconds(unixTime), null, out var step).Should().BeTrue();
        step.Should().Be(unixTime / 30);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    [InlineData(-2, false)]
    [InlineData(2, false)]
    public void One_step_of_drift_is_accepted(int drift, bool accepted)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code = Totp.Compute(Seed, now.ToUnixTimeSeconds() / 30 + drift);

        Totp.Verify(Seed, code, now, null, out _).Should().Be(accepted);
    }

    [Fact]
    public void A_used_step_is_refused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var code = Totp.Compute(Seed, now.ToUnixTimeSeconds() / 30);
        Totp.Verify(Seed, code, now, null, out var step).Should().BeTrue();

        Totp.Verify(Seed, code, now, step, out _).Should().BeFalse("the same step twice");
        Totp.Verify(Seed, Totp.Compute(Seed, step - 1), now, step, out _).Should().BeFalse("an earlier step");
        Totp.Verify(Seed, Totp.Compute(Seed, step + 1), now, step, out var next).Should().BeTrue("a later step");
        next.Should().Be(step + 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Malformed_codes_are_refused(string code) =>
        Totp.Verify(Seed, code, DateTimeOffset.FromUnixTimeSeconds(59), null, out _).Should().BeFalse();
}
