using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Security.Tests;

/// <summary>Plan 3.5 UI-1a, task 1: the rules that need no database for the app module tables.</summary>
public sealed class AppModuleSecurityTests
{
    private static readonly Guid SystemUserId = Guid.NewGuid();

    private static Task EnforceAsync(OrganizationRequest request, Caller caller)
    {
        using var conn = new SqlConnection();
        return SystemTableRules.EnforceAsync(request, caller, SystemUserId, new SecurityCache(new SystemTableRulesTests.NoDatabase()), new SystemTableRulesTests.NoMetadata(),
            new DataSession(conn, null), default);
    }

    [Theory]
    [InlineData("appmodulecomponent")]
    [InlineData("appmoduleroles")]
    public async Task App_module_tables_are_import_only(string table)
    {
        var caller = new Caller(SystemUserId, Guid.Empty);
        var row = new Entity(table, Guid.NewGuid());
        foreach (OrganizationRequest request in new OrganizationRequest[]
                 {
                     new CreateRequest { Target = row }, new UpdateRequest { Target = row },
                     new DeleteRequest { Target = row.ToEntityReference() },
                 })
        {
            var act = () => EnforceAsync(request, caller);
            (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Be(AppModuleSchema.ImportOnly);
        }
    }

    [Fact]
    public void Import_only_text_is_the_plans()
    {
        AppModuleSchema.ImportOnly.Should().Be("App modules are created by solution import in this version.");
    }
}
