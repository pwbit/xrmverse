using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The import-only columns (the opaque content a solution import keeps and the export writes again): no request sets
/// them, through the SDK or the Web API, on Create or Update (400 naming the column). Every request runs as the System
/// Administrator: the rule holds for every caller.
/// </summary>
[Collection("xrmd")]
public sealed class ImportOnlyColumnTests(XrmdFixture fx)
{
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public static TheoryData<string, string> Columns => new()
    {
        { "webresource", "xrmverse_componentxml" },
        { "savedquery", "xrmverse_componentxml" },
        { "savedqueryvisualization", "xrmverse_componentxml" },
        { "sdkmessageprocessingstep", "xrmverse_componentxml" },
        { "systemform", "xrmverse_componentxml" },
        { "sitemap", "xrmverse_componentxml" },
        { "solution", "xrmverse_unsupportedxml" },
        { "appmodule", "descriptor" },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task Create_setting_an_import_only_column_is_400_naming_it(string table, string column)
    {
        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity(table) { [column] = "<forged />" }));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain(column).And.Contain(table);
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public async Task Update_setting_an_import_only_column_is_400_naming_it(string table, string column)
    {
        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity(table, Guid.NewGuid()) { [column] = null }));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain(column).And.Contain(table);
    }

    /// <summary>
    /// The kept XML of a custom control (plan 3.5 UI-1d) is import-only too; no request writes the table at all (ruling R1), so the
    /// refusal is the table's.
    /// </summary>
    [Fact]
    public async Task Custom_control_import_only_column_is_refused()
    {
        var create = await FaultAsync(() => Admin.CreateAsync(new Entity(CustomControlSchema.Table)
        {
            [CustomControlSchema.Name] = "xv_Xv.Forged",
            ["xrmverse_componentxml"] = "<forged />",
        }));
        create.HttpStatus.Should().Be(400, create.ToString());
        create.Message.Should().Be(CustomControlSchema.ImportOnly);
        var update = await FaultAsync(() => Admin.UpdateAsync(new Entity(CustomControlSchema.Table, Guid.NewGuid()) { ["xrmverse_componentxml"] = null }));
        update.HttpStatus.Should().Be(400, update.ToString());
    }

    [Theory]
    [InlineData("solutions", "xrmverse_unsupportedxml")]
    [InlineData("appmodules", "descriptor")]
    [InlineData("savedqueries", "xrmverse_componentxml")]
    public async Task Web_API_PATCH_of_an_import_only_column_is_400_naming_it(string set, string column)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/data/v9.2/{set}({Guid.NewGuid()})")
        {
            Content = new StringContent($$"""{ "{{column}}": "<forged />" }""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(column);
    }
}
