using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Customer columns (<see cref="AttributeTypeCode.Customer"/>): a lookup that holds an <c>account</c> or a <c>contact</c>,
/// stored as the polymorphic lookups are (the id and a <c>&lt;name&gt;type</c> column) and implying one one-to-many
/// relationship per target. The only way to create one is the <c>CreateCustomerRelationships</c> message, as in Dataverse.
/// </summary>
public static class CustomerColumns
{
    /// <summary>The message that creates a Customer column with its two relationships.</summary>
    public const string CreateMessage = "CreateCustomerRelationships";

    public const string Account = "account", Contact = "contact";

    /// <summary>The targets of every Customer column, in the order the relationships and navigation properties are listed.</summary>
    public static readonly IReadOnlyList<string> Targets = [Account, Contact];

    public static bool IsCustomer(AttributeMetadata? attribute) =>
        attribute is LookupAttributeMetadata { AttributeType: AttributeTypeCode.Customer };

    /// <summary>True when <paramref name="targets"/> holds exactly <c>account</c> and <c>contact</c> (any order, no repeat).</summary>
    public static bool AreCustomerTargets(IEnumerable<string?>? targets)
    {
        var list = (targets ?? []).ToList();
        return list.Count == Targets.Count && Targets.All(t => list.Contains(t, StringComparer.Ordinal));
    }

    /// <summary>400 text of a Customer column defined by another route than <see cref="CreateMessage"/>.</summary>
    public static string OnlyViaMessage(string where) =>
        $"{where} is a Customer column: a Customer column is created only with the {CreateMessage} message (it takes the lookup and its " +
        "two relationships, one to account and one to contact).";

    /// <summary>400 text of a Customer column whose targets are not exactly <c>account</c> and <c>contact</c>.</summary>
    public static string TargetsMessage(string where) =>
        $"{where}: a Customer column targets exactly the tables {Account} and {Contact}, one relationship each.";

    /// <summary>The Web API navigation property of the target <paramref name="target"/> of a Customer column when its relationship names none.</summary>
    public static string DefaultNavigation(string lookupLogicalName, string target) => $"{lookupLogicalName}_{target}";
}
