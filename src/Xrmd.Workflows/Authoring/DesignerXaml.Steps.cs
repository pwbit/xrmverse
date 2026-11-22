using System.Xml.Linq;

namespace Xrmd.Workflows.Authoring;

internal sealed partial class DesignerXaml
{
    /// <summary>The next designer step name (A8): the kind's prefix and the process-wide pre-order counter.</summary>
    private string Next(string prefix) => prefix + Invariant(++_counter);

    /// <summary>The <c>DisplayName</c> of a step: its generated name, then <c>": " + description</c> when it has one (A8).</summary>
    private static string Display(string name, string? description) => description is null ? name : $"{name}: {description}";

    /// <summary>The activities of a list of steps, in order (a wait with only a timeout is followed by its <c>then</c> steps).</summary>
    private List<XElement> StepList(IReadOnlyList<Step> steps)
    {
        var activities = new List<XElement>();
        foreach (var step in steps)
            activities.AddRange(StepActivities(step));
        return activities;
    }

    private IEnumerable<XElement> StepActivities(Step step) => step switch
    {
        Create create => [WriteCreate(create)],
        Update update => [WriteUpdate(update)],
        AssignOwner assign => [WriteAssign(assign)],
        SetState setState => [WriteSetState(setState)],
        SendEmail email => [WriteSendEmail(email)],
        If condition => [WriteIf(condition)],
        Wait { Until: null } wait => WriteTimeoutOnly(wait),
        Wait wait => [WriteWait(wait)],
        Stop stop => [WriteStop(stop)],
        StartChild child => [WriteStartChild(child)],
        PerformAction action => [WritePerformAction(action)],
        CustomActivity custom => [WriteCustomActivity(custom)],
        SetOutput output => [WriteSetOutput(output)],
        RuleSetValue { DefaultOnly: true } value => [WriteRuleSetDefault(value)],
        RuleSetValue value => [WriteRuleSetValue(value)],
        RuleShowError error => [WriteRuleShowError(error)],
        RuleClientOnly client => [WriteRuleClientOnly(client)],
        _ => throw new InvalidOperationException($"Unknown step {step.GetType().Name}."),
    };

    // ---- data steps (export OnCreate, Recalculate, OnUpdate) ------------------------------------------------------------------

    /// <summary>The columns of a create, update or send email step: each value's activities, then its <c>SetEntityProperty</c>.</summary>
    private void SetColumns(Scope scope, List<XElement> activities, string entity, string temp, IReadOnlyList<Assignment> set)
    {
        foreach (var assignment in set)
        {
            var value = Emit(assignment.Value, scope, activities);
            var name = Mxswa + "SetEntityProperty";
            activities.Add(new XElement(name,
                // An empty value (assumed): the markup extension first, as the export orders EntityId="{x:Null}".
                value is null ? new XAttribute("Value", "{x:Null}") : null,
                new XAttribute("Attribute", assignment.Column),
                new XAttribute("Entity", Expr(temp)),
                new XAttribute("EntityName", entity),
                value is null ? null : new XAttribute("Value", Expr(value)),
                TargetTypeProperty(name, assignment.Type)));
        }
    }

    private XElement WriteCreate(Create create)
    {
        var name = Next("CreateStep");
        var scope = new Scope(name);
        var key = create.Key ?? name + "_localParameter";
        if (create.Key is not null)
            Assume("createdKey:id");
        var temp = RecordKey.Created(key + "#Temp", create.Entity).Expression;
        var activities = new List<XElement> { Assign("mxs:Entity", temp, $"New Entity({Vb.String(create.Entity)})") };
        SetColumns(scope, activities, create.Entity, temp, create.Set);
        activities.Add(new XElement(Mxswa + "CreateEntity",
            new XAttribute("EntityId", "{x:Null}"), DisplayName(name), new XAttribute("Entity", Expr(temp)), new XAttribute("EntityName", create.Entity)));
        activities.Add(Assign("mxs:Entity", RecordKey.Created(key, create.Entity).Expression, temp));
        return DesignerSequence(Display(name, create.Description), scope.Variables, activities);
    }

    /// <summary>An update: the primary record as in the export (Recalculate); a related or created record the same way on its key (assumed).</summary>
    private XElement WriteUpdate(Update update)
    {
        var name = Next("UpdateStep");
        var scope = new Scope(name);
        var entity = update.Record.Entity;
        var temp = RecordKey.Created(update.Record.Key + "#Temp", entity).Expression;
        var activities = new List<XElement>
        {
            Assign("mxs:Entity", temp, $"New Entity({Vb.String(entity)})"),
            Assign("s:Guid", temp + ".Id", update.Record.Expression + ".Id"),
        };
        SetColumns(scope, activities, entity, temp, update.Set);
        activities.Add(new XElement(Mxswa + "UpdateEntity", DisplayName(name), new XAttribute("Entity", Expr(temp)), new XAttribute("EntityName", entity)));
        activities.Add(Assign("mxs:Entity", update.Record.Expression, temp));
        return DesignerSequence(Display(name, update.Description), scope.Variables, activities);
    }

    /// <summary>Send an email (export OnUpdate); <c>Persist</c> after it in a background workflow only (real-time: assumed).</summary>
    private XElement WriteSendEmail(SendEmail email)
    {
        var name = Next("SendEmailStep");
        var scope = new Scope(name);
        var key = email.Key ?? name + "_localParameter";
        var temp = RecordKey.Created(key + "#Temp", "email").Expression;
        var activities = new List<XElement> { Assign("mxs:Entity", temp, $"New Entity({Vb.String("email")})") };
        SetColumns(scope, activities, "email", temp, email.Set);
        activities.Add(new XElement(Mxswa + "SendEmail", new XAttribute("EntityId", "{x:Null}"), DisplayName(name), new XAttribute("Entity", Expr(temp))));
        // An email later steps refer to (an id) is kept under its key, as a create does (assumed).
        if (email.Key is not null)
        {
            activities.Add(Assign("mxs:Entity", RecordKey.Created(key, "email").Expression, temp));
            Assume("createdKey:id");
            Assume("sendEmail:assignToId");
        }
        if (_process.Background)
            activities.Add(new XElement(Wf + "Persist"));
        else
            Assume("sendEmail:noPersist");
        return DesignerSequence(Display(name, email.Description), scope.Variables, activities);
    }

    /// <summary>Assign a record (assumed): <c>mxswa:AssignEntity</c> in a designer sequence.</summary>
    private XElement WriteAssign(AssignOwner assign)
    {
        var name = Next("AssignStep");
        var scope = new Scope(name);
        var activities = new List<XElement>();
        var owner = Emit(assign.Owner, scope, activities)!;
        activities.Add(new XElement(Mxswa + "AssignEntity",
            new XAttribute("EntityId", "{x:Null}"), DisplayName(name), new XAttribute("Entity", Expr(assign.Record.Expression)),
            new XAttribute("EntityName", assign.Record.Entity), new XAttribute("Owner", Expr(owner))));
        return DesignerSequence(Display(name, assign.Description), scope.Variables, activities);
    }

    /// <summary>Set the state and status of a record (assumed): <c>mxswa:SetState</c> with option set literals.</summary>
    private XElement WriteSetState(SetState setState)
    {
        var name = Next("SetStateStep");
        var element = Mxswa + "SetState";
        XElement Option(string member, int value) => new(Mxswa + $"SetState.{member}",
            new XElement(Wf + "InArgument", TypeArguments("mxs:OptionSetValue"),
                new XElement(Mxswa + "ReferenceLiteral", TypeArguments("mxs:OptionSetValue"),
                    new XElement(Mxswa + "ReferenceLiteral.Value", new XElement(Mxs + "OptionSetValue", new XAttribute("Value", Invariant(value)))))));
        var activity = new XElement(element,
            new XAttribute("EntityId", "{x:Null}"), DisplayName(name), new XAttribute("Entity", Expr(setState.Record.Expression)),
            new XAttribute("EntityName", setState.Record.Entity),
            Option("State", setState.State), Option("Status", setState.Status));
        return DesignerSequence(Display(name, setState.Description), [], [activity]);
    }

    // ---- start child, stop (export OnCreate, Child, Recalculate) --------------------------------------------------------------

    private XElement WriteStartChild(StartChild child)
    {
        var name = Next("ChildWorkflowStep");
        var inputs = name + "_1";
        var variables = new List<XElement>
        {
            Variable("scg:Dictionary(x:String, x:Object)", inputs, "[New Dictionary(Of System.String, System.Object)]"),
        };
        var activity = new XElement(Mxswa + "StartChildWorkflow",
            DisplayName(name), new XAttribute("EntityId", Expr(child.Record.Expression + ".Id")), new XAttribute("EntityName", child.Record.Entity),
            new XAttribute("InputParameters", Expr(inputs)), new XAttribute("WorkflowId", child.WorkflowId.ToString("D")));
        return DesignerSequence(Display(name, child.Description), variables, [activity]);
    }

    /// <summary>
    /// Stop the workflow (export Child, Recalculate: canceled with the empty message); succeeded or with a message is the same shape
    /// (assumed). The label id is generated (A9).
    /// </summary>
    private XElement WriteStop(Stop stop)
    {
        var name = Next("StopWorkflowStep");
        var scope = new Scope(name);
        if (!stop.Canceled)
            Assume("stop:succeeded");
        if (stop.Message is not null)
            Assume("stop:message");
        var activities = new List<XElement>();
        var message = Emit(stop.Message ?? new Literal(PropertyType.String, ""), scope, activities) ?? "Nothing";
        var status = stop.Canceled ? "Canceled" : "Succeeded";
        activities.Add(new XElement(Wf + "TerminateWorkflow", DisplayName(name),
            new XAttribute("Exception", Expr($"New Microsoft.Xrm.Sdk.InvalidPluginExecutionException(Microsoft.Xrm.Sdk.OperationStatus.{status})")),
            new XAttribute("Reason", Expr($"DirectCast({message}, System.String)"))));
        var variables = new List<XElement>(scope.Variables)
        {
            Variable("x:String", "stepLabelLabelId", WorkflowIdentity.LabelId(_process.Id, name).ToString("D")),
            new(Wf + "Variable", TypeArguments("x:String"), new XAttribute("Name", "stepLabelDescription"),
                new XElement(Wf + "Variable.Default", new XElement(Wf + "Literal", TypeArguments("x:String"), new XAttribute("Value", "")))),
            Variable("x:Int32", "stepLabelLanguageCode", "1033"),
        };
        return DesignerSequence(Display(name, stop.Description), variables, activities);
    }

    // ---- conditions and waits (export Recalculate, Newbusinessrule, OnUpdate) -------------------------------------------------

    /// <summary>
    /// A <c>ConditionBranch</c> whose <c>Then</c> is a <c>Composite</c> of the branch's steps; no condition for a timeout branch. The
    /// <c>Description</c> is the condition's description in a business rule (export Newbusinessrule), else <c>x:Null</c>.
    /// </summary>
    private XElement ConditionBranch(string name, string? condition, IEnumerable<XElement> then, string? description = null) =>
        Reference("ConditionBranch", name,
            condition is null ? null : Arguments(In("x:Boolean", "Condition", Expr(condition))),
            Properties(
                new XElement(Mxswa + "ActivityReference", Key("Then"),
                    new XAttribute("AssemblyQualifiedName", Xaml.ActivityCatalog.CrmActivities + "Composite" + CrmWorkflow), DisplayName(name),
                    Properties(Collection("Variable", "Variables", []), Collection("Activity", "Activities", then))),
                XNull("Else"),
                description is null ? XNull("Description") : new XElement(X + "String", Key("Description"), description)));

    /// <summary>
    /// "Check condition": the branches in one <c>ConditionSequence</c> (the second and later ones for <c>elseIf</c>, assumed), the
    /// <c>else</c> as a last branch whose condition is <c>[True]</c> with the designer's <c>True</c> variable (export Newbusinessrule).
    /// In a business rule the description is each branch's <c>Description</c>, not part of the step name (export Newbusinessrule;
    /// on an <c>elseIf</c> branch assumed), and the rule's actions are named after the first branch (A8).
    /// </summary>
    private XElement WriteIf(If condition)
    {
        var name = Next("ConditionStep");
        var rule = _process.Category == 2;
        var description = rule ? condition.Description : null;
        var variables = new List<XElement>();
        var activities = new List<XElement>();
        var outerRuleBranch = _ruleBranch;
        string? firstBranch = null;
        // Export Newbusinessrule: one condition with a description, one branch and an else. The rest is assumed.
        if (rule && condition.Description is null)
            Assume("ruleCondition:noDescription");
        if (rule && condition.Description is not null && condition.Branches.Count > 1)
            Assume("ruleCondition:elseIfDescription");
        if (rule && outerRuleBranch is not null)
            Assume("ruleCondition:nested");
        foreach (var branch in condition.Branches)
        {
            var scope = new Scope(Next("ConditionBranchStep"));
            _ruleBranch = firstBranch ??= scope.Name;
            var result = scope.Condition(part: false);
            EmitCondition(branch.When, scope, result, activities);
            var then = StepList(branch.Then);
            activities.Add(ConditionBranch(scope.Name, result, then, description));
            variables.AddRange(scope.Variables);
        }
        if (condition.Else is { } otherwise)
        {
            var scope = new Scope(Next("ConditionBranchStep"));
            _ruleBranch = firstBranch;
            variables.Add(Variable("x:Boolean", "True", "False"));
            Emit(new Literal(PropertyType.Boolean, "true"), scope, activities);
            var then = StepList(otherwise);
            activities.Add(ConditionBranch(scope.Name, "True", then, description));
            variables.AddRange(scope.Variables);
        }
        _ruleBranch = outerRuleBranch;
        return Reference("ConditionSequence", rule ? name : Display(name, condition.Description),
            Arguments(In("x:Boolean", "Wait", "False")),
            Properties(
                Collection("Variable", "Variables", variables),
                Collection("Activity", "Activities", activities),
                new XElement(X + "Boolean", Key("ContainsElseBranch"), "False")));
    }

    /// <summary>
    /// A wait until a condition (export OnUpdate): a <c>ConditionSequence</c> with <c>Wait</c> True whose branch holds the steps that
    /// follow; with a timeout, a second branch whose <c>Then</c> starts with <c>mxswa:Postpone</c> (assumed, fixture background-wait).
    /// </summary>
    private XElement WriteWait(Wait wait)
    {
        var name = Next("WaitStep");
        var variables = new List<XElement>();
        var activities = new List<XElement>();

        var scope = new Scope(Next("WaitBranchStep"));
        var result = scope.Condition(part: false);
        EmitCondition(wait.Until!, scope, result, activities);
        var then = StepList(wait.Then);
        activities.Add(ConditionBranch(scope.Name, result, then));
        variables.AddRange(scope.Variables);

        if (wait.Timeout is not null)
        {
            var timeout = new Scope(Next("TimeoutBranchStep"));
            var until = Emit(wait.Timeout, timeout, activities)!;
            var onTimeout = StepList(wait.OnTimeout ?? []);
            activities.Add(ConditionBranch(timeout.Name, null, [Postpone("Timeout", until), .. onTimeout]));
            variables.AddRange(timeout.Variables);
        }

        return Reference("ConditionSequence", Display(name, wait.Description),
            Arguments(In("x:Boolean", "Wait", "True")),
            Properties(
                Collection("Variable", "Variables", variables),
                Collection("Activity", "Activities", activities),
                XNull("ContainsElseBranch")));
    }

    /// <summary>A wait with only a timeout (assumed): a designer sequence ending with <c>mxswa:Postpone</c>, then the <c>then</c> steps.</summary>
    private IEnumerable<XElement> WriteTimeoutOnly(Wait wait)
    {
        var name = Next("WaitStep");
        var scope = new Scope(name);
        var activities = new List<XElement>();
        var until = Emit(wait.Timeout!, scope, activities)!;
        activities.Add(Postpone(name, until));
        return [DesignerSequence(Display(name, wait.Description), scope.Variables, activities), .. StepList(wait.Then)];
    }

    private static XElement Postpone(string displayName, string until) =>
        new(Mxswa + "Postpone", new XAttribute("BlockExecution", "True"), DisplayName(displayName), new XAttribute("PostponeUntil", Expr(until)));

    // ---- perform action, custom activity (assumed: fixtures action-call, custom-activity) -------------------------------------

    private XElement WritePerformAction(PerformAction action)
    {
        var name = Next("ActionStep");
        var scope = new Scope(name);
        var activities = new List<XElement>();
        var inputs = action.Inputs.Select(i => (i.Name, i.Type, Ref: Emit(i.Value, scope, activities))).ToList();
        activities.Add(Reference("ExecuteAction", name, Arguments(
            In("x:String", "ActionName", action.Message),
            action.Target is null ? null : In("mxs:EntityReference", "Target", Expr(action.Target.Expression)),
            inputs.Select(i => In(i.Type.Xaml, i.Name, i.Ref is null ? "{x:Null}" : Expr(i.Ref))),
            Out("x:Object", "Result", action.Result ?? name + "_result"))));
        return DesignerSequence(Display(name, action.Description), scope.Variables, activities);
    }

    private XElement WriteCustomActivity(CustomActivity custom)
    {
        var name = Next("CustomActivityStep");
        var scope = new Scope(name);
        Assume($"customActivity:{custom.Type}");
        if (custom.Id is not null)
            _stepNames[custom.Id] = name;
        var activities = new List<XElement>();
        var dot = custom.Type.LastIndexOf('.');
        var ns = CustomNamespace(custom.Type[..dot], custom.Assembly);
        var local = custom.Type[(dot + 1)..];
        var inputs = custom.Inputs.Select(i => (i.Name, i.Type, Ref: Emit(i.Value, scope, activities))).ToList();
        activities.Add(new XElement(ns + local, DisplayName(name),
            inputs.Select(i => new XElement(ns + $"{local}.{i.Name}",
                new XElement(Wf + "InArgument", TypeArguments(i.Type.Xaml), i.Ref is null ? "{x:Null}" : Expr(i.Ref)))),
            custom.Outputs.Select(o => new XElement(ns + $"{local}.{o.Property}",
                new XElement(Wf + "OutArgument", TypeArguments("x:Object"), Expr($"{name}_{o.Name}"))))));
        return DesignerSequence(Display(name, custom.Description), scope.Variables, activities);
    }

    // ---- actions: set output argument (export Recalculate) ----------------------------------------------------------------

    /// <summary>
    /// Set an output argument (export Recalculate, a Decimal): the value, the designer's <c>TypeName</c> variable (the type's
    /// assembly-qualified name) and an <c>Assign</c> to the argument of the value cast with <c>DirectCast</c>. Other types are the same
    /// shape (assumed: catalog <c>Vb:DirectCast:OtherPropertyTypes</c>).
    /// </summary>
    private XElement WriteSetOutput(SetOutput output)
    {
        var name = Next("AssignOutputArgumentStep");
        var scope = new Scope(name);
        var activities = new List<XElement>();
        var value = Emit(output.Value, scope, activities) ?? "Nothing";
        // The export writes no space after the comma here (unlike the stop step's DirectCast).
        activities.Add(Assign(output.Type.Xaml, output.Argument, $"DirectCast({value},{output.Type.Clr})"));
        var variables = new List<XElement>(scope.Variables) { Variable("x:String", "TypeName", AssemblyQualifiedName(output.Type)) };
        return DesignerSequence(Display(name, output.Description), variables, activities);
    }

    /// <summary>The assembly-qualified name of a property type's CLR type (export: <c>System.Decimal, mscorlib, Version=4.0.0.0, ...</c>).</summary>
    private static string AssemblyQualifiedName(PropertyType type) => type.Clr.StartsWith("System.", StringComparison.Ordinal)
        ? $"{type.Clr}, mscorlib, {ClrVersion}"
        : $"{type.Clr}, Microsoft.Xrm.Sdk, {SdkVersion}";

    // ---- business rule actions (export Newbusinessrule; the others assumed, fixture business-rule-entity) --------------------

    /// <summary>
    /// The name of a business rule action (A8): <c>{prefix}_{first branch of its condition}_{k}</c>, <c>k</c> counting the rule's
    /// actions in document order; the actions do not advance the designer step counter (export: <c>SetAttributeValueStep_ConditionBranchStep2_1</c>,
    /// then <c>ConditionBranchStep3</c>).
    /// </summary>
    private string RuleName(string prefix) => $"{prefix}_{_ruleBranch}_{Invariant(++_ruleActions)}";

    /// <summary>
    /// Set (or clear, assumed) a column of the primary record (export Newbusinessrule): the primary record's <c>#Temp</c> entity with
    /// its id, the column set on it, <c>mcwc:SetAttributeValue</c>, and the entity copied back.
    /// </summary>
    private XElement WriteRuleSetValue(RuleSetValue value)
    {
        var name = RuleName("SetAttributeValueStep");
        var scope = new Scope(name);
        _usesClientActivities = true;
        if (value.Value is Empty)
            Assume("clearValue");
        var primary = RecordKey.Primary(_process.Entity);
        var temp = RecordKey.Created(primary.Key + "#Temp", _process.Entity).Expression;
        var activities = new List<XElement>
        {
            Assign("mxs:Entity", temp, $"New Entity({Vb.String(_process.Entity)})"),
            Assign("s:Guid", temp + ".Id", primary.Expression + ".Id"),
        };
        SetColumns(scope, activities, _process.Entity, temp, [new Assignment(value.Field, value.Type, value.Value)]);
        activities.Add(new XElement(Mcwc + "SetAttributeValue", DisplayName(name), new XAttribute("Entity", Expr(temp)), new XAttribute("EntityName", _process.Entity)));
        activities.Add(Assign("mxs:Entity", primary.Expression, temp));
        return DesignerSequence(Display(name, value.Description), scope.Variables, activities);
    }

    /// <summary>Set a default value (assumed, fixture business-rule-entity): the value, then <c>SetDefaultValue</c> with <c>Attribute</c> and <c>Value</c>.</summary>
    private XElement WriteRuleSetDefault(RuleSetValue value)
    {
        var name = RuleName("SetDefaultValueStep");
        var scope = new Scope(name);
        var activities = new List<XElement>();
        var result = Emit(value.Value, scope, activities)!;
        activities.Add(Reference("SetDefaultValue", name, Arguments(In("x:String", "Attribute", value.Field), In("x:Object", "Value", Expr(result)))));
        return DesignerSequence(Display(name, value.Description), scope.Variables, activities);
    }

    /// <summary>
    /// Show an error message (export Newbusinessrule): <c>mcwc:SetMessage</c> on the column (<c>ControlId</c>; left out without a
    /// column, assumed) of the primary record, <c>Level</c> <c>[Nothing]</c>, its text in one <c>mcwo:StepLabel</c> whose id is
    /// generated (A9).
    /// </summary>
    private XElement WriteRuleShowError(RuleShowError error)
    {
        var name = RuleName("SetMessageStep");
        _usesClientActivities = _usesObjectModel = true;
        if (error.Field is null)
            Assume("showError:noField");
        var primary = RecordKey.Primary(_process.Entity);
        var element = new XElement(Mcwc + "SetMessage",
            error.Field is null ? null : new XAttribute("ControlId", error.Field),
            new XAttribute("Entity", Expr(primary.Expression)),
            new XAttribute("EntityName", _process.Entity),
            new XAttribute("Level", Expr("Nothing")),
            new XElement(Mcwc + "SetMessage.StepLabels",
                new XElement(Mcwo + "StepLabel",
                    new XAttribute("Description", error.Message),
                    new XAttribute("LabelId", WorkflowIdentity.LabelId(_process.Id, name).ToString("D")),
                    new XAttribute("LanguageCode", "1033"))));
        return DesignerSequence(Display(name, error.Description), [], [element]);
    }

    /// <summary>
    /// A form-only action (assumed, fixture business-rule-entity): <c>SetVisibility</c> (<c>IsVisible</c>), <c>SetDisplayMode</c>
    /// (<c>IsReadOnly</c>, by analogy: no file shows it) or <c>SetFieldRequiredLevel</c> (<c>RequiredLevel</c>, the SDK's
    /// <c>AttributeRequiredLevel</c>: none 0, required 2, recommended 3) with the column's <c>ControlId</c>.
    /// </summary>
    private XElement WriteRuleClientOnly(RuleClientOnly client)
    {
        static string Flag(string setting) => setting == "true" ? "True" : "False";
        var (type, key, xamlType, value) = client.Kind switch
        {
            "setVisibility" => ("SetVisibility", "IsVisible", "x:Boolean", Flag(client.Setting)),
            "setLock" => ("SetDisplayMode", "IsReadOnly", "x:Boolean", Flag(client.Setting)),
            _ => ("SetFieldRequiredLevel", "RequiredLevel", "x:Int32", client.Setting switch { "required" => "2", "recommended" => "3", _ => "0" }),
        };
        var name = RuleName(type + "Step");
        var activity = Reference(type, name, Arguments(In("x:String", "ControlId", client.Field), In(xamlType, key, value)));
        return DesignerSequence(Display(name, client.Description), [], [activity]);
    }
}
