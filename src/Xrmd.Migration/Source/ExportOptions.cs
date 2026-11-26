using Xrmd.Migration.Mapping;

namespace Xrmd.Migration.Source;

/// <param name="Solutions">Solutions to export by unique name (with <paramref name="AllUnmanaged"/>, added to every unmanaged one).</param>
/// <param name="ManagedSolutionFiles">Unique name → path of the original managed solution file.</param>
/// <param name="Include">Tables the exclusion catalog marks as optional, to export anyway.</param>
/// <param name="Exclude">Tables to leave out; wins over everything else.</param>
/// <param name="Parallel">Data tables exported at the same time.</param>
/// <param name="RequireAdmin">Stop when the signed-in user is not System Administrator (otherwise a warning).</param>
/// <param name="Maps">The mapping files of the run, already combined and checked (<see cref="MapSet.Combine"/>); null for an export without maps.</param>
/// <param name="MapTexts">The name (file name without extension) and YAML of each mapping file, stored in the package; required with <paramref name="Maps"/>.</param>
/// <param name="ArchiveCandidates">Export the rows of standard tables xrmverse lacks and no map names, so that an import can keep them in archive tables (spec §8.7); <c>--no-archive-candidates</c> turns it off.</param>
public sealed record ExportOptions(IReadOnlyList<string> Solutions, bool AllUnmanaged, IReadOnlyDictionary<string, string> ManagedSolutionFiles,
    IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, int Parallel = 4, bool RequireAdmin = false,
    MapSet? Maps = null, IReadOnlyList<(string Name, string Yaml)>? MapTexts = null, bool ArchiveCandidates = true);
