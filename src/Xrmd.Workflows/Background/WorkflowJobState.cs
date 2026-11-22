using System.Globalization;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Workflows.Background;

/// <summary>
/// The <c>data</c> of a background workflow job (plan 2.14, rulings W9 and W10): the activation it runs, the record it runs on, the
/// step a suspended run resumes at and the variables its steps wrote. Everything a run needs to go on after a host restart.
/// </summary>
/// <param name="ActivationId">The activation (<c>workflow</c> of type 2).</param>
/// <param name="Primary">The record the run is on.</param>
/// <param name="ResumePath">The step a suspended run resumes at (null: the run starts from the beginning).</param>
/// <param name="Variables">The run's variables, each as written by <see cref="ToJson"/>.</param>
public sealed record WorkflowJobState(Guid ActivationId, EntityReference Primary, string? ResumePath,
    IReadOnlyDictionary<string, JsonElement> Variables)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private sealed record Dto(Guid ActivationId, string Entity, Guid Id, string? ResumePath, Dictionary<string, JsonElement>? Variables);

    /// <summary>A new job's state: no step to resume at, no variables.</summary>
    public static WorkflowJobState Start(Guid activationId, EntityReference primary) =>
        new(activationId, primary, null, new Dictionary<string, JsonElement>());

    /// <summary>The JSON stored in <c>asyncoperation.data</c>.</summary>
    public string Serialize() =>
        JsonSerializer.Serialize(new Dto(ActivationId, Primary.LogicalName, Primary.Id, ResumePath,
            Variables.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal)), Options);

    /// <summary>The state written by <see cref="Serialize"/>.</summary>
    /// <exception cref="XrmdFaultException">400 when <paramref name="data"/> is not such a state.</exception>
    public static WorkflowJobState Parse(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(data, Options);
        }
        catch (JsonException ex)
        {
            throw XrmdFaultException.InvalidArgument($"The data of the workflow job cannot be read: {ex.Message}");
        }
        if (dto is null || dto.ActivationId == Guid.Empty || string.IsNullOrEmpty(dto.Entity) || dto.Id == Guid.Empty)
            throw XrmdFaultException.InvalidArgument("The data of the workflow job cannot be read: it names no workflow or no record.");
        return new WorkflowJobState(dto.ActivationId, new EntityReference(dto.Entity, dto.Id), dto.ResumePath,
            dto.Variables ?? new Dictionary<string, JsonElement>());
    }

    // ---- variables ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The variables of a run as JSON, each value tagged with its type. Only values a later step can read back exactly are kept: a
    /// suspended run whose variables hold anything else fails rather than resume with a different value (never widened silently).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 naming the variable that cannot be kept.</exception>
    public static IReadOnlyDictionary<string, JsonElement> ToJson(IEnumerable<KeyValuePair<string, object?>> variables, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
            result[name] = JsonSerializer.SerializeToElement(Encode(value)
                ?? throw XrmdFaultException.InvalidArgument(
                    $"Workflow '{workflowName}' cannot wait: the value of '{name}' (of type {value!.GetType().Name}) cannot be kept until it resumes."),
                Options);
        return result;
    }

    /// <summary>The variables written by <see cref="ToJson"/>, back as the values the engine reads.</summary>
    /// <exception cref="XrmdFaultException">400 when a value cannot be read.</exception>
    public void RestoreInto(IDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        foreach (var (name, json) in Variables)
            variables[name] = Decode(name, json);
    }

    private sealed record Tagged(string Type, JsonElement? Value);

    /// <summary>The tagged form of a value; null when the type is not kept.</summary>
    private static Tagged? Encode(object? value)
    {
        static JsonElement J<T>(T v) => JsonSerializer.SerializeToElement(v, Options);
        return value switch
        {
            null => new Tagged("Null", null),
            string v => new Tagged("String", J(v)),
            bool v => new Tagged("Boolean", J(v)),
            int v => new Tagged("Int32", J(v)),
            long v => new Tagged("Int64", J(v)),
            decimal v => new Tagged("Decimal", J(v.ToString(CultureInfo.InvariantCulture))),
            double v => new Tagged("Double", J(v.ToString("R", CultureInfo.InvariantCulture))),
            Guid v => new Tagged("Guid", J(v)),
            DateTime v => new Tagged("DateTime", J((v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v).ToString("O", CultureInfo.InvariantCulture))),
            OptionSetValue v => new Tagged("OptionSetValue", J(v.Value)),
            Money v => new Tagged("Money", J(v.Value.ToString(CultureInfo.InvariantCulture))),
            EntityReference v => new Tagged("EntityReference", J(new Dictionary<string, string>
            {
                ["logicalName"] = v.LogicalName, ["id"] = v.Id.ToString(),
            })),
            _ => null,
        };
    }

    private static object? Decode(string name, JsonElement json)
    {
        try
        {
            var tagged = json.Deserialize<Tagged>(Options) ?? throw new JsonException("no value");
            var v = tagged.Value;
            return tagged.Type switch
            {
                "Null" => null,
                "String" => v!.Value.GetString(),
                "Boolean" => v!.Value.GetBoolean(),
                "Int32" => v!.Value.GetInt32(),
                "Int64" => v!.Value.GetInt64(),
                "Decimal" => decimal.Parse(v!.Value.GetString()!, CultureInfo.InvariantCulture),
                "Double" => double.Parse(v!.Value.GetString()!, CultureInfo.InvariantCulture),
                "Guid" => v!.Value.GetGuid(),
                "DateTime" => DateTime.Parse(v!.Value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "OptionSetValue" => new OptionSetValue(v!.Value.GetInt32()),
                "Money" => new Money(decimal.Parse(v!.Value.GetString()!, CultureInfo.InvariantCulture)),
                "EntityReference" => new EntityReference(v!.Value.GetProperty("logicalName").GetString(), Guid.Parse(v.Value.GetProperty("id").GetString()!)),
                var other => throw new JsonException($"unknown type '{other}'"),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw XrmdFaultException.InvalidArgument($"The data of the workflow job cannot be read: the variable '{name}' ({ex.Message}).");
        }
    }
}
