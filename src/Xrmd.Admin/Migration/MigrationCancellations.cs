namespace Xrmd.Admin.Migration;

/// <summary>
/// The cancellation of the running migration jobs, by run (plan M3 task 6): a cancel request trips the token of the run's current job.
/// A job holds its own registration: when an older job of the run ends after a newer one started (a resume during an unwind), it
/// removes only its own.
/// </summary>
public sealed class MigrationCancellations
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Registration> _byRun = [];

    /// <summary>The registration of one job of a run: its token, and whether an administrator cancelled it.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly MigrationCancellations _owner;
        internal readonly CancellationTokenSource Source = new();

        internal Registration(MigrationCancellations owner, Guid runId)
        {
            _owner = owner;
            RunId = runId;
        }

        public Guid RunId { get; }

        public CancellationToken Token => Source.Token;

        /// <summary>True when the token was tripped by <see cref="Cancel"/> (not by the host stopping).</summary>
        public bool IsCancelled { get; internal set; }

        public void Dispose() => _owner.Release(this);
    }

    /// <summary>Registers a job of <paramref name="runId"/>; it becomes the one <see cref="Cancel"/> reaches.</summary>
    public Registration Register(Guid runId)
    {
        var registration = new Registration(this, runId);
        lock (_gate) _byRun[runId] = registration;
        return registration;
    }

    /// <summary>Trips the token of the run's current job; false when no job of the run is running in this process.</summary>
    public bool Cancel(Guid runId)
    {
        lock (_gate)
        {
            if (!_byRun.TryGetValue(runId, out var registration)) return false;
            registration.IsCancelled = true;
            registration.Source.Cancel();
            return true;
        }
    }

    private void Release(Registration registration)
    {
        lock (_gate)
        {
            if (_byRun.TryGetValue(registration.RunId, out var current) && ReferenceEquals(current, registration)) _byRun.Remove(registration.RunId);
            registration.Source.Dispose();
        }
    }
}
