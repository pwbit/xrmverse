namespace Xrmd.Abstractions;

/// <summary>
/// The flag of an asynchronous flow that writes the records of a migration from Dataverse (migration spec §7.5): inside it the core
/// keeps the system columns a record carries (created and modified on and by, owning business unit), applies no closed-activity rule
/// and no upload size limit, writes no audit row and wakes no workflow wait; and the pipeline refuses to run, so no plug-in step,
/// workflow or webhook can fire. A flag of the flow only, entered by the migration importer: nothing a request carries can set it.
/// </summary>
public static class MigrationScope
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>True inside <see cref="Enter"/> on the current asynchronous flow.</summary>
    public static bool IsActive => Active.Value;

    /// <summary>Marks the current asynchronous flow as a migration write until the result is disposed.</summary>
    public static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
