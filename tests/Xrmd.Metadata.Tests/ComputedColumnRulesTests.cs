using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Computed;
using Xrmd.Tests.Shared;

namespace Xrmd.Metadata.Tests;

/// <summary>The metadata rules of calculated columns (plan 2.12a, Task 1), with a fake translator.</summary>
public class ComputedColumnRulesTests
{
    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(attributes);
        return entity;
    }

    private static T Calculated<T>(T attribute) where T : AttributeMetadata
    {
        attribute.SourceType = 1;
        ComputedColumnRules.SetFormula(attribute, "<xaml/>");
        return attribute;
    }

    private static void Validate(EntityMetadata table, AttributeMetadata a, AttributeMetadata? current = null, IComputedColumns? computed = null,
        params EntityMetadata[] others) =>
        ComputedColumnRules.ValidateDefinition(table, a, current,
            name => name == table.LogicalName ? table : others.FirstOrDefault(o => o.LogicalName == name), computed ?? new FakeComputedColumns());

    [Fact]
    public void Double_calculated_column_is_refused()
    {
        var a = Calculated(new DoubleAttributeMetadata { LogicalName = "xrmv_c" });
        var act = () => Validate(Table("xrmv_t"), a);
        act.Should().Throw<XrmdValidationException>().WithMessage("A calculated column cannot be of type Double: 'xrmv_t.xrmv_c'.");
    }

    [Fact]
    public void Prompt_is_refused_and_formula_needs_its_definition()
    {
        var prompt = new IntegerAttributeMetadata { LogicalName = "xrmv_c", SourceType = ComputedColumnRules.Prompt };
        var refusePrompt = () => Validate(Table("xrmv_t"), prompt);
        refusePrompt.Should().Throw<XrmdValidationException>().WithMessage("Prompt columns are not supported: 'xrmv_t.xrmv_c'.");

        // Plan 2.12c: SourceType 3 is a formula column (FormulaColumnRules), checked by the translator: a missing formula is refused.
        var formula = new IntegerAttributeMetadata { LogicalName = "xrmv_c", SourceType = ComputedColumnRules.Formula };
        var refuseFormula = () => Validate(Table("xrmv_t"), formula, computed: new FakeComputedColumns()
            .Throwing("xrmv_t", "xrmv_c", "The formula column 'xrmv_t.xrmv_c' has no FormulaDefinition."));
        refuseFormula.Should().Throw<XrmdValidationException>().WithMessage("The formula column 'xrmv_t.xrmv_c' has no FormulaDefinition.");
    }

    [Fact]
    public void SourceType_cannot_change_on_update()
    {
        var current = new IntegerAttributeMetadata { LogicalName = "xrmv_c" };
        var table = Table("xrmv_t", current);
        var toCalculated = () => Validate(table, Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_c" }), current);
        toCalculated.Should().Throw<XrmdValidationException>().WithMessage("*SourceType*cannot change*'xrmv_t.xrmv_c'*");

        var calculated = Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_d" });
        var toSimple = () => Validate(table, new IntegerAttributeMetadata { LogicalName = "xrmv_d", SourceType = 0 }, calculated);
        toSimple.Should().Throw<XrmdValidationException>().WithMessage("*SourceType*cannot change*");

        // Leaving SourceType out, or repeating it, is not a change.
        var fake = new FakeComputedColumns().With("xrmv_t", "xrmv_d");
        Validate(table, new IntegerAttributeMetadata { LogicalName = "xrmv_d" }, calculated, fake);
        Validate(table, Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_d" }), calculated, fake);
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("decimal")]
    [InlineData("money")]
    public void MinMax_on_calculated_is_refused(string kind)
    {
        AttributeMetadata a = kind switch
        {
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_c", MinValue = 0 },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_c", MaxValue = 10 },
            _ => new MoneyAttributeMetadata { LogicalName = "xrmv_c", MaxValue = 10 },
        };
        Calculated(a);
        var act = () => Validate(Table("xrmv_t"), a);
        act.Should().Throw<XrmdValidationException>()
            .WithMessage("MinValue and MaxValue cannot be set on a calculated column: 'xrmv_t.xrmv_c'.");
    }

    /// <summary>
    /// The 2.12a parity export writes the widest range of the type on every calculated Whole number, Decimal and Currency column: that
    /// is no limit, and is accepted (a narrower one is still refused).
    /// </summary>
    [Theory]
    [InlineData("integer")]
    [InlineData("decimal")]
    [InlineData("money")]
    public void The_widest_range_of_the_type_is_not_a_limit(string kind)
    {
        AttributeMetadata a = kind switch
        {
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_c", MinValue = int.MinValue, MaxValue = int.MaxValue },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_c", MinValue = -100000000000m, MaxValue = 100000000000m },
            _ => new MoneyAttributeMetadata { LogicalName = "xrmv_c", MinValue = -922337203685477d, MaxValue = 922337203685477d },
        };
        Calculated(a);
        Validate(Table("xrmv_t"), a, computed: new FakeComputedColumns().With("xrmv_t", "xrmv_c"));
        a.SourceType.Should().Be(1);

        AttributeMetadata narrower = kind switch
        {
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_c", MinValue = int.MinValue, MaxValue = 1000 },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_c", MinValue = -100000000000m, MaxValue = 1000m },
            _ => new MoneyAttributeMetadata { LogicalName = "xrmv_c", MinValue = 0d, MaxValue = 922337203685477d },
        };
        Calculated(narrower);
        var act = () => Validate(Table("xrmv_t"), narrower, computed: new FakeComputedColumns().With("xrmv_t", "xrmv_c"));
        act.Should().Throw<XrmdValidationException>().WithMessage("MinValue and MaxValue cannot be set on a calculated column: 'xrmv_t.xrmv_c'.");
    }

    [Fact]
    public void Calculated_is_forced_read_only()
    {
        var a = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_c", IsValidForCreate = true, IsValidForUpdate = true });
        a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired);
        Validate(Table("xrmv_t"), a, computed: new FakeComputedColumns().With("xrmv_t", "xrmv_c"));
        a.IsValidForCreate.Should().BeFalse();
        a.IsValidForUpdate.Should().BeFalse();
        a.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
    }

    [Fact]
    public void Without_a_translator_calculated_is_refused()
    {
        var a = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_c" });
        var act = () => ComputedColumnRules.ValidateDefinition(Table("xrmv_t"), a, null, _ => null, computed: null);
        act.Should().Throw<XrmdValidationException>().WithMessage("Calculated columns are not available on this server.");
    }

    [Fact]
    public void Simple_column_is_not_touched()
    {
        var a = new StringAttributeMetadata { LogicalName = "xrmv_s", IsValidForCreate = true };
        ComputedColumnRules.ValidateDefinition(Table("xrmv_t"), a, null, _ => null, computed: null);
        a.IsValidForCreate.Should().BeTrue();
    }

    [Fact]
    public void Mask_is_zero_for_simple_columns_null_without_the_property_and_from_the_definition_for_calculated()
    {
        var simple = new IntegerAttributeMetadata { LogicalName = "xrmv_s" };
        var lookup = new LookupAttributeMetadata { LogicalName = "xrmv_l", Targets = ["xrmv_t"] };
        var calc = Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_c" });
        var table = Table("xrmv_t", simple, lookup, calc);
        var fake = new FakeComputedColumns().With("xrmv_t", "xrmv_c", new ComputedExpr.Column(null, "xrmv_s"));
        foreach (var a in new AttributeMetadata[] { simple, lookup, calc })
            ComputedColumnRules.ApplyMask(table, a, n => n == "xrmv_t" ? table : null, fake);
        ComputedColumnRules.MaskOf(simple).Should().Be(0);
        ComputedColumnRules.MaskOf(lookup).Should().BeNull();
        ComputedColumnRules.MaskOf(calc).Should().Be(1);

        // A definition that cannot be translated, or no translator: no mask.
        ComputedColumnRules.ApplyMask(table, calc, _ => null, new FakeComputedColumns().Throwing("xrmv_t", "xrmv_c", "broken"));
        ComputedColumnRules.MaskOf(calc).Should().BeNull();
        ComputedColumnRules.ApplyMask(table, calc, _ => null, null);
        ComputedColumnRules.MaskOf(calc).Should().BeNull();

        ComputedColumnRules.SetFormula(calc, "<x/>");
        ComputedColumnRules.ApplyMask(table, calc, n => table, fake);
        var stored = ComputedColumnRules.WithoutMask(calc);
        ComputedColumnRules.MaskOf(stored).Should().BeNull();
        ComputedColumnRules.FormulaOf(stored).Should().Be("<x/>");
        ComputedColumnRules.MaskOf(calc).Should().Be(1); // the original is untouched
    }

    [Fact]
    public void A_dependents_check_names_the_column_whose_definition_cannot_be_read()
    {
        var table = Table("xrmv_t", new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_ok" }), Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_bad" }));
        var fake = new FakeComputedColumns().With("xrmv_t", "xrmv_ok", new ComputedExpr.Column(null, "xrmv_n"))
            .Throwing("xrmv_t", "xrmv_bad", "unexpected element");
        var act = () => ComputedColumnRules.Dependents("xrmv_t", "xrmv_n", [table], fake);
        act.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.ErrorCode == Xrmd.Core.Errors.ErrorCodes.InvalidArgument)
            .WithMessage("The calculated column 'xrmv_t.xrmv_bad' has a definition that cannot be read: unexpected element.");
    }

    [Fact]
    public void A_dependents_check_skips_the_column_itself_and_the_columns_of_a_deleted_table()
    {
        // Final review I1: a column never reads itself, so its own unreadable definition does not block its delete or repair.
        var table = Table("xrmv_t", new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_bad" }));
        var other = Table("xrmv_o", new IntegerAttributeMetadata { LogicalName = "xrmv_on" });
        var fake = new FakeComputedColumns().Throwing("xrmv_t", "xrmv_bad", "unexpected element");

        ComputedColumnRules.Dependents("xrmv_t", "xrmv_bad", [table, other], fake).Should().BeEmpty();
        ComputedColumnRules.DependentsOfTable(table, [table, other], fake).Should().BeEmpty();

        // Another column's check still names it.
        var act = () => ComputedColumnRules.Dependents("xrmv_o", "xrmv_on", [table, other], fake);
        act.Should().Throw<Xrmd.Core.Errors.XrmdFaultException>().WithMessage("*'xrmv_t.xrmv_bad'*unexpected element*");
    }

    [Fact]
    public void Delete_refusal_names_formula_dependents_as_formula_columns()
    {
        // Plan 2.12c: the K4 refusal keeps its calculated text for calculated dependents and names a formula dependent as such.
        var formula = new IntegerAttributeMetadata { LogicalName = "xrmv_fx", SourceType = ComputedColumnRules.Formula };
        var table = Table("xrmv_t", new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_c" }), formula,
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_c2" }));
        ComputedColumnRules.DeleteRefusal([], [table]).Should().BeNull();
        ComputedColumnRules.DeleteRefusal(["xrmv_t.xrmv_c", "xrmv_t.xrmv_c2"], [table]).Should()
            .Be("The component cannot be deleted because it is referenced by calculated column 'xrmv_t.xrmv_c', 'xrmv_t.xrmv_c2'.");
        ComputedColumnRules.DeleteRefusal(["xrmv_t.xrmv_fx"], [table]).Should()
            .Be("The component cannot be deleted because it is referenced by formula column 'xrmv_t.xrmv_fx'.");
        ComputedColumnRules.DeleteRefusal(["xrmv_t.xrmv_c", "xrmv_t.xrmv_fx"], [table]).Should()
            .Be("The component cannot be deleted because it is referenced by calculated column 'xrmv_t.xrmv_c' and formula column 'xrmv_t.xrmv_fx'.");
    }

    [Fact]
    public void A_dependents_check_turns_only_definition_errors_into_400()
    {
        // An unconfigured column makes the fake throw InvalidOperationException: not a definition error, so it is not turned into a 400.
        var table = Table("xrmv_t", new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_c" }));
        var act = () => ComputedColumnRules.Dependents("xrmv_t", "xrmv_n", [table], new FakeComputedColumns());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Calculated_primary_name_is_refused()
    {
        // Final review I3: a lookup reads its target's name from the stored primary name column; a calculated one has none.
        var table = Table("xrmv_t");
        Set(table, nameof(EntityMetadata.PrimaryNameAttribute), "xrmv_name");
        var a = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 });
        var act = () => Validate(table, a, computed: new FakeComputedColumns().With("xrmv_t", "xrmv_name"));
        act.Should().Throw<XrmdValidationException>()
            .WithMessage("The primary name column of a table cannot be a calculated column: 'xrmv_t.xrmv_name'.");
    }

    [Fact]
    public void SourceTypeMask_bits()
    {
        var parentTotal = Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_ptotal" });
        var parent = Table("xrmv_p", new IntegerAttributeMetadata { LogicalName = "xrmv_pn" }, parentTotal);
        var child = Table("xrmv_c",
            new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_chain" }),
            new LookupAttributeMetadata { LogicalName = "xrmv_parentid", Targets = ["xrmv_p"] });
        EntityMetadata? Find(string n) => n == "xrmv_p" ? parent : n == "xrmv_c" ? child : null;

        int Mask(params ComputedExpr.Column[] sources) =>
            ComputedColumnRules.SourceTypeMask(child, new ComputedDefinition(new ComputedExpr.Literal(0), sources, 0), Find);

        Mask(Col(null, "xrmv_n")).Should().Be(1);
        Mask(Col("xrmv_parentid", "xrmv_pn")).Should().Be(2);
        Mask(Col(null, "xrmv_n"), Col(null, "xrmv_chain")).Should().Be(9);
        Mask(Col("xrmv_parentid", "xrmv_ptotal")).Should().Be(8);
        Mask(Col(null, "xrmv_n"), Col("xrmv_parentid", "xrmv_pn")).Should().Be(3);
    }

    private static ComputedExpr.Column Col(string? lookup, string attribute) => new(lookup, attribute);

    [Fact]
    public void Dependents_name_sources_and_lookups_and_validate_stores_the_mask()
    {
        var parent = Table("xrmv_p", new IntegerAttributeMetadata { LogicalName = "xrmv_pn" });
        var child = Table("xrmv_c",
            new IntegerAttributeMetadata { LogicalName = "xrmv_n" },
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_own" }),
            Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_up" }),
            new LookupAttributeMetadata { LogicalName = "xrmv_parentid", Targets = ["xrmv_p"] });
        var fake = new FakeComputedColumns()
            .With("xrmv_c", "xrmv_own", new ComputedExpr.Column(null, "xrmv_n"))
            .With("xrmv_c", "xrmv_up", new ComputedExpr.Column("xrmv_parentid", "xrmv_pn"));
        EntityMetadata[] all = [parent, child];

        ComputedColumnRules.Dependents("xrmv_c", "xrmv_n", all, fake).Should().Equal("xrmv_c.xrmv_own");
        ComputedColumnRules.Dependents("xrmv_p", "xrmv_pn", all, fake).Should().Equal("xrmv_c.xrmv_up");
        ComputedColumnRules.Dependents("xrmv_c", "xrmv_parentid", all, fake).Should().Equal("xrmv_c.xrmv_up");
        ComputedColumnRules.Dependents("xrmv_c", "xrmv_other", all, fake).Should().BeEmpty();
        ComputedColumnRules.DependentsOfTable(parent, all, fake).Should().Equal("xrmv_c.xrmv_up");
        ComputedColumnRules.DependentsOfTable(child, all, fake).Should().BeEmpty();

        var added = Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_new" });
        ComputedColumnRules.ValidateDefinition(child, added, null, n => all.FirstOrDefault(t => t.LogicalName == n),
            new FakeComputedColumns().With("xrmv_c", "xrmv_new", new ComputedExpr.Column("xrmv_parentid", "xrmv_pn")));
        ComputedColumnRules.MaskOf(added).Should().Be(2);
    }
}
