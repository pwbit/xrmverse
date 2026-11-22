using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Registration;

/// <summary>A registered step image. <paramref name="Attributes"/> null means all attributes.</summary>
public sealed record PluginStepImage(Guid Id, int ImageType, string Alias, string[]? Attributes, string MessagePropertyName);

/// <summary>
/// A plug-in step read from the registration tables, with its plug-in type, assembly and images. A step on a webhook
/// (<paramref name="Endpoint"/> set) has <paramref name="PluginTypeId"/> and <paramref name="AssemblyId"/> <see cref="Guid.Empty"/>,
/// <paramref name="TypeName"/> empty and <paramref name="AssemblyVersion"/> 0. <paramref name="AsyncAutoDelete"/>: the
/// <c>asyncautodelete</c> of the step (a successful asynchronous job is deleted).
/// </summary>
public sealed record PluginStep(
    Guid Id, string Name, string Message, string? PrimaryEntity, int Stage, int Mode, int Rank,
    string[]? FilteringAttributes, string? UnsecureConfiguration, string? SecureConfiguration, Guid? ImpersonatingUserId,
    Guid PluginTypeId, string TypeName, Guid AssemblyId, long AssemblyVersion, IReadOnlyList<PluginStepImage> Images,
    bool AsyncAutoDelete = false, WebhookEndpoint? Endpoint = null);
