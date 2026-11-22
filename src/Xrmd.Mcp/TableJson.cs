using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Mcp;

/// <summary>
/// The compact table metadata of <c>describe_table</c> and <c>xrmverse://tables/{name}</c>:
/// <c>{logicalName, entitySetName, displayName, primaryIdAttribute, primaryNameAttribute, ownership, isCustom, columns}</c>,
/// each column <c>{name, type, displayName?, required?, readOnly?, maxLength?, targets?, options?}</c>. Helper columns
/// (<c>AttributeOf</c> set, e.g. <c>owneridname</c>) are left out. With the knowledge tables (D3), the table and its columns get
/// their <c>description</c>, a column its <c>synonyms</c> and the table a <c>glossary</c> (<c>[{term, description?}]</c>), each
/// left out when empty.
/// </summary>
internal static class TableJson
{
    public static JsonObject Describe(EntityMetadata m, TableKnowledge? knowledge = null)
    {
        var columns = new JsonArray();
        foreach (var a in (m.Attributes ?? []).Where(a => string.IsNullOrEmpty(a.AttributeOf)).OrderBy(a => a.LogicalName, StringComparer.Ordinal))
        {
            var column = Column(a);
            if (knowledge?.Synonyms.TryGetValue(a.LogicalName, out var synonyms) == true && synonyms.Count > 0)
                column["synonyms"] = new JsonArray(synonyms.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            columns.Add(column);
        }
        var json = new JsonObject
        {
            ["logicalName"] = m.LogicalName,
            ["entitySetName"] = m.EntitySetName,
            ["displayName"] = Label(m.DisplayName),
            ["primaryIdAttribute"] = m.PrimaryIdAttribute,
            ["primaryNameAttribute"] = m.PrimaryNameAttribute,
            ["ownership"] = m.OwnershipType?.ToString(),
            ["isCustom"] = IsCustom(m),
        };
        if (Label(m.Description) is { Length: > 0 } description) json["description"] = description;
        json["columns"] = columns;
        if (knowledge?.Glossary is { Count: > 0 } glossary)
            json["glossary"] = new JsonArray(glossary.Select(t => (JsonNode?)Term(t, withSources: false)).ToArray());
        return json;
    }

    /// <summary>A glossary term: <c>{term, description?, sources?}</c>.</summary>
    public static JsonObject Term(GlossaryTerm term, bool withSources)
    {
        var json = new JsonObject { ["term"] = term.Term };
        if (!string.IsNullOrEmpty(term.Description)) json["description"] = term.Description;
        if (withSources) json["sources"] = new JsonArray(term.Sources.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return json;
    }

    /// <summary>One row of <c>list_tables</c>.</summary>
    public static JsonObject Summary(EntityMetadata m) => new()
    {
        ["logicalName"] = m.LogicalName,
        ["entitySetName"] = m.EntitySetName,
        ["displayName"] = Label(m.DisplayName),
        ["isCustom"] = IsCustom(m),
    };

    /// <summary>The store's definitions carry no <c>IsCustomEntity</c> (reads add it): custom unless a seeded system table.</summary>
    private static bool IsCustom(EntityMetadata m) => m.IsCustomEntity ?? !Xrmd.Metadata.MetadataStore.IsSystemEntity(m);

    private static JsonObject Column(AttributeMetadata a)
    {
        var json = new JsonObject
        {
            ["name"] = a.LogicalName,
            ["type"] = a.AttributeTypeName?.Value is { Length: > 0 } typeName ? TrimType(typeName) : a.AttributeType?.ToString(),
        };
        if (Label(a.DisplayName) is { } label) json["displayName"] = label;
        if (Label(a.Description) is { Length: > 0 } description) json["description"] = description;
        if (a.RequiredLevel?.Value is AttributeRequiredLevel.ApplicationRequired or AttributeRequiredLevel.SystemRequired) json["required"] = true;
        if (a.IsValidForCreate == false && a.IsValidForUpdate == false) json["readOnly"] = true;
        if (a.IsSecured == true) json["secured"] = true; // field security (plan 14)
        switch (a)
        {
            case StringAttributeMetadata s when s.MaxLength is { } max:
                json["maxLength"] = max;
                break;
            case MemoAttributeMetadata memo when memo.MaxLength is { } max:
                json["maxLength"] = max;
                break;
            case LookupAttributeMetadata lookup when lookup.Targets is { Length: > 0 } targets:
                json["targets"] = new JsonArray(targets.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                break;
            case EnumAttributeMetadata choice when choice.OptionSet?.Options is { Count: > 0 } options:
                json["options"] = new JsonArray(options.Select(o => (JsonNode?)new JsonObject
                {
                    ["value"] = o.Value,
                    ["label"] = Label(o.Label),
                }).ToArray());
                break;
        }
        return json;
    }

    /// <summary><c>StringType</c> → <c>String</c>, as <see cref="AttributeTypeCode"/> names them.</summary>
    private static string TrimType(string typeName) =>
        typeName.EndsWith("Type", StringComparison.Ordinal) && typeName.Length > 4 ? typeName[..^4] : typeName;

    private static string? Label(Label? label) =>
        label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels?.FirstOrDefault()?.Label;
}
