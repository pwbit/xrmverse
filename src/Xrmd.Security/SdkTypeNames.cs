using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Model.Security;

namespace Xrmd.Security;

/// <summary>
/// S.2e3c2a Decision 2: the type name a fault text shows for a value. The rules match model values, but a text that names a value's
/// type keeps the SDK name it shows today ("got a Money", "not a EntityReference"): each model value type gives the simple name of its
/// SDK counterpart (the set the SDK mirror converts), written here as a string literal; an array of one of them that name with
/// <c>[]</c>; any other type its own <see cref="Type.Name"/>. Task 4 (ruling 3a): a text that prints the value itself prints it as the
/// SDK object the caller sent did (<see cref="Text"/>).
/// </summary>
internal static class SdkTypeNames
{
    /// <summary>The full name of the SDK counterpart of each model value type.</summary>
    private static readonly Dictionary<Type, string> FullNames = new()
    {
        [typeof(Record)] = "Microsoft.Xrm.Sdk.Entity",
        [typeof(RecordReference)] = "Microsoft.Xrm.Sdk.EntityReference",
        [typeof(RecordCollection)] = "Microsoft.Xrm.Sdk.EntityCollection",
        [typeof(RecordReferenceCollection)] = "Microsoft.Xrm.Sdk.EntityReferenceCollection",
        [typeof(ChoiceValue)] = "Microsoft.Xrm.Sdk.OptionSetValue",
        [typeof(ChoiceValues)] = "Microsoft.Xrm.Sdk.OptionSetValueCollection",
        [typeof(MoneyValue)] = "Microsoft.Xrm.Sdk.Money",
        [typeof(ColumnSelection)] = "Microsoft.Xrm.Sdk.Query.ColumnSet",
        [typeof(RecordQuery)] = "Microsoft.Xrm.Sdk.Query.QueryExpression",
        [typeof(AttributeQuery)] = "Microsoft.Xrm.Sdk.Query.QueryByAttribute",
        [typeof(FetchQuery)] = "Microsoft.Xrm.Sdk.Query.FetchExpression",
        [typeof(RelationshipName)] = "Microsoft.Xrm.Sdk.Relationship",
        [typeof(AliasedColumnValue)] = "Microsoft.Xrm.Sdk.AliasedValue",
        [typeof(AccessMask)] = "Microsoft.Crm.Sdk.Messages.AccessRights",
        [typeof(PrincipalAccessValue)] = "Microsoft.Crm.Sdk.Messages.PrincipalAccess",
        [typeof(PrivilegeGrant)] = "Microsoft.Crm.Sdk.Messages.RolePrivilege",
        [typeof(ParameterBag)] = "Microsoft.Xrm.Sdk.ParameterCollection",
    };

    /// <summary>The name a fault text shows for a value of <paramref name="type"/>.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return SdkName(type) is { } full ? full[(full.LastIndexOf('.') + 1)..] : type.Name;
    }

    /// <summary>
    /// What a fault text prints for <paramref name="value"/>: the <see cref="object.ToString"/> of the SDK object the caller sent, which
    /// for each of these classes (and their arrays) is its full type name, except a relationship, which prints its schema name and role
    /// (<c>name.Role</c>, the role's member names being the SDK's); an enum, whose member names are the SDK's, and any other value print
    /// themselves.
    /// </summary>
    public static string? Text(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            RelationshipName relationship => $"{relationship.SchemaName}.{relationship.PrimaryEntityRole}",
            Enum => value.ToString(),
            _ => SdkName(value.GetType()) ?? value.ToString(),
        };
    }

    /// <summary>
    /// Fix round 1 (ruling 4b): <see cref="Record.GetValue{T}(string)"/> as the SDK's <c>GetAttributeValue&lt;T&gt;</c> answered it
    /// (default when absent or null, the value when it is a <typeparamref name="T"/>), except that a value of another type throws the
    /// <see cref="InvalidCastException"/> text the runtime gave for the SDK cast: <see cref="CastText"/> names the SDK types. The text
    /// reaches plug-ins and custom workflow activities as the fault message, so every model read in Xrmd.Security that replaced a
    /// <c>GetAttributeValue</c> goes through here.
    /// </summary>
    /// <exception cref="InvalidCastException">The stored value is not a <typeparamref name="T"/>.</exception>
    public static T? SdkValue<T>(this Record record, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return record.GetValue<T>(attributeName);
        }
        catch (InvalidCastException)
        {
            throw new InvalidCastException(CastText(record[attributeName]!.GetType(), typeof(T)));
        }
    }

    /// <summary>
    /// The runtime's text for a failed cast of a <paramref name="from"/> to a <paramref name="to"/>, as observed on the SDK cast,
    /// with each model value type named by its SDK counterpart's full name and any other type by its <see cref="Type.ToString"/>
    /// (<c>System.Nullable`1[System.Guid]</c>).
    /// </summary>
    public static string CastText(Type from, Type to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return $"Unable to cast object of type '{SdkName(from) ?? from.ToString()}' to type '{SdkName(to) ?? to.ToString()}'.";
    }

    /// <summary>The full SDK name of <paramref name="type"/> (an array of one with <c>[]</c>); null for a type that has none.</summary>
    private static string? SdkName(Type type)
    {
        if (FullNames.TryGetValue(type, out var name)) return name;
        return type.IsArray && type.GetElementType() is { } element && FullNames.TryGetValue(element, out var elementName)
            ? elementName + "[]"
            : null;
    }
}
