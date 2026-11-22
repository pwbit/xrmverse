using System.Reflection;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Tds.Execution;

namespace Xrmd.Tds.Tests;

/// <summary>
/// The mapping of SqlClient's command timeout (error -2): each page the executor reads is one SqlCommand with the default
/// CommandTimeout of 30 s, and a slow page must reach the client as the time-limit error (40000), not the generic 50000.
/// </summary>
public sealed class TdsQueryHandlerTests
{
    [Fact]
    public void A_command_timeout_is_recognized()
    {
        TdsQueryHandler.IsCommandTimeout(CreateSqlException(-2)).Should().BeTrue();
    }

    [Fact]
    public void A_wrapped_command_timeout_is_recognized()
    {
        TdsQueryHandler.IsCommandTimeout(new InvalidOperationException("wrapped", CreateSqlException(-2))).Should().BeTrue();
    }

    [Fact]
    public void Other_errors_are_not_a_command_timeout()
    {
        TdsQueryHandler.IsCommandTimeout(CreateSqlException(1205)).Should().BeFalse();
        TdsQueryHandler.IsCommandTimeout(new InvalidOperationException("other")).Should().BeFalse();
        TdsQueryHandler.IsCommandTimeout(new TimeoutException()).Should().BeFalse();
    }

    [Fact]
    public void The_timeout_message_is_the_batch_limit_message()
    {
        TdsErrors.TimeoutMessage(new TdsOptions().Timeout).Should().Be("The query exceeded the 5 minute limit.");
    }

    /// <summary>SqlException has no public constructor: it is built through SqlClient's internal factory, as SqlClient does.</summary>
    private static SqlException CreateSqlException(int number)
    {
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
        var errorCtor = typeof(SqlError).GetConstructors(Internal)
            .Where(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(int))
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters().Select((p, i) => i == 0 ? number : Default(p.ParameterType)).ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructor(Internal, Type.EmptyTypes)!.Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", Internal, [typeof(SqlError)])!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [errors, "16.00.0000"])!;
    }

    private static object? Default(Type type) => type == typeof(string) ? "test" : type.IsValueType ? Activator.CreateInstance(type) : null;
}
