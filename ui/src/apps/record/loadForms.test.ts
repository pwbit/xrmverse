import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AppModel } from '../../model/app';
import type { FormModel } from '../../model/form';
import { XrmApi } from '../../shell/api/client';
import { server } from '../../test/server';
import { loadForms, pickForm } from './loadForms';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const F1 = '10000000-0000-0000-0000-000000000001';
const F2 = '10000000-0000-0000-0000-000000000002';
const F3 = '10000000-0000-0000-0000-000000000003';
const ROLE = '66666666-0000-0000-0000-000000000001';

const formXml = (display = '') => `<form>${display}<tabs><tab name="t"><columns><column width="100%"><sections><section name="s"><rows/></section></sections></column></columns></tab></tabs></form>`;

interface Row {
  id: string;
  name: string;
  table?: string;
  type?: number;
  state?: number;
  isdefault?: boolean;
  display?: string;
}
const row = (r: Row) => ({
  systemformid: r.id,
  name: r.name,
  objecttypecode: r.table ?? 'account',
  type: r.type ?? 2,
  isdefault: r.isdefault ?? false,
  formactivationstate: r.state ?? 1,
  formxml: formXml(r.display),
});

function mockForms(rows: Row[]) {
  server.use(http.get('*/api/data/v9.2/systemforms', () => HttpResponse.json({ value: rows.map(row) })));
}

const app = (forms: string[] = []): AppModel => ({
  id: 'a1',
  uniqueName: 'app',
  name: 'App',
  clientType: 4,
  components: { entities: [], views: [], forms },
});
const user = { roleIds: [] as string[], isSystemAdministrator: false };
const names = (forms: FormModel[]) => forms.map((f) => f.name);

describe('loadForms', () => {
  it('forms_limited_to_app_components_when_present', async () => {
    mockForms([{ id: F1, name: 'One' }, { id: F2, name: 'Two' }]);
    // The id's case and braces differ from the row's: they are compared normalised.
    const forms = await loadForms(api(), app([`{${F2.toUpperCase()}}`]), 'account', user);
    expect(names(forms)).toEqual(['Two']);
  });

  it('all_main_forms_when_app_lists_none', async () => {
    mockForms([{ id: F1, name: 'One' }, { id: F2, name: 'Two' }, { id: F3, name: 'Other table', table: 'contact' }]);
    // A form component of another table does not limit this table's forms.
    expect(names(await loadForms(api(), app([F3]), 'account', user))).toEqual(['One', 'Two']);
    expect(names(await loadForms(api(), app(), 'account', user))).toEqual(['One', 'Two']);
  });

  it('quick_view_in_app_does_not_limit_main_forms', async () => {
    mockForms([{ id: F1, name: 'One' }, { id: F2, name: 'Two' }, { id: F3, name: 'Quick', type: 6 }]);
    expect(names(await loadForms(api(), app([F3]), 'account', user))).toEqual(['One', 'Two']);
  });

  it('inactive_and_non_main_forms_excluded', async () => {
    mockForms([
      { id: F1, name: 'Main' },
      { id: F2, name: 'Inactive', state: 0 },
      { id: F3, name: 'Quick view', type: 6 },
    ]);
    expect(names(await loadForms(api(), app(), 'account', user))).toEqual(['Main']);
  });

  it('form_security_hides_form_without_user_role', async () => {
    mockForms([
      { id: F1, name: 'Open' },
      { id: F2, name: 'Everyone', display: '<DisplayConditions><Everyone/></DisplayConditions>' },
      { id: F3, name: 'Restricted', display: `<DisplayConditions><Role Id="{${ROLE.toUpperCase()}}"/></DisplayConditions>` },
    ]);
    expect(names(await loadForms(api(), app(), 'account', user))).toEqual(['Everyone', 'Open']);
    expect(names(await loadForms(api(), app(), 'account', { ...user, roleIds: [ROLE] }))).toEqual(['Everyone', 'Open', 'Restricted']);
  });

  it('fallback_form_shown_only_to_users_without_an_assigned_form', async () => {
    const OTHER = '66666666-0000-0000-0000-000000000002';
    mockForms([
      { id: F1, name: 'Sales', display: `<DisplayConditions Order="1"><Role Id="{${ROLE}}"/></DisplayConditions>` },
      { id: F2, name: 'Contingency', display: `<DisplayConditions Order="2" FallbackForm="true"><Role Id="{${OTHER}}"/></DisplayConditions>` },
    ]);
    // No role of the user has a form: the fallback form.
    expect(names(await loadForms(api(), app(), 'account', user))).toEqual(['Contingency']);
    // A role with a form of its own: that form only.
    expect(names(await loadForms(api(), app(), 'account', { ...user, roleIds: [ROLE] }))).toEqual(['Sales']);
    expect(names(await loadForms(api(), app(), 'account', { ...user, roleIds: [OTHER] }))).toEqual(['Contingency']);
  });

  it('system_administrator_sees_all_forms', async () => {
    mockForms([
      { id: F1, name: 'Open' },
      { id: F3, name: 'Restricted', display: `<DisplayConditions><Role Id="{${ROLE}}"/></DisplayConditions>` },
    ]);
    const forms = await loadForms(api(), app(), 'account', { roleIds: [], isSystemAdministrator: true });
    expect(names(forms).sort()).toEqual(['Open', 'Restricted']);
  });

  it('order_by_display_conditions_then_default_then_name', async () => {
    const order = (n: number) => `<DisplayConditions Order="${n}"><Everyone/></DisplayConditions>`;
    mockForms([
      { id: F1, name: 'Zed' },
      { id: F2, name: 'Beta' },
      { id: F3, name: 'Late', display: order(5) },
      { id: '10000000-0000-0000-0000-000000000004', name: 'Early', display: order(1) },
      { id: '10000000-0000-0000-0000-000000000005', name: 'Default', isdefault: true },
    ]);
    expect(names(await loadForms(api(), app(), 'account', user))).toEqual(['Early', 'Late', 'Default', 'Beta', 'Zed']);
  });
});

describe('pickForm', () => {
  const form = (id: string, name: string) => ({ id, name }) as FormModel;
  const forms = [form(F1, 'One'), form(F2, 'Two')];

  it('pick_form_falls_back_with_notice', () => {
    expect(pickForm(forms, F2)).toEqual({ form: forms[1] });
    expect(pickForm(forms, `{${F2.toUpperCase()}}`)).toEqual({ form: forms[1] });
    expect(pickForm(forms)).toEqual({ form: forms[0] });
    expect(pickForm(forms, F3)).toEqual({ form: forms[0], notice: 'formNotAvailable' });
  });

  it('pick_form_url_then_last_form_then_first', () => {
    expect(pickForm(forms, undefined, F2)).toEqual({ form: forms[1] });
    expect(pickForm(forms, undefined, `{${F2.toUpperCase()}}`)).toEqual({ form: forms[1] });
    // The URL wins over the last form.
    expect(pickForm(forms, F1, F2)).toEqual({ form: forms[0] });
    // A last form no longer listed is ignored, without a notice.
    expect(pickForm(forms, undefined, F3)).toEqual({ form: forms[0] });
  });

  it('pick_form_no_form_notice', () => {
    expect(pickForm([], F1)).toEqual({ notice: 'noForm' });
    expect(pickForm([])).toEqual({ notice: 'noForm' });
  });
});
