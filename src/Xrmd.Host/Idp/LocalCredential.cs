namespace Xrmd.Host.Idp;

/// <summary>The local sign-in of a <c>systemuser</c> (plan 2.8, L6/L7): password hash, optional TOTP and the lockout counters.</summary>
public sealed class LocalCredential
{
    public Guid SystemUserId { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public byte[]? TotpKey { get; set; }
    public long? TotpLastStep { get; set; }
    public int FailedCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime ChangedOnUtc { get; set; }
}

/// <summary>One certificate of the organization's key ring, as PFX bytes (<c>Use</c> is <c>sig</c> or <c>enc</c>).</summary>
public sealed class SigningKey
{
    public Guid Id { get; set; }
    public string Use { get; set; } = "";
    public byte[] Pfx { get; set; } = [];
    public DateTime CreatedOnUtc { get; set; }
}
