namespace Xrmd.Workflows.Authoring;

/// <summary>
/// The resolved and typed form of a workflow document that <see cref="DesignerXaml"/> writes (plan 2.21, A6–A9): every table,
/// column, record, child workflow and literal already checked against the organization by <see cref="WorkflowCompiler"/>, so
/// writing cannot fail. Names and variables are not here: the writer allocates them (A8).
/// </summary>
internal sealed partial class DesignerXaml
{
    /// <summary>
    /// A whole process: its id (the <c>x:Class</c> and the label ids), its primary table (<c>none</c> for a global action) and its steps;
    /// its category (0 workflow, 2 business rule, 3 action) and, for an action, its arguments and whether it is bound to the table
    /// (the generated <c>Target</c>, A15).
    /// </summary>
    internal sealed record Process(Guid Id, string Entity, bool Background, IReadOnlyList<Step> Steps)
    {
        public int Category { get; init; }

        public IReadOnlyList<ProcessArgument> Arguments { get; init; } = [];

        public bool Bound { get; init; }
    }

    /// <summary>An argument of an action (A15): an <c>x:Members</c> property with the export's five <c>mxsw:Argument*Attribute</c>s.</summary>
    internal sealed record ProcessArgument(string Name, bool IsInput, PropertyType Type, string? Entity, bool Required, string Description)
    {
        /// <summary>The YAML <c>type</c> (A15), for the assumed-shape name of the types no export confirmed.</summary>
        public string YamlType { get; init; } = "";
    }

    // ---- records -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A record as the designer keys it: <c>InputEntities("primaryEntity")</c>, <c>InputEntities("related_{lookup}#{table}")</c> or
    /// <c>CreatedEntities("{key}")</c>, with its table.
    /// </summary>
    internal sealed record RecordKey(string Dictionary, string Key, string Entity)
    {
        public static RecordKey Primary(string entity) => new("InputEntities", "primaryEntity", entity);

        public static RecordKey Related(string lookup, string entity) => new("InputEntities", $"related_{lookup}#{entity}", entity);

        public static RecordKey Created(string key, string entity) => new("CreatedEntities", key, entity);

        /// <summary>The VB expression of the record, e.g. <c>InputEntities("primaryEntity")</c>.</summary>
        public string Expression => $"{Dictionary}({Vb.String(Key)})";
    }

    // ---- values ---------------------------------------------------------------------------------------------------------

    internal abstract record Value;

    /// <summary>A literal: <c>CreateCrmType</c> of <see cref="Type"/> with <see cref="Text"/>.</summary>
    internal sealed record Literal(PropertyType Type, string Text) : Value;

    /// <summary>The empty value (<c>~</c>): <c>{x:Null}</c>, which clears a column (A6).</summary>
    internal sealed record Empty : Value;

    /// <summary>A lookup literal: a <c>Guid</c> <c>CreateCrmType</c> and the <c>EntityReference</c> one that names it (export OnUpdate).</summary>
    internal sealed record Lookup(string Entity, Guid Id, string Name) : Value;

    /// <summary>A column read (<c>GetEntityProperty</c>); <see cref="Target"/> null writes <c>x:Null</c> (a condition operand).</summary>
    internal sealed record Read(RecordKey Record, string Attribute, PropertyType? Target) : Value;

    /// <summary>An input argument of an action (<c>CustomOperationArguments</c>).</summary>
    internal sealed record Argument(string Name, PropertyType? Target) : Value;

    /// <summary>The execution time (<c>RetrieveCurrentTime</c>, assumed).</summary>
    internal sealed record Now : Value;

    /// <summary>
    /// An <c>EvaluateExpression</c> operator over values: <c>Add</c>, <c>Subtract</c>, ... or <c>SelectFirstNonNull</c> (the one-operand
    /// wrapper of a dynamic value, A6, and <c>firstOf</c>).
    /// </summary>
    internal sealed record Operator(string Name, PropertyType Type, IReadOnlyList<Value> Operands) : Value;

    /// <summary><c>String.Concat(...)</c> of the parts (assumed).</summary>
    internal sealed record Concat(IReadOnlyList<Value> Parts) : Value;

    /// <summary>An output of an earlier step, written as the VB expression itself (assumed, fixture action-call).</summary>
    internal sealed record Output(string Expression) : Value;

    /// <summary>
    /// An output of an earlier custom activity step (the step's YAML id): the variable <c>{step name}_{name}</c> the writer named
    /// when it wrote that step (fixture custom-activity, review I1).
    /// </summary>
    internal sealed record StepOutput(string StepId, string Name) : Value;

    // ---- conditions -----------------------------------------------------------------------------------------------------

    internal abstract record Condition;

    /// <summary>An <c>EvaluateCondition</c>: <see cref="Operator"/> is a <c>ConditionOperator</c> name.</summary>
    internal sealed record Compare(string Operator, Value Left, Value? Right) : Condition;

    /// <summary>An <c>EvaluateLogicalCondition</c>: <c>And</c> or <c>Or</c> of two conditions, or <c>Not</c> of <see cref="Left"/>.</summary>
    internal sealed record Logical(string Operator, Condition Left, Condition? Right) : Condition;

    // ---- steps ----------------------------------------------------------------------------------------------------------

    internal abstract record Step(string? Description);

    /// <summary>A column set by a create, update or send email step, with the column's type (the <c>SetEntityProperty</c> target).</summary>
    internal sealed record Assignment(string Column, PropertyType Type, Value Value);

    /// <summary>Create a record; <see cref="Key"/> is the step id (null: the designer's <c>{name}_localParameter</c>).</summary>
    internal sealed record Create(string Entity, string? Key, IReadOnlyList<Assignment> Set, string? Description) : Step(Description);

    internal sealed record Update(RecordKey Record, IReadOnlyList<Assignment> Set, string? Description) : Step(Description);

    internal sealed record AssignOwner(RecordKey Record, Value Owner, string? Description) : Step(Description);

    internal sealed record SetState(RecordKey Record, int State, int Status, string? Description) : Step(Description);

    internal sealed record SendEmail(string? Key, IReadOnlyList<Assignment> Set, string? Description) : Step(Description);

    internal sealed record Branch(Condition When, IReadOnlyList<Step> Then);

    internal sealed record If(IReadOnlyList<Branch> Branches, IReadOnlyList<Step>? Else, string? Description) : Step(Description);

    /// <summary>A wait; <see cref="Timeout"/> is a <c>DateTime</c> value.</summary>
    internal sealed record Wait(Condition? Until, Value? Timeout, IReadOnlyList<Step> Then, IReadOnlyList<Step>? OnTimeout, string? Description) : Step(Description);

    /// <summary>Stop the workflow; <see cref="Message"/> is a text value (null: the empty text the export writes).</summary>
    internal sealed record Stop(bool Canceled, Value? Message, string? Description) : Step(Description);

    internal sealed record StartChild(Guid WorkflowId, RecordKey Record, string? Description) : Step(Description);

    /// <summary>An input of a perform action or custom activity step, typed by the callee's argument (or by its value when unknown).</summary>
    internal sealed record Input(string Name, PropertyType Type, Value Value);

    /// <summary>Perform an action; <see cref="Result"/> names the variable later steps read its outputs from.</summary>
    internal sealed record PerformAction(string Message, RecordKey? Target, IReadOnlyList<Input> Inputs, string? Result, string? Description) : Step(Description);

    /// <summary>Set an output argument of an action (export Recalculate): the value cast to the argument's type.</summary>
    internal sealed record SetOutput(string Argument, PropertyType Type, Value Value, string? Description) : Step(Description);

    /// <summary>
    /// Business rule "set field value" (export Newbusinessrule), "clear field value" (<see cref="Value"/> <see cref="Empty"/>) or
    /// "set default value" (<see cref="DefaultOnly"/>) of a column of the primary record.
    /// </summary>
    internal sealed record RuleSetValue(string Field, PropertyType Type, Value Value, bool DefaultOnly, string? Description) : Step(Description);

    /// <summary>Business rule "show error message" (export Newbusinessrule) on <see cref="Field"/> (null: none, assumed).</summary>
    internal sealed record RuleShowError(string? Field, string Message, string? Description) : Step(Description);

    /// <summary>
    /// A business rule action that only affects forms: <c>setVisibility</c>, <c>setLock</c> or <c>setRequirement</c> (<see cref="Kind"/>)
    /// with its setting (<c>true</c>/<c>false</c>, or <c>none</c>/<c>recommended</c>/<c>required</c>).
    /// </summary>
    internal sealed record RuleClientOnly(string Kind, string Field, string Setting, string? Description) : Step(Description);

    /// <summary>
    /// A custom workflow activity; <see cref="Outputs"/> maps an output property to its name, which later steps read in the variable
    /// <c>{step name}_{name}</c>; <see cref="Id"/> is the step's YAML id (null: no step reads its outputs).
    /// </summary>
    internal sealed record CustomActivity(string Type, string Assembly, IReadOnlyList<Input> Inputs, IReadOnlyList<(string Property, string Name)> Outputs, string? Id, string? Description)
        : Step(Description);
}
