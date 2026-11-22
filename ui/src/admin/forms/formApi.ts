import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import type { XrmApi } from '../../shell/api/client';
import { publishTable } from '../views/viewApi';
import { writeForm, type FormDesign, type FormRow } from './formDesign';

/** A `systemform` row as the designer reads it. */
export type FormRecord = FormRow & { systemformid: string; ismanaged: boolean };

const LIST_COLUMNS = 'systemformid,name,description,type,isdefault,formactivationstate,ismanaged,objecttypecode';
const COLUMNS = `${LIST_COLUMNS},formxml`;

const quote = (s: string) => `'${s.replace(/'/g, "''")}'`;

/** `If-Match: *` makes a PATCH an update only: a form deleted meanwhile answers 404 instead of being upserted as an orphan. */
const UPDATE_ONLY = { 'If-Match': '*' };

/** The table's `systemform` rows, ordered by type then name. `formxml` is not selected: the list does not need it. */
export async function listForms(api: XrmApi, table: string): Promise<FormRecord[]> {
  const query = `systemforms?$select=${LIST_COLUMNS}&$filter=${encodeURIComponent(`objecttypecode eq ${quote(table)}`)}&$orderby=type,name`;
  const rows: FormRecord[] = [];
  for await (const page of api.list<FormRecord>(query)) rows.push(...page);
  return rows;
}

export function loadForm(api: XrmApi, id: string): Promise<FormRecord> {
  return api.get<FormRecord>(`systemforms(${id})?$select=${COLUMNS}`);
}

/**
 * Creates (no `id`) or updates the form without publishing. Returns the form's id: a caller that publishes next keeps it
 * even when the publish fails, so a retry updates the created form instead of creating another. A create posts the
 * design's type: 2 for a new form (`newForm`), the source's type for a Save as copy.
 */
export async function storeForm(api: XrmApi, d: FormDesign, id?: string): Promise<string> {
  const formxml = writeForm(d);
  if (id) {
    await api.patch(`systemforms(${id})`, { name: d.name, description: d.description, formxml }, UPDATE_ONLY);
    return id;
  }
  const created = await api.post('systemforms', {
    name: d.name, description: d.description, formxml, objecttypecode: d.table, type: d.type, isdefault: false, formactivationstate: 1,
  });
  if (!created.id) throw new XrmError(0, t('api.error.invalidBody'));
  return created.id;
}

export async function deleteForm(api: XrmApi, id: string, table: string): Promise<void> {
  await api.delete(`systemforms(${id})`);
  await publishTable(api, table);
}

/**
 * Makes `id` the table's default Main form: it is set first, then the other default Main forms are cleared (each tried even
 * when one fails) and the table is published. The first failure is rethrown at the end, so the clicked form is already
 * default when the caller shows it.
 */
export async function setDefaultForm(api: XrmApi, id: string, table: string): Promise<void> {
  await api.patch(`systemforms(${id})`, { isdefault: true }, UPDATE_ONLY);
  let failure: unknown;
  try {
    const filter = `objecttypecode eq ${quote(table)} and type eq 2 and isdefault eq true and systemformid ne ${id}`;
    const others: { systemformid: string }[] = [];
    for await (const page of api.list<{ systemformid: string }>(`systemforms?$select=systemformid&$filter=${encodeURIComponent(filter)}`)) others.push(...page);
    for (const other of others) {
      try {
        await api.patch(`systemforms(${other.systemformid})`, { isdefault: false }, UPDATE_ONLY);
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

/** Activates or deactivates the form (`formactivationstate` 1 or 0), then publishes its table. */
export async function setFormActive(api: XrmApi, id: string, table: string, active: boolean): Promise<void> {
  await api.patch(`systemforms(${id})`, { formactivationstate: active ? 1 : 0 }, UPDATE_ONLY);
  await publishTable(api, table);
}
