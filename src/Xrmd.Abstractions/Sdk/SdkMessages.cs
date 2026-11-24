using Microsoft.Xrm.Sdk;
using Xrmd.Model.Messages;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Converters between the SDK's request and response objects and our own model (<c>Xrmd.Model.Messages</c>). Parameter and
/// result values are converted with <see cref="SdkRecords.ValueToModel"/> / <see cref="SdkRecords.ValueToSdk"/>. A typed SDK
/// request (RetrieveMultipleRequest, UpsertRequest, ...) converts to the plain <see cref="Request"/> (name plus parameters,
/// which carry everything), and a <see cref="Request"/> always converts back to the base <see cref="OrganizationRequest"/>.
/// Query parameters pass through unchanged (the query model in messages is not part of this step).
/// </summary>
public static class SdkMessages
{
    public static Request ToModel(this OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new Request { Name = request.RequestName, RequestId = request.RequestId };
        foreach (var p in request.Parameters) result.Parameters[p.Key] = SdkRecords.ValueToModel(p.Value);
        return result;
    }

    public static OrganizationRequest ToSdk(this Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new OrganizationRequest { RequestName = request.Name, RequestId = request.RequestId };
        foreach (var p in request.Parameters) result.Parameters[p.Key] = SdkRecords.ValueToSdk(p.Value);
        return result;
    }

    public static Response ToModel(this OrganizationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new Response { Name = response.ResponseName };
        foreach (var p in response.Results) result.Results[p.Key] = SdkRecords.ValueToModel(p.Value);
        return result;
    }

    public static OrganizationResponse ToSdk(this Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new OrganizationResponse { ResponseName = response.Name };
        foreach (var p in response.Results) result.Results[p.Key] = SdkRecords.ValueToSdk(p.Value);
        return result;
    }
}
