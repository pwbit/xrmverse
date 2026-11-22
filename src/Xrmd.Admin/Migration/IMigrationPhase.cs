using Xrmd.Abstractions;
using Xrmd.Migration.Packaging;

namespace Xrmd.Admin.Migration;

/// <summary>
/// One phase of a migration import (migration spec §7.6). Phases run in <see cref="Order"/>; the run's ledger keeps the number of
/// the last phase completed, so a resumed run skips the phases with an <see cref="Order"/> up to it. A phase that stops halfway is
/// run again from its start on resume: it skips its own committed work through the ledger's chunks.
/// </summary>
public interface IMigrationPhase
{
    /// <summary>The position of the phase, from 1; unique.</summary>
    int Order { get; }

    /// <summary>The short name shown in the run's status.</summary>
    string Name { get; }

    Task RunAsync(MigrationPhaseContext context, CancellationToken ct);
}

/// <summary>What a phase works with: the run, its open package, the user it runs as, the ledger and the run's options.</summary>
public sealed record MigrationPhaseContext(Guid RunId, PackageReader Package, Caller Caller, MigrationRunStore Store,
    IReadOnlyDictionary<string, string> Options, IProgress<string> Progress, Import.MigrationLedger Ledger, IServiceProvider Services);

/// <summary>A phase found the run cannot go on: the run fails with <see cref="Exception.Message"/>.</summary>
public sealed class MigrationPhaseException(string message) : Exception(message);
