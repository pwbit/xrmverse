using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Workflows.Authoring;

namespace Xrmd.Workflows.Tests.Authoring;

/// <summary>
/// The organization of the parity export (plan 2.21, Task 2): the tables its processes touch (<c>xrmv_order</c>, <c>account</c>,
/// <c>contact</c>, <c>task</c>, <c>email</c>, <c>transactioncurrency</c>, plus <c>systemuser</c> and <c>team</c> for owners) with the
/// columns and types the export's XAML shows, completed with standard Dataverse columns of the same tables for the assumed shapes.
/// Records, process definitions and custom activities are fixed lists the tests can extend.
/// </summary>
internal sealed class FakeAuthoringEnvironment : IAuthoringEnvironment
{
    public static readonly Guid Euro = new("692363f0-a3da-f111-84e8-3833c5c48caf");
    public static readonly Guid ChildId = new("0fe1b782-e5de-f111-84e8-002248872b66");
    public static readonly Guid OnCreateId = new("3e1f0307-d8de-f111-84e8-002248872b66");
    public static readonly Guid TwinA = new("aaaaaaaa-0000-4000-8000-000000000001");
    public static readonly Guid TwinB = new("aaaaaaaa-0000-4000-8000-000000000002");
    public static readonly Guid Ada = new("5e1f0000-0000-4000-8000-000000000001");
    public static readonly Guid Contoso = new("5e1f0000-0000-4000-8000-000000000002");

    private readonly Dictionary<string, EntityMetadata> _entities;

    public FakeAuthoringEnvironment()
    {
        _entities = new[]
        {
            Entity("xrmv_order", "xrmv_orderid", "xrmv_name",
                Str("xrmv_name"),
                new DecimalAttributeMetadata { LogicalName = "xrmv_amount" },
                Choice("xrmv_status", (128830000, "Open"), (128830001, "Closed")),
                Lookup("xrmv_accountid", "account"),
                // A custom lookup to an activity table (records of activity tables are keyed by activityid, review I4).
                Lookup("xrmv_followuptaskid", "task"),
                Owner(), State(), Status(), CreatedOn()),
            Entity("account", "accountid", "name",
                Str("name"),
                new MemoAttributeMetadata { LogicalName = "description" },
                new IntegerAttributeMetadata { LogicalName = "numberofemployees" },
                new MoneyAttributeMetadata { LogicalName = "revenue" },
                new DoubleAttributeMetadata { LogicalName = "address1_latitude" },
                new DateTimeAttributeMetadata { LogicalName = "lastusedincampaign" },
                new BooleanAttributeMetadata { LogicalName = "creditonhold" },
                Choice("industrycode", (1, "Accounting"), (2, "Agriculture")),
                Lookup("primarycontactid", "contact"),
                Lookup("parentaccountid", "account"),
                Owner(), State(), Status(), CreatedOn()),
            Entity("contact", "contactid", "fullname",
                Str("fullname"), Str("emailaddress1"),
                Typed(new LookupAttributeMetadata { LogicalName = "parentcustomerid", Targets = ["account", "contact"] }, AttributeTypeCode.Customer),
                Owner(), State(), Status(), CreatedOn()),
            Entity("task", "activityid", "subject",
                Str("subject"),
                new MemoAttributeMetadata { LogicalName = "description" },
                Lookup("regardingobjectid", "account", "contact", "xrmv_order"),
                Choice("prioritycode", (0, "Low"), (1, "Normal"), (2, "High")),
                new DateTimeAttributeMetadata { LogicalName = "scheduledend" },
                new IntegerAttributeMetadata { LogicalName = "actualdurationminutes" },
                Owner(), State(), Status(), CreatedOn()),
            Entity("email", "activityid", "subject",
                Str("subject"),
                new MemoAttributeMetadata { LogicalName = "description" },
                Party("to"), Party("cc"), Party("from"),
                Lookup("regardingobjectid", "account", "contact", "xrmv_order"),
                new BooleanAttributeMetadata { LogicalName = "correlatedsubjectchanged" },
                Choice("deliveryprioritycode", (0, "Low"), (1, "Normal"), (2, "High")),
                new BooleanAttributeMetadata { LogicalName = "directioncode" },
                new BooleanAttributeMetadata { LogicalName = "followemailuserpreference" },
                new BooleanAttributeMetadata { LogicalName = "isduplicatesenderunresolved" },
                Choice("notifications", (0, "None"), (1, "The message was saved as a Microsoft Dynamics 365 email record, but not all the attachments could be saved with it.")),
                Choice("prioritycode", (0, "Low"), (1, "Normal"), (2, "High")),
                Lookup("transactioncurrencyid", "transactioncurrency"),
                Owner(), State(), Status(), CreatedOn()),
            Entity("transactioncurrency", "transactioncurrencyid", "currencyname", Str("currencyname"), Str("isocurrencycode")),
            Entity("systemuser", "systemuserid", "fullname", Str("fullname")),
            Entity("team", "teamid", "name", Str("name")),
        }.ToDictionary(e => e.LogicalName, StringComparer.Ordinal);
    }

    /// <summary>The records lookup literals may name: (table, id) to primary name.</summary>
    public Dictionary<(string Entity, Guid Id), string> Records { get; } = new()
    {
        [("transactioncurrency", Euro)] = "Euro",
        [("systemuser", Ada)] = "Ada Lovelace",
        [("account", Contoso)] = "Contoso",
    };

    /// <summary>The process definitions a child workflow step may name.</summary>
    public List<(Guid Id, string Name, bool Subprocess, string? Entity)> Definitions { get; } =
    [
        (ChildId, "Child", true, "xrmv_order"),
        (OnCreateId, "OnCreate", false, "xrmv_order"),
        (TwinA, "Twin", true, "xrmv_order"),
        (TwinB, "Twin", true, "xrmv_order"),
    ];

    /// <summary>The custom workflow activities registered: (type, assembly simple name).</summary>
    public HashSet<(string Type, string Assembly)> WorkflowActivities { get; } = [];

    public bool WorkflowSdkAvailable { get; set; }

    /// <summary>The messages a perform action step may call, with their inputs and outputs (ruling R6).</summary>
    public Dictionary<string, List<CalleeArgument>> Messages { get; } = new(StringComparer.Ordinal)
    {
        ["xrmv_Score"] =
        [
            new("Factor", "System.Decimal", true), new("Label", "System.String", true), new("Count", "System.Int32", true),
            new("Score", "System.Decimal", false),
        ],
        ["xrmv_Recalculate"] = [new("Increment", "System.Decimal", true), new("Total", "System.Decimal", false)],
    };

    /// <summary>The arguments of the custom workflow activities by type, read only when the SDK is there and the type registered (R6).</summary>
    public Dictionary<string, List<CalleeArgument>> ActivityArguments { get; } = new(StringComparer.Ordinal)
    {
        ["Example.Activities.Normalize"] =
        [
            new("Input", "System.String", true), new("Times", "System.Int32", true), new("Normalized", "System.String", false),
        ],
    };

    public Task<IReadOnlyList<CalleeArgument>?> MessageArgumentsAsync(string message, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalleeArgument>?>(Messages.TryGetValue(message, out var arguments) ? arguments : null);

    public IReadOnlyList<CalleeArgument>? WorkflowActivityArguments(string typeName, string assemblyName) =>
        WorkflowSdkAvailable && IsWorkflowActivity(typeName, assemblyName) && ActivityArguments.TryGetValue(typeName, out var arguments)
            ? arguments
            : null;

    /// <summary>The solutions of the organization: unique name to version.</summary>
    public Dictionary<string, string> Solutions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> SolutionVersionAsync(string solution, CancellationToken ct) =>
        Task.FromResult(Solutions.TryGetValue(solution, out var version) ? version : null);

    public EntityMetadata? GetEntity(string logicalName) => _entities.GetValueOrDefault(logicalName);

    public Task<string?> PrimaryNameAsync(string entity, Guid id, CancellationToken ct) =>
        Task.FromResult(Records.TryGetValue((entity, id), out var name) ? name : null);

    public Task<IReadOnlyList<(Guid Id, string Name, bool Subprocess, string? Entity)>> FindDefinitionsAsync(string nameOrId, CancellationToken ct)
    {
        IReadOnlyList<(Guid, string, bool, string?)> found = Guid.TryParse(nameOrId, out var id)
            ? Definitions.Where(d => d.Id == id).ToList()
            : Definitions.Where(d => d.Name == nameOrId).ToList();
        return Task.FromResult(found);
    }

    public Task<string> PublisherPrefixAsync(Guid workflowId, string? solution, CancellationToken ct) => Task.FromResult("xrmv");

    public bool IsWorkflowActivity(string typeName, string assemblyName) =>
        WorkflowActivities.Contains((typeName, assemblyName.Split(',')[0].Trim()));

    // ---- metadata builders ---------------------------------------------------------------------------------------------

    private static EntityMetadata Entity(string name, string primaryId, string primaryName, params AttributeMetadata[] columns)
    {
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name };
        Set(entity, nameof(EntityMetadata.PrimaryIdAttribute), primaryId);
        Set(entity, nameof(EntityMetadata.PrimaryNameAttribute), primaryName);
        var key = new UniqueIdentifierAttributeMetadata { LogicalName = primaryId };
        Set(key, nameof(AttributeMetadata.IsValidForUpdate), (bool?)false);
        AttributeMetadata[] all = [key, .. columns];
        foreach (var column in all)
        {
            column.SchemaName ??= column.LogicalName;
            Set(column, nameof(AttributeMetadata.EntityLogicalName), name);
        }
        Set(entity, nameof(EntityMetadata.Attributes), all);
        return entity;
    }

    private static StringAttributeMetadata Str(string name) => new() { LogicalName = name, MaxLength = 200 };

    private static PicklistAttributeMetadata Choice(string name, params (int Value, string Label)[] options) => new()
    {
        LogicalName = name,
        OptionSet = new OptionSetMetadata(new OptionMetadataCollection(options.Select(o => new OptionMetadata(new Label(o.Label, 1033), o.Value)).ToList())),
    };

    private static LookupAttributeMetadata Lookup(string name, params string[] targets) => new() { LogicalName = name, Targets = targets };

    private static LookupAttributeMetadata Party(string name) =>
        Typed(new LookupAttributeMetadata { LogicalName = name, Targets = ["account", "contact", "systemuser"] }, AttributeTypeCode.PartyList);

    private static LookupAttributeMetadata Owner()
    {
        var owner = Typed(new LookupAttributeMetadata { LogicalName = "ownerid", Targets = ["systemuser", "team"] }, AttributeTypeCode.Owner);
        return owner;
    }

    private static StateAttributeMetadata State()
    {
        var state = new StateAttributeMetadata { LogicalName = "statecode" };
        state.OptionSet = new OptionSetMetadata(new OptionMetadataCollection(
        [
            new StateOptionMetadata { Value = 0, Label = new Label("Active", 1033), DefaultStatus = 1 },
            new StateOptionMetadata { Value = 1, Label = new Label("Inactive", 1033), DefaultStatus = 2 },
        ]));
        Set(state, nameof(AttributeMetadata.IsValidForCreate), (bool?)false);
        Set(state, nameof(AttributeMetadata.IsValidForUpdate), (bool?)false);
        return state;
    }

    private static StatusAttributeMetadata Status()
    {
        var status = new StatusAttributeMetadata { LogicalName = "statuscode" };
        status.OptionSet = new OptionSetMetadata(new OptionMetadataCollection(
        [
            new StatusOptionMetadata { Value = 1, Label = new Label("Active", 1033), State = 0 },
            new StatusOptionMetadata { Value = 2, Label = new Label("Inactive", 1033), State = 1 },
        ]));
        return status;
    }

    private static DateTimeAttributeMetadata CreatedOn()
    {
        var created = new DateTimeAttributeMetadata { LogicalName = "createdon" };
        Set(created, nameof(AttributeMetadata.IsValidForCreate), (bool?)false);
        Set(created, nameof(AttributeMetadata.IsValidForUpdate), (bool?)false);
        return created;
    }

    private static T Typed<T>(T attribute, AttributeTypeCode type) where T : AttributeMetadata
    {
        Set(attribute, nameof(AttributeMetadata.AttributeType), (AttributeTypeCode?)type);
        return attribute;
    }

    private static void Set(object target, string property, object? value)
    {
        var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!;
        var setter = p.GetSetMethod(nonPublic: true)
                     ?? p.DeclaringType!.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetSetMethod(nonPublic: true)!;
        setter.Invoke(target, [value]);
    }
}
