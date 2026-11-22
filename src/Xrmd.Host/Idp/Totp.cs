using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Host.Idp;

/// <summary>
/// Time-based one-time passwords (plan 2.8, L7): RFC 6238 with HMAC-SHA-1, 6 digits and 30-second steps. A code is accepted for
/// the current step and one step on each side; a step at or before the last one used is refused, so a code works once.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    private const int KeyBytes = 20; // 160 bits, the HMAC-SHA-1 block output (RFC 4226 §4)

    /// <summary>A new random key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>The step of <paramref name="time"/>: whole periods since the Unix epoch.</summary>
    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>The code of <paramref name="step"/> (RFC 4226 HOTP with the step as counter).</summary>
    public static string Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7fffffff;
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// True when <paramref name="code"/> is the code of the step of <paramref name="now"/> or of a neighbouring step, and that step
    /// is after <paramref name="lastStep"/>; <paramref name="step"/> is then the matched step, to store as the new last step.
    /// </summary>
    public static bool Verify(byte[] key, string code, DateTimeOffset now, long? lastStep, out long step)
    {
        step = 0;
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit)) return false;

        var current = StepAt(now);
        var matched = false;
        // Every candidate is computed and compared in constant time, so the timing does not tell which step matched.
        for (var candidate = current - 1; candidate <= current + 1; candidate++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Compute(key, candidate)), Encoding.ASCII.GetBytes(code));
            if (equal && !matched && (lastStep is null || candidate > lastStep))
            {
                matched = true;
                step = candidate;
            }
        }
        return matched;
    }

    /// <summary>The <c>otpauth://</c> URI authenticator apps read (Key Uri Format): label <c>issuer:account</c>, Base32 secret.</summary>
    public static string ProvisioningUri(byte[] key, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32(key)}" +
        $"&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString(); // no padding, as authenticator apps expect
    }
}
