import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import type { Relationships } from '../tables/tableApi';
import {
  listCustomControls, listPublicViews, listQuickViewForms, listWebResources, sameId, subgridSources, WEB_RESOURCE_TYPES,
} from './formControlsApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';

interface Seen { path: string; search: URLSearchParams }

function capture(answer: (s: Seen) => Response): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.get(`*${ROOT}/*`, ({ request }) => {
      const url = new URL(request.url);
      const s = { path: decodeURIComponent(url.pathname).slice(ROOT.length + 1), search: url.searchParams };
      seen.push(s);
      return answer(s);
    }),
  );
  return seen;
}

const rels: Relationships = {
  oneToMany: [
    { schemaName: 'contact_customer_accounts', referencingEntity: 'contact', referencingAttribute: 'parentcustomerid', referencedEntity: 'account' },
    { schemaName: 'account_primary_contact', referencingEntity: 'account', referencingAttribute: 'primarycontactid', referencedEntity: 'contact' },
    { schemaName: 'account_parent_account', referencingEntity: 'account', referencingAttribute: 'parentaccountid', referencedEntity: 'account' },
  ],
  manyToMany: [
    { schemaName: 'new_account_tag', entity1: 'new_tag', entity2: 'account' },
    { schemaName: 'new_account_peer', entity1: 'account', entity2: 'account' },
    { schemaName: 'new_contact_tag', entity1: 'contact', entity2: 'new_tag' },
  ],
};

describe('formControlsApi', () => {
  it('subgridSources_lists_one_to_many_where_the_table_is_referenced_and_many_to_many_on_either_side', () => {
    expect(subgridSources(rels, 'account')).toEqual([
      { relationship: 'account_parent_account', target: 'account', kind: 'oneToMany' },
      { relationship: 'new_account_peer', target: 'account', kind: 'manyToMany' },
      { relationship: 'contact_customer_accounts', target: 'contact', kind: 'oneToMany' },
      { relationship: 'new_account_tag', target: 'new_tag', kind: 'manyToMany' },
    ]);
  });

  it('listPublicViews_reads_active_public_views_of_the_table', async () => {
    const seen = capture(() => HttpResponse.json({ value: [
      { savedqueryid: 'V1', name: 'Active Contacts', isdefault: true },
      { savedqueryid: 'V2', name: 'All Contacts', isdefault: false },
    ] }));
    expect(await listPublicViews(api(), "con'tact")).toEqual([
      { id: 'V1', name: 'Active Contacts', isDefault: true },
      { id: 'V2', name: 'All Contacts', isDefault: false },
    ]);
    expect(seen[0].path).toBe('savedqueries');
    expect(seen[0].search.get('$filter')).toBe("returnedtypecode eq 'con''tact' and querytype eq 0 and statecode eq 0");
    expect(seen[0].search.get('$orderby')).toBe('name');
  });

  it('listQuickViewForms_keeps_only_active_type_6_forms', async () => {
    const seen = capture(() => HttpResponse.json({ value: [
      { systemformid: 'F1', name: 'Main', type: 2, formactivationstate: 1 },
      { systemformid: 'F2', name: 'Card', type: 6, formactivationstate: 1 },
      { systemformid: 'F3', name: 'Old card', type: 6, formactivationstate: 0 },
    ] }));
    expect(await listQuickViewForms(api(), 'contact')).toEqual([{ id: 'F2', name: 'Card' }]);
    expect(seen[0].path).toBe('systemforms');
    expect(seen[0].search.get('$filter')).toBe("objecttypecode eq 'contact'");
  });

  it('listWebResources_filters_by_type_and_orders_by_name', async () => {
    const seen = capture(() => HttpResponse.json({ value: [
      { webresourceid: 'W1', name: 'new_/pages/map.html', displayname: 'Map', webresourcetype: 1 },
      { webresourceid: 'W2', name: 'new_logo.png', displayname: null, webresourcetype: 5 },
    ] }));
    expect(await listWebResources(api(), WEB_RESOURCE_TYPES)).toEqual([
      { id: 'W1', name: 'new_/pages/map.html', displayName: 'Map', type: 1 },
      { id: 'W2', name: 'new_logo.png', displayName: '', type: 5 },
    ]);
    expect(seen[0].path).toBe('webresourceset');
    expect(seen[0].search.get('$filter')).toBe(
      'webresourcetype eq 1 or webresourcetype eq 5 or webresourcetype eq 6 or webresourcetype eq 7 or webresourcetype eq 10 or webresourcetype eq 11',
    );
    expect(seen[0].search.get('$orderby')).toBe('name');
  });

  it('a failed list rejects with the server text', async () => {
    capture(() => HttpResponse.json({ error: { code: '0x80040220', message: 'No read on savedquery' } }, { status: 403 }));
    await expect(listPublicViews(api(), 'contact')).rejects.toThrow('No read on savedquery');
  });

  it('listCustomControls_parses_each_manifest_and_keeps_a_row_that_fails_with_its_reason', async () => {
    const manifest = `<manifest><control namespace="Ctl" constructor="Box" version="1.0.0">
      <property name="value" of-type="SingleLine.Text" usage="bound" required="true" />
    </control></manifest>`;
    const seen = capture(() => HttpResponse.json({ value: [
      { name: 'new_Ctl.Box', manifest },
      { name: 'new_Ctl.Bad', manifest: '<manifest><control /></manifest>' },
      { name: 'new_Ctl.Empty', manifest: null },
    ] }));
    const rows = await listCustomControls(api());
    expect(seen[0].path).toBe('customcontrols');
    expect(seen[0].search.get('$select')).toBe('name,manifest');
    expect(seen[0].search.get('$orderby')).toBe('name');
    expect(rows.map((r) => r.name)).toEqual(['new_Ctl.Box', 'new_Ctl.Bad', 'new_Ctl.Empty']);
    expect(rows[0].manifest?.properties.map((p) => p.name)).toEqual(['value']);
    expect(rows[0].error).toBeUndefined();
    expect(rows[1]).toEqual({ name: 'new_Ctl.Bad', error: 'the manifest has no control with a namespace and a constructor' });
    expect(rows[2].manifest).toBeUndefined();
    expect(rows[2].error).toBeTruthy();
  });

  it('a failed custom control list rejects with the server text', async () => {
    capture(() => HttpResponse.json({ error: { code: '0x80040220', message: 'No read on customcontrol' } }, { status: 403 }));
    await expect(listCustomControls(api())).rejects.toThrow('No read on customcontrol');
  });

  it('sameId_ignores_braces_and_case', () => {
    expect(sameId('{AB-CD}', 'ab-cd')).toBe(true);
    expect(sameId('ab', 'ac')).toBe(false);
  });
});
