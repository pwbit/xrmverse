import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import type { XrmApi } from '../../shell/api/client';
import { withPaging } from '../../parsers/fetchXml';
import { writeView, type ViewDesign, type ViewRow } from './viewDesign';

/** A `savedquery` row as the designer reads it. */
export type ViewRecord = ViewRow & { savedqueryid: string; ismanaged: boolean };

const COLUMNS = 'savedqueryid,name,description,querytype,isdefault,ismanaged,fetchxml,layoutxml,returnedtypecode';
const PREVIEW_ROWS = 10;
const FORMATTED = 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"';

const quote = (s: string) => `'${s.replace(/'/g, "''")}'`;
const escapeXml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** The table's `savedquery` rows, ordered by name. */
export async function listViews(api: XrmApi, table: string): Promise<ViewRecord[]> {
  const query = `savedqueries?$select=${COLUMNS}&$filter=${encodeURIComponent(`returnedtypecode eq ${quote(table)}`)}&$orderby=name`;
  const rows: ViewRecord[] = [];
  for await (const page of api.list<ViewRecord>(query)) rows.push(...page);
  return rows;
}

export function loadView(api: XrmApi, id: string): Promise<ViewRecord> {
  return api.get<ViewRecord>(`savedqueries(${id})?$select=${COLUMNS}`);
}

/**
 * Creates (no `id`) or updates the view without publishing. Returns the view's id: a caller that publishes next keeps it
 * even when the publish fails, so a retry updates the created view instead of creating another.
 */
export async function storeView(api: XrmApi, d: ViewDesign, id?: string): Promise<string> {
  const { fetchxml, layoutxml } = writeView(d);
  if (id) {
    await api.patch(`savedqueries(${id})`, { name: d.name, description: d.description, fetchxml, layoutxml });
    return id;
  }
  const created = await api.post('savedqueries', {
    name: d.name, description: d.description, returnedtypecode: d.table, querytype: d.queryType, isdefault: false, fetchxml, layoutxml,
  });
  if (!created.id) throw new XrmError(0, t('api.error.invalidBody'));
  return created.id;
}

/** Creates (no `id`) or updates the view, then publishes its table. Returns the view's id. */
export async function saveView(api: XrmApi, d: ViewDesign, id?: string): Promise<string> {
  const saved = await storeView(api, d, id);
  await publishTable(api, d.table);
  return saved;
}

export async function publishTable(api: XrmApi, table: string): Promise<void> {
  await api.action('PublishXml', { ParameterXml: `<importexportxml><entities><entity>${escapeXml(table)}</entity></entities></importexportxml>` });
}

export async function deleteView(api: XrmApi, id: string, table: string): Promise<void> {
  await api.delete(`savedqueries(${id})`);
  await publishTable(api, table);
}

/**
 * Makes `id` the table's default Public view: it is set first, then the other default Public views are cleared (each tried
 * even when one fails) and the table is published. The first failure is rethrown at the end, so the clicked view is already
 * default when the caller shows it.
 */
export async function setDefaultView(api: XrmApi, id: string, table: string): Promise<void> {
  await api.patch(`savedqueries(${id})`, { isdefault: true });
  let failure: unknown;
  try {
    const filter = `returnedtypecode eq ${quote(table)} and querytype eq 0 and isdefault eq true and savedqueryid ne ${id}`;
    const others: { savedqueryid: string }[] = [];
    for await (const page of api.list<{ savedqueryid: string }>(`savedqueries?$select=savedqueryid&$filter=${encodeURIComponent(filter)}`)) others.push(...page);
    for (const other of others) {
      try {
        await api.patch(`savedqueries(${other.savedqueryid})`, { isdefault: false });
      } catch (e) {
        failure ??= e;
      }
    }
  } catch (e) {
    failure ??= e;
  }
  try {
    await publishTable(api, table);
  } catch (e) {
    failure ??= e;
  }
  if (failure) throw failure;
}

/** The first 10 rows of the design's FetchXML, with the formatted-value annotations. */
export async function previewView(api: XrmApi, entitySet: string, d: ViewDesign): Promise<Record<string, unknown>[]> {
  const fetch = withPaging(writeView(d).fetchxml, 1, PREVIEW_ROWS);
  const body = await api.get<{ value?: Record<string, unknown>[] }>(`${entitySet}?fetchXml=${encodeURIComponent(fetch)}`, {
    headers: { Prefer: FORMATTED },
  });
  return body.value ?? [];
}
