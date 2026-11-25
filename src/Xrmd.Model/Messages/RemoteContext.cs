using Xrmd.Model.Records;

namespace Xrmd.Model.Messages;

/// <summary>
/// Our counterpart of the SDK's public <c>RemoteExecutionContext</c>: the execution context a remote endpoint (an asynchronous
/// step, a webhook) receives. The 30 data members are the ones of the context payload. Parameter values use the model types of
/// <c>Xrmd.Model.Records</c>; the image dictionaries are ordinal and keep insertion order.
/// </summary>
public sealed class RemoteContext
{
    public Guid BusinessUnitId { get; set; }

    public Guid CorrelationId { get; set; }

    public int Depth { get; set; }

    public string? InitiatingUserAgent { get; set; }

    public Guid InitiatingUserAzureActiveDirectoryObjectId { get; set; }

    public Guid InitiatingUserId { get; set; }

    public ParameterBag InputParameters { get; set; } = new();

    public bool IsExecutingOffline { get; set; }

    public bool IsInTransaction { get; set; }

    public bool IsOfflinePlayback { get; set; }

    public int IsolationMode { get; set; }

    public string? MessageName { get; set; }

    public int Mode { get; set; }

    public DateTime OperationCreatedOn { get; set; }

    public Guid OperationId { get; set; }

    public Guid OrganizationId { get; set; }

    public string? OrganizationName { get; set; }

    public ParameterBag OutputParameters { get; set; } = new();

    public RecordReference? OwningExtension { get; set; }

    public RemoteContext? ParentContext { get; set; }

    public IDictionary<string, Record> PostEntityImages { get; set; } = new Dictionary<string, Record>(StringComparer.Ordinal);

    public IDictionary<string, Record> PreEntityImages { get; set; } = new Dictionary<string, Record>(StringComparer.Ordinal);

    public Guid PrimaryEntityId { get; set; }

    public string? PrimaryEntityName { get; set; }

    public Guid? RequestId { get; set; }

    public string? SecondaryEntityName { get; set; }

    public ParameterBag SharedVariables { get; set; } = new();

    public int Stage { get; set; }

    public Guid UserAzureActiveDirectoryObjectId { get; set; }

    public Guid UserId { get; set; }
}
