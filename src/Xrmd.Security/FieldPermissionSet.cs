namespace Xrmd.Security;

/// <summary>
/// What one caller may do with the secured columns of one table (ruling F2: the OR of every profile of the user and of its
/// teams). A column that is not secured is always allowed. Logical names are compared ignoring case.
/// </summary>
public sealed record FieldPermissionSet
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The set of a caller field security does not apply to (ruling F1): every column is allowed.</summary>
    public static FieldPermissionSet Unrestricted { get; } = new(None, None, None, None);

    private readonly IReadOnlySet<string> _readable, _creatable, _updatable;

    /// <param name="secured">Every secured column of the table.</param>
    /// <param name="readable">The secured columns the caller may read.</param>
    /// <param name="creatable">The secured columns the caller may set on create.</param>
    /// <param name="updatable">The secured columns the caller may update.</param>
    public FieldPermissionSet(IReadOnlySet<string> secured, IReadOnlySet<string> readable, IReadOnlySet<string> creatable,
        IReadOnlySet<string> updatable)
    {
        ArgumentNullException.ThrowIfNull(secured);
        ArgumentNullException.ThrowIfNull(readable);
        ArgumentNullException.ThrowIfNull(creatable);
        ArgumentNullException.ThrowIfNull(updatable);
        Secured = secured;
        _readable = readable;
        _creatable = creatable;
        _updatable = updatable;
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in secured)
            if (!readable.Contains(column)) unreadable.Add(column);
        UnreadableSecured = unreadable;
    }

    /// <summary>Every secured column of the table.</summary>
    public IReadOnlySet<string> Secured { get; }

    /// <summary>The secured columns the caller cannot read.</summary>
    public IReadOnlySet<string> UnreadableSecured { get; }

    /// <summary>
    /// The AND of two sets (impersonation: the user's and the impersonator's): a column is allowed only when both allow it.
    /// <see cref="Unrestricted"/> is the identity.
    /// </summary>
    public FieldPermissionSet And(FieldPermissionSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, Unrestricted) || ReferenceEquals(other, this)) return this;
        if (ReferenceEquals(this, Unrestricted)) return other;
        var secured = new HashSet<string>(Secured, StringComparer.OrdinalIgnoreCase);
        secured.UnionWith(other.Secured);
        HashSet<string> Both(Func<FieldPermissionSet, Func<string, bool>> can)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in secured)
                if (can(this)(column) && can(other)(column)) allowed.Add(column);
            return allowed;
        }
        return new FieldPermissionSet(secured, Both(s => s.CanRead), Both(s => s.CanCreate), Both(s => s.CanUpdate));
    }

    /// <summary>True when the column is not secured or the caller may read it.</summary>
    public bool CanRead(string attribute) => !Secured.Contains(attribute) || _readable.Contains(attribute);

    /// <summary>True when the column is not secured or the caller may set it on create.</summary>
    public bool CanCreate(string attribute) => !Secured.Contains(attribute) || _creatable.Contains(attribute);

    /// <summary>True when the column is not secured or the caller may update it.</summary>
    public bool CanUpdate(string attribute) => !Secured.Contains(attribute) || _updatable.Contains(attribute);
}

/// <summary>The field shares of one record that apply to a caller: the columns it may read and those it may update.</summary>
public sealed record FieldShareGrant(IReadOnlySet<string> Read, IReadOnlySet<string> Update);
