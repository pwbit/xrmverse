import { normaliseId } from '../../parsers/formXml';
import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';

/** `userentityuisettings` (#119, ruling R2): per user and table, the last main form used, as Dataverse stores it. */
const SET = 'userentityuisettingsset';

interface SettingsRow {
  userentityuisettingsid: string;
  lastviewedformxml?: string | null;
}

/** `<MRUForm><Form Type="Main" Id="{guid}" /></MRUForm>`: the main form's id, normalised; undefined when there is none. */
export function lastFormIdOf(xml: string | null | undefined): string | undefined {
  if (!xml) return undefined;
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) return undefined;
  const main = [...doc.getElementsByTagName('Form')].find((f) => (f.getAttribute('Type') ?? 'Main') === 'Main');
  const id = main?.getAttribute('Id');
  return id ? normaliseId(id) : undefined;
}

export const mruFormXml = (formId: string) => `<MRUForm><Form Type="Main" Id="{${normaliseId(formId)}}" /></MRUForm>`;

async function findRow(api: XrmApi, userId: string, table: string): Promise<SettingsRow | undefined> {
  const filter = encodeURIComponent(`_ownerid_value eq ${normaliseId(userId)} and objecttypecode eq ${literal(table)}`);
  const found = await api.get<{ value: SettingsRow[] }>(`${SET}?$select=userentityuisettingsid,lastviewedformxml&$filter=${filter}&$top=1`);
  return found.value[0];
}

/** The id of `userId`'s last main form of `table`, normalised; undefined when none is stored. */
export async function readLastForm(api: XrmApi, userId: string, table: string): Promise<string | undefined> {
  return lastFormIdOf((await findRow(api, userId, table))?.lastviewedformxml);
}

/** Stores `formId` as `userId`'s last main form of `table`: updates the user's row, or creates it. */
export async function writeLastForm(api: XrmApi, userId: string, table: string, formId: string): Promise<void> {
  const row = await findRow(api, userId, table);
  const lastviewedformxml = mruFormXml(formId);
  if (row) {
    if (lastFormIdOf(row.lastviewedformxml) === normaliseId(formId)) return;
    await api.patch(`${SET}(${row.userentityuisettingsid})`, { lastviewedformxml });
  } else {
    await api.post(SET, { objecttypecode: table, lastviewedformxml });
  }
}
