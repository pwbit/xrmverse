namespace Xrmd.Integration.Tests;

/// <summary>Serialises <c>update-ca-certificates</c> runs of the acceptance fixtures that trust a test certificate in the system directory.</summary>
internal static class SystemTrustStoreLock
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
