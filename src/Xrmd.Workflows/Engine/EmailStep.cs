using Microsoft.Crm.Sdk.Messages;
using Xrmd.Workflows.Xaml;

namespace Xrmd.Workflows.Engine;

/// <summary>The send email step: the email is created from the step's values, then sent with the SendEmail message of plan 2.11.</summary>
public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Creates the email (as a create step does: <c>isworkflowcreated</c> true, its sender left to SendEmail, which makes it the
    /// run's caller) and sends it with <c>IssueSend</c> true. xrmverse has no email provider, so the email stays Pending Send (state
    /// 1, status 6; plan 2.11, A17). The email is the step's variable for later steps.
    /// </summary>
    private async Task SendEmailAsync(SendEmailStep step, StepReader reader, WorkflowRun run, CancellationToken ct)
    {
        var email = await EntityOfAsync("email", Guid.Empty, step.Values, reader, keepNulls: false, ct);
        var created = await CreateRecordAsync(email, run, ct);
        if (step.CreatedEmailVariable is { } variable)
            run.Variables[variable] = created;
        await ExecuteRequestAsync(new SendEmailRequest { EmailId = created.Id, IssueSend = true }, run, ct);
    }
}
