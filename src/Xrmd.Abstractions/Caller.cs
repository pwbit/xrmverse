namespace Xrmd.Abstractions;

/// <summary>
/// The calling user's <c>systemuser</c> row and its business unit; <paramref name="ImpersonatorId"/> is the user that
/// acts on behalf of <paramref name="UserId"/>, if any.
/// </summary>
public sealed record Caller(Guid UserId, Guid BusinessUnitId, Guid? ImpersonatorId = null)
{
    /// <summary>The caller of the reads the pipeline makes for itself (e.g. entity images): nobody in particular, unrestricted.</summary>
    public static readonly Caller Internal = new(Guid.Empty, Guid.Empty);
}
