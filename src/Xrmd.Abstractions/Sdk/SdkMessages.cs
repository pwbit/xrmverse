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
        var result = new Request();
        Fill(request, result, SdkRecords.ToModelMap);
        return result;
    }

    public static OrganizationRequest ToSdk(this Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new OrganizationRequest();
        Fill(request, result, SdkRecords.ToSdkMap);
        return result;
    }

    public static Response ToModel(this OrganizationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new Response();
        Fill(response, result, SdkRecords.ToModelMap);
        return result;
    }

    public static OrganizationResponse ToSdk(this Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new OrganizationResponse();
        Fill(response, result, SdkRecords.ToSdkMap);
        return result;
    }

    // Fill: see SdkRecords. The parameter containers are cleared and refilled in source order.

    internal static void Fill(OrganizationRequest from, Request into, IFillMap map)
    {
        into.Name = from.RequestName;
        into.RequestId = from.RequestId;
        Fill(from.Parameters, into.Parameters, map);
    }

    internal static void Fill(Request from, OrganizationRequest into, IFillMap map)
    {
        into.RequestName = from.Name;
        into.RequestId = from.RequestId;
        Fill(from.Parameters, into.Parameters, map);
    }

    internal static void Fill(OrganizationResponse from, Response into, IFillMap map)
    {
        into.Name = from.ResponseName;
        Fill(from.Results, into.Results, map);
    }

    internal static void Fill(Response from, OrganizationResponse into, IFillMap map)
    {
        into.ResponseName = from.Name;
        Fill(from.Results, into.Results, map);
    }

    internal static void Fill(ParameterCollection from, ParameterBag into, IFillMap map)
    {
        into.Clear();
        foreach (var p in from) into[p.Key] = map.Value(p.Value, null);
    }

    internal static void Fill(ParameterBag from, ParameterCollection into, IFillMap map)
    {
        var old = SdkRecords.Snapshot(into);
        SdkRecords.Refill(into, [.. from.Select(p => KeyValuePair.Create(p.Key, map.Value(p.Value, SdkRecords.Slot(old, p.Key))))]);
    }
}
