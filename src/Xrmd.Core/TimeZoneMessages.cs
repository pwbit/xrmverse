using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// #131, ruling Z5: <c>LocalTimeFromUtcTime</c> (<c>TimeZoneCode</c>, <c>UtcTime</c> → <c>LocalTime</c>) and
/// <c>UtcTimeFromLocalTime</c> (<c>TimeZoneCode</c>, <c>LocalTime</c> → <c>UtcTime</c>), computed with <see cref="TimeZoneCatalog"/>
/// outside the pipeline (Dataverse marks neither as an event). The caller must be an organization member (the service checks it).
/// </summary>
public static class TimeZoneMessages
{
    public const string LocalTimeFromUtcTime = "LocalTimeFromUtcTime";
    public const string UtcTimeFromLocalTime = "UtcTimeFromLocalTime";

    /// <summary>True for one of the two messages.</summary>
    public static bool IsTimeZoneMessage(string? name) => name is LocalTimeFromUtcTime or UtcTimeFromLocalTime;

    /// <summary>Runs <paramref name="request"/>; the local time is returned with an unspecified kind, the UTC time with kind UTC.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing or invalid parameter, or a code that is not a time zone code.</exception>
    public static OrganizationResponse Execute(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = request.Parameters.TryGetValue("TimeZoneCode", out var value) && value is int number
            ? number
            : throw Missing(request.RequestName, "TimeZoneCode");
        if (request.RequestName == LocalTimeFromUtcTime)
        {
            var utc = Time(request, "UtcTime");
            return new LocalTimeFromUtcTimeResponse
            {
                ResponseName = LocalTimeFromUtcTime, Results = { ["LocalTime"] = TimeZoneCatalog.LocalFromUtc(code, utc) },
            };
        }
        var local = Time(request, "LocalTime");
        return new UtcTimeFromLocalTimeResponse
        {
            ResponseName = UtcTimeFromLocalTime, Results = { ["UtcTime"] = TimeZoneCatalog.UtcFromLocal(code, local) },
        };
    }

    private static DateTime Time(OrganizationRequest request, string name) =>
        request.Parameters.TryGetValue(name, out var value) && value is DateTime time ? time : throw Missing(request.RequestName, name);

    private static XrmdFaultException Missing(string request, string parameter) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{parameter}' of request '{request}' is missing or invalid.");
}
