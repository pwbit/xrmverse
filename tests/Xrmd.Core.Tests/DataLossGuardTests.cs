using FluentAssertions;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Core.Solutions;

namespace Xrmd.Core.Tests;

/// <summary>The counts of the data-loss guard (plan 2.6, ruling M10; final review I3).</summary>
public sealed class DataLossGuardTests
{
    private static readonly ISqlDialect D = SqlServerDialect.Instance;

    [Fact]
    public void Count_in_a_transaction_holds_a_shared_table_lock()
    {
        DataLossGuard.CountSql(D, "xm_item", null, locked: true)
            .Should().Be("SELECT COUNT_BIG(*) FROM [data].[xm_item] WITH (TABLOCK, HOLDLOCK)");
        DataLossGuard.CountSql(D, "xm_item", ["xm_legacy", "xm_price_base"], locked: true)
            .Should().Be("SELECT COUNT_BIG(*) FROM [data].[xm_item] WITH (TABLOCK, HOLDLOCK) WHERE [xm_legacy] IS NOT NULL OR [xm_price_base] IS NOT NULL");
    }

    [Fact]
    public void Count_outside_a_transaction_takes_no_lock_hint()
    {
        DataLossGuard.CountSql(D, "xm_item", ["xm_legacy"], locked: false)
            .Should().Be("SELECT COUNT_BIG(*) FROM [data].[xm_item] WHERE [xm_legacy] IS NOT NULL");
    }

    [Fact]
    public void Identifiers_are_quoted()
    {
        DataLossGuard.CountSql(D, "a]b", ["c]d"], locked: false)
            .Should().Be("SELECT COUNT_BIG(*) FROM [data].[a]]b] WHERE [c]]d] IS NOT NULL");
    }
}
