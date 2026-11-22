import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { parseXml } from '../../parsers/xml';
import { deleteForm, listForms, loadForm, setDefaultForm, setFormActive, storeForm } from './formApi';
import { readForm, writeForm, type FormDesign } from './formDesign';
import { formRow } from './formDesignTestSupport';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';
const PUBLISH_XML = '<importexportxml><entities><entity>account</entity></entities></importexportxml>';
const error = (status: number, message: string) => HttpResponse.json({ error: { code: '0x80040265', message } }, { status });
const XML = '<form><tabs><tab name="general" id="{a0000000-0000-0000-0000-000000000001}"><labels><label description="General" languagecode="1033" /></labels><columns><column width="100%"><sections></sections></column></columns></tab></tabs></form>';

interface Seen { method: string; path: string; search: URLSearchParams; body: unknown; ifMatch: string | null }

/** Records every Web API request, answering with `answer` (204 by default). */
function capture(answer: (s: Seen, index: number) => Response | undefined = () => undefined): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const text = await request.text();
      const s: Seen = { method: request.method, path: decodeURIComponent(url.pathname).slice(ROOT.length + 1), search: url.searchParams, body: text ? JSON.parse(text) : undefined, ifMatch: request.headers.get('If-Match') };
      seen.push(s);
      return answer(s, seen.length - 1) ?? new HttpResponse(null, { status: 204 });
    }),
  );
  return seen;
}

const design = (): FormDesign => ({ ...readForm(formRow(XML), 1033), name: 'Mine', description: 'Desc', type: 6 });

describe('formApi', () => {
  it('listForms_filters_by_table_orders_by_type_then_name_and_skips_formxml', async () => {
    const seen = capture(() => HttpResponse.json({ value: [{ systemformid: 'A', name: 'A form' }] }));
    expect(await listForms(api(), "ac'me")).toEqual([{ systemformid: 'A', name: 'A form' }]);
    expect(seen[0].path).toBe('systemforms');
    expect(seen[0].search.get('$filter')).toBe("objecttypecode eq 'ac''me'");
    expect(seen[0].search.get('$orderby')).toBe('type,name');
    expect(seen[0].search.get('$select')).not.toContain('formxml');
    expect(seen[0].search.get('$select')).toContain('formactivationstate');
  });

  it('loadForm_reads_one_row_with_formxml', async () => {
    const seen = capture(() => HttpResponse.json({ systemformid: 'F1', name: 'N' }));
    expect(await loadForm(api(), 'F1')).toMatchObject({ name: 'N' });
    expect(seen[0].path).toBe('systemforms(F1)');
    expect(seen[0].search.get('$select')).toContain('formxml');
  });

  it('storeForm_create_posts_the_constraint_body_with_the_design_type_and_returns_the_id_without_publishing', async () => {
    const seen = capture(() => new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/systemforms(11111111-2222-3333-4444-555555555555)` } }));
    expect(await storeForm(api(), design())).toBe('11111111-2222-3333-4444-555555555555');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['POST systemforms']);
    const d = design();
    expect(seen[0].body).toEqual({
      name: 'Mine', description: 'Desc', formxml: writeForm(d), objecttypecode: 'account', type: 6, isdefault: false, formactivationstate: 1,
    });
    expect(() => parseXml((seen[0].body as { formxml: string }).formxml)).not.toThrow();
  });

  it('storeForm_create_without_entity_id_rejects', async () => {
    const seen = capture();
    await expect(storeForm(api(), design())).rejects.toThrow();
    expect(seen).toHaveLength(1);
  });

  it('storeForm_update_patches_only_name_description_and_formxml', async () => {
    const seen = capture();
    expect(await storeForm(api(), design(), 'F1')).toBe('F1');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['PATCH systemforms(F1)']);
    expect(Object.keys(seen[0].body as object).sort()).toEqual(['description', 'formxml', 'name']);
  });

  it('deleteForm_deletes_then_publishes', async () => {
    const seen = capture();
    await deleteForm(api(), 'F1', 'account');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['DELETE systemforms(F1)', 'POST PublishXml']);
    expect(seen[1].body).toEqual({ ParameterXml: PUBLISH_XML });
  });

  it('deleteForm_failure_rejects_and_does_not_publish', async () => {
    const seen = capture((s) => (s.method === 'DELETE' ? error(400, 'Managed component.') : undefined));
    await expect(deleteForm(api(), 'F1', 'account')).rejects.toThrow('Managed component.');
    expect(seen).toHaveLength(1);
  });

  it('setDefaultForm_patches_clicked_first_then_other_main_defaults_then_publishes', async () => {
    const seen = capture((s) => (s.method === 'GET' ? HttpResponse.json({ value: [{ systemformid: 'O1' }, { systemformid: 'O2' }] }) : undefined));
    await setDefaultForm(api(), 'F1', 'account');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual([
      'PATCH systemforms(F1)', 'GET systemforms', 'PATCH systemforms(O1)', 'PATCH systemforms(O2)', 'POST PublishXml',
    ]);
    expect(seen[0].body).toEqual({ isdefault: true });
    expect(seen[2].body).toEqual({ isdefault: false });
    expect(seen[1].search.get('$filter')).toBe("objecttypecode eq 'account' and type eq 2 and isdefault eq true and systemformid ne F1");
  });

  it('setDefaultForm_other_patch_failure_still_publishes_and_rethrows_the_first_error', async () => {
    const seen = capture((s) => {
      if (s.method === 'GET') return HttpResponse.json({ value: [{ systemformid: 'O1' }, { systemformid: 'O2' }] });
      if (s.path === 'systemforms(O1)') return error(400, 'Cannot change O1.');
      if (s.path === 'systemforms(O2)') return error(400, 'Cannot change O2.');
      return undefined;
    });
    await expect(setDefaultForm(api(), 'F1', 'account')).rejects.toThrow('Cannot change O1.');
    expect(seen[0]).toMatchObject({ method: 'PATCH', path: 'systemforms(F1)', body: { isdefault: true } });
    expect(seen.map((s) => s.path)).toContain('systemforms(O2)');
    expect(seen.at(-1)).toMatchObject({ path: 'PublishXml' });
  });

  it('setDefaultForm_failed_first_patch_rejects_without_touching_the_others', async () => {
    const seen = capture((s) => (s.method === 'PATCH' ? error(400, 'No.') : undefined));
    await expect(setDefaultForm(api(), 'F1', 'account')).rejects.toThrow('No.');
    expect(seen).toHaveLength(1);
  });

  it.each([[true, 1], [false, 0]])('setFormActive_%s_patches_formactivationstate_then_publishes', async (active, state) => {
    const seen = capture();
    await setFormActive(api(), 'F1', 'account', active);
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['PATCH systemforms(F1)', 'POST PublishXml']);
    expect(seen[0].body).toEqual({ formactivationstate: state });
  });

  it('every_patch_sends_if_match_star_so_a_deleted_form_answers_404_instead_of_being_upserted', async () => {
    const seen = capture((s) => (s.method === 'GET' ? HttpResponse.json({ value: [{ systemformid: 'O1' }] }) : undefined));
    await storeForm(api(), design(), 'F1');
    await setDefaultForm(api(), 'F1', 'account');
    await setFormActive(api(), 'F1', 'account', false);
    const patches = seen.filter((s) => s.method === 'PATCH');
    expect(patches.map((s) => s.path)).toEqual(['systemforms(F1)', 'systemforms(F1)', 'systemforms(O1)', 'systemforms(F1)']);
    expect(patches.map((s) => s.ifMatch)).toEqual(['*', '*', '*', '*']);
    expect(seen.filter((s) => s.method !== 'PATCH').every((s) => s.ifMatch === null)).toBe(true);
  });

  it('storeForm_update_of_a_deleted_form_rejects_with_the_404', async () => {
    capture((s) => (s.method === 'PATCH' && s.ifMatch === '*' ? error(404, 'systemform With Id = F1 Does Not Exist') : undefined));
    await expect(storeForm(api(), design(), 'F1')).rejects.toThrow('Does Not Exist');
  });

  it('setFormActive_failed_patch_does_not_publish', async () => {
    const seen = capture((s) => (s.method === 'PATCH' ? error(400, 'No.') : undefined));
    await expect(setFormActive(api(), 'F1', 'account', false)).rejects.toThrow('No.');
    expect(seen).toHaveLength(1);
  });
});
