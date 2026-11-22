namespace Xrmd.Abstractions;

/// <summary>The authenticated caller of the current request, and the user it asks to impersonate.</summary>
public interface ICurrentUser
{
    /// <summary>Entra object id (<c>oid</c> claim, falling back to <c>sub</c>).</summary>
    Guid ObjectId { get; }

    /// <summary>Display name (<c>name</c>, falling back to <c>preferred_username</c>, else empty).</summary>
    string Name { get; }

    /// <summary>
    /// The <c>systemuserid</c> to impersonate (HTTP header <c>MSCRMCallerID</c>); null when absent.
    /// <see cref="Guid.Empty"/> means no impersonation.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 InvalidArgument when the value is not a GUID.</exception>
    Guid? CallerId { get; }

    /// <summary>
    /// The Entra object id of the user to impersonate (HTTP header <c>CallerObjectId</c>); null when absent. It wins over
    /// <see cref="CallerId"/>. <see cref="Guid.Empty"/> means no impersonation.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 InvalidArgument when the value is not a GUID.</exception>
    Guid? CallerObjectId { get; }
}
