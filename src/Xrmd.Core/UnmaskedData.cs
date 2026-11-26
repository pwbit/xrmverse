using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Core;

/// <summary>
/// The <c>UnMaskedData</c> optional parameter of Retrieve and RetrieveMultiple (a request parameter on the SDK and SOAP, a
/// query option on the Web API). xrmverse has no masking (ruling F5): asking for unmasked data is a 400 rather than a
/// request that silently means nothing; false, or leaving it out, is accepted. The core Retrieve and RetrieveMultiple refuse
/// it (<c>CoreOperations</c>), so every path through the pipeline is covered, the plug-in service included; the
/// <c>OrganizationService</c> and the Web API refuse it earlier, before any plug-in runs.
/// </summary>
public static class UnmaskedData
{
    /// <summary>The parameter's name (the Web API query option has the same name, matched without case).</summary>
    public const string Parameter = "UnMaskedData";

    /// <exception cref="XrmdFaultException">400 when the request asks for unmasked data.</exception>
    public static void Refuse(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parameters.TryGetValue(Parameter, out var value)) Refuse(value);
    }

    /// <summary>The model form of <see cref="Refuse(OrganizationRequest)"/> (S.2e3b: the core's Retrieve and RetrieveMultiple on the model).</summary>
    /// <exception cref="XrmdFaultException">400 when the request asks for unmasked data.</exception>
    public static void Refuse(Xrmd.Model.Messages.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parameters.TryGetValue(Parameter, out var value)) Refuse(value);
    }

    /// <summary>The Web API form: every query option named <see cref="Parameter"/> (without case) is checked.</summary>
    /// <exception cref="XrmdFaultException">400 when one asks for unmasked data.</exception>
    public static void RefuseQueryOptions(IEnumerable<KeyValuePair<string, string>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (name, value) in options)
            if (string.Equals(name, Parameter, StringComparison.OrdinalIgnoreCase)) Refuse(value);
    }

    /// <exception cref="XrmdFaultException">400 when <paramref name="value"/> is true (a bool or the text "true").</exception>
    public static void Refuse(object? value)
    {
        var requested = value switch
        {
            bool b => b,
            string text => bool.TryParse(text.Trim(), out var parsed) && parsed,
            _ => false,
        };
        if (requested)
            throw XrmdFaultException.InvalidArgument(
                $"{Parameter} is not supported: xrmverse never masks column values, so there is no masked data to reveal. Leave the parameter out.");
    }
}
