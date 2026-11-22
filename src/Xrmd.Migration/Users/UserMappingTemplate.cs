using System.Text;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Users;

/// <summary>
/// The users mapping template (spec §7.4): one entry per exported user, with the action its kind gets by default.
/// Licensed users with an Entra object id → <c>entra</c>; disabled, unlicensed and application users (access mode 4 or 5)
/// → <c>stub</c>; enabled licensed users without an object id → <c>local</c>, user name from the UPN. The SYSTEM and
/// INTEGRATION users are not listed: the import maps them onto the target's system user (spec §7.3).
/// </summary>
public static class UserMappingTemplate
{
    public static async Task WriteAsync(PackageReader reader, TextWriter output, CancellationToken ct)
    {
        await output.WriteLineAsync("# xrmd migrate users: edit the actions, then pass the file to the import with --users.");
        await output.WriteLineAsync("# Actions: entra | entra-remap (objectid: <new id>) | local (username: <name>) | existing (target: <id>) | stub");
        await output.WriteLineAsync("defaults: { licensed: entra, disabled: stub, application: stub }");
        var table = reader.Manifest.Tables.FirstOrDefault(t => t.LogicalName == "systemuser");
        if (table is null || table.Records == 0)
        {
            await output.WriteLineAsync("users: []");
            return;
        }
        var any = false;
        await foreach (var user in ChunkReader.ReadAsync(reader, table.Chunks, ct))
        {
            var name = Text(user, "fullname");
            if (IsSingleton(user, name)) continue;
            if (!any) await output.WriteLineAsync("users:");
            any = true;
            var upn = Text(user, "domainname");
            var objectId = ObjectId(user);
            var kind = Kind(user);
            var action = DefaultAction(user);
            var why = kind == ApplicationKind ? "application user" : user.Values.GetValueOrDefault("isdisabled") is BoolValue { Value: true } ? "disabled"
                : kind == DisabledKind ? "not licensed" : objectId is not null ? "licensed, same tenant" : "no Entra object id";

            await output.WriteLineAsync($"  - id: {user.Id}");
            if (name is not null) await output.WriteLineAsync($"    name: {Quote(name)}");
            if (upn is not null) await output.WriteLineAsync($"    upn: {Quote(upn)}");
            await output.WriteLineAsync($"    action: {action}   # {why}");
            if (action == "local") await output.WriteLineAsync($"    username: {Quote(UserName(upn ?? name ?? user.Id.ToString()))}");
        }
        if (!any) await output.WriteLineAsync("users: []");
    }

    /// <summary>The kinds of user the mapping's <c>defaults</c> name.</summary>
    public const string LicensedKind = "licensed", DisabledKind = "disabled", ApplicationKind = "application";

    /// <summary>
    /// The kind of <paramref name="user"/>: an application user (access mode 4 or 5), a disabled or unlicensed one, or a licensed
    /// one (<c>islicensed</c> absent counts as licensed).
    /// </summary>
    public static string Kind(MigrationRecord user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Values.GetValueOrDefault("accessmode") is OptionValue { Value: 4 or 5 }) return ApplicationKind;
        if (user.Values.GetValueOrDefault("isdisabled") is BoolValue { Value: true } || user.Values.GetValueOrDefault("islicensed") is BoolValue { Value: false })
            return DisabledKind;
        return LicensedKind;
    }

    /// <summary>The action the template gives <paramref name="user"/>: <c>stub</c>, <c>entra</c> (it has an object id) or <c>local</c>.</summary>
    public static string DefaultAction(MigrationRecord user) =>
        Kind(user) != LicensedKind ? "stub" : ObjectId(user) is not null ? "entra" : "local";

    /// <summary>The user's Entra object id, if exported.</summary>
    public static Guid? ObjectId(MigrationRecord user) =>
        user?.Values.GetValueOrDefault("azureactivedirectoryobjectid") is GuidValue g ? g.Value : null;

    /// <summary>The user name the template proposes for a local user: the UPN's part before '@', else the name, else the id.</summary>
    public static string ProposedUserName(MigrationRecord user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return UserName(Text(user, "domainname") ?? Text(user, "fullname") ?? user.Id.ToString());
    }

    private static readonly string[] SingletonNames = ["SYSTEM", "INTEGRATION"];

    /// <summary>SYSTEM and INTEGRATION: built-in users with no sign-in name.</summary>
    public static bool IsSingleton(MigrationRecord user) => IsSingleton(user, Text(user, "fullname"));

    private static bool IsSingleton(MigrationRecord user, string? name) =>
        name is not null && SingletonNames.Contains(name, StringComparer.Ordinal) && string.IsNullOrEmpty(Text(user, "domainname"));

    private static string? Text(MigrationRecord r, string column) => r.Values.GetValueOrDefault(column) is StringValue s ? s.Value : null;

    private static string UserName(string upn)
    {
        var at = upn.IndexOf('@');
        return at > 0 ? upn[..at] : upn;
    }

    /// <summary>A double-quoted YAML scalar: safe for any text (colons, hashes, quotes, leading spaces).</summary>
    private static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
