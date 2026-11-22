namespace Xrmd.Schema;

/// <summary>Reads and writes the workflow YAML (<see cref="WorkflowDocument"/>, plan 2.21).</summary>
/// <remarks>
/// Reading uses the representation model as <see cref="SchemaYaml"/> does: every node keeps its position for errors and no
/// type coercion happens (<c>true</c>, <c>1</c> and <c>2026-01-01</c> are text until a compiler types them by metadata).
/// Everything that needs no metadata is checked here: keys, the kinds a category and a mode accept, step ids, the
/// operators, the nesting and size limits. Anything else is a <see cref="SchemaFormatException"/> with line and column.
/// </remarks>
public static partial class WorkflowYaml
{
    /// <summary>The largest YAML body read (1 MiB, UTF-8).</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>The deepest YAML nesting read (mappings and lists, counted from the document).</summary>
    public const int MaxDepth = 64;

    /// <summary>The operators of a comparison (A7).</summary>
    internal static readonly string[] Operators = ["eq", "ne", "null", "notNull", "contains", "notContains", "beginsWith", "endsWith", "gt", "ge", "lt", "le"];

    private static readonly string[] ValueOperators = ["add", "subtract", "multiply", "increment", "decrement", "set", "concat", "firstOf"];
    private static readonly string[] ValueKinds = ["field", "record", "arg", "output", "now", "lookup", .. ValueOperators];
    private static readonly string[] BusinessRuleKinds = ["setValue", "setDefault", "clearValue", "showError", "setVisibility", "setLock", "setRequirement"];
    private static readonly string[] WorkflowScopes = ["user", "businessUnit", "parentChildBusinessUnits", "organization"];
    /// <summary>The argument types of an action (A15).</summary>
    public static IReadOnlyList<string> ArgumentTypes { get; } = ["boolean", "dateTime", "decimal", "double", "entityReference", "integer", "money", "picklist", "string"];

    private static readonly string[] Requirements = ["none", "recommended", "required"];

    /// <summary>The reference of every key of the format (path and meaning), the source of the generated documentation.</summary>
    public static IReadOnlyList<(string Path, string Meaning)> Keys { get; } =
    [
        ("kind", "workflow, businessRule or action (required)"),
        ("id", "the workflow id (a GUID, optional)"),
        ("name", "the workflow name (required, at most 100 characters)"),
        ("description", "the description"),
        ("entity", "the primary table (required; an action takes a table or none for a global action)"),
        ("steps", "the list of steps (required)"),
        ("mode", "workflow only (required): realtime or background"),
        ("scope", "workflow: user (default), businessUnit, parentChildBusinessUnits or organization; business rule: entity (the only one)"),
        ("runAs", "real-time workflow only: caller (default) or owner"),
        ("logFailures", "real-time workflow only: log a failure (default true)"),
        ("autoDelete", "background workflow only: delete finished runs (default false)"),
        ("onDemand", "workflow only: a user can start it by hand (default false)"),
        ("childProcess", "workflow only: other workflows can start it as a child (default false)"),
        ("trigger.create", "workflow only: true, or { stage: pre | post } (stage on a real-time workflow only)"),
        ("trigger.update", "workflow only: { columns: [<column>, ...], stage: pre | post }; ownerid is record assigned, statecode is status changes"),
        ("trigger.delete", "workflow only: true, or { stage: pre } (stage on a real-time workflow only)"),
        ("message", "action only (required): the message name"),
        ("arguments", "action only: a list of { name, direction: input | output, type, entity?, required?, description? }"),
        ("steps[].create", "{ id?, entity, set }: create a record"),
        ("steps[].update", "{ record, set }: update a record"),
        ("steps[].assign", "{ record, owner }: assign a record"),
        ("steps[].setState", "{ record, state, status }: set the state and status of a record"),
        ("steps[].sendEmail", "{ id?, set }: send an email described by its columns"),
        ("steps[].if", "{ when, then, elseIf?: [{ when, then }], else? }: a condition"),
        ("steps[].wait", "background workflow only: { until?, timeout?, then, onTimeout? } (at least one of until and timeout)"),
        ("steps[].stop", "{ status: succeeded | canceled, message? }: stop the workflow"),
        ("steps[].startChild", "{ workflow, record? }: start a child workflow (a name or GUID) on a record (default primary)"),
        ("steps[].performAction", "{ id?, message, target?, inputs? }: run an action"),
        ("steps[].customActivity", "{ id?, type, assembly, inputs?, outputs? }: run a custom workflow activity"),
        ("steps[].setOutput", "action only: { argument, value }: set an output argument"),
        ("steps[].setValue", "business rule only: { field, value }"),
        ("steps[].setDefault", "business rule only: { field, value }: set when the column is empty"),
        ("steps[].clearValue", "business rule only: { field }"),
        ("steps[].showError", "business rule only: { field?, message }"),
        ("steps[].setVisibility", "business rule only: { field, visible: true | false }"),
        ("steps[].setLock", "business rule only: { field, locked: true | false }"),
        ("steps[].setRequirement", "business rule only: { field, level: none | recommended | required }"),
        ("steps[].*.description", "every step takes an optional description"),
        ("record", "primary, { related: <lookup column> } or { created: <step id> }"),
        ("value", "a scalar (a literal; ~ is the empty value) or one of { field, related?, created? }, { record }, { arg }, { output, of }, { now: true }, { lookup, id, name? }, { add | subtract | multiply | increment | decrement | set | concat | firstOf: [<value>, ...] }"),
        ("condition", "a value plus op and value ({ field: a, op: gt, value: 1 }), or { all: [...] }, { any: [...] }, { not: ... }; op is eq, ne, null, notNull, contains, notContains, beginsWith, endsWith, gt, ge, lt or le"),
    ];
}
