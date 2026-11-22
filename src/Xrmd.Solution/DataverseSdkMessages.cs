namespace Xrmd.Solution;

/// <summary>
/// The well-known <c>sdkmessageid</c> values Dataverse writes in exported steps, by message name. xrmverse derives its own
/// <c>sdkmessage</c> ids (<see cref="Xrmd.Metadata.SdkMessageCatalog.MessageId"/>), so the importer maps an exported id to
/// the message name and the name to the xrmverse row; the exporter does the reverse. Only the messages listed here are
/// recognized by id: an id that is neither here nor an <c>sdkmessage</c> row of the organization fails the import (400).
/// </summary>
/// <remarks>
/// Not published on Microsoft Learn: every value was observed in a real Dataverse export (version 9.2.26102, 2026-11-10),
/// which used these six messages in its steps. An id not listed here makes the import of such a step fail explicitly,
/// never bind the step to another message.
/// </remarks>
public static class DataverseSdkMessages
{
    private static readonly Dictionary<Guid, string> NameById = new()
    {
        [new Guid("9ebdbb1b-ea3e-db11-86a7-000a3a5473e8")] = "Create",
        [new Guid("20bebb1b-ea3e-db11-86a7-000a3a5473e8")] = "Update",
        [new Guid("a1bdbb1b-ea3e-db11-86a7-000a3a5473e8")] = "Delete",
        [new Guid("f2bdbb1b-ea3e-db11-86a7-000a3a5473e8")] = "Retrieve",
        [new Guid("03bebb1b-ea3e-db11-86a7-000a3a5473e8")] = "RetrieveMultiple",
        [new Guid("16e7c888-3b24-448b-b7da-ebfe9b681762")] = "Associate",
    };

    private static readonly Dictionary<string, Guid> IdByName =
        NameById.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The message name of a Dataverse well-known id; null when the id is not in the table.</summary>
    public static string? NameOf(Guid dataverseId) => NameById.GetValueOrDefault(dataverseId);

    /// <summary>The Dataverse well-known id of a message; null when the message is not in the table.</summary>
    public static Guid? IdOf(string messageName) => IdByName.TryGetValue(messageName, out var id) ? id : null;
}
