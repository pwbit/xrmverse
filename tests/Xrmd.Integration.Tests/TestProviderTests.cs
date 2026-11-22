using FluentAssertions;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 2.7a, Task 5: the provider of an integration test run comes from <c>XRMD_TEST_PROVIDER</c>.</summary>
public sealed class TestProviderTests
{
    [Theory]
    [InlineData(null, StorageProvider.SqlServer)]
    [InlineData("sqlserver", StorageProvider.SqlServer)]
    [InlineData("postgres", StorageProvider.PostgreSql)]
    [InlineData(" Postgres ", StorageProvider.PostgreSql)]
    public void The_provider_comes_from_the_environment_value(string? value, StorageProvider expected) =>
        TestProvider.Parse(value).Should().Be(expected);
}
