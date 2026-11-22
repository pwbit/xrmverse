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
/// #109: choice labels in the caller's UI language (<c>usersettings.uilanguageid</c>), falling back to 1033: the formatted
/// value and the order by a choice column (FetchXML/QueryExpression sort a choice by its label) agree.
/// </summary>
[Collection("xrmd")]
public sealed class UserLanguageLabelTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _tag = "ULL" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = ServiceFor(fx, fx.AdminObjectId);
        await EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        foreach (var priority in new[] { 1, 2, 3 })
            await _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{priority}", ["pb_priority"] = new OptionSetValue(priority) });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private QueryExpression ByPriority()
    {
        var q = new QueryExpression(Project) { ColumnSet = new ColumnSet("pb_name", "pb_priority") };
        q.Criteria.AddCondition("pb_name", ConditionOperator.Like, _tag + "%");
        q.AddOrder("pb_priority", OrderType.Ascending);
        return q;
    }

    private static int[] Priorities(EntityCollection rows) => rows.Entities.Select(e => e.GetAttributeValue<OptionSetValue>("pb_priority").Value).ToArray();

    private static string[] Labels(EntityCollection rows) => rows.Entities.Select(e => e.FormattedValues["pb_priority"]).ToArray();

    [Fact]
    public async Task Choice_labels_and_order_use_the_callers_language_with_1033_fallback()
    {
        // A user the Core creates, so that it has a usersettings row (the SQL test helper writes none).
        var objectId = Guid.NewGuid();
        var user = await _admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Lia Lingua " + _tag[..8], ["domainname"] = $"lia.{_tag}@example.invalid", ["azureactivedirectoryobjectid"] = objectId,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await GrantAsync(fx, user, team: false, ("prvReadpb_Project", DepthMask.Global));
        await _admin.UpdateAsync(new Entity("usersettings", user) { ["uilanguageid"] = 1040 });

        var italian = await ServiceFor(fx, objectId).RetrieveMultipleAsync(ByPriority());

        // Bassa (1), High (3: no Italian label, 1033), Media (2).
        Priorities(italian).Should().Equal(1, 3, 2);
        Labels(italian).Should().Equal("Bassa", "High", "Media");

        var english = await _admin.RetrieveMultipleAsync(ByPriority());
        Priorities(english).Should().Equal(3, 1, 2);
        Labels(english).Should().Equal("High", "Low", "Medium");
    }
}
