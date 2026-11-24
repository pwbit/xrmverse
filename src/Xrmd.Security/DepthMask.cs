using Xrmd.Model.Security;

namespace Xrmd.Security;

/// <summary>The depth of a privilege, as stored in <c>roleprivileges.privilegedepthmask</c>.</summary>
[Flags]
public enum DepthMask
{
    /// <summary>Not granted.</summary>
    None = 0,

    /// <summary>User: the records the user (or one of its teams) owns.</summary>
    Basic = 1,

    /// <summary>Business unit: the records of the user's business unit.</summary>
    Local = 2,

    /// <summary>Parent: child business units: the records of the user's business unit and of the units below it.</summary>
    Deep = 4,

    /// <summary>Organization: every record.</summary>
    Global = 8,
}

/// <summary>Conversions of <see cref="DepthMask"/>.</summary>
public static partial class DepthMasks
{
    /// <summary>The mask of a scope: Basic 0→1, Local 1→2, Deep 2→4, Global 3→8.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the four depths (RecordFilter and undefined values included).</exception>
    public static DepthMask From(PrivilegeScope depth) => depth switch
    {
        PrivilegeScope.Basic => DepthMask.Basic,
        PrivilegeScope.Local => DepthMask.Local,
        PrivilegeScope.Deep => DepthMask.Deep,
        PrivilegeScope.Global => DepthMask.Global,
        _ => throw new ArgumentOutOfRangeException(nameof(depth), depth, "Unknown privilege depth."),
    };

    /// <summary>The widest depth in <paramref name="mask"/> (<see cref="DepthMask.None"/> when empty).</summary>
    public static DepthMask Highest(DepthMask mask) =>
        mask.HasFlag(DepthMask.Global) ? DepthMask.Global
        : mask.HasFlag(DepthMask.Deep) ? DepthMask.Deep
        : mask.HasFlag(DepthMask.Local) ? DepthMask.Local
        : mask.HasFlag(DepthMask.Basic) ? DepthMask.Basic
        : DepthMask.None;
}
