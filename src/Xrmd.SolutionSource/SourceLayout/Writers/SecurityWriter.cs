namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Security: each role in <c>Roles/&lt;name&gt;.xml</c>; the field security profiles together in
/// <c>Other/FieldSecurityProfiles.xml</c>.
/// </summary>
internal static class SecurityWriter
{
    public static void Write(UnpackContext context)
    {
        var roles = context.Collection("Roles");
        if (roles is not null)
        {
            RefuseRolesSharingAName(roles);
            foreach (var role in roles.Elements())
            {
                if (role.Name != "Role") throw UnpackRules.Unsupported(role);
                var name = UnpackRules.SafeSegment((string?)role.Attribute("name") ?? "", "role name");
                context.Write($"Roles/{name}.xml", LayoutXml.RootFrom("Role", role));
            }
            roles.RemoveNodes();
        }

        var profiles = context.Collection("FieldSecurityProfiles");
        if (profiles is not null && profiles.HasElements)
        {
            foreach (var profile in profiles.Elements())
                if (profile.Name != "FieldSecurityProfile") throw UnpackRules.Unsupported(profile);
            context.Write("Other/FieldSecurityProfiles.xml", LayoutXml.RootFrom("FieldSecurityProfiles", profiles));
            profiles.RemoveNodes();
        }
    }

    /// <summary>
    /// Refuses roles that share a name: each role is the file <c>Roles/&lt;name&gt;.xml</c>, and the reference folders show
    /// no file name for a second role of the same name (Dataverse lets a solution import add one). Names are compared
    /// ignoring case, as a file system may. The fault names the role and every id, so the extra roles can be found and removed.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 <see cref="Xrmd.Core.Errors.ErrorCodes.UnsupportedComponent"/> (exit 5).</exception>
    private static void RefuseRolesSharingAName(System.Xml.Linq.XElement roles)
    {
        var shared = roles.Elements("Role")
            .GroupBy(r => (string?)r.Attribute("name") ?? "", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (shared is null) return;
        var ids = string.Join(", ", shared.Select(r => (string?)r.Attribute("id") ?? "(no id)"));
        throw UnpackRules.Unsupported($"role shared by {shared.Count()} roles (ids {ids})", shared.Key);
    }

    /// <summary>Pack: the roles and the field security profiles.</summary>
    public static void Read(PackContext context)
    {
        var roles = context.Unclaimed("Roles/").Where(k => !k["Roles/".Length..].Contains('/')).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (roles.Count > 0)
        {
            var container = context.Container("Roles");
            foreach (var path in roles) container.Add(PackContext.FromRoot("Role", context.Load(path, "Role", "the roles")));
        }

        const string profiles = "Other/FieldSecurityProfiles.xml";
        if (context.Has(profiles))
            context.Container("FieldSecurityProfiles").Add(context.Load(profiles, "FieldSecurityProfiles", "the field security profiles").Elements());
    }
}
