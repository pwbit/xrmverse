using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Pipeline;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3a, Task 3: <see cref="IPipelineCore"/> on the model. The SDK extension methods (<see cref="PipelineCoreSdk"/>) answer what the
/// core answered before the move (the Task 1 response goldens and <c>mutation.txt</c>); the model entry returns an unread shell of the
/// core's typed response; a request the core issues keeps its SDK instance, so the marks keyed on it (<see cref="CascadeHint"/>) and its
/// CLR type survive both crossings.
/// </summary>
[Collection("xrmd")]
public sealed class PipelineCoreModelTests(XrmdFixture fx)
{
    private const string Pipe = TestEntities.Pipe;

    public static TheoryData<string> Names => CoreResponseGoldenTests.Names;

    /// <summary>The golden file of each scenario, with (b) called through the extension methods by name.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task Extension_path_returns_todays_response_type(string message)
    {
        var scenario = CoreScenarios.All.Single(s => s.Message == message);
        await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
        var file = new StringBuilder();

        kit.Section = "a";
        file.Append("=== (a) PipelineExecutor.ExecuteAsync ===\n");
        var request = await scenario.Arrange(kit);
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request)).Append("--- response ---\n");
        var (viaPipeline, pipelineFault) = await CoreScenarios.ViaPipelineAsync(kit, request);
        if (scenario.Anchor is { } anchorA && pipelineFault is null) await anchorA(kit);
        file.Append(CoreDump.Response(viaPipeline, pipelineFault, scenario.AllColumnsOrder));

        kit.Section = "b";
        file.Append(scenario.NonPipeline ? "=== (b) IPipelineCore.ExecuteNonPipelineAsync ===\n" : "=== (b) IPipelineCore.ExecuteCoreAsync ===\n");
        request = await scenario.Arrange(kit);
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request)).Append("--- response ---\n");
        var (viaExtension, extensionFault) = await ViaExtensionAsync(kit, scenario, request);
        if (scenario.Anchor is { } anchorB && extensionFault is null) await anchorB(kit);
        file.Append(CoreDump.Response(viaExtension, extensionFault, scenario.AllColumnsOrder));

        CoreDump.AssertGolden(message + ".txt", kit.Normalize(file.ToString()));
    }

    /// <summary>The Task 1 probe through the extension methods: every request is changed exactly as <c>mutation.txt</c> says.</summary>
    [Fact]
    public async Task Extension_path_mutates_requests_as_today()
    {
        var file = new StringBuilder();
        foreach (var scenario in CoreScenarios.All)
        {
            await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
            var request = await scenario.Arrange(kit);
            var tokens = new CoreDump.Tokens();
            var before = CoreDump.Request(request, tokens);
            var (_, fault) = await ViaExtensionAsync(kit, scenario, request);
            var after = CoreDump.Request(request, tokens);
            file.Append(scenario.Message).Append(": ").Append(CoreRequestMutationProbeTests.Changes(before, after))
                .Append(fault is null ? "" : " (fault)").Append('\n');
        }

        CoreDump.AssertGolden("mutation.txt", file.ToString());
    }

    /// <summary>
    /// A model Create through the model entry: the response is an unread shell of the core's <see cref="CreateResponse"/>, paired in
    /// the request's registered mirror; reading the twin in fills it with the stored row's id. The Target crosses as its own twin.
    /// </summary>
    [Fact]
    public async Task Model_path_returns_a_shell_of_the_typed_response()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, false);
        await kit.EnsurePipeAsync();
        await kit.ExecAsync($"DELETE FROM data.{Pipe} WHERE pb_name = 'pcm-create'");
        var target = new Record(Pipe) { ["pb_name"] = "pcm-create", ["pb_count"] = 5 };
        var request = new Request { Name = "Create", Parameters = { ["Target"] = target } };
        var mirror = new SdkMirror();
        SdkMirror.Register(request, mirror);

        Response response;
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            response = await core.ExecuteCoreAsync(request, new CoreCall(kit.Caller, new DataSession(conn, tx)), default);
            await tx.CommitAsync();
        }

        SdkMirror.For(request).Should().BeSameAs(mirror);
        mirror.IsUnread(response).Should().BeTrue();
        response.Name.Should().Be("Create");
        response.Results.Should().BeEmpty();
        mirror.TryGetSdk(response, out var twin).Should().BeTrue();
        var typed = twin.Should().BeOfType<CreateResponse>().Subject;
        var stored = (Guid)(await kit.ScalarAsync($"SELECT pb_pipeid FROM data.{Pipe} WHERE pb_name = 'pcm-create'"))!;
        typed.id.Should().Be(stored);
        mirror.TryGetSdk(target, out var sdkTarget).Should().BeTrue();
        sdkTarget.Should().BeOfType<Entity>().Which.LogicalName.Should().Be(Pipe);

        mirror.In(typed).Should().BeSameAs(response);
        mirror.IsUnread(response).Should().BeFalse();
        response.Results["id"].Should().Be(stored);
    }

    /// <summary>
    /// A model caller whose request has no registered mirror gets the response already read in (the core's adapter made the mirror,
    /// so no one else could read the shell): a Create's <c>id</c> is there, and the mirror is now registered for the request.
    /// </summary>
    [Fact]
    public async Task Model_path_without_a_mirror_returns_the_response_read_in()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, false);
        await kit.EnsurePipeAsync();
        await kit.ExecAsync($"DELETE FROM data.{Pipe} WHERE pb_name = 'pcm-create-unregistered'");
        var request = new Request
        {
            Name = "Create", Parameters = { ["Target"] = new Record(Pipe) { ["pb_name"] = "pcm-create-unregistered" } },
        };

        Response response;
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            response = await core.ExecuteCoreAsync(request, new CoreCall(kit.Caller, new DataSession(conn, tx)), default);
            await tx.CommitAsync();
        }

        var stored = (Guid)(await kit.ScalarAsync($"SELECT pb_pipeid FROM data.{Pipe} WHERE pb_name = 'pcm-create-unregistered'"))!;
        response.Results["id"].Should().Be(stored);
        var mirror = SdkMirror.For(request);
        mirror.Should().NotBeNull();
        mirror!.IsUnread(response).Should().BeFalse();
        mirror.TryGetSdk(response, out var twin).Should().BeTrue();
        twin.Should().BeOfType<CreateResponse>();
    }

    /// <summary>
    /// The SDK extension <c>RetrieveImageAsync</c> returns the core's own <see cref="Entity"/>: the image is converted once, into the
    /// call's mirror (its model twin is the record the core converted it into), and the call writes nothing else but the target.
    /// </summary>
    [Fact]
    public async Task Extension_image_read_returns_the_cores_entity_converted_once()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, false);
        await kit.EnsurePipeAsync();
        await kit.ExecAsync($"DELETE FROM data.{Pipe} WHERE pb_name = 'pcm-image'");
        var id = await kit.Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "pcm-image", ["pb_count"] = 7 });
        var target = new EntityReference(Pipe, id);
        var mirror = new SdkMirror();
        (Entity Image, Record Record)? read = null;
        CoreHandOff.ImageRead.Value = (image, record) => read = (image, record);
        Entity? result;
        try
        {
            await using var scope = kit.Services.CreateAsyncScope();
            var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            result = await PipelineCoreSdk.RetrieveImageAsync(core, target, ["pb_name", "pb_count"], new DataSession(conn, tx), mirror, default);
            await tx.CommitAsync();
        }
        finally
        {
            CoreHandOff.ImageRead.Value = null;
        }

        read.Should().NotBeNull();
        result.Should().BeSameAs(read!.Value.Image, "the caller gets the core's own Entity, not a copy");
        mirror.TryGetModel(read.Value.Image, out var model).Should().BeTrue();
        model.Should().BeSameAs(read.Value.Record, "the core converted the image into the call's mirror");
        var targetSize = new SdkMirror();
        targetSize.In(target);
        var imageSize = new SdkMirror();
        imageSize.In(read.Value.Image);
        mirror.SyncCount.Should().Be(targetSize.SyncCount + imageSize.SyncCount, "the target and the image cross once each");
    }

    /// <summary>
    /// The requests the core issues reach the nested delegate as the core's own typed instances: the Update of a CommitFileBlocksUpload
    /// (not a cascade) and the Delete of an email's attachment (a cascade, <see cref="CascadeHint"/>), through the extension path; and,
    /// through the model entry, a model request registered with the chain's mirror whose twin carries the mark.
    /// </summary>
    [Fact]
    public async Task Nested_request_from_the_core_keeps_the_cascade_mark()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, false);

        // CommitFileBlocksUpload: the nested Update of the record, not a cascade.
        var record = await kit.FileRecordAsync();
        var token = await kit.InitializeUploadAsync(record);
        await kit.Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = CoreScenarioKit.BlockId, BlockData = CoreScenarioKit.FileBytes });
        var commit = new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [CoreScenarioKit.BlockId], FileName = "report.pdf", MimeType = "application/pdf",
        };
        var seen = await NestedViaExtensionAsync(kit, commit);
        seen.Should().ContainSingle().Which.Should().Be((typeof(UpdateRequest), "new_crgfile", false));

        // The Delete of an email with an attachment: the attachment's Delete is a marked cascade.
        var email = await AttachedEmailAsync(kit);
        seen = await NestedViaExtensionAsync(kit, new DeleteRequest { Target = email });
        seen.Should().ContainSingle().Which.Should().Be((typeof(DeleteRequest), "activitymimeattachment", true));

        // The same through the model entry: the nested model request is registered with the chain's mirror, and its twin is the
        // core's marked DeleteRequest.
        email = await AttachedEmailAsync(kit);
        var request = new Request
        {
            Name = "Delete",
            Parameters = { ["Target"] = new Xrmd.Model.Records.RecordReference { LogicalName = "email", Id = email.Id } },
        };
        var modelSeen = new List<(Type Type, bool Registered, bool Marked)>();
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
            var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await core.ExecuteCoreAsync(request, new CoreCall(kit.Caller, session, async (nested, ct) =>
            {
                var mirror = SdkMirror.For(nested);
                mirror.Should().NotBeNull().And.BeSameAs(SdkMirror.For(request));
                mirror!.TryGetSdk(nested, out var sdk).Should().BeTrue();
                var sdkRequest = (OrganizationRequest)sdk!;
                modelSeen.Add((sdkRequest.GetType(), true, CascadeHint.IsMarked(sdkRequest)));
                var sdkResponse = await executor.ExecuteAsync(sdkRequest,
                    new InvocationContext(kit.Caller, kit.Caller.UserId, 2, null, session, Guid.NewGuid()) { IsCoreCascade = CascadeHint.IsMarked(sdkRequest) }, ct);
                return mirror.Shell(sdkResponse);
            }), default);
            await tx.CommitAsync();
        }
        modelSeen.Should().ContainSingle().Which.Should().Be((typeof(DeleteRequest), true, true));
        kit.Cache.Invalidate();
    }

    /// <summary>An email of the administrator with one attachment.</summary>
    private static async Task<EntityReference> AttachedEmailAsync(CoreScenarioKit kit)
    {
        var email = new EntityReference("email", await kit.EmailAsync());
        await kit.Admin.CreateAsync(new Entity("activitymimeattachment")
        {
            ["objectid"] = email, ["filename"] = "note.txt", ["mimetype"] = "text/plain",
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("pcm attachment")),
        });
        return email;
    }

    /// <summary>
    /// Runs <paramref name="request"/> through the extension path with a nested delegate that records the type, the target table and
    /// the cascade mark of each request the core issues, then runs it through the pipeline as the golden's (b) does.
    /// </summary>
    private static async Task<List<(Type Type, string Table, bool Marked)>> NestedViaExtensionAsync(CoreScenarioKit kit, OrganizationRequest request)
    {
        var seen = new List<(Type, string, bool)>();
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await PipelineCoreSdk.ExecuteCoreAsync(core, request, kit.Caller, session, (nested, ct) =>
            {
                var table = nested.Parameters["Target"] switch
                {
                    Entity e => e.LogicalName,
                    EntityReference r => r.LogicalName,
                    _ => "",
                };
                seen.Add((nested.GetType(), table, CascadeHint.IsMarked(nested)));
                return executor.ExecuteAsync(nested,
                    new InvocationContext(kit.Caller, kit.Caller.UserId, 2, null, session, Guid.NewGuid()) { IsCoreCascade = CascadeHint.IsMarked(nested) }, ct);
            }, default);
            await tx.CommitAsync();
        }
        finally
        {
            kit.Cache.Invalidate();
        }
        return seen;
    }

    /// <summary>The golden's (b), with the core called through <see cref="PipelineCoreSdk"/> by name.</summary>
    private static async Task<(OrganizationResponse? Response, Exception? Fault)> ViaExtensionAsync(CoreScenarioKit kit, CoreScenario scenario,
        OrganizationRequest request)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            var response = scenario.NonPipeline
                ? await PipelineCoreSdk.ExecuteNonPipelineAsync(core, request, kit.Caller, session, default)
                : await PipelineCoreSdk.ExecuteCoreAsync(core, request, kit.Caller, session,
                    (nested, ct) => executor.ExecuteAsync(nested,
                        new InvocationContext(kit.Caller, kit.Caller.UserId, 2, null, session, Guid.NewGuid()) { IsCoreCascade = CascadeHint.IsMarked(nested) },
                        ct), default);
            await tx.CommitAsync();
            return (response, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return (null, ex);
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }
}
