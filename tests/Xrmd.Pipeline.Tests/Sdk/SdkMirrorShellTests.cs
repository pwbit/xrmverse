using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>The mirror registry (a model request's chain mirror) and the response shells the core adapter returns (S.2e3a).</summary>
public class SdkMirrorShellTests
{
    [Fact]
    public void Register_and_For_round_trip()
    {
        var mirror = new SdkMirror();
        var request = new Request { Name = "Create" };

        Assert.Null(SdkMirror.For(request));
        SdkMirror.Register(request, mirror);
        Assert.Same(mirror, SdkMirror.For(request));

        SdkMirror.Register(request, mirror); // the same mirror again is a no-op
        Assert.Same(mirror, SdkMirror.For(request));
        Assert.Null(SdkMirror.For(new Request { Name = "Create" }));
    }

    [Fact]
    public void Register_with_another_mirror_throws()
    {
        var mirror = new SdkMirror();
        var request = new Request { Name = "Create" };
        SdkMirror.Register(request, mirror);

        Assert.Throws<InvalidOperationException>(() => SdkMirror.Register(request, new SdkMirror()));
        Assert.Same(mirror, SdkMirror.For(request));
    }

    [Fact]
    public void Shell_out_unread_returns_the_typed_twin_without_sync()
    {
        var mirror = new SdkMirror();
        var id = Guid.NewGuid();
        var sdk = new CreateResponse { ResponseName = "Create", Results = { ["id"] = id } }; // the core names its responses
        var before = mirror.SyncCount;

        var shell = mirror.Shell(sdk);

        Assert.Equal("Create", shell.Name);
        Assert.True(mirror.IsUnread(shell));
        Assert.True(mirror.TryGetSdk(shell, out var twin));
        Assert.Same(sdk, twin);
        Assert.Same(sdk, mirror.Out(shell));
        Assert.Same(sdk, mirror.Out((object)shell));
        Assert.Equal(id, sdk.id);
        Assert.Single(sdk.Results);
        Assert.Equal(before, mirror.SyncCount);
        Assert.True(mirror.IsUnread(shell));
    }

    [Fact]
    public void Shell_in_fills_and_then_out_writes()
    {
        var mirror = new SdkMirror();
        var id = Guid.NewGuid();
        var sdk = new CreateResponse { ResponseName = "Create", Results = { ["id"] = id } }; // the core names its responses
        var shell = mirror.Shell(sdk);
        var before = mirror.SyncCount;

        Assert.Same(shell, mirror.In(sdk));

        Assert.False(mirror.IsUnread(shell));
        Assert.Equal(id, shell["id"]);
        Assert.True(mirror.SyncCount > before); // In counts as today

        var other = Guid.NewGuid();
        shell["id"] = other;
        var afterIn = mirror.SyncCount;
        Assert.Same(sdk, mirror.Out(shell));
        Assert.Equal(other, sdk.id);
        Assert.True(mirror.SyncCount > afterIn); // a read shell is an ordinary twin
    }

    [Fact]
    public void Shell_of_a_paired_response_is_its_twin()
    {
        var mirror = new SdkMirror();
        var sdk = new RetrieveMultipleResponse { Results = { ["EntityCollection"] = new EntityCollection() } };
        var model = mirror.In(sdk)!;
        var before = mirror.SyncCount;

        Assert.Same(model, mirror.Shell(sdk));
        Assert.False(mirror.IsUnread(model));
        Assert.Equal(before, mirror.SyncCount);

        // A shell asked for twice is the same shell, still unread.
        var create = new CreateResponse { Results = { ["id"] = Guid.NewGuid() } };
        var shell = mirror.Shell(create);
        Assert.Same(shell, mirror.Shell(create));
        Assert.True(mirror.IsUnread(shell));
    }

    /// <summary>
    /// The read rule: a shell's results are empty until its twin is read in, so a model reader of a core response calls
    /// <c>mirror.In(twin)</c> first. A model write to an unread shell is refused at <c>Out</c> instead of being dropped.
    /// </summary>
    [Fact]
    public void Unread_shell_results_are_empty()
    {
        var mirror = new SdkMirror();
        var sdk = new RetrieveMultipleResponse
        {
            ResponseName = "RetrieveMultiple",
            Results = { ["EntityCollection"] = new EntityCollection(new List<Entity> { new("account", Guid.NewGuid()) }) },
        };

        var shell = mirror.Shell(sdk);

        Assert.Empty(shell.Results);
        Assert.Equal("RetrieveMultiple", shell.Name);
        Assert.Same(sdk, mirror.Out(shell)); // unread and unwritten: the twin as it is

        shell["Written"] = 1;
        var thrown = Assert.Throws<InvalidOperationException>(() => mirror.Out(shell));
        Assert.Contains("read in", thrown.Message);
        Assert.False(sdk.Results.ContainsKey("Written"));
        Assert.True(mirror.IsUnread(shell));

        mirror.In(sdk); // reading in replaces the shell's results with the twin's
        var rows = Assert.IsType<RecordCollection>(shell["EntityCollection"]);
        Assert.IsType<Record>(Assert.Single(rows.Records));
        Assert.False(shell.Results.ContainsKey("Written"));
        Assert.Same(sdk, mirror.Out(shell));
    }

    [Fact]
    public void Unread_shell_with_a_changed_name_or_replaced_results_refuses_out()
    {
        var mirror = new SdkMirror();
        var sdk = new CreateResponse { ResponseName = "Create", Results = { ["id"] = Guid.NewGuid() } };

        var renamed = mirror.Shell(sdk);
        renamed.Name = "Other";
        Assert.Throws<InvalidOperationException>(() => mirror.Out(renamed));
        Assert.Throws<InvalidOperationException>(() => mirror.Out((object)renamed));

        var other = new CreateResponse { ResponseName = "Create", Results = { ["id"] = Guid.NewGuid() } };
        var replaced = mirror.Shell(other);
        replaced.Results = new ParameterBag { ["id"] = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => mirror.Out(replaced));
        Assert.Single(other.Results);
    }

    /// <summary>
    /// The name check compares with the name the shell was made with, not the twin's current one: SDK code that renames the twin
    /// after <c>Shell</c> leaves an untouched shell valid (task 2 review, m3).
    /// </summary>
    [Fact]
    public void Unread_shell_checks_the_name_it_was_made_with()
    {
        var mirror = new SdkMirror();
        var sdk = new CreateResponse { ResponseName = "Create", Results = { ["id"] = Guid.NewGuid() } };
        var shell = mirror.Shell(sdk);
        var before = mirror.SyncCount;

        sdk.ResponseName = "Renamed";

        Assert.Same(sdk, mirror.Out(shell));
        Assert.Same(sdk, mirror.Out((object)shell));
        Assert.Equal("Renamed", sdk.ResponseName);
        Assert.True(mirror.IsUnread(shell));
        Assert.Equal(before, mirror.SyncCount);

        shell.Name = "Renamed"; // a model write, even one that matches the twin's new name
        Assert.Throws<InvalidOperationException>(() => mirror.Out(shell));
    }

    [Fact]
    public void Pair_of_a_shell_with_its_own_twin_keeps_it_unread()
    {
        var mirror = new SdkMirror();
        var id = Guid.NewGuid();
        var sdk = new CreateResponse { ResponseName = "Create", Results = { ["id"] = id } };
        var shell = mirror.Shell(sdk);
        var before = mirror.SyncCount;

        mirror.Pair(shell, sdk);

        Assert.True(mirror.IsUnread(shell));
        Assert.Same(sdk, mirror.Out(shell));
        Assert.Equal(id, sdk.id);
        Assert.Single(sdk.Results);
        Assert.Equal(before, mirror.SyncCount);
    }

    [Fact]
    public void Shell_of_a_plain_response_keeps_its_name_and_pairs_it()
    {
        var mirror = new SdkMirror();
        var sdk = new OrganizationResponse { ResponseName = "WhoAmI", Results = { ["UserId"] = Guid.NewGuid() } };

        var shell = mirror.Shell(sdk);

        Assert.Equal("WhoAmI", shell.Name);
        Assert.True(mirror.TryGetModel(sdk, out var model));
        Assert.Same(shell, model);
    }

    [Fact]
    public void Pair_of_an_unread_shell_clears_its_mark()
    {
        var mirror = new SdkMirror();
        var shell = mirror.Shell(new CreateResponse());
        var replacement = new CreateResponse();

        mirror.Pair(shell, replacement);

        Assert.False(mirror.IsUnread(shell));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var mirror = new SdkMirror();
        Assert.Throws<ArgumentNullException>(() => SdkMirror.Register(null!, mirror));
        Assert.Throws<ArgumentNullException>(() => SdkMirror.Register(new Request(), null!));
        Assert.Throws<ArgumentNullException>(() => SdkMirror.For(null!));
        Assert.Throws<ArgumentNullException>(() => mirror.Shell(null!));
        Assert.Throws<ArgumentNullException>(() => mirror.IsUnread(null!));
    }
}
