using FluentAssertions;
using Xrmd.Abstractions;
using Xrmd.Pipeline.Connector;
using Xunit;

namespace Xrmd.Pipeline.Tests;

/// <summary>Plan X.12b, task 4: what the executor's post-operation hands the <see cref="IRowChangeHook"/>.</summary>
public sealed class RowChangeHookTests
{
    private sealed class CountingHook(bool mayHave) : IRowChangeHook
    {
        public int MayHaveCalls { get; private set; }
        public List<(string Message, string Table, Guid Id, IReadOnlyCollection<string> Columns)> Calls { get; } = [];

        public bool MayHave(string table)
        {
            MayHaveCalls++;
            return mayHave;
        }

        public Task<bool> OnChangedAsync(string message, string table, Guid id, IReadOnlyCollection<string> sentColumns, DataSession s,
            CancellationToken ct)
        {
            Calls.Add((message, table, id, sentColumns));
            return Task.FromResult(true);
        }
    }

    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Table_without_subscriptions_costs_no_read()
    {
        var hook = new CountingHook(mayHave: false);
        var columnsRead = 0;
        var queued = await RowChanges.NotifyAsync(hook, "Update", "account", Id, () => { columnsRead++; return ["name"]; }, null!, default);
        queued.Should().BeFalse();
        hook.MayHaveCalls.Should().Be(1);
        hook.Calls.Should().BeEmpty();
        columnsRead.Should().Be(0, "the sent columns are not read for a table without subscriptions");
    }

    [Theory]
    [InlineData("Create", "create")]
    [InlineData("Update", "update")]
    [InlineData("Delete", "delete")]
    public async Task Writes_are_passed_with_their_change_name(string message, string change)
    {
        var hook = new CountingHook(mayHave: true);
        (await RowChanges.NotifyAsync(hook, message, "account", Id, () => ["name"], null!, default)).Should().BeTrue();
        var call = hook.Calls.Single();
        (call.Message, call.Table, call.Id).Should().Be((change, "account", Id));
        if (change == "delete") call.Columns.Should().BeEmpty();
        else call.Columns.Should().Equal("name");
    }

    [Fact]
    public async Task Assign_is_an_update_of_ownerid()
    {
        var hook = new CountingHook(mayHave: true);
        await RowChanges.NotifyAsync(hook, "Assign", "account", Id, () => ["assignee"], null!, default);
        var call = hook.Calls.Single();
        call.Message.Should().Be("update");
        call.Columns.Should().Equal("ownerid");
    }

    [Theory]
    [InlineData("CreateMultiple", "account")]
    [InlineData("UpdateMultiple", "account")]
    [InlineData("Upsert", "account")]
    [InlineData("Retrieve", "account")]
    [InlineData("Create", null)]
    public async Task Other_messages_and_no_table_are_not_passed(string message, string? table)
    {
        var hook = new CountingHook(mayHave: true);
        (await RowChanges.NotifyAsync(hook, message, table, Id, () => [], null!, default)).Should().BeFalse();
        hook.MayHaveCalls.Should().Be(0);
        hook.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task No_id_and_no_hook_are_not_passed()
    {
        var hook = new CountingHook(mayHave: true);
        (await RowChanges.NotifyAsync(hook, "Create", "account", Guid.Empty, () => [], null!, default)).Should().BeFalse();
        hook.Calls.Should().BeEmpty();
        (await RowChanges.NotifyAsync(null, "Create", "account", Id, () => [], null!, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Delete", true)]
    [InlineData("delete", true)]
    [InlineData("Create", false)]
    [InlineData("Update", false)]
    [InlineData("Assign", false)]
    [InlineData("SetState", false)]
    [InlineData("Retrieve", false)]
    public void Only_a_delete_is_told_before_the_core_operation(string message, bool before) =>
        RowChanges.BeforeCoreOperation(message).Should().Be(before);
}
