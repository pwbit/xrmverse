using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestEntities;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan "personal date format", ruling F4: the formatted value of a date and time column is written in the caller's
/// <c>usersettings</c> formats and, for a User Local column, in the caller's time zone; the admin keeps the defaults.
/// </summary>
[Collection("xrmd")]
public sealed class UserDateFormatTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _tag = "UDF" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _project;

    public async Task InitializeAsync()
    {
        _admin = ServiceFor(fx, fx.AdminObjectId);
        await EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _project = await _admin.CreateAsync(new Entity(Project)
        {
            ["pb_name"] = _tag, ["pb_startdate"] = new DateTime(2026, 11, 20, 23, 30, 0, DateTimeKind.Utc),
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private QueryExpression Query()
    {
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_name", "pb_startdate") };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Equal, _tag);
        return q;
    }

    [Fact]
    public async Task Dates_are_formatted_in_the_callers_format_and_time_zone()
    {
        var objectId = Guid.NewGuid();
        var user = await _admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Dora Data " + _tag[..8], ["domainname"] = $"dora.{_tag}@example.invalid", ["azureactivedirectoryobjectid"] = objectId,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await GrantAsync(fx, user, team: false, ("prvReadpb_Project", DepthMask.Global));
        await _admin.UpdateAsync(new Entity("usersettings", user)
        {
            ["timezonecode"] = 110, ["dateformatstring"] = "dd/MM/yyyy", ["dateseparator"] = ".", ["timeformatstring"] = "HH:mm",
        });
        var dora = ServiceFor(fx, objectId);

        // 23:30 UTC is 00:30 of the next day in W. Europe (UTC+1 in November); the value itself stays UTC.
        var row = (await dora.RetrieveMultipleAsync(Query())).Entities.Should().ContainSingle().Subject;
        row.FormattedValues["pb_startdate"].Should().Be("21.11.2026 00:30");
        row.GetAttributeValue<DateTime>("pb_startdate").Should().Be(new DateTime(2026, 11, 20, 23, 30, 0, DateTimeKind.Utc));
        (await dora.RetrieveAsync(Project, _project, new ColumnSet("pb_startdate"))).FormattedValues["pb_startdate"].Should().Be("21.11.2026 00:30");

        var admin = (await _admin.RetrieveMultipleAsync(Query())).Entities.Should().ContainSingle().Subject;
        admin.FormattedValues["pb_startdate"].Should().Be("11/20/2026 11:30 PM");
    }
}
