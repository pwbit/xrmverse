using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Model.Messages;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>S.2e3b, Task 2: the marks on a model request and on an SDK request are separate tables; only the pipeline copies them.</summary>
public class RequestHintsTests
{
    [Fact]
    public void Model_and_sdk_marks_are_separate()
    {
        var model = new Request { Name = "Delete" };
        var sdk = new OrganizationRequest("Delete");
        Assert.False(CascadeHint.IsMarked(model));
        Assert.Null(AuditActionHint.Of(model));

        CascadeHint.Mark(model);
        AuditActionHint.Mark(model, 7);

        Assert.True(CascadeHint.IsMarked(model));
        Assert.Equal(7, AuditActionHint.Of(model));
        Assert.False(CascadeHint.IsMarked(sdk), "a model mark is not an SDK mark");
        Assert.Null(AuditActionHint.Of(sdk));

        var other = new Request { Name = "Delete" };
        CascadeHint.Mark(sdk);
        AuditActionHint.Mark(sdk, 9);
        Assert.False(CascadeHint.IsMarked(other));
        Assert.Equal(7, AuditActionHint.Of(model));
        Assert.Equal(9, AuditActionHint.Of(sdk));
    }
}
