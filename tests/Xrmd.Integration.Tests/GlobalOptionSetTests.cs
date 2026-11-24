using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Global option sets (<c>meta.optionset</c>): Picklist attributes bound to one show and validate its options, options added
/// to the set reach every bound attribute, a set in use cannot be deleted, <c>GlobalOptionSetDefinitions</c> lists them.
/// </summary>
[Collection("xrmd")]
public sealed class GlobalOptionSetTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Priority = "new_priority";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private static string NewName(string prefix = "pb_gos") => prefix + Guid.NewGuid().ToString("N")[..10];

    private static Label Label(string en, string it) => new(new LocalizedLabel(en, 1033), new[] { new LocalizedLabel(en, 1033), new LocalizedLabel(it, 1040) });

    private static OptionSetMetadata GlobalSet(string name, params (int Value, string En, string It)[] options)
    {
        var collection = new OptionMetadataCollection();
        foreach (var (value, en, it) in options) collection.Add(new OptionMetadata(Label(en, it), value));
        return new OptionSetMetadata(collection)
        {
            Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist, DisplayName = Label("Priority", "Priorità"),
        };
    }

    private static PicklistAttributeMetadata BoundPicklist(string logicalName, string optionSetName) => new()
    {
        LogicalName = logicalName,
        SchemaName = logicalName,
        OptionSet = new OptionSetMetadata { IsGlobal = true, Name = optionSetName },
    };

    private static EntityMetadata Entity(string logicalName, params AttributeMetadata[] attributes) =>
        TestHelpers.Entity(logicalName, logicalName, "Gos", attributes);

    private static StringAttributeMetadata PrimaryName() => new() { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 };

    /// <summary>Runs <paramref name="write"/> in a session of its own, committed when it succeeds.</summary>
    private async Task InSessionAsync(Func<DataSession, Task> write)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        await tx.CommitAsync();
        session.Committed();
    }

    private static readonly SemaphoreSlim PriorityGate = new(1, 1);

    /// <summary><c>new_priority</c> (1 Low, 2 High) exists, created once per database.</summary>
    private async Task EnsurePriorityAsync()
    {
        await PriorityGate.WaitAsync();
        try
        {
            if (Store.GetOptionSet(Priority) is null)
                await InSessionAsync(s => Store.CreateOptionSetAsync(s, GlobalSet(Priority, (1, "Low", "Bassa"), (2, "High", "Alta")), default));
        }
        finally
        {
            PriorityGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<HttpResponseMessage> CreateRecordAsync(string entity, string attribute, int value)
    {
        var set = Store.GetEntity(entity)!.EntitySetName;
        return await SendAsync(HttpMethod.Post, set, $$"""{ "pb_name": "x", "{{attribute}}": {{value}} }""");
    }

    [Fact]
    public async Task Picklist_bound_to_a_global_option_set_validates_and_shows_its_options()
    {
        await EnsurePriorityAsync();
        var entity = NewName();
        await Store.CreateEntityAsync(Entity(entity, BoundPicklist("pb_priority", Priority)), PrimaryName());

        var ok = await CreateRecordAsync(entity, "pb_priority", 2);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());
        var refused = await CreateRecordAsync(entity, "pb_priority", 3);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, refusedBody);
        refusedBody.Should().Contain("Value 3 is not a valid option");

        var json = await GetJsonAsync($"EntityDefinitions(LogicalName='{entity}')/Attributes(LogicalName='pb_priority')" +
                                      "/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$expand=OptionSet,GlobalOptionSet");
        json["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.PicklistAttributeMetadata");
        json["LogicalName"]!.GetValue<string>().Should().Be("pb_priority");
        foreach (var name in new[] { "OptionSet", "GlobalOptionSet" })
        {
            var optionSet = json[name]!.AsObject();
            optionSet["IsGlobal"]!.GetValue<bool>().Should().BeTrue();
            optionSet["Name"]!.GetValue<string>().Should().Be(Priority);
            var options = optionSet["Options"]!.AsArray();
            options.Select(o => o!["Value"]!.GetValue<int>()).Should().Equal(1, 2);
            options[1]!["Label"]!["LocalizedLabels"]!.AsArray().Select(l => l!["Label"]!.GetValue<string>()).Should().Equal("High", "Alta");
        }

        // meta.attribute keeps only the name of the global set; the cache resolves it, also after a reload.
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT definition FROM meta.attribute WHERE entity_logical_name = @e AND logical_name = 'pb_priority'");
            cmd.Parameters.AddWithValue("@e", entity);
            var stored = JsonNode.Parse((string)(await cmd.ExecuteScalarAsync())!)!["OptionSet"]!;
            stored["Name"]!.GetValue<string>().Should().Be(Priority);
            stored["Options"]?.AsArray().Should().BeEmpty();
        }
        var reloaded = new MetadataStore(fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        await reloaded.LoadAsync();
        reloaded.GetEntity(entity)!.Attributes.OfType<PicklistAttributeMetadata>().Single().OptionSet.Options
            .Select(o => o.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Adding_an_option_to_a_global_set_reaches_every_attribute()
    {
        var set = NewName("new_set");
        await InSessionAsync(s => Store.CreateOptionSetAsync(s, GlobalSet(set, (1, "Low", "Bassa"), (2, "High", "Alta")), default));
        var first = NewName();
        var second = NewName();
        await Store.CreateEntityAsync(Entity(first, BoundPicklist("pb_level", set)), PrimaryName());
        await Store.CreateEntityAsync(Entity(second), PrimaryName());
        await Store.AddAttributeAsync(second, BoundPicklist("pb_grade", set));

        (await CreateRecordAsync(first, "pb_level", 3)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InSessionAsync(async s =>
        {
            await Store.UpdateOptionSetAsync(s, GlobalSet(set, (3, "Urgent", "Urgente"), (2, "Very high", "Molto alta")), default);
            Store.GetOptionSet(set)!.As<OptionSetMetadata>().Options.Should().HaveCount(2, "nothing is published before the commit");
        });

        var published = Store.GetOptionSet(set).Should().BeOfType<OptionSetMetadata>().Subject;
        published.Options.Select(o => (o.Value, o.Label.UserLocalizedLabel?.Label ?? o.Label.LocalizedLabels[0].Label))
            .Should().Equal((1, "Low"), (2, "Very high"), (3, "Urgent"));
        foreach (var (entity, attribute) in new[] { (first, "pb_level"), (second, "pb_grade") })
        {
            Store.GetEntity(entity)!.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == attribute)
                .OptionSet.Options.Select(o => o.Value).Should().Equal(1, 2, 3);
            var res = await CreateRecordAsync(entity, attribute, 3);
            res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Deleting_a_global_option_set_in_use_is_refused()
    {
        var set = NewName("new_set");
        await InSessionAsync(s => Store.CreateOptionSetAsync(s, GlobalSet(set, (1, "Low", "Bassa")), default));
        var entity = NewName();
        await Store.CreateEntityAsync(Entity(entity, BoundPicklist("pb_level", set)), PrimaryName());

        var fault = (await FluentActions.Awaiting(() => InSessionAsync(s => Store.DeleteOptionSetAsync(s, set, default)))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be($"The component cannot be deleted because it is referenced by attribute '{entity}.pb_level'.");
        Store.GetOptionSet(set).Should().NotBeNull();

        // Once the attribute is gone, the set can be deleted.
        await InSessionAsync(async s =>
        {
            await Store.DeleteAttributeAsync(s, entity, "pb_level", default);
            await Store.DeleteOptionSetAsync(s, set, default);
        });
        Store.GetOptionSet(set).Should().BeNull();
        Store.GetAllOptionSets().Should().NotContain(o => o.Name == set);
    }

    [Fact]
    public async Task Option_set_names_are_unique_ignoring_case_and_ids_are_stable()
    {
        var set = NewName("new_set");
        await InSessionAsync(s => Store.CreateOptionSetAsync(s, GlobalSet(set, (1, "Low", "Bassa")), default));

        await FluentActions.Awaiting(() => InSessionAsync(s => Store.CreateOptionSetAsync(s, GlobalSet(set.ToUpperInvariant(), (1, "A", "A")), default)))
            .Should().ThrowAsync<InvalidOperationException>();
        Store.GetOptionSet(set.ToUpperInvariant())!.Name.Should().Be(set);

        var expected = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("xrmd:optionset:" + set)));
        Store.GetOptionSet(set)!.MetadataId.Should().Be(expected);
    }

    [Fact]
    public async Task Picklist_bound_to_an_unknown_global_option_set_is_refused()
    {
        var entity = NewName();
        await FluentActions.Awaiting(() => Store.CreateEntityAsync(Entity(entity, BoundPicklist("pb_level", "new_missing")), PrimaryName()))
            .Should().ThrowAsync<XrmdValidationException>().WithMessage("*new_missing*");
        Store.GetEntity(entity).Should().BeNull();
    }

    [Fact]
    public async Task GlobalOptionSetDefinitions_lists_and_gets_by_name()
    {
        await EnsurePriorityAsync();

        var list = await GetJsonAsync("GlobalOptionSetDefinitions");
        list["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#GlobalOptionSetDefinitions");
        var listed = list["value"]!.AsArray().Single(o => o!["Name"]!.GetValue<string>() == Priority)!;
        listed["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.OptionSetMetadata");

        var single = await GetJsonAsync($"GlobalOptionSetDefinitions(Name='{Priority}')");
        single["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#GlobalOptionSetDefinitions/$entity");
        single["Name"]!.GetValue<string>().Should().Be(Priority);
        single["IsGlobal"]!.GetValue<bool>().Should().BeTrue();
        single["OptionSetType"]!.GetValue<string>().Should().Be("Picklist");
        single["Options"]!.AsArray().Select(o => o!["Value"]!.GetValue<int>()).Should().Equal(1, 2);
        var id = single["MetadataId"]!.GetValue<Guid>();

        (await GetJsonAsync($"GlobalOptionSetDefinitions({id})"))["Name"]!.GetValue<string>().Should().Be(Priority);
        (await SendAsync(HttpMethod.Get, "GlobalOptionSetDefinitions(Name='new_nothing')")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOptionSet_global_rejects_unapplied_properties()
    {
        var admin = SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);
        var set = NewName("new_uos");
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateOptionSetRequest { OptionSet = GlobalSet(set, (1, "Low", "Bassa")) });

        var changes = GlobalSet(set, (1, "Low", "Bassa"));
        changes.IsCustomizable = new BooleanManagedProperty(false);
        var message = await BadRequestAsync(() => admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.UpdateOptionSetRequest { OptionSet = changes }));
        message.Should().Contain("IsCustomizable").And.Contain(set);
        (Store.GetOptionSet(set)!.IsCustomizable?.Value ?? true).Should().BeTrue();

        // What is applied (labels, new options) and a retrieved definition sent back unchanged pass.
        var retrieved = (OptionSetMetadata)((Microsoft.Xrm.Sdk.Messages.RetrieveOptionSetResponse)await admin.ExecuteAsync(
            new Microsoft.Xrm.Sdk.Messages.RetrieveOptionSetRequest { Name = set })).OptionSetMetadata;
        retrieved.DisplayName = Label("Renamed", "Rinominato");
        retrieved.Options.Add(new OptionMetadata(Label("High", "Alta"), 2));
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.UpdateOptionSetRequest { OptionSet = retrieved });
        ((OptionSetMetadata)Store.GetOptionSet(set)!).Options.Select(o => o.Value).Should().Equal(1, 2);
        Store.GetOptionSet(set)!.DisplayName.UserLocalizedLabel.Label.Should().Be("Renamed");
    }
}
