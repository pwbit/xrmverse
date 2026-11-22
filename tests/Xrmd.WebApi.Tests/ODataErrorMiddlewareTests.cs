using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.WebApi;

namespace Xrmd.WebApi.Tests;

public class ODataErrorMiddlewareTests
{
    private sealed class RecordingLogger : ILogger<ODataErrorMiddleware>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private static async Task<(int Status, JsonNode Error)> InvokeAsync(Exception ex, ILogger<ODataErrorMiddleware>? logger = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ODataErrorMiddleware(_ => throw ex, logger ?? NullLogger<ODataErrorMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, (await JsonNode.ParseAsync(context.Response.Body))!["error"]!);
    }

    [Fact]
    public async Task Fault_keeps_code_and_status()
    {
        var (status, error) = await InvokeAsync(XrmdFaultException.ObjectDoesNotExist("account", Guid.Empty));

        status.Should().Be(404);
        error["code"]!.GetValue<string>().Should().Be("0x80040217");
        error["message"]!.GetValue<string>().Should().Be($"account With Id = {Guid.Empty} Does Not Exist");
    }

    [Fact]
    public async Task Validation_exception_is_400_invalid_argument()
    {
        var (status, error) = await InvokeAsync(new XrmdValidationException("bad name"));

        status.Should().Be(400);
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should().Be("bad name");
    }

    [Fact]
    public async Task Other_argument_exceptions_are_500_without_details()
    {
        var (status, error) = await InvokeAsync(new ArgumentNullException("secretParameter"));

        status.Should().Be(500);
        error["code"]!.GetValue<string>().Should().Be("0x80040216");
        error["message"]!.GetValue<string>().Should().NotContain("secretParameter");
    }

    [Fact]
    public async Task Not_supported_exception_is_500()
    {
        // Pinned for plan 2.11 (review M5): the Web API routes that refuse a message throw a 400 fault themselves.
        var (status, error) = await InvokeAsync(new NotSupportedException("Request 'Book' is not supported yet."));

        status.Should().Be(500);
        error["code"]!.GetValue<string>().Should().Be("0x80040216");
    }

    [Fact]
    public async Task Other_exceptions_are_500_without_details()
    {
        var (status, error) = await InvokeAsync(new InvalidOperationException("secret connection string"));

        status.Should().Be(500);
        error["code"]!.GetValue<string>().Should().Be("0x80040216");
        error["message"]!.GetValue<string>().Should().NotContain("secret");
    }

    [Fact]
    public async Task Bad_http_request_keeps_its_status()
    {
        var (status, error) = await InvokeAsync(new BadHttpRequestException("Request body too large.", 413));

        status.Should().Be(413);
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
    }

    [Fact]
    public async Task Unexpected_error_is_logged_but_not_implemented_is_not()
    {
        var logger = new RecordingLogger();
        await InvokeAsync(new InvalidOperationException("boom"), logger);
        logger.Levels.Should().Equal(LogLevel.Error);

        logger.Levels.Clear();
        var (status, _) = await InvokeAsync(new XrmdFaultException(ErrorCodes.Unexpected, "not supported yet", 501), logger);
        status.Should().Be(501);
        logger.Levels.Should().NotContain(LogLevel.Error);
    }
}
