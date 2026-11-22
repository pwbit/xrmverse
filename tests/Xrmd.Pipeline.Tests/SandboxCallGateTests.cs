using System.ServiceModel;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// The call gate shared by a plug-in's and a custom workflow activity's organization service (plan 2.14, Task 8b fix I2): one call
/// at a time, revoked once the token is cancelled, and <see cref="SandboxCallGate.RevokeAsync"/> waits for the call in flight.
/// </summary>
public class SandboxCallGateTests
{
    private const string Revoked = "revoked";

    [Fact]
    public void A_call_runs_the_operation_and_maps_its_fault()
    {
        var gate = new SandboxCallGate(CancellationToken.None, Revoked);
        gate.Call(_ => Task.FromResult(42)).Should().Be(42);

        var act = () => gate.Call<int>(_ => throw new XrmdFaultException(ErrorCodes.InvalidArgument, "bad", 400));
        act.Should().Throw<FaultException<OrganizationServiceFault>>()
            .Which.Detail.Should().Match<OrganizationServiceFault>(f => f.ErrorCode == ErrorCodes.InvalidArgument && f.Message == "bad");
    }

    [Fact]
    public void A_call_after_cancellation_is_revoked_without_running()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var gate = new SandboxCallGate(cts.Token, Revoked);
        var ran = false;

        var act = () => gate.Call(_ => { ran = true; return Task.FromResult(1); });

        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.Message.Should().Be(Revoked);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_waits_for_the_call_in_flight_then_keeps_every_call_out()
    {
        using var cts = new CancellationTokenSource();
        var gate = new SandboxCallGate(cts.Token, Revoked);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var inFlight = Task.Run(() => gate.Call(async _ => { entered.SetResult(); await release.Task; return 1; }));
        await entered.Task;

        await cts.CancelAsync();
        (await gate.RevokeAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse("the call in flight still holds the gate");
        release.SetResult();
        await inFlight;
        (await gate.RevokeAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        var ran = false;
        var act = () => gate.Call(_ => { ran = true; return Task.FromResult(1); });
        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.Message.Should().Be(Revoked);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task A_call_waiting_for_the_gate_rechecks_the_token_once_it_has_it()
    {
        // The token is cancelled while the second call waits; the first call releases the gate in the same instant. Whichever wins,
        // the second operation must never run.
        using var cts = new CancellationTokenSource();
        var gate = new SandboxCallGate(cts.Token, Revoked);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = Task.Run(() => gate.Call(async _ => { entered.SetResult(); await release.Task; return 1; }));
        await entered.Task;
        var ran = false;
        var second = Task.Run(() => gate.Call(_ => { ran = true; return Task.FromResult(2); }));
        await Task.Delay(50);

        await cts.CancelAsync();
        release.SetResult();
        await first;

        var act = async () => await second;
        (await act.Should().ThrowAsync<FaultException<OrganizationServiceFault>>()).Which.Detail.Message.Should().Be(Revoked);
        ran.Should().BeFalse();
    }
}
