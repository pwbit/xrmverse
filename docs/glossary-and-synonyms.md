# Glossary and synonyms

[← xrmverse documentation](../README.md#documentation)

## Introduction

In Dataverse, makers can describe their data for AI agents. A **knowledge source** lists the tables an agent may use.
**Synonyms** say what a column means in business words. **Glossary terms** explain domain words and acronyms. The Microsoft
documentation describes the five tables in the Learn references for
[dvtablesearch](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/dvtablesearch),
[dvtablesearchentity](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/dvtablesearchentity),
[dvtablesearchattribute](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/dvtablesearchattribute),
[copilotsynonyms](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/copilotsynonyms) and
[copilotglossaryterm](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/copilotglossaryterm).

xrmverse keeps the same tables, so the same SDK and Web API calls work. It also gives the records to agents that use the
built-in [MCP server](mcp.md). The server itself does nothing else with them: no search, no ranking, no query rewriting.

### Compared with Dataverse

| Area | xrmverse | Compared with Dataverse |
|---|---|---|
| Tables | The five Dataverse tables with their names and lookups | Same, with some columns left out (see [Known limits](#known-limits)) |
| Who uses them | Agents through the built-in [MCP server](mcp.md#knowledge-for-agents) | **Differs**: in Dataverse, agents built in Copilot Studio with Dataverse search on |
| Search, ranking, embeddings | Not offered | **Narrower**: Dataverse search is not in xrmverse |
| Solutions | Not solution components; an import logs a warning | **Narrower** |
| Object type codes | 9960 to 9964, assigned by xrmverse | **Differs**: use logical names |

### Tasks at a glance

| Task | How | Chapter |
|---|---|---|
| Describe a knowledge source, its tables and columns | Create the rows with the Web API or SDK | [Create rows with the Web API](#create-rows-with-the-web-api) |
| Let an agent read them | MCP `describe_table`, `list_glossary_terms` | [What MCP returns](#what-mcp-returns) |
| Move them to another organization | Data APIs (not solutions) | [Solutions](#solutions) |

## The tables

Every organization has five tables. All are user-owned, so the usual [privileges, depth and sharing](security.md) apply.

| Table | Entity set | Holds | Columns |
|---|---|---|---|
| `dvtablesearch` | `dvtablesearchs` | A knowledge source | `name` (required), `description`, `searchtype`, `source` |
| `dvtablesearchentity` | `dvtablesearchentities` | A table in a source | `name` (required), `dvtablesearch` (lookup), `entitylogicalname` |
| `dvtablesearchattribute` | `dvtablesearchattributes` | A column in a source table | `name` (required), `dvtablesearchentity` (lookup), `attributelogicalname`, `issearchable`, `isretrievable` |
| `copilotsynonyms` | `copilotsynonyms` | The synonyms of one column | `name`, `columnlogicalname` (required), `synonyms`, `description`, `skillentity` (lookup to `dvtablesearchentity`) |
| `copilotglossaryterm` | `copilotglossaryterms` | A glossary term | `name`, `term` (required), `description`, `skill` (lookup to `dvtablesearch`) |

```
dvtablesearch (knowledge source)
  ├─< dvtablesearchentity (a table in the source)
  │     ├─< dvtablesearchattribute (a column)
  │     └─< copilotsynonyms (synonyms of a column)
  └─< copilotglossaryterm (a term)
```

- **Names.** The table names are Dataverse's technical identifiers and stay as they are. The features of xrmverse are called
  glossary and synonyms.
- **Delete cascades.** Deleting a source deletes its tables, columns, synonyms and terms. Deleting a table in a source deletes
  its columns and synonyms. Everything else is `NoCascade`. This is our reading, not verified on a real Dataverse.
- **Object type codes** 9960 to 9964 are assigned by xrmverse, because Learn does not publish them. On a real environment they
  differ. Use logical names, not codes.
- **Required columns.** The server does not enforce the "application required" level on Create, as in Dataverse. A row
  without `name` on `dvtablesearch` is accepted by the Web API.
- **Free text.** `entitylogicalname`, `attributelogicalname` and `columnlogicalname` are stored as given. They are not checked
  against the metadata, as in Dataverse.
- **`searchtype`** is stored only: 0 is DataverseSearch and 1 is FederatedSearch. xrmverse runs neither.

## Create rows with the Web API

A source, one of its tables, the synonyms of a column and a glossary term:

```http
POST /api/data/v9.2/dvtablesearchs
{ "name": "Sales", "description": "Tables for the sales agent" }

POST /api/data/v9.2/dvtablesearchentities
{ "name": "Accounts", "entitylogicalname": "account", "dvtablesearch@odata.bind": "/dvtablesearchs(<source id>)" }

POST /api/data/v9.2/copilotsynonyms
{ "columnlogicalname": "revenue", "synonyms": "turnover, sales; income",
  "skillentity@odata.bind": "/dvtablesearchentities(<table row id>)" }

POST /api/data/v9.2/copilotglossaryterms
{ "term": "VP", "description": "Vice president, the executive sponsor of a key account",
  "skill@odata.bind": "/dvtablesearchs(<source id>)" }
```

The SDK works the same way: create the rows with `Create` and set the lookups with `EntityReference`.

## What MCP returns

Agents read the rows through the [MCP server](mcp.md#knowledge-for-agents), as the calling user.

- `describe_table` and the resource `xrmverse://tables/{name}` add the table `description`, the `description` and `synonyms` of
  each column, and the `glossary` of the table. Each field is left out when it is empty.
- `list_glossary_terms` lists the terms, optionally for one table or one source.

Rules:

- Only active rows count (`statecode` 0).
- A source counts only when it is active and the caller can read it. A term is listed only if its source is.
- The synonyms string is split on `,` and `;`, trimmed, and empty parts are dropped. Learn does not fix a separator, so this is
  our choice. Synonyms of the same column from several sources are merged.
- In `describe_table`, synonyms rows that name a column the table does not have are skipped. `list_glossary_terms` with `table` does not check that the table exists: an unknown name just matches no source.
- A caller without the read privilege on a knowledge table gets no synonyms or terms, not an error.
- Security applies as for any read: depth, sharing and field security.

## Solutions

The five tables are solution components in Dataverse, but their export format is not in Learn. xrmverse does not import or
export them as components. A root component of a type xrmverse does not know is not applied. The import job logs
`Root component of type {type} ({id}) is not applied: xrmverse does not support the component type.` and goes on. Move the rows
with the data APIs meanwhile.

## Known limits

None of these is silent: each is refused with an explicit error or stated here.

- **Dataverse search is not supported.** The `searchquery`, `searchsuggest`, `searchautocomplete`, `searchstatus` and
  `searchstatistics` messages and the `/api/search/v2.0/*` endpoints are not implemented.
- **No vector search, no embeddings, no semantic ranking.** `searchtype` and the `issearchable` and `isretrievable` columns are
  stored and do nothing.
- **Columns left out on purpose.** They point to tables xrmverse does not have, or describe behaviour it does not run. A write
  that names one fails with the usual unknown-column error.
  - `dvtablesearch`: `appid`, `appmoduleid`, `appmoduleuniquename`, `apptype`, `connectionreference`, `knowledgeconfig`,
    `knowledgesourceconsumerid`, `knowledgesourceprofileid`, `m365appmoduleid`, `m365appmoduleidsecondary`.
  - `dvtablesearchentity`: `entity`, `unstructuredfilesearchentityid`.
  - `dvtablesearchattribute`: `attribute`, `isattributeenabledforvectorsearch`.
- **No agent builder.** The tools that use these records in Dataverse cannot connect to an xrmverse organization. Only MCP
  clients read them.
- **Not solution components** (see above).
- **No paging.** `list_glossary_terms` returns every matching term in one answer.
- **Not verified against Dataverse.** Cascades, required levels and privilege names follow our reading of Learn.

## See also

- [MCP](mcp.md)
- [Security](security.md)
- [xrmverse compared with Dataverse](dataverse-comparison.md)
