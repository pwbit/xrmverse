using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// Which endpoint each Web API form of a custom API call reaches (plan 15, task 5, C8 and ruling X11), against
/// <see cref="FakeStore"/>: the explicit routes <c>{set}({id})/{operation}</c> and <c>{set}/{operation}</c> (GET and POST)
/// reach <see cref="CustomApiEndpoints"/> with the bound segment and the operation; the global forms (GET and POST,
/// with and without the namespace) reach the entity set routes (the integration tests show that they hand an unknown set
/// to <see cref="CustomApiEndpoints.InvokeAsync"/>). The routes of records, files, <c>WhoAmI</c> and the audit operations are
/// unchanged. The endpoints are not run: the pipeline stops after routing.
/// </summary>
public sealed class CustomApiRoutingTests
{
    private const string Root = "/api/data/v9.2/";
    private const string Ns = "Microsoft.Dynamics.CRM.";
    private static readonly Guid Id = Guid.Parse("611e7713-68d7-4622-b552-85060af450bc");

    private static readonly Lazy<(WebApplication App, RequestDelegate Pipeline)> Host = new(Build);

    private static EntityMetadata Account()
    {
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", EntitySetName = "accounts" };
        account.SetAttributes(SystemAttributes.Complete(account,
            [new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 }]));
        return account;
    }

    private static FakeStore Store() => new(Account());

    /// <summary>
    /// The Web API's routes on an application whose pipeline stops after routing (as <see cref="FileRoutingTests"/>): the
    /// handlers' services are registered, unresolvable, so that their parameters bind as services.
    /// </summary>
    private static (WebApplication, RequestDelegate) Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddSingleton<IMetadataStore>(Store());
        foreach (var type in new[]
                 {
                     typeof(OrganizationService), typeof(CallerResolver), typeof(MetadataOperations), typeof(ODataEdmModelBuilder),
                     typeof(ODataQueryParser), typeof(FileColumnService), typeof(SecurityGuard), typeof(IDbConnectionFactory),
                     typeof(PipelineExecutor), typeof(AsyncOperationQueue), typeof(CustomApiRegistry),
                 })
            builder.Services.AddScoped(type, _ => throw new InvalidOperationException("Routing tests never run an endpoint."));
        var app = builder.Build();
        app.UseRouting();
        app.Use((HttpContext _, RequestDelegate _) => Task.CompletedTask);
        app.MapGroup("/api/data/v9.2").MapWebApiEndpoints();
        return (app, ((IApplicationBuilder)app).Build());
    }

    /// <summary>The endpoint <paramref name="method"/> <paramref name="path"/> selects: its display name, pattern and route values.</summary>
    private static async Task<JsonObject> RouteAsync(HttpMethod method, string path)
    {
        var (app, pipeline) = Host.Value;
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method.Method;
        var query = path.IndexOf('?');
        context.Request.Path = Root + (query < 0 ? path : path[..query]);
        if (query >= 0) context.Request.QueryString = new QueryString(path[query..]);
        await pipeline(context);
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        endpoint?.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Should().Contain(method.Method);
        var json = new JsonObject { ["endpoint"] = endpoint?.DisplayName, ["pattern"] = endpoint?.RoutePattern.RawText };
        foreach (var (key, value) in context.Request.RouteValues) json[key] = value?.ToString();
        return json;
    }

    [Theory]
    [InlineData("POST", "accounts(611e7713-68d7-4622-b552-85060af450bc)", Ns + "xt_Bound")]
    [InlineData("GET", "accounts(611e7713-68d7-4622-b552-85060af450bc)", Ns + "xt_BoundGet(Name='a')")]
    [InlineData("GET", "accounts(611e7713-68d7-4622-b552-85060af450bc)", Ns + "xt_BoundGet()")]
    [InlineData("POST", "accounts", Ns + "xt_Collection")]
    [InlineData("GET", "accounts", Ns + "xt_CollectionGet(Name=@p1)")]
    [InlineData("GET", "accounts", Ns + "xt_CollectionGet")]
    public async Task Bound_and_collection_forms_reach_the_custom_api_route(string method, string segment, string operation)
    {
        var route = await RouteAsync(new HttpMethod(method), $"{segment}/{operation}");

        route["endpoint"]!.GetValue<string>().Should().Be(CustomApiEndpoints.EndpointName);
        route["segment"]!.GetValue<string>().Should().Be(segment);
        route["operation"]!.GetValue<string>().Should().Be(operation);
    }

    [Theory]
    [InlineData("POST", "xt_Echo")]
    [InlineData("POST", Ns + "xt_Echo")]
    [InlineData("GET", "xt_Get(Name=@p1)")]
    [InlineData("GET", Ns + "xt_Get(Name='a')")]
    public async Task Global_forms_reach_the_entity_set_route(string method, string segment)
    {
        var route = await RouteAsync(new HttpMethod(method), segment);

        route["pattern"]!.GetValue<string>().Should().EndWith("/{segment}");
        route["segment"]!.GetValue<string>().Should().Be(segment);
        CustomApiEndpoints.IsGlobalOperation(Store(), segment).Should().BeTrue();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Navigation_property_with_a_key_reaches_the_custom_api_route(string method)
    {
        // The parentheses make it an operation segment: InvokeAsync answers it with the navigation property's 400.
        var route = await RouteAsync(new HttpMethod(method), $"accounts({Id})/contact_customer_accounts({Id})");

        route["endpoint"]!.GetValue<string>().Should().Be(CustomApiEndpoints.EndpointName);
        route["operation"]!.GetValue<string>().Should().Be($"contact_customer_accounts({Id})");
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("accounts(611e7713-68d7-4622-b552-85060af450bc)")]
    public void Entity_set_segments_stay_with_the_entity_set_routes(string segment) =>
        CustomApiEndpoints.IsGlobalOperation(Store(), segment).Should().BeFalse();

    [Theory]
    [InlineData(Ns + "xt_Bound", true)]
    [InlineData("xt_Get()", true)]
    [InlineData("xt_Get(Name='a')", true)]
    [InlineData("name", false)]
    [InlineData("contact_customer_accounts", false)]
    public void Operation_segments_carry_the_namespace_or_parentheses(string segment, bool expected) =>
        CustomApiEndpoints.IsOperationSegment(segment).Should().Be(expected);

    [Fact]
    public async Task Other_routes_keep_their_endpoints()
    {
        (await RouteAsync(HttpMethod.Post, "accounts"))["pattern"]!.GetValue<string>().Should().EndWith("/{segment}");
        (await RouteAsync(HttpMethod.Get, "WhoAmI"))["pattern"]!.GetValue<string>().Should().EndWith("/WhoAmI");
        (await RouteAsync(HttpMethod.Get, $"accounts({Id})/{Ns}RetrieveAuditDetails"))["pattern"]!.GetValue<string>()
            .Should().EndWith("/{segment}/" + Ns + "RetrieveAuditDetails");
        (await RouteAsync(HttpMethod.Post, $"roles({Id})/{Ns}AddPrivilegesRole"))["pattern"]!.GetValue<string>()
            .Should().EndWith("/{segment}/" + Ns + "AddPrivilegesRole");
        (await RouteAsync(HttpMethod.Post, "ImportSolution"))["pattern"]!.GetValue<string>().Should().EndWith("/ImportSolution");
        (await RouteAsync(HttpMethod.Get, "RetrieveFormattedImportJobResults(ImportJobId=@p1)"))["pattern"]!.GetValue<string>()
            .Should().EndWith("/RetrieveFormattedImportJobResults({parameters})");
        // A file or image property is not an operation segment: the file route keeps it.
        var file = await RouteAsync(HttpMethod.Get, $"accounts({Id})/name");
        file["pattern"]!.GetValue<string>().Should().EndWith("/{segment}/{property}");
        (await RouteAsync(HttpMethod.Get, "EntityDefinitions(LogicalName='account')/Attributes(LogicalName='name')"))["pattern"]!
            .GetValue<string>().Should().Contain("EntityDefinitions");
    }
}
