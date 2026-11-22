using Microsoft.Xrm.Sdk;

namespace Xrmd.Pipeline;

/// <summary>Plain, settable implementation of the plugin execution context (up to <see cref="IPluginExecutionContext7"/>).</summary>
public sealed class XrmdPluginExecutionContext : IPluginExecutionContext7
{
    /// <inheritdoc />
    public ParameterCollection InputParameters { get; set; } = new();
    /// <inheritdoc />
    public ParameterCollection OutputParameters { get; set; } = new();
    /// <inheritdoc />
    public ParameterCollection SharedVariables { get; set; } = new();
    /// <inheritdoc />
    public EntityImageCollection PreEntityImages { get; set; } = new();
    /// <inheritdoc />
    public EntityImageCollection PostEntityImages { get; set; } = new();
    /// <inheritdoc />
    public EntityImageCollection[] PreEntityImagesCollection { get; set; } = [];
    /// <inheritdoc />
    public EntityImageCollection[] PostEntityImagesCollection { get; set; } = [];
    /// <inheritdoc />
    public int Stage { get; set; }
    /// <inheritdoc />
    public int Mode { get; set; }
    /// <inheritdoc />
    public int IsolationMode { get; set; } = 2;
    /// <inheritdoc />
    public int Depth { get; set; } = 1;
    /// <inheritdoc />
    public string MessageName { get; set; } = string.Empty;
    /// <inheritdoc />
    public string PrimaryEntityName { get; set; } = "none";
    /// <inheritdoc />
    public Guid PrimaryEntityId { get; set; }
    /// <inheritdoc />
    public string SecondaryEntityName { get; set; } = "none";
    /// <inheritdoc />
    public Guid UserId { get; set; }
    /// <inheritdoc />
    public Guid InitiatingUserId { get; set; }
    /// <inheritdoc />
    public Guid BusinessUnitId { get; set; }
    /// <inheritdoc />
    public Guid OrganizationId { get; set; }
    /// <inheritdoc />
    public string OrganizationName { get; set; } = string.Empty;
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }
    /// <inheritdoc />
    public Guid? RequestId { get; set; }
    /// <inheritdoc />
    public Guid OperationId { get; set; }
    /// <inheritdoc />
    public DateTime OperationCreatedOn { get; set; }
    /// <inheritdoc />
    public EntityReference OwningExtension { get; set; } = new();
    /// <inheritdoc />
    public bool IsInTransaction { get; set; }
    /// <inheritdoc />
    public bool IsExecutingOffline { get; set; }
    /// <inheritdoc />
    public bool IsOfflinePlayback { get; set; }
    /// <inheritdoc />
    public IPluginExecutionContext? ParentContext { get; set; }

    /// <inheritdoc />
    public Guid InitiatingUserApplicationId { get; set; }
    /// <inheritdoc />
    public Guid InitiatingUserAzureActiveDirectoryObjectId { get; set; }
    /// <inheritdoc />
    public bool IsPortalsClientCall { get; set; }
    /// <inheritdoc />
    public Guid PortalsContactId { get; set; }
    /// <inheritdoc />
    public Guid UserAzureActiveDirectoryObjectId { get; set; }
    /// <inheritdoc />
    public Guid AuthenticatedUserId { get; set; }
    /// <inheritdoc />
    public string? InitiatingUserAgent { get; set; }
    /// <inheritdoc />
    public string? EnvironmentId { get; set; }
    /// <inheritdoc />
    public Guid TenantId { get; set; }
    /// <inheritdoc />
    public bool IsApplicationUser { get; set; }

    /// <summary>
    /// A copy of this context for another stage. <see cref="SharedVariables"/>, <see cref="InputParameters"/> and
    /// <see cref="OutputParameters"/> are shared with the original; the entity images are not.
    /// </summary>
    public XrmdPluginExecutionContext Clone(int stage)
    {
        var c = (XrmdPluginExecutionContext)MemberwiseClone();
        c.Stage = stage;
        c.PreEntityImages = new();
        c.PostEntityImages = new();
        c.PreEntityImagesCollection = new EntityImageCollection[0];
        c.PostEntityImagesCollection = new EntityImageCollection[0];
        c.OwningExtension = new();
        return c;
    }
}
