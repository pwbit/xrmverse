using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;

namespace Xrmd.Core;

/// <summary>
/// The typed SDK response of each message on the model (S.2e3b, Decision 7; S.2e3c1, Decision 4): the adapter pairs the model response
/// of a handler with one of these and writes it out once, so a caller that takes the SDK response (the pipeline, an extension method)
/// gets the typed object filled.
/// </summary>
internal static class CoreResponses
{
    /// <summary>A new typed response for <paramref name="canonicalName"/>, its <c>ResponseName</c> set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The name is not a message on the model (ordinal, canonical case).</exception>
    public static OrganizationResponse Typed(string canonicalName)
    {
        OrganizationResponse response = canonicalName switch
        {
            "Create" => new CreateResponse(),
            "Retrieve" => new RetrieveResponse(),
            "Update" => new UpdateResponse(),
            "Delete" => new DeleteResponse(),
            "RetrieveMultiple" => new RetrieveMultipleResponse(),
            "Assign" => new AssignResponse(),
            "Associate" => new AssociateResponse(),
            "Disassociate" => new DisassociateResponse(),
            // S.2e3c1 Task 3: access, sharing and record teams.
            "GrantAccess" => new GrantAccessResponse(),
            "ModifyAccess" => new ModifyAccessResponse(),
            "RevokeAccess" => new RevokeAccessResponse(),
            "RetrievePrincipalAccess" => new RetrievePrincipalAccessResponse(),
            "RetrieveSharedPrincipalsAndAccess" => new RetrieveSharedPrincipalsAndAccessResponse(),
            "AddUserToRecordTeam" => new AddUserToRecordTeamResponse(),
            "RemoveUserFromRecordTeam" => new RemoveUserFromRecordTeamResponse(),
            // S.2e3c1 Task 4: role privileges, exchange rate, change tracking.
            "AddPrivilegesRole" => new AddPrivilegesRoleResponse(),
            "ReplacePrivilegesRole" => new ReplacePrivilegesRoleResponse(),
            "RemovePrivilegeRole" => new RemovePrivilegeRoleResponse(),
            "RetrieveUserPrivileges" => new RetrieveUserPrivilegesResponse(),
            "RetrieveExchangeRate" => new RetrieveExchangeRateResponse(),
            "RetrieveEntityChanges" => new RetrieveEntityChangesResponse(),
            // S.2e3c1 Task 5: queues.
            "AddToQueue" => new AddToQueueResponse(),
            "RemoveFromQueue" => new RemoveFromQueueResponse(),
            "RouteTo" => new RouteToResponse(),
            "AddPrincipalToQueue" => new AddPrincipalToQueueResponse(),
            "PickFromQueue" => new PickFromQueueResponse(),
            "ReleaseToQueue" => new ReleaseToQueueResponse(),
            "RetrieveUserQueues" => new RetrieveUserQueuesResponse(),
            // S.2e3c1 Task 6: the non-pipeline reads.
            "WhoAmI" => new WhoAmIResponse(),
            "RetrieveAppComponents" => new RetrieveAppComponentsResponse(),
            "RetrieveUnpublished" => new RetrieveUnpublishedResponse(),
            "InitializeFrom" => new InitializeFromResponse(),
            "RetrieveFormattedImportJobResults" => new RetrieveFormattedImportJobResultsResponse(),
            _ => throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a message on the model."),
        };
        response.ResponseName = canonicalName;
        return response;
    }

    /// <summary>
    /// A new typed request for <paramref name="name"/> (S.2e3b Task 8: the twin of a request the core issues on the model, such as the
    /// Delete of a cascaded child, which the security guard and the plug-ins see as before; S.2e3c1 Task 5, Decision 4: the Create,
    /// Update and Delete of <c>queueitem</c> and the Associate of <c>queuemembership_association</c> the queue messages issue): one of
    /// the eight CRUD requests, or a plain <see cref="OrganizationRequest"/> of that name for any other message.
    /// </summary>
    public static OrganizationRequest TypedRequest(string? name) => name switch
    {
        "Create" => new CreateRequest(),
        "Retrieve" => new RetrieveRequest(),
        "Update" => new UpdateRequest(),
        "Delete" => new DeleteRequest(),
        "RetrieveMultiple" => new RetrieveMultipleRequest(),
        "Assign" => new AssignRequest(),
        "Associate" => new AssociateRequest(),
        "Disassociate" => new DisassociateRequest(),
        _ => new OrganizationRequest(name),
    };

    /// <summary>True for the eight CRUD names <see cref="Typed"/> answers.</summary>
    public static bool IsCrud(string canonicalName) =>
        canonicalName is "Create" or "Retrieve" or "Update" or "Delete" or "RetrieveMultiple" or "Assign" or "Associate" or "Disassociate";
}
