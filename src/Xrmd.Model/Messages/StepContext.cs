using Xrmd.Model.Records;

namespace Xrmd.Model.Messages;

/// <summary>
/// The execution context of a pipeline step, on the model: the 30 members of <see cref="RemoteContext"/> (same names and types,
/// except that the parent is a <see cref="StepContext"/> and <see cref="OwningExtension"/> is never null) and the members of the
/// plug-in context that a remote endpoint does not receive (the per-step image collections, the caller's identity details, the
/// environment). The defaults are those of the plug-in execution context of today: sandbox isolation, depth 1, empty message
/// name, "none" for the entity names.
/// </summary>
public sealed class StepContext
{
    public Guid BusinessUnitId { get; set; }

    public Guid CorrelationId { get; set; }

    public int Depth { get; set; } = 1;

    public string? InitiatingUserAgent { get; set; }

    public Guid InitiatingUserAzureActiveDirectoryObjectId { get; set; }

    public Guid InitiatingUserId { get; set; }

    public ParameterBag InputParameters { get; set; } = new();

    public bool IsExecutingOffline { get; set; }

    public bool IsInTransaction { get; set; }

    public bool IsOfflinePlayback { get; set; }

    public int IsolationMode { get; set; } = 2;

    public string? MessageName { get; set; } = string.Empty;

    public int Mode { get; set; }

    public DateTime OperationCreatedOn { get; set; }

    public Guid OperationId { get; set; }

    public Guid OrganizationId { get; set; }

    public string? OrganizationName { get; set; } = string.Empty;

    public ParameterBag OutputParameters { get; set; } = new();

    public RecordReference OwningExtension { get; set; } = NewOwner();

    public StepContext? ParentContext { get; set; }

    public IDictionary<string, Record> PostEntityImages { get; set; } = NewImages();

    public IDictionary<string, Record> PreEntityImages { get; set; } = NewImages();

    public Guid PrimaryEntityId { get; set; }

    public string? PrimaryEntityName { get; set; } = "none";

    public Guid? RequestId { get; set; }

    public string? SecondaryEntityName { get; set; } = "none";

    public ParameterBag SharedVariables { get; set; } = new();

    public int Stage { get; set; }

    public Guid UserAzureActiveDirectoryObjectId { get; set; }

    public Guid UserId { get; set; }

    // Not part of the remote context.

    public IList<IDictionary<string, Record>> PreEntityImagesCollection { get; set; } = new List<IDictionary<string, Record>>();

    public IList<IDictionary<string, Record>> PostEntityImagesCollection { get; set; } = new List<IDictionary<string, Record>>();

    public Guid AuthenticatedUserId { get; set; }

    public Guid InitiatingUserApplicationId { get; set; }

    public Guid PortalsContactId { get; set; }

    public Guid TenantId { get; set; }

    public bool IsPortalsClientCall { get; set; }

    public bool IsApplicationUser { get; set; }

    public string? EnvironmentId { get; set; }

    /// <summary>
    /// True on every parent level <see cref="FromRemote"/> builds (not on the level it returns, which is the caller's and so an
    /// exact inverse of <see cref="ToRemote"/>). Such a level stands for a deserialized remote context, which today's
    /// plug-in context copy does not read the agent and the Azure AD object ids of: <see cref="ToRemote"/> writes
    /// <see cref="InitiatingUserAgent"/> null and both Azure AD object ids empty for it. An adapter lends it as a view that is
    /// not an <c>IPluginExecutionContext2</c> or later. <see cref="Clone"/> copies the flag, so a clone of such a level is one too.
    /// </summary>
    public bool FromRemoteLevel { get; set; }

    /// <summary>An owner reference as a new SDK <c>EntityReference()</c> is: <see cref="RecordReference.LogicalName"/> null.</summary>
    private static RecordReference NewOwner() => new() { LogicalName = null! };

    private static Dictionary<string, Record> NewImages() => new(StringComparer.Ordinal);

    /// <summary>
    /// A copy of this context for another stage. <see cref="SharedVariables"/>, <see cref="InputParameters"/> and
    /// <see cref="OutputParameters"/> are shared with the original; the entity images, the image collections and
    /// <see cref="OwningExtension"/> are new.
    /// </summary>
    public StepContext Clone(int stage)
    {
        var c = (StepContext)MemberwiseClone();
        c.Stage = stage;
        c.PreEntityImages = NewImages();
        c.PostEntityImages = NewImages();
        c.PreEntityImagesCollection = new List<IDictionary<string, Record>>();
        c.PostEntityImagesCollection = new List<IDictionary<string, Record>>();
        c.OwningExtension = NewOwner();
        return c;
    }

    /// <summary>
    /// The 30 members of the remote context, the parent chain converted recursively. The parameter bags and images are copied
    /// entry by entry (the values are shared), so later changes to this context do not show in the result.
    /// </summary>
    public RemoteContext ToRemote()
    {
        var remote = new RemoteContext
        {
            BusinessUnitId = BusinessUnitId,
            CorrelationId = CorrelationId,
            Depth = Depth,
            InitiatingUserAgent = FromRemoteLevel ? null : InitiatingUserAgent,
            InitiatingUserAzureActiveDirectoryObjectId = FromRemoteLevel ? Guid.Empty : InitiatingUserAzureActiveDirectoryObjectId,
            InitiatingUserId = InitiatingUserId,
            IsExecutingOffline = IsExecutingOffline,
            IsInTransaction = IsInTransaction,
            IsOfflinePlayback = IsOfflinePlayback,
            IsolationMode = IsolationMode,
            MessageName = MessageName,
            Mode = Mode,
            OperationCreatedOn = OperationCreatedOn,
            OperationId = OperationId,
            OrganizationId = OrganizationId,
            OrganizationName = OrganizationName,
            OwningExtension = OwningExtension,
            ParentContext = ParentContext?.ToRemote(),
            PrimaryEntityId = PrimaryEntityId,
            PrimaryEntityName = PrimaryEntityName,
            RequestId = RequestId,
            SecondaryEntityName = SecondaryEntityName,
            Stage = Stage,
            UserAzureActiveDirectoryObjectId = FromRemoteLevel ? Guid.Empty : UserAzureActiveDirectoryObjectId,
            UserId = UserId,
        };
        foreach (var (key, value) in InputParameters) remote.InputParameters[key] = value;
        foreach (var (key, value) in OutputParameters) remote.OutputParameters[key] = value;
        foreach (var (key, value) in SharedVariables) remote.SharedVariables[key] = value;
        foreach (var (key, value) in PreEntityImages) remote.PreEntityImages[key] = value;
        foreach (var (key, value) in PostEntityImages) remote.PostEntityImages[key] = value;
        return remote;
    }

    /// <summary>
    /// The inverse of <see cref="ToRemote"/> for the 30 members: bags and images are copied entry by entry (values shared), the
    /// parent chain recursively. The members the remote context lacks keep their defaults; a null owning extension becomes a
    /// reference with a null logical name. Every parent level is marked <see cref="FromRemoteLevel"/>, the returned level is not.
    /// </summary>
    public static StepContext FromRemote(RemoteContext remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return Build(remote, false);
    }

    private static StepContext Build(RemoteContext remote, bool remoteLevel)
    {
        var context = new StepContext
        {
            BusinessUnitId = remote.BusinessUnitId,
            CorrelationId = remote.CorrelationId,
            Depth = remote.Depth,
            InitiatingUserAgent = remote.InitiatingUserAgent,
            InitiatingUserAzureActiveDirectoryObjectId = remote.InitiatingUserAzureActiveDirectoryObjectId,
            InitiatingUserId = remote.InitiatingUserId,
            IsExecutingOffline = remote.IsExecutingOffline,
            IsInTransaction = remote.IsInTransaction,
            IsOfflinePlayback = remote.IsOfflinePlayback,
            IsolationMode = remote.IsolationMode,
            MessageName = remote.MessageName,
            Mode = remote.Mode,
            OperationCreatedOn = remote.OperationCreatedOn,
            OperationId = remote.OperationId,
            OrganizationId = remote.OrganizationId,
            OrganizationName = remote.OrganizationName,
            OwningExtension = remote.OwningExtension ?? NewOwner(),
            ParentContext = remote.ParentContext is null ? null : Build(remote.ParentContext, true),
            PrimaryEntityId = remote.PrimaryEntityId,
            PrimaryEntityName = remote.PrimaryEntityName,
            RequestId = remote.RequestId,
            SecondaryEntityName = remote.SecondaryEntityName,
            Stage = remote.Stage,
            UserAzureActiveDirectoryObjectId = remote.UserAzureActiveDirectoryObjectId,
            UserId = remote.UserId,
            FromRemoteLevel = remoteLevel,
        };
        foreach (var (key, value) in remote.InputParameters) context.InputParameters[key] = value;
        foreach (var (key, value) in remote.OutputParameters) context.OutputParameters[key] = value;
        foreach (var (key, value) in remote.SharedVariables) context.SharedVariables[key] = value;
        foreach (var (key, value) in remote.PreEntityImages) context.PreEntityImages[key] = value;
        foreach (var (key, value) in remote.PostEntityImages) context.PostEntityImages[key] = value;
        return context;
    }
}
