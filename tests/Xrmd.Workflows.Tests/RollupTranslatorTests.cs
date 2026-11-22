using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Rollup;
using Xrmd.Workflows.Xaml;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The translation of rollup column XAML into <see cref="RollupDefinition"/> (plan 2.12b, Task 2): the four definitions of the parity
/// export (tests/fixtures/rollup/real), the provisional fixtures of tests/fixtures/rollup (shapes the export lacks, ruling R1) and
/// variants of them for the refusals (rulings R3, R4, R5).
/// </summary>
public class RollupTranslatorTests
{
    private const string Order = "xrmv_calcorder";
    private const string OrderRelationship = "xrmv_calcorder_AccountId_account";
    private const string OrderLink = "xrmv_calcorder.xrmv_accountid.xrmv_calcorder_AccountId_account";
    private const string OrderKey = "relatedlinked_xrmv_calcorder_AccountId_account#xrmv_accountid#xrmv_calcorder#Temp";

    private static string Real(string column) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", $"account-{column}.xaml"));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", name + ".xaml"));

    private static readonly string[] RealColumns = ["xrmv_ordertotal", "xrmv_ordercount", "xrmv_lastorder", "xrmv_treebudget",
        "xrmv_avgamount", "xrmv_firstorder", "xrmv_wonprice", "xrmv_treeorders"];

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(new Label(), v)).ToList()));

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name };
        entity.SetAttributes(attributes);
        return entity;
    }

    /// <summary>The tables and relationships of the parity export, as far as the definitions read them.</summary>
    private sealed class Model
    {
        public Dictionary<string, EntityMetadata> Tables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OneToManyRelationshipMetadata> Relationships { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Model(bool hierarchical = true)
        {
            Tables[Order] = Table(Order,
                new AttributeMetadata { LogicalName = "xrmv_calcorderid" },
                new DecimalAttributeMetadata { LogicalName = "xrmv_amount", Precision = 2 },
                new MoneyAttributeMetadata { LogicalName = "xrmv_price" },
                new PicklistAttributeMetadata { LogicalName = "xrmv_status", OptionSet = Options(1, 2, 3) },
                new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", DateTimeBehavior = DateTimeBehavior.UserLocal },
                new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 },
                new LookupAttributeMetadata { LogicalName = "xrmv_accountid", Targets = ["account"] });
            Tables["account"] = Table("account",
                new AttributeMetadata { LogicalName = "accountid" },
                new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 },
                new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] },
                new MoneyAttributeMetadata { LogicalName = "xrmv_budget" },
                Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_ordertotal" }, Real("xrmv_ordertotal")),
                Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_ordercount" }, Real("xrmv_ordercount")),
                Rollup(new DateTimeAttributeMetadata { LogicalName = "xrmv_lastorder", DateTimeBehavior = DateTimeBehavior.UserLocal }, Real("xrmv_lastorder")),
                Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_treebudget" }, Real("xrmv_treebudget")));
            Relationships[OrderRelationship] = new OneToManyRelationshipMetadata
            {
                SchemaName = OrderRelationship, ReferencedEntity = "account", ReferencedAttribute = "accountid",
                ReferencingEntity = Order, ReferencingAttribute = "xrmv_accountid",
            };
            Relationships["account_parent_account"] = new OneToManyRelationshipMetadata
            {
                SchemaName = "account_parent_account", ReferencedEntity = "account", ReferencedAttribute = "accountid",
                ReferencingEntity = "account", ReferencingAttribute = "parentaccountid", IsHierarchical = hierarchical,
            };
        }

        public EntityMetadata? Find(string name) => Tables.GetValueOrDefault(name);

        public OneToManyRelationshipMetadata? Relationship(string name) => Relationships.GetValueOrDefault(name);

        /// <summary>Translates the rollup <paramref name="column"/> of <c>account</c>; <paramref name="xaml"/> replaces its definition.</summary>
        public RollupDefinition Translate(string column, string? xaml = null, AttributeMetadata? make = null, RollupColumnsService? service = null)
        {
            var account = Tables["account"];
            var attribute = make ?? account.Attributes.Single(a => a.LogicalName == column);
            if (xaml is not null)
                Rollup(attribute, xaml);
            return (service ?? new RollupColumnsService()).Translate(account, attribute, Find, Relationship);
        }
    }

    private static T Rollup<T>(T attribute, string xaml) where T : AttributeMetadata
    {
        attribute.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(attribute, xaml);
        return attribute;
    }

    private static Column Own(string attribute) => new(null, attribute);

    /// <summary>A new rollup column of <c>account</c> (Whole number) defined by <paramref name="xaml"/>.</summary>
    private static RollupDefinition TranslateNew(string xaml, Model? model = null) =>
        (model ?? new Model()).Translate("xrmv_new", xaml, new IntegerAttributeMetadata { LogicalName = "xrmv_new" });

    private static Action Refusal(string xaml, Model? model = null) => () => TranslateNew(xaml, model);

    private static int Code(Action act) => act.Should().Throw<XrmdFaultException>().Which.ErrorCode;

    // ---- the parity export --------------------------------------------------------------------------------------------

    [Fact]
    public void Real_ordertotal_is_sum_with_status_filter()
    {
        var parsed = XamlWorkflowParser.ParseRollup(Real("xrmv_ordertotal"));
        parsed.Operator.Should().Be("Sum");
        parsed.HierarchicalRelationship.Should().BeNull();
        parsed.SourceFilter.Should().BeNull();
        parsed.Related.Should().Be(new RollupXamlRelated(Order, "xrmv_accountid", OrderRelationship,
            new ConditionExpr.Compare("Equal", new ValueExpr.AttributeOf(new RecordRef.Primary(), "xrmv_status"),
                new ValueExpr.Literal("OptionSetValue", 2))));
        (parsed.AggregateEntity, parsed.AggregateAttribute).Should().Be((Order, "xrmv_amount"));

        var d = new Model().Translate("xrmv_ordertotal");

        d.Aggregate.Should().Be(RollupAggregate.Sum);
        d.AggregatedAttribute.Should().Be("xrmv_amount");
        d.HierarchicalRelationship.Should().BeNull();
        d.SourceFilter.Should().BeNull();
        d.Related!.Relationship.Should().Be(OrderRelationship);
        d.Related.Entity.Should().Be(Order);
        d.Related.Lookup.Should().Be("xrmv_accountid");
        d.Related.Filter.Should().Be(new ComputedCondition.Compare("Equal", Own("xrmv_status"), new Literal(2)));
        d.Sources.Should().Equal(new RollupSource(Order, "xrmv_amount"), new RollupSource(Order, "xrmv_accountid"),
            new RollupSource(Order, "xrmv_status"));
    }

    [Fact]
    public void Real_ordercount_is_count_of_primary_key()
    {
        var d = new Model().Translate("xrmv_ordercount");

        d.Aggregate.Should().Be(RollupAggregate.Count);
        d.AggregatedAttribute.Should().Be("xrmv_calcorderid");
        d.Related.Should().Be(new RollupRelated(OrderRelationship, Order, "xrmv_accountid", null));
        d.HierarchicalRelationship.Should().BeNull();
        d.SourceFilter.Should().BeNull();
        d.Sources.Should().Equal(new RollupSource(Order, "xrmv_calcorderid"), new RollupSource(Order, "xrmv_accountid"));
    }

    [Fact]
    public void Real_lastorder_is_max_of_startedon()
    {
        var d = new Model().Translate("xrmv_lastorder");

        d.Aggregate.Should().Be(RollupAggregate.Max);
        d.AggregatedAttribute.Should().Be("xrmv_startedon");
        d.Related.Should().Be(new RollupRelated(OrderRelationship, Order, "xrmv_accountid", null));
        d.AggregatedEntity("account").Should().Be(Order);
    }

    [Fact]
    public void Real_treebudget_is_hierarchy_sum_of_own_column()
    {
        var parsed = XamlWorkflowParser.ParseRollup(Real("xrmv_treebudget"));
        parsed.HierarchicalRelationship.Should().Be("account_parent_account");
        parsed.Related.Should().BeNull();

        var d = new Model().Translate("xrmv_treebudget");

        d.Aggregate.Should().Be(RollupAggregate.Sum);
        d.HierarchicalRelationship.Should().Be("account_parent_account");
        d.Related.Should().BeNull();
        d.AggregatedAttribute.Should().Be("xrmv_budget");
        d.AggregatedEntity("account").Should().Be("account");
        d.Sources.Should().Equal(new RollupSource("account", "xrmv_budget"), new RollupSource("account", "parentaccountid"));
    }

    [Fact]
    public void Every_real_rollup_uses_only_export_catalog_entries()
    {
        var used = RealColumns.SelectMany(c => XamlWorkflowParser.CatalogEntriesUsed(Real(c), XamlWorkflowParser.RollupCategory,
            workflowSdkAvailable: false)).ToHashSet();

        used.Should().Contain(["Shape:RollupRule", "Shape:RollupHierarchy", "Shape:RollupRelatedLink", "EvaluateExpression:Sum",
            "EvaluateExpression:Count", "EvaluateExpression:Maximum", "EvaluateExpression:Minimum", "EvaluateExpression:Average",
            "EvaluateCondition:Equal", "EvaluateExpression:CreateCrmType"]);
        string.Join(", ", ActivityCatalog.Entries.Where(e => used.Contains(e.Element) && e.Source != "export").Select(e => $"{e.Element} ({e.Source})"))
            .Should().BeEmpty("every entry the real rollup definitions use is confirmed by the exports");
    }

    [Fact]
    public void Real_rollups_of_export_1_0_0_5_translate()
    {
        var model = new Model();

        var avg = model.Translate("xrmv_avgamount", Real("xrmv_avgamount"), new DecimalAttributeMetadata { LogicalName = "xrmv_avgamount" });
        avg.Aggregate.Should().Be(RollupAggregate.Avg);
        avg.AggregatedAttribute.Should().Be("xrmv_amount");
        avg.Related.Should().Be(new RollupRelated(OrderRelationship, Order, "xrmv_accountid", null));

        var min = model.Translate("xrmv_firstorder", Real("xrmv_firstorder"),
            new DateTimeAttributeMetadata { LogicalName = "xrmv_firstorder", DateTimeBehavior = DateTimeBehavior.UserLocal });
        min.Aggregate.Should().Be(RollupAggregate.Min);
        min.AggregatedAttribute.Should().Be("xrmv_startedon");
        min.Related!.Filter.Should().BeNull();

        var won = model.Translate("xrmv_wonprice", Real("xrmv_wonprice"), new MoneyAttributeMetadata { LogicalName = "xrmv_wonprice" });
        won.Aggregate.Should().Be(RollupAggregate.Sum);
        won.AggregatedAttribute.Should().Be("xrmv_price");
        won.Related!.Filter.Should().BeNull();

        var tree = model.Translate("xrmv_treeorders", Real("xrmv_treeorders"), new IntegerAttributeMetadata { LogicalName = "xrmv_treeorders" });
        tree.Aggregate.Should().Be(RollupAggregate.Count);
        tree.AggregatedAttribute.Should().Be("xrmv_calcorderid");
        tree.HierarchicalRelationship.Should().BeNull();
        tree.SourceFilter.Should().BeNull();
    }

    private static string Designer(string column) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", "designer", $"account-{column}.xaml"));

    [Fact]
    public void Designer_two_related_conditions_are_anded()
    {
        var d = new Model().Translate("xrmv_firstorder", Designer("xrmv_firstorder"),
            new DateTimeAttributeMetadata { LogicalName = "xrmv_firstorder", DateTimeBehavior = DateTimeBehavior.UserLocal });

        d.Aggregate.Should().Be(RollupAggregate.Min);
        d.AggregatedAttribute.Should().Be("xrmv_startedon");
        d.Related!.Filter.Should().NotBeNull();
        d.Related.Filter!.ToString().Should().Contain("xrmv_status").And.Contain("xrmv_amount");
    }

    [Fact]
    public void Designer_hierarchy_count_with_source_filter()
    {
        var d = new Model().Translate("xrmv_treeorders", Designer("xrmv_treeorders"), new IntegerAttributeMetadata { LogicalName = "xrmv_treeorders" });

        d.Aggregate.Should().Be(RollupAggregate.Count);
        d.HierarchicalRelationship.Should().Be("account_parent_account");
        d.Related!.Relationship.Should().Be(OrderRelationship);
        d.SourceFilter.Should().NotBeNull();
        d.SourceFilter!.ToString().Should().Contain("xrmv_budget");
    }

    // ---- the provisional fixtures (assumed) ------------------------------------------------------------------------

    [Fact]
    public void Source_filter_and_two_conditions_are_anded()
    {
        var withSource = TranslateNew(Fixture("source-filter"));
        withSource.Aggregate.Should().Be(RollupAggregate.Count);
        withSource.HierarchicalRelationship.Should().Be("account_parent_account");
        withSource.Related.Should().Be(new RollupRelated(OrderRelationship, Order, "xrmv_accountid", null));
        withSource.SourceFilter.Should().Be(new ComputedCondition.Compare("NotNull", Own("xrmv_budget"), null));
        withSource.Sources.Should().BeEquivalentTo([new RollupSource(Order, "xrmv_calcorderid"), new RollupSource(Order, "xrmv_accountid"),
            new RollupSource("account", "xrmv_budget"), new RollupSource("account", "parentaccountid")]);

        var two = new Model().Translate("xrmv_bigtotal", Fixture("two-conditions"), new DecimalAttributeMetadata { LogicalName = "xrmv_bigtotal" });
        two.Aggregate.Should().Be(RollupAggregate.Sum);
        two.Related!.Filter.Should().Be(new ComputedCondition.And(
            new ComputedCondition.Compare("Equal", Own("xrmv_status"), new Literal(1)),
            new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(100m))));
        two.SourceFilter.Should().BeNull();
    }

    [Fact]
    public void Minimum_and_average_operators()
    {
        var model = new Model();
        var min = model.Translate("xrmv_firstorder", Fixture("minimum"),
            new DateTimeAttributeMetadata { LogicalName = "xrmv_firstorder", DateTimeBehavior = DateTimeBehavior.UserLocal });
        min.Aggregate.Should().Be(RollupAggregate.Min);
        min.AggregatedAttribute.Should().Be("xrmv_startedon");

        var avg = model.Translate("xrmv_avgamount", Fixture("average"), new DecimalAttributeMetadata { LogicalName = "xrmv_avgamount" });
        avg.Aggregate.Should().Be(RollupAggregate.Avg);
        avg.AggregatedAttribute.Should().Be("xrmv_amount");
    }

    [Fact]
    public void Currency_sum_aggregates_the_related_money_column()
    {
        var d = new Model().Translate("xrmv_wonprice", Fixture("currency-sum"), new MoneyAttributeMetadata { LogicalName = "xrmv_wonprice" });

        d.Aggregate.Should().Be(RollupAggregate.Sum);
        d.AggregatedAttribute.Should().Be("xrmv_price");
        d.Related!.Filter.Should().Be(new ComputedCondition.Compare("Equal", Own("xrmv_status"), new Literal(2)));
    }

    // ---- refusals --------------------------------------------------------------------------------------------------

    [Fact]
    public void Relationship_display_name_and_entity_key_must_agree()
    {
        var xaml = Real("xrmv_ordercount").Replace($"DisplayName=\"{OrderLink}\"", "DisplayName=\"xrmv_calcorder.xrmv_otherid.xrmv_calcorder_AccountId_account\"");

        Refusal(xaml).Should().Throw<XrmdValidationException>()
            .WithMessage("*'account.xrmv_new'*xrmv_calcorder.xrmv_otherid.xrmv_calcorder_AccountId_account*");
        var entityName = Real("xrmv_ordercount").Replace("EntityName=\"xrmv_calcorder\" />", "EntityName=\"account\" />");
        Refusal(entityName).Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_new'*");
    }

    [Fact]
    public void Unknown_aggregate_operator_is_refused_with_its_name()
    {
        var xaml = Real("xrmv_ordertotal").Replace("x:Key=\"ExpressionOperator\">Sum<", "x:Key=\"ExpressionOperator\">Median<");

        Refusal(xaml).Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_new'*Median*");
        // The aggregate operators are rollup definitions only: a process using one has an unsupported step.
        XamlWorkflowParser.Parse(Real("xrmv_treebudget"), 0, workflowSdkAvailable: false).Root.DepthFirst().OfType<UnsupportedStep>()
            .Should().Contain(u => u.Reason.Contains("'Sum'"));
    }

    [Fact]
    public void Unknown_relationship_is_refused()
    {
        var xaml = Real("xrmv_ordercount").Replace(OrderRelationship, "xrmv_calcorder_nosuch_account");
        Code(Refusal(xaml)).Should().Be(unchecked((int)0x80060534));
        Refusal(xaml).Should().Throw<XrmdFaultException>().WithMessage("*xrmv_calcorder_nosuch_account*");

        // The relationship exists, but its referencing column is not the one the definition names.
        var otherLookup = Real("xrmv_ordercount").Replace(".xrmv_accountid.", ".xrmv_name.").Replace("#xrmv_accountid#", "#xrmv_name#");
        Code(Refusal(otherLookup)).Should().Be(unchecked((int)0x80060534));
    }

    [Fact]
    public void Self_referential_related_is_refused()
    {
        var xaml = Real("xrmv_ordercount")
            .Replace(OrderLink, "account.parentaccountid.account_parent_account")
            .Replace(OrderKey, "relatedlinked_account_parent_account#parentaccountid#account#Temp")
            .Replace("EntityName=\"xrmv_calcorder\"", "EntityName=\"account\"")
            .Replace("Attribute=\"xrmv_calcorderid\"", "Attribute=\"accountid\"");

        Code(Refusal(xaml)).Should().Be(unchecked((int)0x80060551));
    }

    [Fact]
    public void Activitypointer_related_is_refused()
    {
        foreach (var table in new[] { "activitypointer", "activityparty" })
        {
            var xaml = Real("xrmv_ordercount")
                .Replace(OrderLink, $"{table}.regardingobjectid.account_{table}")
                .Replace(OrderKey, $"relatedlinked_account_{table}#regardingobjectid#{table}#Temp")
                .Replace("EntityName=\"xrmv_calcorder\"", $"EntityName=\"{table}\"")
                .Replace("Attribute=\"xrmv_calcorderid\"", $"Attribute=\"{table}id\"");

            Code(Refusal(xaml)).Should().Be(unchecked((int)0x80060552), table);
        }
    }

    [Fact]
    public void Hierarchy_without_hierarchical_relationship_is_refused()
    {
        // The table's relationship is not hierarchical.
        Code(() => new Model(hierarchical: false).Translate("xrmv_treebudget")).Should().Be(unchecked((int)0x80060535));
        // Another relationship than the table's hierarchy, or none of that name.
        var other = Real("xrmv_treebudget").Replace("Default=\"account_parent_account\"", $"Default=\"{OrderRelationship}\"");
        Code(() => new Model().Translate("xrmv_treebudget", other)).Should().Be(unchecked((int)0x80060535));
        var missing = Real("xrmv_treebudget").Replace("Default=\"account_parent_account\"", "Default=\"account_nosuch_account\"");
        var act = () => new Model().Translate("xrmv_treebudget", missing);
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == unchecked((int)0x80060535)).WithMessage("*account_nosuch_account*");
    }

    [Fact]
    public void No_related_and_no_hierarchy_is_refused()
    {
        var xaml = Real("xrmv_treebudget").Replace("Default=\"account_parent_account\"", "Default=\"[Nothing]\"");

        Code(() => new Model().Translate("xrmv_treebudget", xaml)).Should().Be(unchecked((int)0x80060533));
    }

    [Fact]
    public void Calculated_xaml_is_not_a_rollup_definition()
    {
        var calculated = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "real", "xrmv_calcorder-xrmv_net.xaml"));

        Refusal(calculated).Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_new'*not valid*");
        var parse = () => XamlWorkflowParser.ParseRollup(calculated);
        parse.Should().Throw<XamlWorkflowException>();
    }

    [Fact]
    public void Condition_operator_outside_the_catalog_is_refused_with_the_code_of_its_section()
    {
        var related = Real("xrmv_ordertotal").Replace("x:Key=\"ConditionOperator\">Equal<", "x:Key=\"ConditionOperator\">Like<");
        var relatedRefusal = Refusal(related);
        Code(relatedRefusal).Should().Be(unchecked((int)0x80060539));
        relatedRefusal.Should().Throw<XrmdFaultException>().WithMessage("*Like*");

        var source = Fixture("source-filter").Replace("x:Key=\"ConditionOperator\">NotNull<", "x:Key=\"ConditionOperator\">Under<");
        Code(Refusal(source)).Should().Be(unchecked((int)0x80060538));
    }

    [Fact]
    public void Filter_on_a_column_that_does_not_exist_or_does_not_fit_is_refused()
    {
        var missing = Real("xrmv_ordertotal").Replace("Attribute=\"xrmv_status\"", "Attribute=\"xrmv_nosuch\"");
        Code(Refusal(missing)).Should().Be(unchecked((int)0x80060539));

        // 5 is not a choice of xrmv_status.
        var option = Real("xrmv_ordertotal").Replace("OptionSetValue, \"2\"", "OptionSetValue, \"5\"");
        Refusal(option).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == unchecked((int)0x80060539)).WithMessage("*5*");
    }

    [Fact]
    public void Translations_are_cached_by_definition_text()
    {
        var service = new RollupColumnsService();
        var model = new Model();
        var first = model.Translate("xrmv_ordertotal", service: service);
        service.Parses.Should().Be(1);
        model.Translate("xrmv_ordertotal", service: service).Should().BeEquivalentTo(first);
        service.Parses.Should().Be(1, "the second translation reads the cached parse");

        // A changed definition is read again.
        var changed = model.Translate("xrmv_ordertotal", Real("xrmv_ordertotal").Replace(">Sum<", ">Maximum<"), service: service);
        changed.Aggregate.Should().Be(RollupAggregate.Max);
        service.Parses.Should().Be(2);
    }

    [Fact]
    public void A_section_reads_and_writes_only_its_own_variables()
    {
        // The Target filter reads RollupRuleStep1_2, which the Source section declares and binds (account.xrmv_budget).
        var crossSection = Fixture("source-filter").Replace("""
                    EntityName="xrmv_calcorder" />
            """.Trim(), """
                    EntityName="xrmv_calcorder" />
                    <Sequence.Variables>
                      <Variable x:TypeArguments="x:Boolean" Default="False" Name="RollupRuleStep1_9" />
                    </Sequence.Variables>
                    <mxswa:ActivityReference AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.EvaluateCondition, Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" DisplayName="EvaluateCondition">
                      <mxswa:ActivityReference.Arguments>
                        <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">NotNull</InArgument>
                        <x:Null x:Key="Parameters" />
                        <InArgument x:TypeArguments="x:Object" x:Key="Operand">[RollupRuleStep1_2]</InArgument>
                        <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[RollupRuleStep1_9]</OutArgument>
                      </mxswa:ActivityReference.Arguments>
                    </mxswa:ActivityReference>
            """.Trim());
        crossSection.Should().Contain("RollupRuleStep1_9");
        var parse = () => XamlWorkflowParser.ParseRollup(crossSection);
        parse.Should().Throw<XamlWorkflowException>().WithMessage("*EvaluateCondition*RollupRuleStep1_2*Target*");
        Refusal(crossSection).Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_new'*RollupRuleStep1_2*");

        // A column read into a variable its section does not declare.
        var undeclared = Real("xrmv_ordertotal").Replace("""
                      <Variable x:TypeArguments="x:Object" Name="RollupRuleStep1_2" />
            """.Trim() + "\r\n", "");
        undeclared.Should().NotContain("Name=\"RollupRuleStep1_2\"");
        var read = () => XamlWorkflowParser.ParseRollup(undeclared);
        read.Should().Throw<XamlWorkflowException>().WithMessage("*GetEntityProperty*RollupRuleStep1_2*Target*");
    }

    [Fact]
    public void A_member_other_than_the_entity_dictionaries_is_refused_by_name()
    {
        var xaml = Real("xrmv_ordercount").Replace("""
            <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
            """.Trim(), """
            <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" /><x:Property Name="xrmv_extra" Type="InArgument(x:String)" />
            """.Trim());

        var parse = () => XamlWorkflowParser.ParseRollup(xaml);
        parse.Should().Throw<XamlWorkflowException>().WithMessage("*'xrmv_extra'*");
    }
}
