namespace Xrmd.Core.Elastic;

/// <summary>
/// One failed target of a bulk message on an elastic table (plan 3.4, ruling E5), as serialized (System.Text.Json, these property
/// names) into the fault's <c>ErrorDetails["Plugin.BulkApiErrorDetails"]</c>.
/// </summary>
/// <param name="RequestIndex">The position of the target in the request's <c>Targets</c>, from 0.</param>
/// <param name="Id">The target's id, empty when it had none (a Create without id).</param>
/// <param name="StatusCode">The HTTP status of the target's failure.</param>
public sealed record BulkApiErrorDetail(int RequestIndex, string Id, int StatusCode);
