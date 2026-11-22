using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// <see cref="ComputedOrder"/> (plan 2.12b, R14): a name ending in <c>_date</c>, <c>_state</c> or <c>_base</c> counts as its main column
/// only when it is that column's companion, never when it is a simple column of its own.
/// </summary>
public class ComputedOrderTests
{
    private const string Table = "xv_t";

    /// <summary>Each calculated column reads the columns <see cref="Reads"/> gives for it.</summary>
    private sealed class FakeComputed(Dictionary<string, string[]> reads) : IComputedColumns
    {
        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            new(new ComputedExpr.Literal(null), [.. reads[attribute.LogicalName].Select(c => new ComputedExpr.Column(null, c))], 1);
    }

    private static AttributeMetadata Calculated(string name) =>
        new DecimalAttributeMetadata { LogicalName = name, SourceType = ComputedColumnRules.Calculated };

    private static IReadOnlyList<string> Order(EntityMetadata table, Dictionary<string, string[]> reads, params string[] columns) =>
        ComputedOrder.Order([.. columns.Select(c => (Table, c))], n => n == Table ? table : null, _ => null, new FakeComputed(reads), null,
            readersLast: true).Select(c => c.Attribute).ToList();

    [Fact]
    public void A_simple_column_named_like_a_companion_is_not_its_main_column()
    {
        // xv_end reads the simple column xv_start_date, not the calculated xv_start: no cycle, xv_end goes first.
        var table = new EntityMetadata { LogicalName = Table };
        table.SetAttributes([Calculated("xv_start"), new DateTimeAttributeMetadata { LogicalName = "xv_start_date" }, Calculated("xv_end")]);
        var reads = new Dictionary<string, string[]> { ["xv_start"] = ["xv_end"], ["xv_end"] = ["xv_start_date"] };

        Order(table, reads, "xv_start", "xv_end").Should().Equal("xv_end", "xv_start");
    }

    [Fact]
    public void A_companion_counts_as_its_rollup_even_before_the_server_generates_it()
    {
        // The import's view has the rollup but not yet its companions: xv_reader reads xv_roll_state, so it waits for xv_roll.
        var table = new EntityMetadata { LogicalName = Table };
        table.SetAttributes([Calculated("xv_reader"), new DecimalAttributeMetadata { LogicalName = "xv_roll", SourceType = RollupColumns.Rollup }]);
        var reads = new Dictionary<string, string[]> { ["xv_reader"] = ["xv_roll_state"] };

        Order(table, reads, "xv_reader", "xv_roll").Should().Equal("xv_roll", "xv_reader");
    }

    [Fact]
    public void The_base_of_a_computed_money_column_counts_as_that_column()
    {
        var totalBase = new MoneyAttributeMetadata { LogicalName = "xv_total_base" };
        totalBase.GetType().GetProperty(nameof(MoneyAttributeMetadata.IsBaseCurrency))!.GetSetMethod(nonPublic: true)!.Invoke(totalBase, [true]);
        var table = new EntityMetadata { LogicalName = Table };
        table.SetAttributes([
            Calculated("xv_reader"), new MoneyAttributeMetadata { LogicalName = "xv_total", SourceType = ComputedColumnRules.Calculated }, totalBase,
        ]);
        var reads = new Dictionary<string, string[]> { ["xv_reader"] = ["xv_total_base"], ["xv_total"] = [] };

        Order(table, reads, "xv_reader", "xv_total").Should().Equal("xv_total", "xv_reader");
    }
}
