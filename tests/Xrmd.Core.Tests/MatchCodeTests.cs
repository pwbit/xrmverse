using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Errors;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Tests;

/// <summary>Plan 2.15, ruling D8: the normalization of condition values and the match code of a row.</summary>
public class MatchCodeTests
{
    private static readonly Dictionary<string, AttributeMetadata> Columns = new()
    {
        ["name"] = new StringAttributeMetadata { LogicalName = "name" },
        ["other"] = new StringAttributeMetadata { LogicalName = "other" },
        ["city"] = new StringAttributeMetadata { LogicalName = "city" },
        ["born"] = new DateTimeAttributeMetadata { LogicalName = "born" },
        ["amount"] = new DecimalAttributeMetadata { LogicalName = "amount" },
        ["whole"] = new DecimalAttributeMetadata { LogicalName = "whole", Precision = 0 },
        ["price"] = new MoneyAttributeMetadata { LogicalName = "price" },
        ["customer"] = new LookupAttributeMetadata(LookupFormat.None) { LogicalName = "customer", Targets = ["account", "contact"] },
    };

    private static DuplicateRuleCondition Condition(string attribute, DuplicateOperator op = DuplicateOperator.ExactMatch, int? param = null,
        bool ignoreBlank = false, string? matching = null) =>
        new(Guid.NewGuid(), attribute, matching ?? attribute, op, param, ignoreBlank);

    private static PublishedRule Rule(bool caseSensitive, params DuplicateRuleCondition[] conditions) =>
        new(Guid.NewGuid(), "contact", "contact", caseSensitive, false, RuleStatus.Published, conditions);

    private static byte[]? Code(PublishedRule rule, Dictionary<string, object?> values, MatchSide side = MatchSide.Matching) =>
        MatchCode.Compute(rule, side, name => values.GetValueOrDefault(name), name => Columns[name]);

    private static string? Normalize(object? value, string attribute, DuplicateRuleCondition? condition = null, bool caseSensitive = false) =>
        MatchCode.Normalize(value, Columns[attribute], condition ?? Condition(attribute), caseSensitive);

    [Fact]
    public void Exact_match_folds_case_unless_case_sensitive()
    {
        Normalize("Ada@Example.com", "name").Should().Be("ADA@EXAMPLE.COM");
        Normalize("Ada@Example.com", "name", caseSensitive: true).Should().Be("Ada@Example.com");

        var folded = Rule(false, Condition("name"));
        Code(folded, new() { ["name"] = "ada@example.com" }).Should().Equal(Code(folded, new() { ["name"] = "ADA@example.COM" }));
        var sensitive = Rule(true, Condition("name"));
        Code(sensitive, new() { ["name"] = "ada@example.com" }).Should().NotEqual(Code(sensitive, new() { ["name"] = "ADA@example.COM" }));
    }

    [Fact]
    public void Trims_before_comparing()
    {
        Normalize("  Ada  ", "name").Should().Be("ADA");
        Normalize("   ", "name").Should().BeNull();
        Normalize("", "name").Should().BeNull();
        var rule = Rule(false, Condition("name"));
        Code(rule, new() { ["name"] = " ada " }).Should().Equal(Code(rule, new() { ["name"] = "ada" }));
    }

    [Fact]
    public void First_and_last_n_characters()
    {
        Normalize("Lovelace", "name", Condition("name", DuplicateOperator.SameFirstCharacters, 4)).Should().Be("LOVE");
        Normalize("Lovelace", "name", Condition("name", DuplicateOperator.SameLastCharacters, 4)).Should().Be("LACE");
        // A value shorter than N is kept whole.
        Normalize("Ada", "name", Condition("name", DuplicateOperator.SameFirstCharacters, 5)).Should().Be("ADA");
        Normalize("Ada", "name", Condition("name", DuplicateOperator.SameLastCharacters, 5)).Should().Be("ADA");
        // Trimmed first, then cut.
        Normalize("  Lovelace ", "name", Condition("name", DuplicateOperator.SameLastCharacters, 3)).Should().Be("ACE");

        var rule = Rule(false, Condition("name", DuplicateOperator.SameFirstCharacters, 4));
        Code(rule, new() { ["name"] = "Lovelace" }).Should().Equal(Code(rule, new() { ["name"] = "love story" }));
    }

    [Fact]
    public void Single_condition_rule_ignores_blank_even_when_false()
    {
        var rule = Rule(false, Condition("name", ignoreBlank: false));
        Code(rule, new() { ["name"] = null }).Should().BeNull();
        Code(rule, new() { ["name"] = "  " }).Should().BeNull();
        Code(rule, new()).Should().BeNull();
    }

    [Fact]
    public void Two_conditions_blank_with_ignore_false_still_codes()
    {
        var rule = Rule(false, Condition("name"), Condition("city", ignoreBlank: false));
        var withBlank = Code(rule, new() { ["name"] = "Ada", ["city"] = null });
        withBlank.Should().NotBeNull().And.HaveCount(32);
        Code(rule, new() { ["name"] = "Ada", ["city"] = " " }).Should().Equal(withBlank);
        Code(rule, new() { ["name"] = "Ada", ["city"] = "London" }).Should().NotEqual(withBlank);
    }

    [Fact]
    public void Two_conditions_blank_with_ignore_true_has_no_code()
    {
        var rule = Rule(false, Condition("name"), Condition("city", ignoreBlank: true));
        Code(rule, new() { ["name"] = "Ada", ["city"] = null }).Should().BeNull();
        Code(rule, new() { ["name"] = "Ada", ["city"] = "London" }).Should().NotBeNull();
    }

    [Fact]
    public void All_blank_has_no_code()
    {
        var rule = Rule(false, Condition("name"), Condition("city"));
        Code(rule, new() { ["name"] = null, ["city"] = "" }).Should().BeNull();
    }

    [Fact]
    public void Same_date_uses_utc_date()
    {
        var sameDate = Condition("born", DuplicateOperator.SameDate);
        Normalize(new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Utc), "born", sameDate).Should().Be("2026-11-11");
        // An unspecified kind is UTC (the stored form); a local time is converted first.
        Normalize(new DateTime(2026, 11, 11, 0, 15, 0, DateTimeKind.Unspecified), "born", sameDate).Should().Be("2026-11-11");
        var local = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Normalize(local, "born", sameDate).Should().Be("2026-11-11");
        Normalize(new DateTime(2026, 11, 11, 23, 30, 5, 250, DateTimeKind.Utc), "born", Condition("born", DuplicateOperator.SameDateAndTime))
            .Should().Be("2026-11-11T23:30:05");

        var rule = Rule(false, sameDate);
        Code(rule, new() { ["born"] = new DateTime(2026, 11, 11, 1, 0, 0, DateTimeKind.Utc) })
            .Should().Equal(Code(rule, new() { ["born"] = new DateTime(2026, 11, 11, 22, 0, 0, DateTimeKind.Utc) }));
    }

    [Fact]
    public void Lookup_codes_include_table()
    {
        var id = Guid.NewGuid();
        Normalize(new EntityReference("account", id), "customer").Should().Be("account:" + id.ToString("N"));
        var rule = Rule(false, Condition("customer"));
        Code(rule, new() { ["customer"] = new EntityReference("account", id) })
            .Should().NotEqual(Code(rule, new() { ["customer"] = new EntityReference("contact", id) }));
        Code(rule, new() { ["customer"] = new EntityReference("account", id) })
            .Should().Equal(Code(rule, new() { ["customer"] = new EntityReference("account", id) { Name = "Contoso" } }));
    }

    [Fact]
    public void Bare_guid_lookup_needs_a_single_target()
    {
        var id = Guid.NewGuid();
        var single = new LookupAttributeMetadata(LookupFormat.None) { LogicalName = "parent", Targets = ["account"] };
        var condition = Condition("parent");
        // A bare id of a single-target lookup is the same value as the reference a stored row gives.
        MatchCode.Normalize(id, single, condition, false).Should().Be(MatchCode.Normalize(new EntityReference("account", id), single, condition, false))
            .And.Be("account:" + id.ToString("N"));
        // A multi-target lookup cannot tell which table a bare id is of: refused, never coded without its table.
        var act = () => Normalize(id, "customer");
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Decimal_trailing_zeros_do_not_matter()
    {
        Normalize(1.5m, "amount").Should().Be("1.5");
        Normalize(1.5000000000m, "amount").Should().Be("1.5");
        Normalize(100m, "amount").Should().Be("100");
        Normalize(100.00m, "amount").Should().Be("100");
        Normalize(new Money(2.10m), "amount").Should().Be("2.1");
        var rule = Rule(false, Condition("amount"));
        Code(rule, new() { ["amount"] = 1.50m }).Should().Equal(Code(rule, new() { ["amount"] = 1.5000m }));
    }

    [Fact]
    public void Decimal_is_rounded_to_the_column_precision_as_sql_server_stores_it()
    {
        // decimal(23,2) by default: SQL Server rounds half away from zero when it converts to the column's scale.
        Normalize(1.234m, "amount").Should().Be("1.23");
        Normalize(1.235m, "amount").Should().Be("1.24");
        Normalize(-1.235m, "amount").Should().Be("-1.24");
        Normalize(2.5m, "whole").Should().Be("3");
        Normalize(-2.5m, "whole").Should().Be("-3");
        var rule = Rule(false, Condition("amount"));
        Code(rule, new() { ["amount"] = 1.234m }).Should().Equal(Code(rule, new() { ["amount"] = 1.23m }));
    }

    [Fact]
    public void Money_is_rounded_to_scale_4()
    {
        Normalize(new Money(1.23456m), "price").Should().Be("1.2346");
        Normalize(new Money(1.00005m), "price").Should().Be("1.0001");
        Normalize(new Money(-1.00005m), "price").Should().Be("-1.0001");
        Normalize(new Money(1.00004m), "price").Should().Be("1");
    }

    [Fact]
    public void Base_and_matching_sides_use_their_own_attributes()
    {
        var rule = Rule(false, Condition("name", matching: "other"));
        var fromBase = Code(rule, new() { ["name"] = "Ada", ["other"] = "Grace" }, MatchSide.Base);
        var fromMatching = Code(rule, new() { ["name"] = "Grace", ["other"] = "Ada" }, MatchSide.Matching);
        fromBase.Should().NotBeNull().And.Equal(fromMatching);
        Code(rule, new() { ["name"] = "Ada", ["other"] = "Grace" }, MatchSide.Matching).Should().NotEqual(fromBase);
    }
}
