import type { AppModel } from '../../model/app';
import type { FormModel } from '../../model/form';
import { normaliseId, parseFormXml, type SystemFormRow } from '../../parsers/formXml';
import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';
import { collect } from '../../shell/auth/roles';

const MAIN_FORM = 2;
const ACTIVE = 1;

/** A `systemform` row as the parser reads it, with its activation state. */
interface FormRow extends SystemFormRow {
  formactivationstate?: number | null;
}

/**
 * The forms of `table` the form selector lists in `app` (Global Constraints, "Which forms, in which order"): the main forms
 * (`type` 2) that are active (`formactivationstate` 1); only the app's form components (`componenttype` 60) when it has any
 * for this table; a form whose `DisplayConditions` name roles only for users holding one of them (a System Administrator
 * sees all), and, for a user who holds none of the roles any form names, the forms marked `FallbackForm` (Learn, "Set the
 * fallback form for a table"); ordered by `DisplayConditions/@Order`, then the default form, then name. A form that cannot
 * be parsed throws.
 */
export async function loadForms(
  api: XrmApi,
  app: AppModel,
  table: string,
  user: { roleIds: string[]; isSystemAdministrator: boolean },
): Promise<FormModel[]> {
  let rows = await collect<FormRow>(
    api,
    `systemforms?$select=systemformid,name,objecttypecode,type,isdefault,formactivationstate,formxml&$filter=${encodeURIComponent(`objecttypecode eq ${literal(table)}`)}`,
  );
  rows = rows.filter((r) => r.objecttypecode === table);
  const inApp = new Set(app.components.forms.map(normaliseId));
  // The app limits the forms of this table only when it names at least one main form of it (any state); quick views and
  // other types it lists do not.
  const limited = rows.some((r) => r.type === MAIN_FORM && inApp.has(normaliseId(r.systemformid)));
  const held = new Set(user.roleIds.map(normaliseId));
  const forms = rows
    .filter((r) => r.type === MAIN_FORM && (r.formactivationstate ?? ACTIVE) === ACTIVE)
    .filter((r) => !limited || inApp.has(normaliseId(r.systemformid)))
    .map(parseFormXml);
  const assigned = forms.some((f) => f.roles.some((r) => held.has(r)));
  return forms
    .filter((f) => user.isSystemAdministrator || f.roles.length === 0 || f.roles.some((r) => held.has(r)) || (!assigned && f.fallback))
    .sort((a, b) => a.order - b.order || Number(b.isDefault) - Number(a.isDefault) || a.name.localeCompare(b.name));
}

export interface PickedForm {
  form?: FormModel;
  notice?: 'formNotAvailable' | 'noForm';
}

/**
 * The form that opens: the requested one when listed, with a notice when it is not; without one, the user's last form of the
 * table (`lastFormId`) when it is still listed; else the first.
 */
export function pickForm(forms: FormModel[], formId?: string, lastFormId?: string): PickedForm {
  if (forms.length === 0) return { notice: 'noForm' };
  if (!formId) {
    const last = lastFormId && forms.find((f) => normaliseId(f.id) === normaliseId(lastFormId));
    return { form: last || forms[0] };
  }
  const wanted = normaliseId(formId);
  const found = forms.find((f) => normaliseId(f.id) === wanted);
  return found ? { form: found } : { form: forms[0], notice: 'formNotAvailable' };
}
