namespace Xrmd.Security;

/// <summary>The standard roles, identified by their Dataverse role template id (which is also the <c>roleid</c> in the root business unit).</summary>
public static class StandardRoles
{
    /// <summary>System Administrator: every privilege at Global depth, recognized by template, with no <c>roleprivileges</c> rows.</summary>
    public static readonly Guid SystemAdministrator = new("627090FF-40A3-4053-8790-584EDC5BE201");

    /// <summary>
    /// System Customizer: customization of roles, plug-in registration, solutions, publishers, views, forms, web resources,
    /// site maps and apps, the metadata privileges (<see cref="PrivilegeNames.Metadata"/>), the solution import and export
    /// privileges (<see cref="PrivilegeNames.Solution"/>) and read access to the system
    /// tables (solution components and import jobs included).
    /// </summary>
    public static readonly Guid SystemCustomizer = new("119F245C-3CC8-4B62-B31C-D1A046CED15D");

    /// <summary>Basic User: read access to the organization's directory tables, and the field share privileges (Create, Read, Write, Delete of <c>principalobjectattributeaccess</c>).</summary>
    public static readonly Guid BasicUser = new("236750CD-45AE-4939-AB12-B24B920CED93");

    /// <summary>Delegate (template "Proxy"): only the privilege to act on behalf of another user.</summary>
    public static readonly Guid Delegate = new("D892CC0B-28C7-4E88-BD92-72F2C366BAED");

    internal static readonly IReadOnlyList<(Guid Id, string Name, string Description)> All =
    [
        (SystemAdministrator, "System Administrator", "Full access to customize and administer the organization."),
        (SystemCustomizer, "System Customizer", "Can customize the organization and register plug-ins."),
        (BasicUser, "Basic User", "Can read the organization's directory tables."),
        (Delegate, "Delegate", "Can act on behalf of another user."),
    ];
}
