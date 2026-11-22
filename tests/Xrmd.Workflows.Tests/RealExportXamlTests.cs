using FluentAssertions;
using Xrmd.Workflows.Xaml;
using static Xrmd.Workflows.Xaml.ValueExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The five XAML files of the parity export <c>xrmdWfTest_1_0_0_2.zip</c> (tests/fixtures/workflows/real, byte for byte; plan 2.14,
/// Task 3b): each parses with no unsupported step, into the step tree the designer meant.
/// </summary>
public class RealExportXamlTests
{
    private const string Order = "xrmv_order";
    private static readonly RecordRef.Primary Primary = new();

    private static WorkflowDefinition Real(string file, int category) =>
        XamlWorkflowParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real", file + ".xaml")),
            category, workflowSdkAvailable: false);

    private const string EntityReference = "Microsoft.Xrm.Sdk.EntityReference";

    /// <summary>
    /// The primary key read with <c>TargetType</c> <c>mxs:EntityReference</c> (parity export): the Guid as a reference to the record.
    /// </summary>
    private static readonly Conversion OrderReference = new("System.Guid", EntityReference, new AttributeOf(Primary, "xrmv_orderid"), Order);

    private static IReadOnlyList<WorkflowStep> Steps(WorkflowStep step) => step.Should().BeOfType<SequenceStep>().Subject.Steps;

    [Fact]
    public void Child_is_one_stop_step_canceled_without_message()
    {
        var d = Real("Child-0FE1B782-E5DE-F111-84E8-002248872B66", 0);

        d.Unsupported.Should().BeEmpty();
        d.Arguments.Should().BeEmpty();
        Steps(d.Root).Should().Equal(new StopStep("0/0", "StopWorkflowStep1", Canceled: true, Message: null));
    }

    [Fact]
    public void OnCreate_creates_a_task_then_starts_the_child_workflow()
    {
        var d = Real("OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66", 0);

        d.Unsupported.Should().BeEmpty();
        d.PrimaryEntity.Should().Be(Order);
        var steps = Steps(d.Root);
        steps.Should().HaveCount(2);

        var create = steps[0].Should().BeOfType<CreateStep>().Subject;
        (create.Path, create.DisplayName, create.Entity, create.Variable).Should().Be(("0/0", "CreateStep1", "task", "CreateStep1_localParameter"));
        create.Values.Should().BeEquivalentTo(new Dictionary<string, ValueExpr>
        {
            ["subject"] = new Literal("String", "TEST"),
            ["regardingobjectid"] = OrderReference,
            ["prioritycode"] = new Literal("OptionSetValue", 1),
        }, o => o.RespectingRuntimeTypes());

        steps[1].Should().Be(new ChildWorkflowStep("0/1", "ChildWorkflowStep2", Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66"), Primary));
    }

    [Fact]
    public void OnUpdate_waits_for_the_amount_then_sends_an_email_to_the_account_primary_contact()
    {
        var d = Real("OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66", 0);

        d.Unsupported.Should().BeEmpty();
        var block = Steps(d.Root).Should().ContainSingle().Subject.Should().BeOfType<SequenceStep>().Subject;
        (block.Path, block.DisplayName).Should().Be(("0/0", "WaitStep1"));
        block.Steps.Should().HaveCount(2);

        // ConditionSequence Wait=True with one ConditionBranch (WaitBranchStep2): wait until the condition holds, no timeout.
        block.Steps[0].Should().Be(new WaitStep("0/0/3", "WaitStep1",
            new ConditionExpr.Compare("GreaterThan", new AttributeOf(Primary, "xrmv_amount"), new Literal("Decimal", 100m)), null, null));

        // The branch's Then runs once the condition holds.
        var then = block.Steps[1].Should().BeOfType<SequenceStep>().Subject;
        (then.Path, then.DisplayName).Should().Be(("0/0/3/then", "WaitBranchStep2"));
        var email = then.Steps.Should().ContainSingle().Subject.Should().BeOfType<SendEmailStep>().Subject;
        (email.Path, email.DisplayName, email.CreatedEmailVariable).Should().Be(("0/0/3/then/0", "SendEmailStep3", "SendEmailStep3_localParameter"));
        email.Values.Should().BeEquivalentTo(new Dictionary<string, ValueExpr>
        {
            // The lookup read with TargetType mxs:EntityCollection: an activity party list with that one party.
            ["to"] = new Conversion(EntityReference, "Microsoft.Xrm.Sdk.EntityCollection",
                new AttributeOf(new RecordRef.Related("xrmv_accountid"), "primarycontactid") { TargetType = EntityReference }),
            ["regardingobjectid"] = OrderReference,
            ["correlatedsubjectchanged"] = new Literal("Boolean", false),
            ["deliveryprioritycode"] = new Literal("OptionSetValue", 1),
            ["directioncode"] = new Literal("Boolean", true),
            ["followemailuserpreference"] = new Literal("Boolean", false),
            ["isduplicatesenderunresolved"] = new Literal("Boolean", false),
            ["notifications"] = new Literal("OptionSetValue", 0),
            ["prioritycode"] = new Literal("OptionSetValue", 1),
            ["transactioncurrencyid"] = new Literal("EntityReference",
                new EntityRefValue("transactioncurrency", Guid.Parse("692363f0-a3da-f111-84e8-3833c5c48caf"))),
        }, o => o.RespectingRuntimeTypes());
    }

    [Fact]
    public void Business_rule_sets_the_status_when_the_amount_is_empty_else_shows_an_error()
    {
        var d = Real("Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66", 2);

        d.Unsupported.Should().BeEmpty();
        d.PrimaryEntity.Should().Be(Order);
        var condition = Steps(d.Root).Should().ContainSingle().Subject.Should().BeOfType<ConditionStep>().Subject;
        (condition.Path, condition.DisplayName).Should().Be(("0/0", "ConditionStep1"));

        var branch = condition.Branches.Should().ContainSingle().Subject;
        branch.When.Should().Be(new ConditionExpr.Compare("Null", new AttributeOf(Primary, "xrmv_amount"), null));
        var then = branch.Then.Should().BeOfType<SequenceStep>().Subject;
        (then.Path, then.DisplayName).Should().Be(("0/0/2/then", "ConditionBranchStep2"));
        then.Steps.Should().Equal(new SetValueStep("0/0/2/then/0", "SetAttributeValueStep_ConditionBranchStep2_1: New Action",
            "xrmv_status", new Literal("OptionSetValue", 128830001), DefaultOnly: false));

        // The second branch's condition is the literal True (the designer's else): it is the otherwise block.
        var otherwise = condition.Otherwise.Should().BeOfType<SequenceStep>().Subject;
        (otherwise.Path, otherwise.DisplayName).Should().Be(("0/0/4/then", "ConditionBranchStep3"));
        var error = otherwise.Steps.Should().ContainSingle().Subject.Should().BeOfType<ShowErrorStep>().Subject;
        (error.Path, error.DisplayName).Should().Be(("0/0/4/then/0", "SetMessageStep_ConditionBranchStep2_2: New Action"));
        error.Message.Should().Be(new Literal("String", "fdsfds"));
        error.Attribute.Should().Be("xrmv_status");
    }

    [Fact]
    public void Branch_description_is_read()
    {
        // Plan 2.21, Task 3: the designer's condition description is the branch's x:String Description (the export's rule), x:Null else.
        var rule = Real("Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66", 2);
        Steps(rule.Root).OfType<ConditionStep>().Single().Branches.Single().Description.Should().Be("New Condition");
        var action = Real("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3);
        Steps(action.Root).OfType<ConditionStep>().Single().Branches.Single().Description.Should().BeNull();
    }

    [Fact]
    public void Set_values_keep_xaml_order()
    {
        // Plan 2.21, Task 3: the columns of a send email (create, update) step in the order the XAML sets them, by contract (an
        // ordered dictionary, not a Dictionary whose enumeration order is unspecified).
        var d = Real("OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66", 0);
        var email = d.Root.DepthFirst().OfType<SendEmailStep>().Single();
        email.Values.Should().BeOfType<OrderedDictionary<string, ValueExpr>>();
        email.Values.Keys.Should().Equal("to", "regardingobjectid", "correlatedsubjectchanged", "deliveryprioritycode", "directioncode",
            "followemailuserpreference", "isduplicatesenderunresolved", "notifications", "prioritycode", "transactioncurrencyid");
        Real("OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66", 0).Root.DepthFirst().OfType<CreateStep>().Single().Values.Keys
            .Should().Equal("subject", "regardingobjectid", "prioritycode");
        Real("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3).Root.DepthFirst().OfType<UpdateStep>().Single().Values
            .Should().BeOfType<OrderedDictionary<string, ValueExpr>>();
    }

    [Fact]
    public void Business_rule_activities_of_the_export_are_unsupported_outside_a_business_rule()
    {
        var d = Real("Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66", 0);

        d.Unsupported.Select(u => u.Element).Should().Equal("SetAttributeValue", "SetMessage");
    }

    [Fact]
    public void Recalculate_declares_its_arguments()
    {
        var d = Real("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3);

        d.Unsupported.Should().BeEmpty();
        d.Arguments.Should().Equal(
            new WorkflowArgument("Increment", "System.Decimal", IsInput: true, Required: true, EntityTarget: null)
                { IsTarget = false, Description = "New Argument" },
            new WorkflowArgument("Total", "System.Decimal", IsInput: false, Required: true, EntityTarget: null)
                { IsTarget = false, Description = "New Argument" },
            new WorkflowArgument("Target", "Microsoft.Xrm.Sdk.EntityReference", IsInput: true, Required: true, EntityTarget: Order)
                { IsTarget = true, Description = "Target Argument" });
    }

    [Fact]
    public void Recalculate_updates_the_amount_assigns_the_total_and_stops_on_a_negative_increment()
    {
        // The amount is read with TargetType x:Decimal: the column's value, which must be a decimal.
        var amount = new AttributeOf(Primary, "xrmv_amount") { TargetType = "System.Decimal" };
        var d = Real("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3);

        d.PrimaryEntity.Should().Be(Order);
        var steps = Steps(d.Root);
        steps.Should().HaveCount(3);

        var update = steps[0].Should().BeOfType<UpdateStep>().Subject;
        (update.Path, update.DisplayName, update.Record).Should().Be(("0/0", "UpdateStep1", (RecordRef)Primary));
        update.Values.Should().BeEquivalentTo(new Dictionary<string, ValueExpr>
        {
            ["xrmv_amount"] = new Arithmetic("Add", [amount, new VariableRef("Increment")]),
        }, o => o.RespectingRuntimeTypes());
        ((Arithmetic)update.Values["xrmv_amount"]).Operands.Should().Equal(amount, new VariableRef("Increment"));

        steps[1].Should().Be(new AssignOutputStep("0/1", "AssignOutputArgumentStep2: Total\\", "Total",
            new Cast("System.Decimal", amount)));

        var condition = steps[2].Should().BeOfType<ConditionStep>().Subject;
        (condition.Path, condition.DisplayName, condition.Otherwise).Should().Be(("0/2", "ConditionStep3", (WorkflowStep?)null));
        var branch = condition.Branches.Should().ContainSingle().Subject;
        branch.When.Should().Be(new ConditionExpr.Compare("LessThan", new VariableRef("Increment"), new Literal("Decimal", 0m)));
        Steps(branch.Then).Should().Equal(new StopStep("0/2/3/then/0", "StopWorkflowStep5", Canceled: true, Message: null));
    }

    [Theory]
    [InlineData("Child-0FE1B782-E5DE-F111-84E8-002248872B66", 0, 1, false, null, 40, true, false)]
    [InlineData("OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66", 0, 1, true, null, 40, false, false)]
    [InlineData("OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66", 0, 0, false, "xrmv_status", null, false, true)]
    [InlineData("Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66", 2, 1, false, null, null, false, false)]
    [InlineData("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3, 0, false, null, 40, false, false)]
    public void Every_real_file_parses_without_unsupported_steps_and_validates_with_its_row(
        string file, int category, int mode, bool onCreate, string? updateList, int? createStage, bool subprocess, bool asyncAutoDelete)
    {
        var d = Real(file, category);

        d.Unsupported.Should().BeEmpty();
        d.Root.DepthFirst().Should().NotContain(s => s is ClientOnlyStep);
        // The row values of customizations.xml for that process.
        var row = new WorkflowRow
        {
            Category = category, Mode = mode, PrimaryEntity = Order, TriggerOnCreate = onCreate, CreateStage = createStage,
            TriggerOnUpdateAttributes = updateList, UniqueName = category == 3 ? "Recalculate" : null, Subprocess = subprocess,
            AsyncAutoDelete = asyncAutoDelete, Scope = 4, RunAs = 1, IsTransacted = true,
        };
        WorkflowValidator.Validate(d, row).Should().BeEmpty();
    }

    /// <summary>The five files with the category of their process in the export.</summary>
    public static readonly (string File, int Category)[] RealFiles =
    [
        ("Child-0FE1B782-E5DE-F111-84E8-002248872B66", 0),
        ("OnCreate-3E1F0307-D8DE-F111-84E8-002248872B66", 0),
        ("OnUpdate-BC9A625B-E4DE-F111-84E8-002248872B66", 0),
        ("Newbusinessrule-216FA344-E5DE-F111-84E8-002248872B66", 2),
        ("Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66", 3),
    ];

    /// <summary>
    /// Plan 2.14, Task 9 (R4): the catalog entries a parse of the real files relies on are exactly the entries marked <c>export</c>,
    /// so no entry an export confirms stays <c>learn</c> or <c>assumed</c>, and no <c>export</c> entry lacks a use. The real files are
    /// the five processes of the 2.14 parity export and the calculated (plan 2.12a, Task 6) and rollup (plan 2.12b, Task 2) column definitions of the 2.12a one.
    /// </summary>
    [Fact]
    public void The_catalog_entries_the_export_uses_are_the_export_entries()
    {
        var used = RealFiles.SelectMany(f => XamlWorkflowParser.CatalogEntriesUsed(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real", f.File + ".xaml")), f.Category,
            workflowSdkAvailable: false)).ToHashSet();
        foreach (var (column, make) in RealCalculatedXamlTests.CalculatedColumns)
            used.UnionWith(XamlWorkflowParser.CatalogEntriesUsed(RealCalculatedXamlTests.Real(column), XamlWorkflowParser.CalculatedCategory,
                workflowSdkAvailable: false, Xrmd.Workflows.Calculated.CalculatedColumns.TargetTypeOf(make())));
        // Plan 2.12b, Task 2: the rollup column definitions of the 2.12a parity export (tests/fixtures/rollup/real).
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real"), "*.xaml"))
            used.UnionWith(XamlWorkflowParser.CatalogEntriesUsed(File.ReadAllText(file), XamlWorkflowParser.RollupCategory, workflowSdkAvailable: false));

        string.Join(", ", ActivityCatalog.Entries.Where(e => used.Contains(e.Element) && e.Source != "export").Select(e => $"{e.Element} ({e.Source})"))
            .Should().BeEmpty("every entry the parity export uses is confirmed by it");
        string.Join(", ", ActivityCatalog.Entries.Where(e => e.Source == "export" && !used.Contains(e.Element)).Select(e => e.Element))
            .Should().BeEmpty("an entry marked export is used by the export");
    }

    /// <summary>Review M5: CatalogEntriesUsed loads the XAML as Parse does, root check included.</summary>
    [Fact]
    public void Catalog_entries_used_refuses_what_parse_refuses()
    {
        var notAnActivity = () => XamlWorkflowParser.CatalogEntriesUsed("<Sequence xmlns=\"urn:other\" />", 0, workflowSdkAvailable: false);
        notAnActivity.Should().Throw<XamlWorkflowException>().WithMessage("*root must be an Activity*");
        var malformed = () => XamlWorkflowParser.CatalogEntriesUsed("<Activity", 0, workflowSdkAvailable: false);
        malformed.Should().Throw<XamlWorkflowException>().WithMessage("*not well-formed*");
        var empty = () => XamlWorkflowParser.CatalogEntriesUsed(" ", 0, workflowSdkAvailable: false);
        empty.Should().Throw<XamlWorkflowException>();
    }
}
