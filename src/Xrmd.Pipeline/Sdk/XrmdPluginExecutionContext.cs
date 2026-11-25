using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;

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
    /// <summary>
    /// The parent's context. On a view the <see cref="PluginContextAdapter"/> lends, the parent level is lent when this is first read
    /// (then kept until the view is lent again), so a step that never reads it never has its parent chain written.
    /// </summary>
    public IPluginExecutionContext? ParentContext
    {
        get
        {
            lock (_parentGate)
            {
                if (_pendingParent is { } lend)
                {
                    _pendingParent = null;
                    _parent = lend();
                }
                return _parent;
            }
        }
        set
        {
            lock (_parentGate)
            {
                _pendingParent = null;
                _parent = value;
            }
        }
    }

    private object _parentGate = new();
    private IPluginExecutionContext? _parent;
    private Func<IPluginExecutionContext?>? _pendingParent;

    /// <summary>Sets the parent to be lent on first read (<see cref="ParentContext"/>).</summary>
    internal void LendParentLater(Func<IPluginExecutionContext?> lend)
    {
        lock (_parentGate)
        {
            _parent = null;
            _pendingParent = lend;
        }
    }

    /// <summary>The parent once lent (read, or set); null while it is still to be lent on first read.</summary>
    internal IPluginExecutionContext? LentParent
    {
        get
        {
            lock (_parentGate) return _pendingParent is null ? _parent : null;
        }
    }

    /// <summary>True when the parent has been lent (read or set), so a re-lend writes it again.</summary>
    internal bool ParentLent
    {
        get
        {
            lock (_parentGate) return _pendingParent is null && _parent is not null;
        }
    }

    /// <summary>The model context this view was lent from by <see cref="PluginContextAdapter"/>; null on a context built directly.</summary>
    public StepContext? Model { get; internal set; }

    /// <summary>True from <see cref="PluginContextAdapter.Lend"/> until <see cref="PluginContextAdapter.Return"/>.</summary>
    internal bool Running { get; set; }

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
    /// <see cref="OutputParameters"/> are shared with the original; the entity images are not. A clone of a view the
    /// <see cref="PluginContextAdapter"/> lent is no view of the model (no <see cref="Model"/>, not running); its parent is the
    /// original's (a parent still to be lent is lent through the original when the clone first reads it).
    /// </summary>
    public XrmdPluginExecutionContext Clone(int stage)
    {
        var c = (XrmdPluginExecutionContext)MemberwiseClone();
        c._parentGate = new object();
        c.Model = null;
        c.Running = false;
        lock (_parentGate)
        {
            if (_pendingParent is not null) c._pendingParent = () => ParentContext;
        }
        c.Stage = stage;
        c.PreEntityImages = new();
        c.PostEntityImages = new();
        c.PreEntityImagesCollection = new EntityImageCollection[0];
        c.PostEntityImagesCollection = new EntityImageCollection[0];
        c.OwningExtension = new();
        return c;
    }
}

/// <summary>
/// Lends the SDK plug-in context for a <see cref="StepContext"/>: one view per model context, paired in the
/// <see cref="SdkMirror"/> and reused, every member written from the model, the parameter bags through the mirror (so entities,
/// options and every other object a step holds keep their identity). <see cref="Return"/> reads the bags back; scalars and images
/// are read-only through the plug-in interfaces and the images are per-step copies, so they are never written back.
/// <para>
/// A level that stands for a deserialized remote context (<see cref="StepContext.FromRemoteLevel"/>) is lent as a real
/// <see cref="RemoteExecutionContext"/>, as a worker's parent is today: not an <see cref="IPluginExecutionContext2"/>, castable to
/// <see cref="RemoteExecutionContext"/>, serializable with its contract.
/// </para>
/// <para>
/// Images, image collections, <c>OwningExtension</c> and the scalars are per-step values written when a view is created; a view that
/// is lent again (a parent of a nested step, <see cref="Refresh"/>) only gets its three parameter bags refilled, so what the plug-in
/// did to its own images and owning extension is not undone. A plug-in that replaces a bag through the public setters of
/// <see cref="XrmdPluginExecutionContext"/> has the new bag read into that level's model context by <see cref="Return"/> and
/// <see cref="Sync"/> (a sibling stage that shared the old bag keeps it).
/// </para>
/// <para>
/// The parent chain is lent lazily: a view's <see cref="IPluginExecutionContext.ParentContext"/> lends its parent level when it is first
/// read, from the model as it is then. A parent once lent stays lent: a re-lend writes it again and <see cref="Return"/>/<see cref="Sync"/>
/// read it back; a parent never read is neither written nor read back. The optional <c>beforeLevel</c> runs before each level is
/// written (its model is about to be written into its view), for the executor to read in a level whose SDK request the core holds.
/// </para>
public static class PluginContextAdapter
{
    /// <summary>
    /// The view of <paramref name="context"/> (created on the first call, reused after), refreshed from the model, with the view of
    /// its parent chain as <see cref="IPluginExecutionContext.ParentContext"/>; marks it running.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is a <see cref="StepContext.FromRemoteLevel"/> level,
    /// which is lent as a parent only (it is not an <see cref="IPluginExecutionContext2"/>).</exception>
    public static XrmdPluginExecutionContext Lend(StepContext context, SdkMirror mirror, Action<StepContext>? beforeLevel = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mirror);
        if (context.FromRemoteLevel)
            throw new InvalidOperationException("A level that stands for a deserialized remote context is lent as a parent only.");
        var view = (XrmdPluginExecutionContext)LendLevel(context, mirror, beforeLevel, null);
        view.Running = true;
        return view;
    }

    /// <summary>
    /// Reads the three parameter bags of <paramref name="view"/> and of each parent view back into the model (in place: an object a
    /// plug-in changed is the same model object, a replaced one a new one, a removed attribute is gone); clears running. The view is
    /// inert from then on: a parent it reads later (a plug-in thread that outlived its step) is still lent, but nothing of the
    /// returned view is read into the model.
    /// </summary>
    public static void Return(XrmdPluginExecutionContext view, SdkMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(mirror);
        ReadBack(view, mirror);
        view.Running = false;
    }

    /// <summary>Reads the bags of the running view of <paramref name="context"/> and its parents into the model (before a nested call).</summary>
    public static void Sync(StepContext context, SdkMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ViewOf(context, mirror) is { } view) ReadBack(view, mirror);
    }

    /// <summary>Writes the model of <paramref name="context"/> and its parents into their views again (after a nested call).</summary>
    public static void Refresh(StepContext context, SdkMirror mirror, Action<StepContext>? beforeLevel = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mirror);
        if (ViewOf(context, mirror) is not null) LendLevel(context, mirror, beforeLevel, null);
    }

    /// <summary>True between <see cref="Lend"/> and <see cref="Return"/> of the view of <paramref name="context"/>.</summary>
    public static bool IsRunning(StepContext context, SdkMirror mirror) => ViewOf(context, mirror)?.Running == true;

    /// <summary>The plug-in view lent for <paramref name="context"/>, or null when there is none (or it is a remote-level view).</summary>
    public static XrmdPluginExecutionContext? ViewOf(StepContext context, SdkMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mirror);
        return mirror.TryGetSdk(context, out var view) ? view as XrmdPluginExecutionContext : null;
    }

    /// <param name="origin">The view lent first in this chain (null for that view itself): before a parent is lent on first read, the
    /// levels from it up to the reading view are read back, so the parent's write never undoes what the step did to an object it shares
    /// with that parent (In before Out).</param>
    private static IPluginExecutionContext LendLevel(StepContext context, SdkMirror mirror, Action<StepContext>? beforeLevel,
        XrmdPluginExecutionContext? origin)
    {
        beforeLevel?.Invoke(context);
        if (context.FromRemoteLevel)
        {
            IPluginExecutionContext? parent = context.ParentContext is { } p ? LendLevel(p, mirror, beforeLevel, origin) : null;
            var remote = mirror.TryGetSdk(context, out var old) && old is RemoteExecutionContext r ? r : null;
            if (remote is null)
            {
                remote = new RemoteExecutionContext();
                // The instance's collections are get-only: they are the SDK side of the model bags, so Out refills them and In reads back.
                PairOwn(context.InputParameters, remote.InputParameters, mirror);
                PairOwn(context.OutputParameters, remote.OutputParameters, mirror);
                PairOwn(context.SharedVariables, remote.SharedVariables, mirror);
                Images(context.PreEntityImages, remote.PreEntityImages, mirror);
                Images(context.PostEntityImages, remote.PostEntityImages, mirror);
                remote.OwningExtension = (EntityReference)mirror.Out(context.OwningExtension)!;
                Scalars(context, remote);
                mirror.Pair(context, remote);
            }
            mirror.Out(context.InputParameters);
            mirror.Out(context.OutputParameters);
            mirror.Out(context.SharedVariables);
            remote.ParentContext = parent is null ? null
                : parent as RemoteExecutionContext ?? throw new InvalidOperationException("The parent of a remote level is a remote level.");
            return remote;
        }
        if (mirror.TryGetSdk(context, out var existing) && existing is XrmdPluginExecutionContext reused)
        {
            reused.InputParameters = mirror.Out(context.InputParameters)!;
            reused.OutputParameters = mirror.Out(context.OutputParameters)!;
            reused.SharedVariables = mirror.Out(context.SharedVariables)!;
            // A parent the plug-in already holds is written again now; one never read stays to be lent on first read.
            if (reused.ParentLent && context.ParentContext is { } lentParent)
                reused.ParentContext = LendLevel(lentParent, mirror, beforeLevel, origin ?? reused);
            else LendParentLater(reused, context, mirror, beforeLevel, origin ?? reused);
            return reused;
        }
        var view = new XrmdPluginExecutionContext
        {
            InputParameters = mirror.Out(context.InputParameters)!,
            OutputParameters = mirror.Out(context.OutputParameters)!,
            SharedVariables = mirror.Out(context.SharedVariables)!,
            OwningExtension = (EntityReference)mirror.Out(context.OwningExtension)!,
            InitiatingUserApplicationId = context.InitiatingUserApplicationId,
            InitiatingUserAzureActiveDirectoryObjectId = context.InitiatingUserAzureActiveDirectoryObjectId,
            IsPortalsClientCall = context.IsPortalsClientCall,
            PortalsContactId = context.PortalsContactId,
            UserAzureActiveDirectoryObjectId = context.UserAzureActiveDirectoryObjectId,
            AuthenticatedUserId = context.AuthenticatedUserId,
            InitiatingUserAgent = context.InitiatingUserAgent,
            EnvironmentId = context.EnvironmentId,
            TenantId = context.TenantId,
            IsApplicationUser = context.IsApplicationUser,
        };
        Images(context.PreEntityImages, view.PreEntityImages, mirror);
        Images(context.PostEntityImages, view.PostEntityImages, mirror);
        view.PreEntityImagesCollection = Collections(context.PreEntityImagesCollection, mirror);
        // One list in both members of the model is one array in the view (the context of an asynchronous job holds the same empty
        // array in both, as the plug-in context it was rebuilt as always did).
        view.PostEntityImagesCollection = ReferenceEquals(context.PostEntityImagesCollection, context.PreEntityImagesCollection)
            ? view.PreEntityImagesCollection
            : Collections(context.PostEntityImagesCollection, mirror);
        Scalars(context, view);
        view.Model = context;
        LendParentLater(view, context, mirror, beforeLevel, origin ?? view);
        mirror.Pair(context, view);
        return view;
    }

    private static void LendParentLater(XrmdPluginExecutionContext view, StepContext context, SdkMirror mirror, Action<StepContext>? beforeLevel,
        XrmdPluginExecutionContext origin)
    {
        if (context.ParentContext is { } parent)
            view.LendParentLater(() =>
            {
                // The levels lent so far (from the origin up to this view) are read back first: the step may have changed an object
                // it shares with the parent (a caller's Target it passed to the service), and the parent's write must carry that.
                // A view read after its Return (a plug-in thread that outlived its step, a context kept in a static) is inert: Return
                // already read it back, and its later writes never reach the model.
                if (origin.Running) ReadBack(origin, mirror);
                return LendLevel(parent, mirror, beforeLevel, origin);
            });
        else view.ParentContext = null;
    }

    private static void PairOwn(ParameterBag bag, ParameterCollection own, SdkMirror mirror)
    {
        if (mirror.TryGetSdk(bag, out var other) && !ReferenceEquals(other, own))
            throw new InvalidOperationException("A bag of a remote level is already paired with another collection.");
        mirror.Pair(bag, own);
    }

    private static void Scalars(StepContext c, XrmdPluginExecutionContext v)
    {
        v.Stage = c.Stage;
        v.Mode = c.Mode;
        v.IsolationMode = c.IsolationMode;
        v.Depth = c.Depth;
        v.MessageName = c.MessageName!;
        v.PrimaryEntityName = c.PrimaryEntityName!;
        v.PrimaryEntityId = c.PrimaryEntityId;
        v.SecondaryEntityName = c.SecondaryEntityName!;
        v.UserId = c.UserId;
        v.InitiatingUserId = c.InitiatingUserId;
        v.BusinessUnitId = c.BusinessUnitId;
        v.OrganizationId = c.OrganizationId;
        v.OrganizationName = c.OrganizationName!;
        v.CorrelationId = c.CorrelationId;
        v.RequestId = c.RequestId;
        v.OperationId = c.OperationId;
        v.OperationCreatedOn = c.OperationCreatedOn;
        v.IsInTransaction = c.IsInTransaction;
        v.IsExecutingOffline = c.IsExecutingOffline;
        v.IsOfflinePlayback = c.IsOfflinePlayback;
    }

    private static void Scalars(StepContext c, RemoteExecutionContext v)
    {
        v.Stage = c.Stage;
        v.Mode = c.Mode;
        v.IsolationMode = c.IsolationMode;
        v.Depth = c.Depth;
        v.MessageName = c.MessageName!;
        v.PrimaryEntityName = c.PrimaryEntityName!;
        v.PrimaryEntityId = c.PrimaryEntityId;
        v.SecondaryEntityName = c.SecondaryEntityName!;
        v.UserId = c.UserId;
        v.InitiatingUserId = c.InitiatingUserId;
        v.BusinessUnitId = c.BusinessUnitId;
        v.OrganizationId = c.OrganizationId;
        v.OrganizationName = c.OrganizationName!;
        v.CorrelationId = c.CorrelationId;
        v.RequestId = c.RequestId;
        v.OperationId = c.OperationId;
        v.OperationCreatedOn = c.OperationCreatedOn;
        v.IsInTransaction = c.IsInTransaction;
        v.IsExecutingOffline = c.IsExecutingOffline;
        v.IsOfflinePlayback = c.IsOfflinePlayback;
        v.InitiatingUserAgent = c.InitiatingUserAgent;
        v.InitiatingUserAzureActiveDirectoryObjectId = c.InitiatingUserAzureActiveDirectoryObjectId;
        v.UserAzureActiveDirectoryObjectId = c.UserAzureActiveDirectoryObjectId;
    }

    private static void Images(IDictionary<string, Record> source, EntityImageCollection target, SdkMirror mirror)
    {
        target.Clear();
        foreach (var (key, record) in source) target[key] = (Entity)mirror.Out(record)!;
    }

    private static EntityImageCollection[] Collections(IList<IDictionary<string, Record>> source, SdkMirror mirror)
    {
        var result = new EntityImageCollection[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            result[i] = new EntityImageCollection();
            Images(source[i], result[i], mirror);
        }
        return result;
    }

    private static void ReadBack(IPluginExecutionContext view, SdkMirror mirror)
    {
        // Only the levels lent so far: a parent never read was never written, so nothing of it is read back.
        for (IPluginExecutionContext? level = view; level is not null;
             level = level is XrmdPluginExecutionContext lazy ? lazy.LentParent : level.ParentContext)
        {
            var input = mirror.In(level.InputParameters);
            var output = mirror.In(level.OutputParameters);
            var shared = mirror.In(level.SharedVariables);
            // A bag replaced through the public setters is a new twin: it becomes the bag of that level's model context.
            if (level is XrmdPluginExecutionContext { Model: { } model })
            {
                model.InputParameters = input!;
                model.OutputParameters = output!;
                model.SharedVariables = shared!;
            }
        }
    }
}
