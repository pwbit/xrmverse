using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Model.Records;

// S.2e3c2b Decision 3: the SDK twins of the stage-20 checks, for the callers still on the SDK: the solution import (S.2g), the tests of
// the endpoint rules and of the secret table's interleaving. Each twin keeps the old name and signature, reads its arguments in with a
// mirror of its own (new SdkMirror()), calls the model member and writes back into the caller's objects what the model member changed:
// the Target, written out when the member answers true, and on a refusal too (a check may change the Target before it refuses, as the
// SDK form did in place). Task 5 removed the twins only the pipeline called (the custom API authoring, the event handler association
// rule and the endpoint validation): the pipeline runs the model members on its model request.

namespace Xrmd.Pipeline.Registration
{
    public sealed partial class RegistrationValidator
    {
        /// <summary>
        /// The SDK twin of <see cref="ValidateAsync(string, Record, Record?, DataSession, CancellationToken)"/> (the solution import,
        /// S.2g). The fills of the model member (the assembly's identity columns,
        /// <c>isworkflowactivity</c>) are written into <paramref name="target"/>.
        /// </summary>
        /// <exception cref="Core.Errors.XrmdFaultException">As the model member.</exception>
        public async Task ValidateAsync(string entity, Entity target, Entity? existing, DataSession s, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(entity);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(s);
            var mirror = new SdkMirror();
            var record = (Record)mirror.In(target)!;
            var stored = existing is null ? null : (Record)mirror.In(existing)!;
            await StageCheckTwins.WriteBackAsync(mirror, record, () => ValidateAsync(entity, record, stored, s, ct));
        }
    }
}

namespace Xrmd.Pipeline.Webhooks
{
    public static partial class ServiceEndpointRules
    {
        /// <summary>
        /// The SDK twin of <see cref="EndpointProblem(Record, bool)"/> (<c>ServiceEndpointRulesTests</c>): <paramref name="record"/> read in,
        /// the model member's answer.
        /// </summary>
        public static string? EndpointProblem(Entity record, bool allowAnyPort)
        {
            ArgumentNullException.ThrowIfNull(record);
            return EndpointProblem((Record)new SdkMirror().In(record)!, allowAnyPort);
        }
    }

    public sealed partial class WebhookSecrets
    {
        /// <summary>
        /// The SDK twin of <see cref="TakeAsync(Record, DataSession, CancellationToken)"/>
        /// (<c>UpsertInterleavingTests</c>). When the model member took the value (a non-null answer, ruling P6), its changes (no
        /// <c>authvalue</c>, the id, <c>isauthvalueset</c>) are written into <paramref name="target"/>; a refusal changes nothing in it.
        /// </summary>
        /// <exception cref="Core.Errors.XrmdFaultException">As the model member.</exception>
        public async Task<Guid?> TakeAsync(Entity target, DataSession s, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!target.Attributes.ContainsKey(ServiceEndpointSchema.AuthValue)) return null;
            var mirror = new SdkMirror();
            var record = (Record)mirror.In(target)!;
            var id = await TakeAsync(record, s, ct);
            if (id is not null) mirror.Out(record);
            return id;
        }
    }
}

namespace Xrmd.Pipeline
{
    /// <summary>The write-back of the stage-20 twins (Decision 3).</summary>
    internal static class StageCheckTwins
    {
        /// <summary>
        /// Runs a model check of <paramref name="target"/> (read in through <paramref name="mirror"/>) and writes the target out into its
        /// SDK twin when the check answers true or throws (a check may change the target before it refuses).
        /// </summary>
        public static async Task WriteBackAsync(SdkMirror mirror, Record target, Func<Task<bool>> check)
        {
            bool changed;
            try
            {
                changed = await check();
            }
            catch
            {
                mirror.Out(target);
                throw;
            }
            if (changed) mirror.Out(target);
        }
    }
}
