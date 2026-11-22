import type { ViewModel } from '../model/view';
import { parseFetch } from '../parsers/fetchXml';
import { parseLayout } from '../parsers/layoutXml';
import type { XrmApi } from '../shell/api/client';
import { XrmError } from '../shell/api/errors';

/** The quick find query type (`savedquery.querytype` 4); 0 is a public (main application) view. */
export const QUICK_FIND_QUERY_TYPE = 4;
const PUBLIC_QUERY_TYPE = 0;
/** The associated view (`savedquery.querytype` 2): what a related-tables item lists (#120). */
export const ASSOCIATED_QUERY_TYPE = 2;
const ENGLISH = 1033;

export interface TableViews {
  /** Public views (`savedquery`, querytype 0), sorted by name. */
  system: ViewModel[];
  /** The user's personal views (`userquery`, querytype 0), sorted by name. */
  personal: ViewModel[];
  /** The table's quick find view (`savedquery`, querytype 4). */
  quickFind?: ViewModel;
  /** The table's associated view (`savedquery`, querytype 2): the default one, else the first by name. */
  associated?: ViewModel;
}

interface QueryRow {
  savedqueryid?: string;
  userqueryid?: string;
  name: string;
  querytype: number;
  isdefault?: boolean | null;
  fetchxml: string;
  layoutxml?: string | null;
  _ownerid_value?: string | null;
}

export const quote = (value: string) => value.replace(/'/g, "''");
const byName = (a: ViewModel, b: ViewModel) => a.name.localeCompare(b.name);

function toView(row: QueryRow, entity: string): ViewModel {
  return {
    id: (row.savedqueryid ?? row.userqueryid ?? '').toLowerCase(),
    name: row.name,
    entity,
    queryType: row.querytype,
    isDefault: row.isdefault === true,
    fetchXml: row.fetchxml,
    columns: row.layoutxml ? parseLayout(row.layoutxml) : [],
    order: parseFetch(row.fetchxml).order,
    ...(row.layoutxml ? { layoutXml: row.layoutxml } : {}),
    ...(row._ownerid_value ? { ownerId: row._ownerid_value.toLowerCase() } : {}),
  };
}

/** The active public, quick find and personal views of a table. A user who may not read `userquery` has no personal views. */
export async function loadViews(client: XrmApi, entity: string): Promise<TableViews> {
  const etn = quote(entity);
  const select = '$select=name,fetchxml,layoutxml,querytype';
  const [saved, personal] = await Promise.all([
    client.get<{ value: QueryRow[] }>(
      `savedqueries?$filter=returnedtypecode eq '${etn}' and statecode eq 0 and (querytype eq ${PUBLIC_QUERY_TYPE} or querytype eq ${QUICK_FIND_QUERY_TYPE} or querytype eq ${ASSOCIATED_QUERY_TYPE})` +
        `&${select},savedqueryid,isdefault`,
    ),
    client
      .get<{ value: QueryRow[] }>(
        `userqueries?$filter=returnedtypecode eq '${etn}' and statecode eq 0 and querytype eq ${PUBLIC_QUERY_TYPE}&${select},userqueryid,_ownerid_value`,
      )
      .catch((e: unknown) => {
        // 403: the user may not read userquery, so has no personal views (Dataverse lists only the views the user can
        // read). The public views still open; any other failure is an error.
        if (e instanceof XrmError && e.status === 403) return { value: [] };
        throw e;
      }),
  ]);
  const views = saved.value.map((r) => toView(r, entity));
  const quickFinds = views.filter((v) => v.queryType === QUICK_FIND_QUERY_TYPE).sort(byName);
  const associated = views.filter((v) => v.queryType === ASSOCIATED_QUERY_TYPE).sort(byName);
  return {
    system: views.filter((v) => v.queryType === PUBLIC_QUERY_TYPE).sort(byName),
    personal: personal.value.map((r) => toView(r, entity)).sort(byName),
    quickFind: quickFinds.find((v) => v.isDefault) ?? quickFinds[0],
    associated: associated.find((v) => v.isDefault) ?? associated[0],
  };
}

/** What the grid needs of a table: its entity set, its primary key and its column labels in the user's language. */
export interface TableInfo {
  logicalName: string;
  entitySetName: string;
  primaryId: string;
  /** The table's SchemaName (its privilege names are `prv{Verb}{SchemaName}`); undefined when the server gives none. */
  schemaName?: string;
  /** `PrimaryNameAttribute`: what a lookup search of the table searches and shows; undefined when the server gives none. */
  primaryName?: string;
  /** The table's display name in the user language (else 1033, else the user label); undefined when it has none. */
  displayName?: string;
  /** Display label by attribute logical name; an attribute without a label is left out. */
  labels: Record<string, string>;
  /** `AttributeType` by attribute logical name (`Money`, `DateTime`…), when the server gives it: how a view aligns the column. */
  types?: Record<string, string>;
}

interface Label {
  Label: string;
  LanguageCode: number;
}

interface AttributeRow {
  LogicalName: string;
  AttributeType?: string | null;
  DisplayName?: { LocalizedLabels?: Label[]; UserLocalizedLabel?: Label | null } | null;
}

/** The label in `lcid`, else 1033, else the user label. */
export function displayLabel(name: AttributeRow['DisplayName'], lcid: number): string | undefined {
  const labels = name?.LocalizedLabels ?? [];
  const label =
    labels.find((l) => l.LanguageCode === lcid) ?? labels.find((l) => l.LanguageCode === ENGLISH) ?? name?.UserLocalizedLabel ?? undefined;
  return label?.Label || undefined;
}

export async function loadTable(client: XrmApi, entity: string, lcid: number): Promise<TableInfo> {
  const key = `EntityDefinitions(LogicalName='${quote(entity)}')`;
  const [definition, attributes] = await Promise.all([
    client.get<{
      LogicalName: string;
      EntitySetName: string;
      PrimaryIdAttribute: string;
      SchemaName?: string | null;
      PrimaryNameAttribute?: string | null;
      DisplayName?: AttributeRow['DisplayName'];
    }>(`${key}?$select=LogicalName,EntitySetName,PrimaryIdAttribute,SchemaName,PrimaryNameAttribute,DisplayName`),
    client.get<{ value: AttributeRow[] }>(`${key}/Attributes?$select=LogicalName,DisplayName,AttributeType`),
  ]);
  const labels: Record<string, string> = {};
  const types: Record<string, string> = {};
  for (const a of attributes.value) {
    const label = displayLabel(a.DisplayName, lcid);
    if (label) labels[a.LogicalName] = label;
    if (a.AttributeType) types[a.LogicalName] = a.AttributeType;
  }
  return {
    logicalName: definition.LogicalName,
    entitySetName: definition.EntitySetName,
    primaryId: definition.PrimaryIdAttribute,
    schemaName: definition.SchemaName || undefined,
    ...(definition.PrimaryNameAttribute ? { primaryName: definition.PrimaryNameAttribute } : {}),
    ...(displayLabel(definition.DisplayName, lcid) ? { displayName: displayLabel(definition.DisplayName, lcid) } : {}),
    labels,
    types,
  };
}
