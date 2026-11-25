using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan X.12b, task 1: the seeded table <c>xrmverse_flowsubscription</c>, Basic User's Create and Read on it, the refusal of a public
/// Create, Update and Delete, and the callback store.
/// </summary>
[Collection("xrmd")]
public sealed class FlowSubscriptionTableTests(XrmdFixture fx)
{
    private const string Refusal = "Flow subscriptions are created and deleted through /api/connector/v1/subscriptions.";

    [Fact]
    public void Table_is_seeded_user_owned_with_its_columns()
    {
        var table = fx.App.Services.GetRequiredService<IMetadataStore>().GetTable(FlowSubscriptionSchema.Table);
        table.Should().NotBeNull();
        table!.OwnershipType.Should().Be(TableOwnership.UserOwned);
        table.ObjectTypeCode.Should().BeLessThan(10000);
        table.EntitySetName.Should().Be(FlowSubscriptionSchema.EntitySet);
        var columns = table.Attributes!.Select(a => a.LogicalName).ToList();
        columns.Should().Contain([FlowSubscriptionSchema.Name, FlowSubscriptionSchema.TableColumn, FlowSubscriptionSchema.Changes,
            FlowSubscriptionSchema.FilteringColumns, FlowSubscriptionSchema.CallbackHost]);
    }

    [Fact]
    public async Task Basic_user_holds_create_and_read_at_user_depth()
    {
        string Granted(string verb) => $"""
            SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' AND privilegedepthmask = 1
            AND privilegeid = '{PrivilegeNames.IdOf(verb + "xrmverse_FlowSubscription")}'
            """;
        foreach (var verb in new[] { "prvCreate", "prvRead" })
            (await TestSql.CountAsync(fx.ConnectionString, Granted(verb))).Should().Be(1, verb);
        foreach (var verb in new[] { "prvWrite", "prvDelete" })
            (await TestSql.CountAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{StandardRoles.BasicUser}' " +
                $"AND privilegeid = '{PrivilegeNames.IdOf(verb + "xrmverse_FlowSubscription")}'")).Should().Be(0, verb);
    }

    [Fact]
    public async Task Public_create_update_and_delete_are_refused()
    {
        var set = "/api/data/v9.2/" + FlowSubscriptionSchema.EntitySet;
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Url, string? Body)[]
        {
            (HttpMethod.Post, set, """{"xrmverse_name":"x","xrmverse_table":"account","xrmverse_changes":1}"""),
            (HttpMethod.Patch, $"{set}({id})", """{"xrmverse_name":"y"}"""),
            (HttpMethod.Delete, $"{set}({id})", null),
        };
        foreach (var (method, url, body) in requests)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await fx.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method}: {text}");
            JsonNode.Parse(text)!["error"]!["message"]!.GetValue<string>().Should().Be(Refusal, method.ToString());
        }
    }

    [Fact]
    public async Task Callback_table_exists()
    {
        (await TestSql.TableExistsAsync(fx.ConnectionString, "meta", "flow_subscription_callback")).Should().BeTrue();
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "meta", "flow_subscription_callback", "subscriptionid")).Should().BeTrue();
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "meta", "flow_subscription_callback", "callbackurl")).Should().BeTrue();
        (await TestSql.CountAsync(fx.ConnectionString, """
            SELECT COUNT(*) FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage k ON k.constraint_name = tc.constraint_name AND k.table_schema = tc.table_schema
            WHERE tc.table_schema = 'meta' AND tc.table_name = 'flow_subscription_callback'
            AND tc.constraint_type = 'PRIMARY KEY' AND k.column_name = 'subscriptionid'
            """)).Should().Be(1);
        (await TestSql.CountAsync(fx.ConnectionString, """
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'meta' AND table_name = 'flow_subscription_callback'
            AND column_name = 'callbackurl' AND is_nullable = 'NO' AND character_maximum_length = 2048
            """)).Should().Be(1);
    }
}
