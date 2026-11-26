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
