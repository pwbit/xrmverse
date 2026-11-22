namespace Xrmd.Admin.Migration;

/// <summary>Phase 1 (migration spec §7.6): every entry of the package matches its checksum in the manifest.</summary>
public sealed class VerifyPackagePhase : IMigrationPhase
{
    /// <summary>The most problems listed in the run's message.</summary>
    public const int MaxListed = 10;

    public int Order => 1;

    public string Name => "verify";

    /// <exception cref="MigrationPhaseException">An entry is missing or does not match its checksum.</exception>
    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await context.Package.VerifyAsync(null, ct);
        if (result.Problems.Count > 0)
            throw new MigrationPhaseException("The package is damaged: " +
                string.Join("; ", result.Problems.Take(MaxListed).Select(p => $"{p.Path}: {p.Problem}")) +
                (result.Problems.Count > MaxListed ? $"; and {result.Problems.Count - MaxListed} more." : "."));
        context.Progress.Report($"Verified {result.EntriesChecked} entries ({result.BytesChecked} bytes).");
    }
}
