using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution.Layers;

/// <summary>What an import of a solution file does (plan 2.6, ruling M6).</summary>
public enum ImportKind
{
    /// <summary>An unmanaged file: the components are written as they are (and layered components get their Active marker).</summary>
    Unmanaged,

    /// <summary>A managed file whose unique name is not installed: a new segment, a Managed layer per component.</summary>
    Install,

    /// <summary>A managed file of an installed managed solution, same or higher version: its layers are replaced (ruling M8).</summary>
    Update,

    /// <summary>A managed file imported with <c>HoldingSolution</c>: the holding solution <c>{name}_Upgrade</c> (ruling M11).</summary>
    Stage,
}

/// <summary>
/// A component of a managed import, as <see cref="ManagedImport.PlanAsync"/> decides it: its type and id (null when the import
/// creates it), the key of its payload in <see cref="SolutionPackage.Sources"/> (a plug-in type's is its assembly's, a step
/// image's its step's), whether it is seeded, and how the import log names it.
/// </summary>
/// <param name="Type">The component type (<see cref="ComponentType.Normalize"/>d).</param>
/// <param name="Id">Its id in xrmverse before the import; null when it does not exist yet.</param>
/// <param name="Source">The key of its payload (<see cref="LayerPayload.Key"/>), also the key the import's writes skip it by.</param>
/// <param name="Seeded">True for a component of the organization's seed (a system table, column or option set).</param>
/// <param name="Group">The group of the import log it is logged in.</param>
/// <param name="LogId">Its id in the import log.</param>
/// <param name="LogName">Its name in the import log.</param>
/// <param name="Description">How a log line names it (e.g. <c>attribute 'xm_item.xm_code'</c>).</param>
public sealed record ManagedComponent(int Type, Guid? Id, (int Type, string Key) Source, bool Seeded, string Group, string LogId,
    string? LogName, string Description);

/// <summary>What <see cref="ManagedImport.PlanAsync"/> decided, before the import writes anything of the file.</summary>
/// <param name="SolutionId">The managed solution.</param>
/// <param name="Kept">The payload keys of the components whose file content is not applied (a layer above wins).</param>
/// <param name="SystemPayloads">The System layer to insert for each seeded component layered for the first time.</param>
/// <param name="ActiveAfter">The components that carry an Active marker after the import (every other one of the file has none).</param>
public sealed record ManagedPlan(Guid SolutionId, IReadOnlySet<(int Type, string Key)> Kept,
    IReadOnlyDictionary<(int Type, Guid Id), string> SystemPayloads, IReadOnlySet<(int Type, Guid Id)> ActiveAfter);

/// <summary>
/// The managed install and update of a solution file (plan 2.6, rulings M6, M7 and M8): classifies the import, decides for each
/// component whether the file is applied or a layer above keeps the current state, and writes the layers. The importer applies
/// the file in between (<see cref="SolutionImporter"/>), so a managed import has one write path for components. Holds no state.
/// </summary>
/// <param name="layers">The layer store.</param>
/// <param name="exporter">Captures the System layer of a seeded component (<see cref="SolutionExporter.WriteComponentFragment"/>).</param>
public sealed class ManagedImport(ComponentLayerStore layers, SolutionExporter exporter)
{
    /// <summary>The suffix of a holding solution's unique name (Learn).</summary>
    public const string HoldingSuffix = "_Upgrade";

    /// <summary>
    /// What importing <paramref name="package"/> with <paramref name="options"/> does (ruling M6), on <paramref name="s"/>:
    /// <list type="bullet">
    /// <item>an unmanaged file: <see cref="ImportKind.Unmanaged"/>; 400 when its unique name is installed managed, or with
    /// <c>HoldingSolution</c>;</item>
    /// <item>a managed file whose name is not installed: <see cref="ImportKind.Install"/> (400 with <c>HoldingSolution</c>);
    /// installed unmanaged: 400;</item>
    /// <item>installed managed: 400 for a lower version; <see cref="ImportKind.Stage"/> with <c>HoldingSolution</c> (a strictly
    /// higher version, no <c>{name}_Upgrade</c> yet, a holding name of at most 65 characters); otherwise
    /// <see cref="ImportKind.Update"/> (same or higher version), 400 while <c>{name}_Upgrade</c> exists.</item>
    /// </list>
    /// With <see cref="ImportOptions.Upgrade"/> (<c>StageAndUpgrade</c>), an installed managed solution is staged as with
    /// <c>HoldingSolution</c> and one that is not installed is an <see cref="ImportKind.Install"/>; an unmanaged file is a 400.
    /// Versions are <c>major.minor.build.revision</c>, compared numerically per part (a missing part is 0).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the solution and the reason.</exception>
    public async Task<ImportKind> ClassifyAsync(DataSession s, SolutionPackage package, ImportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        var name = package.Solution.UniqueName;
        var installed = await InstalledAsync(s, name, ct);
        if (!package.Solution.Managed)
        {
            if (options.Upgrade)
                throw XrmdFaultException.InvalidArgument($"StageAndUpgrade applies to managed solutions only: the file of '{name}' is unmanaged.");
            if (options.HoldingSolution)
                throw XrmdFaultException.InvalidArgument($"HoldingSolution applies to managed solutions only: the file of '{name}' is unmanaged.");
            if (installed is { Managed: true })
                throw XrmdFaultException.InvalidArgument(
                    $"The solution '{name}' is installed as managed: an unmanaged file with the same unique name cannot be imported over it.");
            return ImportKind.Unmanaged;
        }

        if (installed is null)
        {
            // StageAndUpgrade of a solution that is not installed: a plain install (Learn: the upgrade applies when it can).
            if (options.HoldingSolution && !options.Upgrade)
                throw XrmdFaultException.InvalidArgument(
                    $"HoldingSolution needs the managed solution '{name}' installed: it is not, import it without HoldingSolution.");
            return ImportKind.Install;
        }
        if (!installed.Value.Managed)
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{name}' exists as unmanaged: a managed file with the same unique name cannot be imported over it.");
        var comparison = CompareVersions(package.Solution.Version, installed.Value.Version, name);
        if (comparison < 0)
            throw XrmdFaultException.InvalidArgument(
                $"The managed solution '{name}' is installed at version {installed.Value.Version}: the file's version {package.Solution.Version} is lower.");
        var holding = await InstalledAsync(s, name + HoldingSuffix, ct) is not null;
        if (options.HoldingSolution || options.Upgrade)
        {
            // Each operation names itself: StageAndUpgrade's refusals do not speak of HoldingSolution.
            var operation = options.Upgrade ? "StageAndUpgrade" : "HoldingSolution";
            if (name.Length + HoldingSuffix.Length > SolutionComponentTracker.UniqueNameMaxLength)
                throw XrmdFaultException.InvalidArgument(
                    $"The holding solution of '{name}' cannot be created: '{name}{HoldingSuffix}' is longer than {SolutionComponentTracker.UniqueNameMaxLength} characters.");
            if (comparison == 0)
                throw XrmdFaultException.InvalidArgument(
                    $"{operation} needs a version higher than the installed {installed.Value.Version} of '{name}': the file has {package.Solution.Version}.");
            if (holding)
                throw XrmdFaultException.InvalidArgument(
                    $"{operation}: the solution '{name}' already has a pending upgrade '{name}{HoldingSuffix}'.");
            return ImportKind.Stage;
        }
        if (holding)
            throw XrmdFaultException.InvalidArgument(
                $"The solution '{name}' has a pending upgrade '{name}{HoldingSuffix}': it cannot be updated until the upgrade is applied.");
        return ImportKind.Update;
    }

    /// <summary>
    /// Decides each component of a managed install, update or stage (ruling M7), before the import writes the file, on
    /// <paramref name="s"/> (the import's transaction, the metadata write lock taken: the published metadata is the current
    /// one). Gives a new solution its rank (the next segment, ruling M2; an update keeps its rank, ruling R4; a stage's holding
    /// solution <paramref name="solutionName"/> <c>{name}_Upgrade</c> takes its base's segment with sub 1023, ruling M11). For each
    /// existing component:
    /// <list type="bullet">
    /// <item>with <paramref name="overwrite"/>, its Active marker is removed unless a higher managed layer exists (ruling M6);</item>
    /// <item>without any layer: a seeded one gets its System layer captured (written after the import); a custom one (created
    /// unmanaged here) gets an Active marker, so its unmanaged state stays on top (not with <paramref name="overwrite"/>);</item>
    /// <item>the file is applied only when the new layer is the top non-Active layer and there is no Active marker; otherwise
    /// the component is kept with an information line "kept: layer above" naming it, and for a form, site map or app module a
    /// warning that its change is hidden (ruling M3: xrmverse does not merge their layers).</item>
    /// </list>
    /// </summary>
    public async Task<ManagedPlan> PlanAsync(DataSession s, ImportKind kind, Guid solutionId, string solutionName,
        IReadOnlyList<ManagedComponent> components, bool overwrite, ImportJobLog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(log);
        if (kind is not (ImportKind.Install or ImportKind.Update or ImportKind.Stage))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only an install, an update or a stage is planned here.");

        var order = kind == ImportKind.Update ? await layers.RankOrderAsync(s, solutionId, ct) : null;
        if (kind == ImportKind.Stage)
        {
            // The holding solution sits directly above its base: the base's segment, sub 1023 (ruling M2).
            if (!solutionName.EndsWith(HoldingSuffix, StringComparison.Ordinal))
                throw new ArgumentException($"A stage writes the holding solution '{{name}}{HoldingSuffix}', not '{solutionName}'.", nameof(solutionName));
            var baseName = solutionName[..^HoldingSuffix.Length];
            var baseOrder = await SolutionIdAsync(s, baseName, ct) is { } baseId ? await layers.RankOrderAsync(s, baseId, ct) : null;
            if (baseOrder is not { } ranked)
                throw new InvalidOperationException($"The managed solution '{baseName}' has no rank: it cannot be staged.");
            var segment = checked((int)(ranked / ComponentLayerStore.SubsPerSegment));
            await layers.SetRankAsync(s, solutionId, segment, ComponentLayerStore.HoldingSub, ct);
            order = ComponentLayerStore.Order(segment, ComponentLayerStore.HoldingSub);
        }
        else if (order is null)
        {
            // A new base solution sits above every one installed before (Learn); an update keeps its rank.
            var segment = checked((int)await layers.NextSegmentAsync(s, ct));
            await layers.SetRankAsync(s, solutionId, segment, 0, ct);
            order = ComponentLayerStore.Order(segment, 0);
        }

        var kept = new HashSet<(int, string)>();
        var system = new Dictionary<(int, Guid), string>();
        var active = new HashSet<(int, Guid)>();
        foreach (var component in components)
        {
            if (component.Id is not { } id) continue; // created by the import: the file is applied
            var type = ComponentType.Normalize(component.Type);
            var stack = await layers.StackAsync(s, type, id, ct);
            var hasActive = stack.Any(l => l.Kind == LayerKind.Active);
            var higher = stack.FirstOrDefault(l => l.Kind == LayerKind.Managed && l.SolutionId != solutionId && l.Order > order);
            if (overwrite && hasActive && higher is null)
            {
                await layers.RemoveActiveAsync(s, type, id, ct);
                hasActive = false;
            }
            var newMarker = false;
            if (stack.All(l => l.Kind == LayerKind.Active))
            {
                if (component.Seeded) system[(type, id)] = await exporter.WriteComponentFragment(s, type, id, ct);
                else newMarker = !overwrite;
            }
            if (hasActive || newMarker) active.Add((type, id));
            if (!hasActive && !newMarker && higher is null) continue;

            kept.Add(component.Source);
            var byManaged = higher is not null && !hasActive && !newMarker;
            var above = byManaged
                ? $"the managed solution '{await SolutionNameAsync(s, higher!.SolutionId, ct) ?? higher.SolutionId.ToString("D")}'"
                : "unmanaged customizations";
            log.Info(component.Group, component.LogId, component.LogName,
                $"kept: layer above: {component.Description} keeps its current state, because {above} {(byManaged ? "sits" : "sit")} above the layer of '{solutionName}' (recorded, not applied).");
            if (type is ComponentType.SystemForm or ComponentType.SiteMap or ComponentType.AppModule)
                log.Warn(component.Group, component.LogId, component.LogName,
                    $"The change '{solutionName}' makes to {component.Description} is hidden by {above} above it: xrmverse does not merge the layers of forms, site maps and app modules.");
        }
        return new ManagedPlan(solutionId, kept, system, active);
    }

    /// <summary>
    /// Writes the layers of a managed install or update once the file is applied, on <paramref name="s"/> (ruling M7): the
    /// System layers captured by <see cref="PlanAsync"/>, the solution's Managed layer on every component of the file (an
    /// update replaces the ones of the components the file has and keeps the others, ruling M8), and the Active markers: those
    /// the plan keeps or adds, none on any other component of the file (rulings R5: the import's own writes leave none).
    /// </summary>
    /// <param name="s">The import's session.</param>
    /// <param name="package">The file.</param>
    /// <param name="plan">What <see cref="PlanAsync"/> decided.</param>
    /// <param name="written">Every component of the file, by type and id after the import, with the key of its payload.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task WriteLayersAsync(DataSession s, SolutionPackage package, ManagedPlan plan,
        IReadOnlyList<(int Type, Guid Id, (int Type, string Key)? Source)> written, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(written);
        foreach (var ((type, id), payload) in plan.SystemPayloads)
            await layers.UpsertAsync(s, new ComponentLayer(type, id, ComponentLayerStore.SystemLayerSolutionId, 0, LayerKind.System, payload), ct);

        var done = new HashSet<(int, Guid)>();
        foreach (var (rawType, id, source) in written)
        {
            var type = ComponentType.Normalize(rawType);
            if (!done.Add((type, id))) continue;
            var payload = source is { } key && package.Sources?.ContainsKey(key) == true ? LayerPayload.From(package, key.Type, key.Key) : null;
            await layers.UpsertAsync(s, new ComponentLayer(type, id, plan.SolutionId, 0, LayerKind.Managed, payload), ct);
            if (plan.ActiveAfter.Contains((type, id))) await layers.MarkActiveAsync(s, type, id, ct);
            else await layers.RemoveActiveAsync(s, type, id, ct);
        }
    }

    /// <summary>
    /// Compares two versions <c>major.minor.build.revision</c> numerically per part (a missing part is 0).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the solution for a version that is not of that form.</exception>
    public static int CompareVersions(string left, string right, string solutionName)
    {
        var a = Parts(left, solutionName);
        var b = Parts(right, solutionName);
        for (var i = 0; i < 4; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    private static long[] Parts(string version, string solutionName)
    {
        var parts = (version ?? "").Split('.');
        var numbers = new long[4];
        if (parts.Length is < 1 or > 4) throw InvalidVersion(version, solutionName);
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                throw InvalidVersion(version, solutionName);
        return numbers;
    }

    private static XrmdFaultException InvalidVersion(string? version, string solutionName) =>
        XrmdFaultException.InvalidArgument(
            $"The version '{version}' of the solution '{solutionName}' is not a version major.minor.build.revision of numbers.");

    private static async Task<(bool Managed, string Version)?> InstalledAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT ismanaged, version FROM {s.DataTable("solution")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1"));
        cmd.AddParameter("@name", DbType.String, uniqueName, SolutionComponentTracker.UniqueNameMaxLength + HoldingSuffix.Length);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (!reader.IsDBNull(0) && reader.GetBoolean(0), reader.IsDBNull(1) ? "" : reader.GetString(1));
    }

    private static async Task<Guid?> SolutionIdAsync(DataSession s, string uniqueName, CancellationToken ct)
    {
        await using var cmd = s.Command(s.Dialect.Limit($"SELECT solutionid FROM {s.DataTable("solution")} WHERE {s.Dialect.TextKey("uniquename")} = {s.Dialect.TextKey("@name")}", "1"));
        cmd.AddParameter("@name", DbType.String, uniqueName, SolutionComponentTracker.UniqueNameMaxLength + HoldingSuffix.Length);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<string?> SolutionNameAsync(DataSession s, Guid solutionId, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT uniquename FROM {s.DataTable("solution")} WHERE solutionid = @id");
        cmd.AddParameter("@id", DbType.Guid, solutionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
