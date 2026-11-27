using Xrmd.Core.Errors;

namespace Xrmd.Solution.SourceLayout;

/// <summary>What <see cref="SourceFolder.Plan"/> decided: the files to write and the files to delete (relative, <c>/</c> separators).</summary>
public sealed record SyncPlan(IReadOnlyList<string> Write, IReadOnlyList<string> Remove);

/// <summary>
/// Reads and writes an unpacked solution folder (the layout of the Power Platform CLI: <c>Other/Solution.xml</c>,
/// <c>Other/Customizations.xml</c>, <c>Entities/…</c>). The folder content is treated as untrusted: links that lead out of
/// the folder are refused, and nothing is ever written or deleted outside it. Only the component folders of the layout
/// (<see cref="InComponentFolder"/>) are read, written or cleaned up: anything else in the folder (a README, <c>.git/</c>,
/// CI files, notes) is never listed, compared, packed or removed. Files named <c>*_managed.xml</c> belong to the managed
/// variant of the solution and are neither read nor removed.
/// </summary>
public static class SourceFolder
{
    private const string SolutionFile = "Other/Solution.xml";

    /// <summary>
    /// The folder that holds <c>Other/Solution.xml</c>: <paramref name="dir"/> itself, or <c>dir/src</c> when
    /// <paramref name="dir"/> holds a <c>.cdsproj</c> file.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when neither holds an unpacked solution.</exception>
    public static string Resolve(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        if (File.Exists(Path.Combine(dir, "Other", "Solution.xml"))) return dir;
        var src = Path.Combine(dir, "src");
        if (HasCdsproj(dir) && File.Exists(Path.Combine(src, "Other", "Solution.xml"))) return src;
        throw XrmdFaultException.InvalidArgument($"The folder '{dir}' holds no unpacked solution (no Other/Solution.xml, also not under src/ of a .cdsproj folder).");
    }

    /// <summary>
    /// Where a write goes: <see cref="Resolve"/> when it succeeds, else <c>dir/src</c> when <paramref name="dir"/> holds a
    /// <c>.cdsproj</c>, else <paramref name="dir"/>. Creates nothing; <paramref name="dir"/> need not exist.
    /// </summary>
    public static string ResolveForWrite(string dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        try { return Resolve(dir); }
        catch (XrmdFaultException) { }
        return HasCdsproj(dir) ? Path.Combine(dir, "src") : dir;
    }

    /// <summary>Every file in the component folders of the resolved folder except <c>*_managed.xml</c>, with its bytes.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when no solution is found or a link leads out of the folder.</exception>
    public static SourceFiles Read(string dir)
    {
        var root = Resolve(dir);
        var files = new SourceFiles();
        foreach (var key in Enumerate(root))
        {
            files.Add(key, File.ReadAllBytes(ToPath(root, key)));
        }
        return files;
    }

    /// <summary>
    /// Compares <paramref name="wanted"/> with the folder: Write holds the keys missing on disk or whose content differs
    /// (<see cref="SourceComparer.SameContent"/>: a file that differs only in formatting or in the order of a table's columns
    /// is left as it is, so a sync after a clean diff writes nothing), Remove the files in the component folders (never <c>*_managed.xml</c>) that are not wanted. Both are sorted ordinally.
    /// </summary>
    public static SyncPlan Plan(string dir, SourceFiles wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var root = ResolveForWrite(dir);
        var onDisk = Directory.Exists(root) ? Enumerate(root).ToHashSet(StringComparer.Ordinal) : [];

        var wantedFolded = new HashSet<string>(wanted.Keys.Where(k => !IsManaged(k)), StringComparer.OrdinalIgnoreCase);
        var write = new List<string>();
        foreach (var (key, bytes) in wanted)
        {
            if (IsManaged(key)) continue;
            if (!onDisk.Contains(key) || !SourceComparer.SameContent(key, File.ReadAllBytes(ToPath(root, key)), bytes)) write.Add(key);
        }
        // A file that differs from a wanted key only by case is the same file on a case-insensitive file system: Apply renames it.
        var remove = onDisk.Where(k => !wantedFolded.Contains(k)).ToList();
        write.Sort(StringComparer.Ordinal);
        remove.Sort(StringComparer.Ordinal);
        return new SyncPlan(write, remove);
    }

    /// <summary>
    /// The one scope rule of unpack, pack, diff and sync: whether <paramref name="key"/> lies in a component folder of the
    /// layout (<c>Other/</c>, <c>Entities/</c>, <c>WebResources/</c> …, matched by exact case) and not inside a folder whose
    /// name starts with a dot. Files elsewhere (a README, <c>.git/</c>, CI files, notes a person keeps beside the solution)
    /// are never read, compared, packed, written or removed.
    /// </summary>
    public static bool InComponentFolder(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!LayoutFolders.IsComponent(key)) return false;
        var segments = key.Split('/');
        return segments[^1].Length > 0 && !segments[..^1].Any(s => s.StartsWith('.'));
    }

    /// <summary>
    /// Carries out <paramref name="plan"/>: writes the <see cref="SyncPlan.Write"/> files (bytes taken from
    /// <paramref name="wanted"/>), deletes the <see cref="SyncPlan.Remove"/> files and removes the directories that this
    /// leaves empty (never the folder itself).
    /// </summary>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument when a path is unsafe, lies outside the component folders (<see cref="InComponentFolder"/>), is
    /// a name to write that some file system cannot hold (<see cref="UnpackRules.IsPortableSegment"/>), is missing from
    /// <paramref name="wanted"/>, or passes a link that leads out of the folder. Every path is checked before the first
    /// change is made.
    /// </exception>
    public static void Apply(string dir, SyncPlan plan, SourceFiles wanted)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(wanted);
        var root = ResolveForWrite(dir);
        var rootFull = FullRoot(root);

        foreach (var key in plan.Write.Concat(plan.Remove))
        {
            SourceFiles.ValidateKey(key);
            if (IsManaged(key)) throw XrmdFaultException.InvalidArgument($"The managed file '{key}' is not part of a source folder sync.");
            if (!InComponentFolder(key))
                throw XrmdFaultException.InvalidArgument($"'{key}' lies outside the component folders of the layout; a source folder update never writes or removes it.");
            RefuseLinkOut(rootFull, key);
        }
        foreach (var key in plan.Write)
        {
            if (!key.Split('/').All(UnpackRules.IsPortableSegment))
                throw XrmdFaultException.InvalidArgument($"The path '{key}' holds a name that cannot be written on every file system (Windows reserves it or a character in it).");
            if (!wanted.ContainsKey(key)) throw XrmdFaultException.InvalidArgument($"The file '{key}' is to be written but has no content.");
        }

        var variants = new List<string>();
        if (Directory.Exists(root))
        {
            var disk = Enumerate(root).ToList();
            var writeSet = plan.Write.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var exact = plan.Write.ToHashSet(StringComparer.Ordinal);
            variants.AddRange(disk.Where(k => writeSet.Contains(k) && !exact.Contains(k)));
        }
        foreach (var variant in variants)
        {
            File.Delete(ToPath(root, variant));
            PruneEmptyParents(rootFull, Path.GetDirectoryName(Path.GetFullPath(ToPath(root, variant)))!);
        }

        Directory.CreateDirectory(root);
        foreach (var key in plan.Write)
        {
            var path = ToPath(root, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, wanted[key]);
        }
        foreach (var key in plan.Remove)
        {
            var path = ToPath(root, key);
            if (File.Exists(path)) File.Delete(path);
            PruneEmptyParents(rootFull, Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    private static string FullRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static bool HasCdsproj(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.cdsproj").Any();

    private static bool IsManaged(string key) => key.EndsWith("_managed.xml", StringComparison.OrdinalIgnoreCase);

    private static string ToPath(string root, string key) => Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));

    private static IEnumerable<string> Enumerate(string root)
    {
        var rootFull = FullRoot(root);
        var result = new List<string>();
        Walk(rootFull, rootFull, "", result);
        return result;
    }

    private static void Walk(string rootFull, string current, string prefix, List<string> result)
    {
        foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
        {
            var key = prefix + entry.Name;
            // Only the component folders are looked at: at the top every other entry is skipped unopened (links included),
            // and below it a folder whose name starts with a dot is never entered.
            if (prefix.Length == 0 ? entry is not DirectoryInfo || !LayoutFolders.IsComponent(key + "/") : entry is DirectoryInfo && entry.Name.StartsWith('.')) continue;
            var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isLink)
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: true);
                var full = Path.GetFullPath(target?.FullName ?? entry.FullName);
                if (target is null || !IsInside(rootFull, full))
                    throw XrmdFaultException.InvalidArgument($"'{key}' is a link that leads out of the solution folder.");
            }
            if (entry is DirectoryInfo)
            {
                if (!isLink) Walk(rootFull, entry.FullName, key + "/", result);
            }
            else if (!IsManaged(entry.Name))
            {
                result.Add(key);
            }
        }
    }

    private static bool IsInside(string rootFull, string full)
    {
        var rel = Path.GetRelativePath(rootFull, full);
        return rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }

    private static void RefuseLinkOut(string rootFull, string key)
    {
        var path = rootFull;
        foreach (var segment in key.Split('/'))
        {
            path = Path.Combine(path, segment);
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null) info = new DirectoryInfo(path);
            if (!info.Exists && info.LinkTarget is null) return;
            if (info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsInside(rootFull, Path.GetFullPath(target.FullName)))
                throw XrmdFaultException.InvalidArgument($"'{key}' passes through a link that leads out of the solution folder.");
        }
    }

    private static void PruneEmptyParents(string rootFull, string directory)
    {
        while (IsInside(rootFull, directory) && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), rootFull, StringComparison.Ordinal)
               && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }
}
