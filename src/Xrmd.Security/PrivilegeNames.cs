using System.Security.Cryptography;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Security;

/// <summary>Names and ids of privileges: <c>"prv" + verb + SchemaName</c>, compared case-insensitively.</summary>
public static class PrivilegeNames
{
    /// <summary>Privilege to act on behalf of another user (not tied to an entity).</summary>
    public const string ActOnBehalfOfAnotherUser = "prvActOnBehalfOfAnotherUser";

    /// <summary>Privilege to create entities (metadata); not tied to an entity.</summary>
    public const string CreateEntity = "prvCreateEntity";

    /// <summary>Privilege to create attributes (metadata); not tied to an entity.</summary>
    public const string CreateAttribute = "prvCreateAttribute";

    /// <summary>Privilege to update entities (metadata: UpdateEntity); not tied to an entity.</summary>
    public const string WriteEntity = "prvWriteEntity";

    /// <summary>Privilege to delete entities (metadata: DeleteEntity); not tied to an entity.</summary>
    public const string DeleteEntity = "prvDeleteEntity";

    /// <summary>Privilege to update attributes (metadata: UpdateAttribute, InsertOptionValue and UpdateOptionValue on a local option set); not tied to an entity.</summary>
    public const string WriteAttribute = "prvWriteAttribute";

    /// <summary>Privilege to delete attributes (metadata: DeleteAttribute); not tied to an entity.</summary>
    public const string DeleteAttribute = "prvDeleteAttribute";

    /// <summary>Privilege to create relationships (metadata: CreateOneToMany, CreateManyToMany); not tied to an entity.</summary>
    public const string CreateRelationship = "prvCreateRelationship";

    /// <summary>Privilege to delete relationships (metadata: DeleteRelationship); not tied to an entity.</summary>
    public const string DeleteRelationship = "prvDeleteRelationship";

    /// <summary>Privilege to create global option sets (metadata: CreateOptionSet); not tied to an entity.</summary>
    public const string CreateOptionSet = "prvCreateOptionSet";

    /// <summary>Privilege to update global option sets (metadata: UpdateOptionSet, InsertOptionValue and UpdateOptionValue on a global option set); not tied to an entity.</summary>
    public const string WriteOptionSet = "prvWriteOptionSet";

    /// <summary>Privilege to delete global option sets (metadata: DeleteOptionSet); not tied to an entity.</summary>
    public const string DeleteOptionSet = "prvDeleteOptionSet";

    /// <summary>Privilege to publish customizations (PublishXml, PublishAllXml); not tied to an entity.</summary>
    public const string PublishCustomization = "prvPublishCustomization";

    /// <summary>Privilege to import solutions (ImportSolution, ImportSolutionAsync); not tied to an entity.</summary>
    public const string ImportCustomization = "prvImportCustomization";

    /// <summary>Privilege to export solutions (ExportSolution); not tied to an entity.</summary>
    public const string ExportCustomization = "prvExportCustomization";

    /// <summary>Privilege to read the audit table and the audit details of a record (RetrieveAuditDetails); not tied to an entity.</summary>
    public const string ReadAuditSummary = "prvReadAuditSummary";

    /// <summary>Privilege to read the change history of a record (RetrieveRecordChangeHistory, RetrieveAttributeChangeHistory); not tied to an entity.</summary>
    public const string ReadRecordAuditHistory = "prvReadRecordAuditHistory";

    /// <summary>Privilege to delete the change history of a record (DeleteRecordChangeHistory); not tied to an entity.</summary>
    public const string DeleteRecordChangeHistory = "prvDeleteRecordChangeHistory";

    /// <summary>Privilege to delete audit data by date (DeleteAuditData, the partition delete); not tied to an entity.</summary>
    public const string DeleteAuditPartitions = "prvDeleteAuditPartitions";

    /// <summary>
    /// Privilege to start a workflow on demand (ExecuteWorkflow; Learn: "Execute Workflow Job"); not tied to an entity. Its id is
    /// derived like every xrmverse privilege id (<see cref="IdOf"/>), not Dataverse's (checklist 1i).
    /// </summary>
    public const string WorkflowExecution = "prvWorkflowExecution";

    /// <summary>
    /// Privilege to activate or deactivate a real-time workflow or an action (plan 2.21, ruling A17; Learn: "Activate Real-time
    /// Processes"); not tied to an entity. Its id is derived as every xrmverse privilege id (<see cref="IdOf"/>).
    /// </summary>
    public const string ActivateSynchronousWorkflow = "prvActivateSynchronousWorkflow";

    /// <summary>
    /// Privilege to activate or deactivate a business rule (plan 2.21, ruling A17; Learn: "Activate Business Rules"); not tied to an
    /// entity. Its id is derived as every xrmverse privilege id (<see cref="IdOf"/>).
    /// </summary>
    public const string ActivateBusinessRule = "prvActivateBusinessRule";

    /// <summary>
    /// Privilege to give a record its creation date on Create through <c>overriddencreatedon</c> (#343.5; Learn: "Override Created on
    /// or Created by for Records during Data Import"); not tied to an entity. Granted to System Customizer; System Administrator holds
    /// it by its template. Its id is derived as every xrmverse privilege id (<see cref="IdOf"/>).
    /// </summary>
    public const string OverrideCreatedOnCreatedBy = "prvOverrideCreatedOnCreatedBy";

    /// <summary>
    /// The audit privileges (granted to System Administrator only, which holds every privilege by its template; any other role
    /// can be given them through the role privilege messages).
    /// </summary>
    public static readonly IReadOnlyList<string> Audit =
        [ReadAuditSummary, ReadRecordAuditHistory, DeleteRecordChangeHistory, DeleteAuditPartitions];

    /// <summary>The metadata privileges (granted to System Customizer at Global depth).</summary>
    public static readonly IReadOnlyList<string> Metadata =
    [
        CreateEntity, CreateAttribute, WriteEntity, DeleteEntity, WriteAttribute, DeleteAttribute, CreateRelationship,
        DeleteRelationship, CreateOptionSet, WriteOptionSet, DeleteOptionSet, PublishCustomization,
    ];

    /// <summary>The solution privileges: import and export (granted to System Customizer at Global depth).</summary>
    public static readonly IReadOnlyList<string> Solution = [ImportCustomization, ExportCustomization];

    /// <summary>Every privilege that is not tied to an entity.</summary>
    public static readonly IReadOnlyList<string> NonEntity =
        [
            ActOnBehalfOfAnotherUser, .. Metadata, .. Solution, .. Audit, WorkflowExecution, ActivateSynchronousWorkflow, ActivateBusinessRule,
            OverrideCreatedOnCreatedBy,
        ];

    /// <summary>The verbs, in the order privileges are created; each has a single-bit <see cref="AccessRights"/> value.</summary>
    internal static readonly IReadOnlyList<(AccessRights Right, string Verb)> Verbs =
    [
        (AccessRights.CreateAccess, "Create"), (AccessRights.ReadAccess, "Read"), (AccessRights.WriteAccess, "Write"),
        (AccessRights.DeleteAccess, "Delete"), (AccessRights.AppendAccess, "Append"), (AccessRights.AppendToAccess, "AppendTo"),
        (AccessRights.AssignAccess, "Assign"), (AccessRights.ShareAccess, "Share"),
    ];

    /// <summary>
    /// The stem of the privilege names of <paramref name="entity"/> (plan 2.11 A12): <c>Activity</c> for the pointer and every
    /// activity table, <c>Note</c> for <c>annotation</c>, the SchemaName (else the logical name) otherwise.
    /// </summary>
    public static string Stem(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ActivitySchema.PrivilegeStem(entity) ?? entity.SchemaName ?? entity.LogicalName;
    }

    /// <summary>The privilege name for <paramref name="right"/> on <paramref name="entity"/>, e.g. <c>prvReadAccount</c>, <c>prvReadActivity</c>.</summary>
    /// <exception cref="XrmdValidationException">The right is not a single access right with a verb (400).</exception>
    public static string For(AccessRights right, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // The audit table has no entity privileges of its own: reading it is the audit summary privilege.
        if (right == AccessRights.ReadAccess && entity.LogicalName == AuditTable.Name) return ReadAuditSummary;
        foreach (var (r, verb) in Verbs)
            if (r == right) return "prv" + verb + Stem(entity);
        throw new XrmdValidationException($"Access right '{right}' has no privilege.");
    }

    /// <summary>Stable privilege id: MD5 of <c>xrmd:privilege:</c> + the lower-cased name.</summary>
    public static Guid IdOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:privilege:" + name.ToLowerInvariant())));
    }
}
