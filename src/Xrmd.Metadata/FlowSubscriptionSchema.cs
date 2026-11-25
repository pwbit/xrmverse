namespace Xrmd.Metadata;

/// <summary>
/// The webhook subscriptions of xrmverse's own Power Automate connector trigger (plan X.12b): the seeded user-owned table
/// <c>xrmverse_flowsubscription</c> and the callback store <c>meta.flow_subscription_callback</c>.
/// </summary>
/// <remarks>
/// The table is xrmverse's own (no Dataverse counterpart). Its object type code, 9970, is xrmverse's own too: it is below 10000
/// (a system table, <c>MetadataStore.IsSystemEntity</c>) and no seed fixture uses it. Public Create, Update, Assign and Delete are
/// refused (<c>SystemTableRules</c>): the rows are written through <c>/api/connector/v1/subscriptions</c> only. The callback URL is
/// a secret (it carries the flow's signature), so it lives in <see cref="CallbackTable"/>, outside the data table, and no read path
/// returns it.
/// </remarks>
public static class FlowSubscriptionSchema
{
    /// <summary>The table.</summary>
    public const string Table = "xrmverse_flowsubscription";

    /// <summary>The entity set of the table.</summary>
    public const string EntitySet = "xrmverse_flowsubscriptions";

    /// <summary>The primary key column.</summary>
    public const string Id = "xrmverse_flowsubscriptionid";

    /// <summary>The primary name column.</summary>
    public const string Name = "xrmverse_name";

    /// <summary>The table the subscription watches.</summary>
    public const string TableColumn = "xrmverse_table";

    /// <summary>The changes to notify: a mask of <see cref="Create"/>, <see cref="Update"/> and <see cref="Delete"/>.</summary>
    public const string Changes = "xrmverse_changes";

    /// <summary>The bits of <see cref="Changes"/>.</summary>
    public const int Create = 1, Update = 2, Delete = 4;

    /// <summary>The columns whose change notifies, comma-separated; null means all.</summary>
    public const string FilteringColumns = "xrmverse_filteringcolumns";

    /// <summary>The host of the callback URL (informational: the URL itself is in <see cref="CallbackTable"/>).</summary>
    public const string CallbackHost = "xrmverse_callbackhost";

    /// <summary>The callback store: <c>subscriptionid</c> (primary key) and <c>callbackurl</c>, one row per subscription.</summary>
    public const string CallbackTable = "meta.flow_subscription_callback";

    /// <summary>The message of the refusal of a public Create, Update, Assign or Delete of the table.</summary>
    public const string PublicWriteRefused = "Flow subscriptions are created and deleted through /api/connector/v1/subscriptions.";
}
