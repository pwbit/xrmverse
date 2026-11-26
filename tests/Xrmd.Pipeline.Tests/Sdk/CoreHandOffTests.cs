using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Model.Messages;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// <see cref="CoreHandOff"/>: a held window belongs to the run that opened it, like the <c>AtCore</c> entry beside it, so a run that
/// re-enters on the same model request inside an outer window neither replaces nor removes the outer entry.
/// </summary>
public sealed class CoreHandOffTests
{
    [Fact]
    public void Only_the_run_that_opened_a_window_closes_it()
    {
        var request = new Request { Name = "Update" };
        var outer = new OrganizationRequest("Update");
        var inner = new OrganizationRequest("Update");

        CoreHandOff.Hold(request, outer).Should().BeTrue("no window was open");
        CoreHandOff.Hold(request, inner).Should().BeFalse("the outer run holds the request");
        CoreHandOff.TryGetHeld(request, out var held).Should().BeTrue();
        held.Should().BeSameAs(outer);

        CoreHandOff.Release(request, owns: false);
        CoreHandOff.TryGetHeld(request, out held).Should().BeTrue("the inner run did not open the window");
        held.Should().BeSameAs(outer);

        CoreHandOff.Release(request, owns: true);
        CoreHandOff.TryGetHeld(request, out _).Should().BeFalse();
    }
}
