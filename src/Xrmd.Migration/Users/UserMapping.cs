using System.Text.Json.Nodes;

namespace Xrmd.Migration.Users;

/// <summary>A users mapping that cannot be used: the server answers 400, the CLI exits 2.</summary>
public sealed class UserMappingException(string message) : Exception(message);

/// <summary>The actions of the users mapping (migration spec §7.4).</summary>
public static class UserActions
{
    public const string Entra = "entra", EntraRemap = "entra-remap", Local = "local", Existing = "existing", Stub = "stub";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Entra, EntraRemap, Local, Existing, Stub };
}

/// <summary>One user of the mapping: its Dataverse id (kept), the action, and the action's argument.</summary>
public sealed record UserMappingEntry(Guid Id, string Action, Guid? ObjectId, string? UserName, Guid? Target);

/// <summary>The default action of each kind of user the mapping does not list.</summary>
public sealed record UserDefaults(string Licensed, string Disabled, string Application);

/// <summary>
/// The users mapping of an import (migration spec §7.4), as JSON in the start request: the shape of the YAML template that
/// <c>xrmd migrate users</c> writes (<c>{"defaults":{…},"users":[{"id","action","objectid"|"username"|"target"}]}</c>).
/// </summary>
public sealed record UserMapping(UserDefaults Defaults, IReadOnlyDictionary<Guid, UserMappingEntry> Users)
{
    public static readonly UserMapping Empty = new(new UserDefaults(UserActions.Entra, UserActions.Stub, UserActions.Stub), new Dictionary<Guid, UserMappingEntry>());

    /// <summary>Parses and checks the mapping; null is the empty mapping (every user gets the default of its kind).</summary>
    /// <exception cref="UserMappingException">An unknown action, a missing argument, a user listed twice or an object id used twice.</exception>
    public static UserMapping Parse(JsonNode? json)
    {
        if (json is null) return Empty;
        if (json is not JsonObject o) throw new UserMappingException("'users' must be an object: {\"defaults\": {...}, \"users\": [...]}.");
        var defaults = Empty.Defaults;
        if (o["defaults"] is JsonObject d)
            defaults = new UserDefaults(DefaultAction(d, "licensed", defaults.Licensed), DefaultAction(d, "disabled", defaults.Disabled),
                DefaultAction(d, "application", defaults.Application));
        var users = new Dictionary<Guid, UserMappingEntry>();
        var objectIds = new HashSet<Guid>();
        foreach (var node in o["users"] as JsonArray ?? [])
        {
            var id = Guid.TryParse(Text(node, "id"), out var g) ? g : throw new UserMappingException("Every user of the mapping needs an 'id' (a GUID).");
            var action = Text(node, "action") ?? throw new UserMappingException($"The user {id} of the mapping has no 'action'.");
            if (!UserActions.All.Contains(action))
                throw new UserMappingException($"The user {id} of the mapping has an unknown action '{action}' (entra, entra-remap, local, existing, stub).");
            Guid? objectId = Guid.TryParse(Text(node, "objectid"), out var oid) ? oid : null;
            Guid? target = Guid.TryParse(Text(node, "target"), out var t) ? t : null;
            var userName = Text(node, "username");
            switch (action)
            {
                case UserActions.EntraRemap when objectId is null:
                    throw new UserMappingException($"The user {id} of the mapping is entra-remap without an 'objectid'.");
                case UserActions.Local when string.IsNullOrWhiteSpace(userName):
                    throw new UserMappingException($"The user {id} of the mapping is local without a 'username'.");
                case UserActions.Existing when target is null:
                    throw new UserMappingException($"The user {id} of the mapping is existing without a 'target'.");
            }
            if (objectId is { } duplicate && !objectIds.Add(duplicate))
                throw new UserMappingException($"The object id {duplicate} is given to more than one user of the mapping.");
            if (!users.TryAdd(id, new UserMappingEntry(id, action, objectId, userName, target)))
                throw new UserMappingException($"The user {id} is listed more than once in the mapping.");
        }
        return new UserMapping(defaults, users);
    }

    /// <summary>The mapping as JSON, as it is stored in the run's options.</summary>
    public JsonObject ToJson() => new()
    {
        ["defaults"] = new JsonObject { ["licensed"] = Defaults.Licensed, ["disabled"] = Defaults.Disabled, ["application"] = Defaults.Application },
        ["users"] = new JsonArray([.. Users.Values.Select(u => (JsonNode)new JsonObject
        {
            ["id"] = u.Id.ToString(), ["action"] = u.Action, ["objectid"] = u.ObjectId?.ToString(), ["username"] = u.UserName,
            ["target"] = u.Target?.ToString(),
        })]),
    };

    private static string DefaultAction(JsonObject defaults, string kind, string fallback)
    {
        var action = Text(defaults, kind) ?? fallback;
        return action is UserActions.Entra or UserActions.Local or UserActions.Stub
            ? action
            : throw new UserMappingException($"The default action for {kind} users must be entra, local or stub, not '{action}'.");
    }

    private static string? Text(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
