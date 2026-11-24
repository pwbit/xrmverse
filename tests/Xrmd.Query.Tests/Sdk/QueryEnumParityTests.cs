using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Query.Sdk;
using Xunit;

namespace Xrmd.Query.Tests.Sdk;

public class QueryEnumParityTests
{
    private static void AssertParity<TSdk, TModel>() where TSdk : struct, Enum where TModel : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TSdk>().OrderBy(n => n, StringComparer.Ordinal), Enum.GetNames<TModel>().OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in Enum.GetNames<TSdk>())
            Assert.Equal(Convert.ToInt32(Enum.Parse<TSdk>(name)), Convert.ToInt32(Enum.Parse<TModel>(name)));
    }

    [Fact] public void FilterOperator_matches_ConditionOperator() => AssertParity<ConditionOperator, FilterOperator>();
    [Fact] public void FilterLogic_matches_LogicalOperator() => AssertParity<LogicalOperator, FilterLogic>();
    [Fact] public void JoinKind_matches_JoinOperator() => AssertParity<JoinOperator, JoinKind>();

    [Fact]
    public void Converters_round_trip_every_sdk_member()
    {
        foreach (var x in Enum.GetValues<ConditionOperator>()) Assert.Equal(x, x.ToModel().ToSdk());
        foreach (var x in Enum.GetValues<LogicalOperator>()) Assert.Equal(x, x.ToModel().ToSdk());
        foreach (var x in Enum.GetValues<JoinOperator>()) Assert.Equal(x, x.ToModel().ToSdk());
    }

    [Fact]
    public void Undefined_values_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ConditionOperator)99999).ToModel());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((FilterOperator)99999).ToSdk());
    }
}
