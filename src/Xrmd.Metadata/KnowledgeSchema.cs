namespace Xrmd.Metadata;

/// <summary>
/// The knowledge tables (D3): the seeded user-owned tables <c>dvtablesearch</c> (a knowledge source), <c>dvtablesearchentity</c>,
/// <c>dvtablesearchattribute</c>, <c>copilotsynonyms</c> (the synonyms of a column) and <c>copilotglossaryterm</c> (a glossary
/// term), defined in <c>fixtures/seed</c>.
/// </summary>
/// <remarks>The object type codes are xrmverse-assigned (Learn does not publish them), see <c>fixtures/README.md</c>.</remarks>
public static class KnowledgeSchema
{
    /// <summary>The logical names of the five tables.</summary>
    public const string Source = "dvtablesearch", SourceEntity = "dvtablesearchentity", SourceAttribute = "dvtablesearchattribute",
        Synonyms = "copilotsynonyms", GlossaryTerm = "copilotglossaryterm";

    /// <summary>The columns the tables share or the MCP features read.</summary>
    public const string SourceLookup = "dvtablesearch", EntityLogicalName = "entitylogicalname", SkillEntity = "skillentity",
        ColumnLogicalName = "columnlogicalname", SynonymsText = "synonyms", Skill = "skill", Term = "term",
        Description = "description", Name = "name";
}
