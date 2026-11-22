namespace Xrmd.Security;

/// <summary>The business unit hierarchy, from the <c>parentbusinessunitid</c> of every unit (null for the root).</summary>
public sealed class BusinessUnitTree
{
    private readonly IReadOnlyDictionary<Guid, Guid?> _parents;
    private readonly Dictionary<Guid, List<Guid>> _children = [];
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _subtrees = [];
    private readonly object _lock = new();

    /// <param name="parents">Business unit id → parent id (null for the root).</param>
    public BusinessUnitTree(IReadOnlyDictionary<Guid, Guid?> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        _parents = parents;
        foreach (var (id, parent) in parents)
            if (parent is { } p)
            {
                if (!_children.TryGetValue(p, out var list)) _children[p] = list = [];
                list.Add(id);
            }
    }

    /// <summary>The unit and every unit below it; empty for an unknown unit.</summary>
    public IReadOnlySet<Guid> SubtreeOf(Guid businessUnitId)
    {
        lock (_lock)
        {
            if (_subtrees.TryGetValue(businessUnitId, out var cached)) return cached;
            var result = new HashSet<Guid>();
            if (_parents.ContainsKey(businessUnitId))
            {
                var pending = new Stack<Guid>([businessUnitId]);
                while (pending.TryPop(out var id))
                    if (result.Add(id) && _children.TryGetValue(id, out var children))
                        foreach (var child in children) pending.Push(child);
            }
            _subtrees[businessUnitId] = result;
            return result;
        }
    }

    /// <summary>The ancestors of the unit, from its parent up to the root (the unit excluded); empty for an unknown unit.</summary>
    public IReadOnlyList<Guid> AncestorsOf(Guid businessUnitId)
    {
        var result = new List<Guid>();
        var seen = new HashSet<Guid> { businessUnitId };
        var current = _parents.GetValueOrDefault(businessUnitId);
        while (current is { } id && seen.Add(id)) // seen: a cycle in the data cannot loop forever
        {
            result.Add(id);
            current = _parents.GetValueOrDefault(id);
        }
        return result;
    }
}
