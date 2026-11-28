using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

// S.2e3c3a Decision 4: the SDK halves of the merge stages (standard Upsert and bulk), for the callers still on the SDK:
// OrganizationService and ElasticBulkMessages (S.2e3c3b, S.2e4) and the pipeline's own SDK edge (the primary entity of a bulk
// request and its typed response; the create-only mark of an SDK Upsert request, which RunAsync carries onto the model request).
// Each member keeps its old name, signature and texts; the texts come from the private helpers of the model half, so both halves
// fault alike. They go with the SDK view (S.2i).

namespace Xrmd.Pipeline.Bulk
{
    public static partial class BulkTargets
    {
        /// <summary>
        /// The targets of a bulk request: each target (<see cref="Entity"/>, or <see cref="EntityReference"/> for DeleteMultiple)
        /// with its table name, the collection's <c>EntityName</c>, and the single-record request of a target.
        /// </summary>
        public sealed record TargetList(string Message, string? CollectionName, string?[] Names, object?[] Items, Func<int, OrganizationRequest> Single)
        {
            /// <summary>The number of targets.</summary>
            public int Count => Names.Length;
        }

        /// <summary>Reads the targets of <paramref name="request"/>, a bulk message.</summary>
        /// <exception cref="XrmdFaultException">400 when <c>Targets</c> is missing, of the wrong type or empty.</exception>
        public static TargetList Read(OrganizationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var message = MessageOf(request.RequestName) ?? throw new ArgumentException(NotBulk(request.RequestName), nameof(request));
            request.Parameters.TryGetValue(TargetsParameter, out var value);
            if (message == DeleteMultiple)
            {
                var references = value as EntityReferenceCollection ?? throw MissingTargets(message);
                DemandAny(message, references.Count);
                var list = references.ToList();
                return new TargetList(message, null, [.. list.Select(r => r?.LogicalName)], [.. list],
                    i => new DeleteRequest { Target = list[i] });
            }
            var entities = value as EntityCollection ?? throw MissingTargets(message);
            DemandAny(message, entities.Entities.Count);
            var rows = entities.Entities.ToList();
            return new TargetList(message, entities.EntityName, [.. rows.Select(e => e?.LogicalName)], [.. rows],
                i => message switch
                {
                    CreateMultiple => new CreateRequest { Target = rows[i] },
                    UpdateMultiple => new UpdateRequest { Target = rows[i] },
                    _ => new UpsertRequest { Target = rows[i] },
                });
        }

        /// <summary>The one table every target belongs to: the collection's <c>EntityName</c>, else the first target's.</summary>
        /// <exception cref="XrmdFaultException">400 for a target of another table or an unknown table.</exception>
        public static EntityMetadata TableOf(TargetList targets, IMetadataStore store)
        {
            ArgumentNullException.ThrowIfNull(targets);
            ArgumentNullException.ThrowIfNull(store);
            var name = string.IsNullOrEmpty(targets.CollectionName) ? targets.Names[0] : targets.CollectionName;
            DemandOneTable(targets, name);
            return (name is null ? null : store.GetEntity(name))
                   // Exact Dataverse text, as CoreOperations.EntityOf: client code matches on it to detect an unknown table.
                   ?? throw XrmdFaultException.InvalidArgument(
                       $"The entity with a name = '{name}' with namemapping = 'Logical' was not found in the MetadataCache.");
        }

        /// <summary>Every target must be a record of <paramref name="table"/>.</summary>
        /// <exception cref="XrmdFaultException">400 naming the first target of another table.</exception>
        public static void DemandOneTable(TargetList targets, string? table)
        {
            ArgumentNullException.ThrowIfNull(targets);
            DemandOneTable(targets.Message, targets.Names, table);
        }

        /// <summary>The table a bulk request's <c>Targets</c> name (the collection's <c>EntityName</c>, else the first target's);
        /// null when it names none. No check: <see cref="TableOf"/> validates.</summary>
        public static string? TableNameOf(OrganizationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return request.Parameters.TryGetValue(TargetsParameter, out var value) && value is EntityCollection targets
                ? string.IsNullOrEmpty(targets.EntityName) ? targets.Entities.FirstOrDefault()?.LogicalName : targets.EntityName
                : null;
        }

        /// <summary>
        /// The id a target names: <see cref="Entity.Id"/> (<see cref="EntityReference.Id"/>), else the primary id in its key
        /// attributes, else (an entity) its primary id attribute; empty when none.
        /// </summary>
        public static Guid IdOf(EntityMetadata table, object? target)
        {
            ArgumentNullException.ThrowIfNull(table);
            var pk = SystemAttributes.PrimaryIdName(table);
            var (id, keys, attributes) = target switch
            {
                Entity e => (e.Id, e.KeyAttributes, (AttributeCollection?)e.Attributes),
                EntityReference r => (r.Id, r.KeyAttributes, null),
                _ => (Guid.Empty, null, null),
            };
            if (id != Guid.Empty) return id;
            if (keys is not null && keys.TryGetValue(pk, out var key)) return KeyId(key);
            return attributes is not null && attributes.TryGetValue(pk, out var value) && value is Guid attribute ? attribute : Guid.Empty;
        }

        /// <summary>UpsertMultiple: the same id twice fails the whole request, before anything runs (ruling B3).</summary>
        /// <exception cref="XrmdFaultException">400 naming the repeated record.</exception>
        public static void DemandDistinctIds(EntityMetadata table, IEnumerable<Guid> ids)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(ids);
            var seen = new HashSet<Guid>();
            foreach (var id in ids)
                if (id != Guid.Empty && !seen.Add(id))
                    throw XrmdFaultException.InvalidArgument(
                        $"UpsertMultiple names the '{table.LogicalName}' record {id} more than once: each target must be a different record.");
        }

        /// <summary>
        /// Validates a bulk request for the standard-table pipeline (#76) and returns the request to run there: the same message with
        /// only its <c>Targets</c> (the caller copies the optional parameters it forwards), an UpdateMultiple without its repeated
        /// ids (ruling B3: no plug-in sees them).
        /// </summary>
        /// <param name="table">The one table of the targets.</param>
        /// <exception cref="XrmdFaultException">400 for missing or empty targets, targets of another table, an unknown table, or an
        /// UpsertMultiple naming the same id twice.</exception>
        public static OrganizationRequest Prepare(OrganizationRequest bulk, IMetadataStore store, out EntityMetadata table)
        {
            ArgumentNullException.ThrowIfNull(bulk);
            ArgumentNullException.ThrowIfNull(store);
            var targets = Read(bulk);
            table = TableOf(targets, store);
            var entityTable = table;
            if (targets.Message == DeleteMultiple) return bulk;
            var ids = targets.Items.Select(item => IdOf(entityTable, item)).ToArray();
            if (targets.Message == UpsertMultiple) DemandDistinctIds(table, ids);

            var seen = new HashSet<Guid>();
            var kept = new EntityCollection { EntityName = table.LogicalName };
            for (var i = 0; i < targets.Count; i++)
                if (targets.Message != UpdateMultiple || !IsRepeated(ids[i], seen))
                    kept.Entities.Add((Entity)targets.Items[i]!);
            return new OrganizationRequest(targets.Message) { RequestId = bulk.RequestId, [TargetsParameter] = kept };
        }

        /// <summary>
        /// The response of a bulk message from the responses of its single-record requests, in target order (ruling B14):
        /// <see cref="CreateMultipleResponse"/> (<c>Ids</c>), an empty <see cref="UpdateMultipleResponse"/>,
        /// <see cref="UpsertMultipleResponse"/> (one <see cref="UpsertResponse"/> per target) or an empty <c>DeleteMultiple</c>
        /// response. A null response (a target that did not run) has no entry, except in <c>Ids</c>/<c>Results</c> where it fails.
        /// </summary>
        public static OrganizationResponse Response(string message, IReadOnlyList<OrganizationResponse?> singles)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(singles);
            return MessageOf(message) switch
            {
                CreateMultiple => new CreateMultipleResponse
                {
                    ResponseName = CreateMultiple,
                    Results = { ["Ids"] = singles.Select(r => (Guid)r!.Results["id"]).ToArray() },
                },
                UpdateMultiple => new UpdateMultipleResponse { ResponseName = UpdateMultiple },
                UpsertMultiple => UpsertResults(singles),
                _ => new OrganizationResponse { ResponseName = DeleteMultiple },
            };
        }

        private static UpsertMultipleResponse UpsertResults(IReadOnlyList<OrganizationResponse?> responses)
        {
            var response = new UpsertMultipleResponse { ResponseName = UpsertMultiple };
            // UpsertMultipleResponse.Results (the typed array) hides the parameter collection of the same name.
            ((OrganizationResponse)response).Results["Results"] = responses.Select(AsUpsertResponse).ToArray();
            return response;
        }

        private static UpsertResponse AsUpsertResponse(OrganizationResponse? response)
        {
            if (response is UpsertResponse upsert) return upsert;
            var converted = new UpsertResponse { ResponseName = "Upsert" };
            if (response is not null) converted.Results.AddRange(response.Results);
            return converted;
        }
    }
}

namespace Xrmd.Pipeline.Merge
{
    public static partial class StandardUpsert
    {
        private static readonly ConditionalWeakTable<OrganizationRequest, object> CreateOnly = new();

        /// <summary>
        /// Marks <paramref name="request"/> (an <c>Upsert</c>) as create-only (Web API <c>If-None-Match: *</c>, ruling B11). The mark
        /// lives beside the request, never in its parameters, so a client cannot send it.
        /// </summary>
        public static void MarkCreateOnly(OrganizationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            CreateOnly.AddOrUpdate(request, new object());
        }

        /// <summary>True when <paramref name="request"/> was marked by <see cref="MarkCreateOnly(OrganizationRequest)"/>.</summary>
        public static bool IsCreateOnly(OrganizationRequest request) => CreateOnly.TryGetValue(request, out _);

        /// <summary>
        /// At the crossing of <paramref name="sdk"/> into its model twin <paramref name="model"/> (S.2e3c2b, Decision 7): the create-only
        /// mark of the SDK request, set by its caller (<see cref="MarkCreateOnly(OrganizationRequest)"/>), follows onto the model request.
        /// </summary>
        internal static void CopyCreateOnly(OrganizationRequest sdk, Xrmd.Model.Messages.Request model)
        {
            ArgumentNullException.ThrowIfNull(sdk);
            ArgumentNullException.ThrowIfNull(model);
            if (IsCreateOnly(sdk)) MarkCreateOnly(model);
        }
    }
}
