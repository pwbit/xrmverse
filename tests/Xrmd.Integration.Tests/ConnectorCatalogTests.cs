using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.WebApi.Connector;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>X.12a, Task 1: the connector's table catalog.</summary>
[Collection("real-export")]
public sealed class ConnectorCatalogTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly List<Guid> _seededApis = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes the custom APIs a test seeded: the collection's RealExportImportTests count them.</summary>
    public async Task DisposeAsync()
    {
        var admin = ServiceFor(fx, fx.AdminObjectId);
        foreach (var id in _seededApis) await admin.DeleteAsync(Xrmd.Metadata.CustomApiTables.Api, id);
        _seededApis.Clear();
        fx.App.Services.GetRequiredService<CustomApiRegistry>().Invalidate();
    }

    private async Task<T> WithCatalogAsync<T>(Func<ConnectorCatalog, Task<T>> action)
    {
        using var scope = fx.App.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ConnectorCatalog>());
    }

    private async Task<Caller> ContactOnlyUserAsync()
    {
        var bu = await RootBusinessUnitAsync(fx);
        var userId = await CreateUserAsync(fx, bu);
        var role = await CreateRoleAsync(fx, "connector-" + Guid.NewGuid().ToString("N")[..8], ("prvReadcontact", DepthMask.Global));
        await AssignRoleAsync(fx, userId, role);
        return new Caller(userId, bu);
    }

    private Task<Caller> AdminAsync() => TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);

    [Fact]
    public async Task ListTables_admin_sees_account_with_entity_set_name()
    {
        var admin = await AdminAsync();
        var tables = await WithCatalogAsync(c => c.ListTablesAsync(admin, default));
        tables.Should().Contain(t => t.LogicalName == "account" && t.EntitySetName == "accounts");
        tables.Select(t => t.DisplayName).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListTables_user_without_read_on_account_does_not_see_it()
    {
        var user = await ContactOnlyUserAsync();
        var tables = await WithCatalogAsync(c => c.ListTablesAsync(user, default));
        tables.Should().Contain(t => t.LogicalName == "contact");
        tables.Should().NotContain(t => t.LogicalName == "account");
    }

    [Fact]
    public async Task ListTables_excludes_intersect_tables()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var intersects = store.GetAllTables().Where(t => t.IsIntersect == true).Select(t => t.LogicalName).ToList();
        intersects.Should().NotBeEmpty();
        var admin = await AdminAsync();
        var tables = await WithCatalogAsync(c => c.ListTablesAsync(admin, default));
        tables.Select(t => t.LogicalName).Should().NotIntersectWith(intersects);
    }

    [Fact]
    public async Task GetReadableTable_unknown_table_is_404()
    {
        var admin = await AdminAsync();
        var act = () => WithCatalogAsync(c => c.GetReadableTableAsync(admin, "no_such_table", default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e => e.HttpStatus == 404 && e.ErrorCode == ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task GetReadableTable_entity_set_name_is_404()
    {
        var admin = await AdminAsync();
        var act = () => WithCatalogAsync(c => c.GetReadableTableAsync(admin, "accounts", default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e => e.HttpStatus == 404 && e.ErrorCode == ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task GetReadableTable_without_read_is_403()
    {
        var user = await ContactOnlyUserAsync();
        var act = () => WithCatalogAsync(c => c.GetReadableTableAsync(user, "account", default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e => e.HttpStatus == 403 && e.ErrorCode == ErrorCodes.PrivilegeDenied);
    }

    // ---- custom APIs (Task 3) ----------------------------------------------------------------------------------------

    private async Task SeedApisAsync()
    {
        _seededApis.AddRange(await CustomApiTestSolution.CreateAsync(ServiceFor(fx, fx.AdminObjectId),
            new TestApi("xt_cc_Plain", Requests: [("Text", 10, false, null)], Responses: [("Out", 10)]),
            new TestApi("xt_cc_Bound", BindingType: 1, BoundEntity: "account"),
            new TestApi("xt_cc_Function", IsFunction: true),
            new TestApi("xt_cc_Private", IsPrivate: true),
            new TestApi("xt_cc_Guarded", PrivilegeName: "prvWriteRole")));
        fx.App.Services.GetRequiredService<CustomApiRegistry>().Invalidate();
    }

    private async Task<Caller> BasicUserAsync()
    {
        var bu = await RootBusinessUnitAsync(fx);
        var userId = await CreateUserAsync(fx, bu);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return new Caller(userId, bu);
    }

    [Fact]
    public async Task ListCustomApis_hides_bound_function_private_and_unprivileged()
    {
        await SeedApisAsync();
        var user = await BasicUserAsync();
        var forUser = await WithCatalogAsync(c => c.ListCustomApisAsync(user, default));
        forUser.Select(a => a.UniqueName).Where(n => n.StartsWith("xt_cc_")).Should().Equal("xt_cc_Plain");
        var admin = await AdminAsync();
        var forAdmin = await WithCatalogAsync(c => c.ListCustomApisAsync(admin, default));
        forAdmin.Select(a => a.UniqueName).Where(n => n.StartsWith("xt_cc_")).Should().Equal("xt_cc_Guarded", "xt_cc_Plain");
        forAdmin.Select(a => a.UniqueName).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("xt_cc_Function", "a function")]
    [InlineData("xt_cc_Bound", "bound")]
    [InlineData("xt_cc_Private", "private")]
    public async Task GetRunnableCustomApi_function_bound_or_private_is_400(string name, string why)
    {
        await SeedApisAsync();
        var admin = await AdminAsync();
        var act = () => WithCatalogAsync(c => c.GetRunnableCustomApiAsync(admin, name, default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e =>
            e.HttpStatus == 400 && e.ErrorCode == ErrorCodes.InvalidArgument
            && e.Message == $"The custom API '{name}' cannot be run by the connector: it is {why}.");
    }

    [Fact]
    public async Task GetRunnableCustomApi_unknown_is_404()
    {
        var admin = await AdminAsync();
        var act = () => WithCatalogAsync(c => c.GetRunnableCustomApiAsync(admin, "xt_cc_Nope", default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e => e.HttpStatus == 404 && e.ErrorCode == ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task GetRunnableCustomApi_without_execute_privilege_is_403()
    {
        await SeedApisAsync();
        var user = await BasicUserAsync();
        var act = () => WithCatalogAsync(c => c.GetRunnableCustomApiAsync(user, "xt_cc_Guarded", default));
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Should().Match<XrmdFaultException>(e => e.HttpStatus == 403 && e.ErrorCode == ErrorCodes.PrivilegeDenied);
        var admin = await AdminAsync();
        (await WithCatalogAsync(c => c.GetRunnableCustomApiAsync(admin, "xt_cc_Plain", default))).UniqueName.Should().Be("xt_cc_Plain");
    }
}
