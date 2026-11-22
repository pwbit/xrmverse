using System.Data;
using FluentAssertions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Core.Tests.Storage;

/// <summary>
/// Plan 2.7a, Task 2 (Task 1 review M1): the helpers that take (name, value) pairs type each parameter explicitly, with the type
/// SqlClient inferred for the value before.
/// </summary>
public sealed class DbValueTypesTests
{
    public static TheoryData<object?, DbType> Values => new()
    {
        { Guid.Empty, DbType.Guid },
        { "x", DbType.String },
        { 1, DbType.Int32 },
        { 1L, DbType.Int64 },
        { true, DbType.Boolean },
        { new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DbType.DateTime },
        { 1.5m, DbType.Decimal },
        { 1.5d, DbType.Double },
        { new byte[] { 1 }, DbType.Binary },
        { DayOfWeek.Monday, DbType.Int32 },
        { DBNull.Value, DbType.String },
        { null, DbType.String },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Each_value_has_the_type_SqlClient_inferred(object? value, DbType expected) => DbValueTypes.Of(value).Should().Be(expected);

    [Fact]
    public void An_unknown_type_is_refused() =>
        FluentActions.Invoking(() => DbValueTypes.Of(new object())).Should().Throw<ArgumentException>();
}
