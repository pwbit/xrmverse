using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Retrieve reads the record as the caller and, when that finds nothing, once more unrestricted to tell 403 from 404. A
/// record committed by another session between the two reads must not turn into a 403 for a caller who may read it (seen
/// as a flaky 403 on <c>importjobs(id)</c> while <c>xrmd solution import --async</c> polled a failing import).
/// </summary>
[Collection("xrmd")]
public sealed class RetrieveRaceTests(XrmdFixture fx)
{
    [Fact]
    public async Task A_record_committed_between_the_two_reads_is_returned_not_403()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var service = ServiceFor(fx, fx.AdminObjectId);
        var me = (WhoAmIResponse)await service.ExecuteAsync(new WhoAmIRequest());
        var caller = new Caller(me.UserId, me.BusinessUnitId);
        var id = Guid.NewGuid();

        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        core.AfterRestrictedRetrieveMiss = () =>
            service.CreateAsync(new Entity(TestEntities.Project, id) { ["pb_name"] = "race " + id.ToString("N")[..6] });
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        var entity = await core.RetrieveAsync(new DataSession(conn, null), caller, TestEntities.Project, id, new ColumnSet("pb_name"), default);

        entity.Id.Should().Be(id);
        await service.DeleteAsync(TestEntities.Project, id);
    }
}
