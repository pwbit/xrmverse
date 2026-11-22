namespace Xrmd.Security;

/// <summary>
/// The manager hierarchy (<c>systemuser.parentsystemuserid</c>, plan 2.5) with each user's business unit and teams. Pure:
/// built from the rows, it reads nothing itself. A cycle in the data (written by SQL or an older version) cannot loop: each
/// walk visits every user once.
/// </summary>
public sealed class ReportingTree
{
    private static readonly IReadOnlySet<Guid> NoTeams = new HashSet<Guid>();

    private readonly IReadOnlyDictionary<Guid, (Guid? Manager, Guid BusinessUnit)> _users;
    private readonly IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> _teamsOfUser;
    private readonly Dictionary<Guid, List<Guid>> _directReports = [];

    /// <param name="users">User id → its manager (null for none) and its business unit.</param>
    /// <param name="teamsOfUser">User id → the teams it is a member of (a missing user has none).</param>
    public ReportingTree(IReadOnlyDictionary<Guid, (Guid? Manager, Guid BusinessUnit)> users,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> teamsOfUser)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(teamsOfUser);
        _users = users;
        _teamsOfUser = teamsOfUser;
        foreach (var (id, (manager, _)) in users)
            if (manager is { } m)
            {
                if (!_directReports.TryGetValue(m, out var list)) _directReports[m] = list = [];
                list.Add(id);
            }
    }

    /// <summary>
    /// The users below <paramref name="managerId"/> down to <paramref name="depth"/> levels (1 = direct reports), breadth
    /// first, each user once; the manager itself is never one of its reports. Empty for an unknown user or a depth below 1.
    /// </summary>
    public IReadOnlyList<(Guid UserId, int Level)> ReportsOf(Guid managerId, int depth)
    {
        var result = new List<(Guid, int)>();
        if (depth < 1 || !_users.ContainsKey(managerId)) return result;
        var visited = new HashSet<Guid> { managerId };
        var level = new List<Guid> { managerId };
        for (var current = 1; current <= depth && level.Count > 0; current++)
        {
            var next = new List<Guid>();
            foreach (var manager in level)
                if (_directReports.TryGetValue(manager, out var reports))
                    foreach (var report in reports)
                        if (visited.Add(report))
                        {
                            result.Add((report, current));
                            next.Add(report);
                        }
            level = next;
        }
        return result;
    }

    /// <summary>The teams the user is a member of (owner and access teams); empty for an unknown user.</summary>
    public IReadOnlySet<Guid> TeamsOf(Guid userId) => _teamsOfUser.GetValueOrDefault(userId) ?? NoTeams;

    /// <summary>The user's business unit; null for an unknown user.</summary>
    public Guid? BusinessUnitOf(Guid userId) => _users.TryGetValue(userId, out var user) ? user.BusinessUnit : null;
}
