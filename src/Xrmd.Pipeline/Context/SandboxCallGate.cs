using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.Pipeline;

/// <summary>
/// The calls of sandboxed code (a plug-in, a custom workflow activity, plan 2.14 W17) to its organization service: run
/// synchronously (the code runs on a pool thread without a synchronization context) with <paramref name="token"/>, one at a time on
/// the shared connection, every failure turned into a <see cref="FaultException{OrganizationServiceFault}"/> with its code. Once
/// <paramref name="token"/> is cancelled (the code timed out, the request was cancelled or the code returned) every call fails with
/// <paramref name="revokedMessage"/> without touching the connection, and <see cref="RevokeAsync"/> waits for the call in flight.
/// </summary>
public sealed class SandboxCallGate(CancellationToken token, string revokedMessage)
{
    /// <summary>One call at a time; held for good by <see cref="RevokeAsync"/> once the in-flight call (if any) is over.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Waits (at most <paramref name="timeout"/>) for the call in flight to end, then keeps every later call out. The caller
    /// cancels the token first, so an in-flight call is being cancelled.
    /// </summary>
    /// <returns>False when a call was still in flight after <paramref name="timeout"/>.</returns>
    public Task<bool> RevokeAsync(TimeSpan timeout) => _gate.WaitAsync(timeout);

    /// <summary>Runs <paramref name="operation"/> as described on the class.</summary>
    /// <exception cref="FaultException{OrganizationServiceFault}">The operation failed, or the calls are revoked.</exception>
    public T Call<T>(Func<CancellationToken, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (token.IsCancellationRequested) throw Revoked();
        try
        {
            _gate.Wait(token);
        }
        catch (OperationCanceledException)
        {
            throw Revoked();
        }

        try
        {
            if (token.IsCancellationRequested) throw Revoked();
            return operation(token).GetAwaiter().GetResult();
        }
        catch (XrmdFaultException ex) when (!token.IsCancellationRequested)
        {
            throw ToFault(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && SqlFaults.Translate(ex) is not null)
        {
            // A deadlock or timeout: the code sees the Dataverse SQL error code; the transaction is doomed by the caller.
            var sql = SqlFaults.Translate(ex)!;
            throw ToFault(sql.ErrorCode, sql.Message);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is not FaultException and not OperationCanceledException)
        {
            // Not a Dataverse fault (an unsupported message, a SQL error...): the code still sees an OrganizationServiceFault.
            throw ToFault(ErrorCodes.Unexpected, ex.Message);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is not FaultException)
        {
            // Whatever an interrupted call failed with (cancellation, SQL "operation cancelled"...).
            throw Revoked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private FaultException<OrganizationServiceFault> Revoked() => ToFault(ErrorCodes.UnexpectedPluginException, revokedMessage);

    private static FaultException<OrganizationServiceFault> ToFault(int errorCode, string message) =>
        new(new OrganizationServiceFault { ErrorCode = errorCode, Message = message, Timestamp = DateTime.UtcNow },
            new FaultReason(message));
}
