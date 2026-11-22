namespace Xrmd.Migration.Source;

/// <param name="Solutions">Solutions to export by unique name (with <paramref name="AllUnmanaged"/>, added to every unmanaged one).</param>
/// <param name="ManagedSolutionFiles">Unique name → path of the original managed solution file.</param>
/// <param name="Include">Tables the exclusion catalog marks as optional, to export anyway.</param>
/// <param name="Exclude">Tables to leave out; wins over everything else.</param>
/// <param name="Parallel">Data tables exported at the same time.</param>
/// <param name="RequireAdmin">Stop when the signed-in user is not System Administrator (otherwise a warning).</param>
public sealed record ExportOptions(IReadOnlyList<string> Solutions, bool AllUnmanaged, IReadOnlyDictionary<string, string> ManagedSolutionFiles,
    IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, int Parallel = 4, bool RequireAdmin = false);
