import { renderHook, waitFor, act } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import type { AttributeMeta } from '../../admin/tables/metadata';
import { attr, entity } from '../../admin/tables/testAttrs';
import type { Cell, FormModel } from '../../model/form';
import { AuthProvider, useAuth } from '../../shell/auth/AuthProvider';
import { server } from '../../test/server';
import type { AccessRight } from './access';
import type { RecordContext } from './loadRecord';
import { useRecordForm } from './useRecordForm';
import { mockApps } from '../testSupport';

const ID = 'cccccccc-0000-0000-0000-000000000003';

const cell = (attribute: string, disabled = false): Cell => ({
  id: attribute,
  labels: { 1033: `${attribute} label` },
  showLabel: true,
  visible: true,
  colSpan: 1,
  rowSpan: 1,
  control: { kind: 'field', id: attribute, attribute, disabled },
});
const formOf = (cells: Cell[], footer: Cell[] = []): FormModel => ({
  id: 'f',
  name: 'Main',
  entity: 'xv_project',
  type: 2,
  isDefault: true,
  order: 0,
  roles: [],
  tabs: [{ name: 't', labels: {}, visible: true, columns: [{ width: '100%', sections: [{ name: 's', labels: {}, showLabel: true, visible: true, columns: 1, labelPosition: 'left', rows: cells.map((c) => [c]) }] }] }],
  header: [],
  footer,
  hasScripts: false, libraries: [], events: [],
});

function contextOf(attributes: AttributeMeta[], data: Record<string, unknown> | undefined, rights: AccessRight[] = ['Read', 'Write']): RecordContext {
  return {
    entity: entity(attributes),
    fieldContext: { table: 'xv_project', lookups: [], globalChoices: {}, record: data ? { set: 'xv_projects', id: ID, data } : undefined },
    record: data,
    rights: data ? rights : [],
    canCreate: true,
  };
}

function Gate({ children }: { children: ReactNode }) {
  return useAuth().api ? <>{children}</> : null;
}
const wrapper = ({ children }: { children: ReactNode }) => (
  <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
    <AuthProvider>
      <Gate>{children}</Gate>
    </AuthProvider>
  </MemoryRouter>
);

interface Sent {
  method: string;
  path: string;
  body?: unknown;
}

/** Signs in, records the writes and answers the record read with `after` (what a refresh returns). */
function setup(ctx: RecordContext, form: FormModel, over: { patchStatus?: number; after?: Record<string, unknown>; readStatus?: number } = {}) {
  mockApps();
  const sent: Sent[] = [];
  const note = async (request: Request): Promise<Sent> => {
    const path = decodeURIComponent(new URL(request.url).pathname.replace('/api/data/v9.2/', ''));
    const body = ['POST', 'PATCH'].includes(request.method) ? await request.json() : undefined;
    const s = { method: request.method, path, body };
    sent.push(s);
    return s;
  };
  server.use(
    onPath('get', `xv_projects(${ID})`, () =>
      over.readStatus ? HttpResponse.json({ error: { code: '0x80040216', message: 'read failed' } }, { status: over.readStatus }) : HttpResponse.json(over.after ?? ctx.record ?? {}),
    ),
    http.delete('*/api/data/v9.2/*', async ({ request }) => {
      await note(request);
      return new HttpResponse(null, { status: 204 });
    }),
    http.post('*/api/data/v9.2/xv_projects', async ({ request }) => {
      await note(request);
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': 'http://x/api/data/v9.2/xv_projects(new-id-1)' } });
    }),
    http.patch('*/api/data/v9.2/*', async ({ request }) => {
      await note(request);
      return over.patchStatus
        ? HttpResponse.json({ error: { code: '0x80040333', message: 'Duplicate record found' } }, { status: over.patchStatus })
        : new HttpResponse(null, { status: 204 });
    }),
  );
  const hook = renderHook(() => useRecordForm(ctx, form), { wrapper });
  return { hook, sent, form };
}

const ready = async (hook: { result: { current: unknown } }) => waitFor(() => expect(hook.result.current).not.toBeNull());
const name = attr('xv_name', { isPrimaryName: true });
const note = attr('xv_note');
const RECORD = { xv_projectid: ID, xv_name: 'Alpha', xv_note: 'n1' };

describe('useRecordForm read-only rules', () => {
  it('read_only_without_write_access', async () => {
    const { hook } = setup(contextOf([name], RECORD, ['Read']), formOf([cell('xv_name')]));
    await ready(hook);
    expect(hook.result.current.readOnly).toBe(true);
    expect(hook.result.current.isReadOnly('xv_name')).toBe(true);
    let saved: unknown = 'not called';
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toBeUndefined();
  });

  it('read_only_control_disabled_in_formxml', async () => {
    const { hook } = setup(contextOf([name, note], RECORD), formOf([cell('xv_name', true), cell('xv_note')]));
    await ready(hook);
    expect(hook.result.current.readOnly).toBe(false);
    expect(hook.result.current.isReadOnly('xv_name')).toBe(true);
    expect(hook.result.current.isReadOnly('xv_note')).toBe(false);
  });

  it('read_only_not_valid_for_update', async () => {
    const fixed = attr('xv_fixed', { validForUpdate: false });
    const update = setup(contextOf([name, fixed], RECORD), formOf([cell('xv_name'), cell('xv_fixed')]));
    await ready(update.hook);
    expect(update.hook.result.current.isReadOnly('xv_fixed')).toBe(true);
    // Valid for create: editable on the create form.
    const create = setup(contextOf([name, fixed], undefined), formOf([cell('xv_name'), cell('xv_fixed')]));
    await ready(create.hook);
    expect(create.hook.result.current.isReadOnly('xv_fixed')).toBe(false);
  });

  it('read_only_by_field_security', async () => {
    const secret = attr('xv_secret', { secured: true });
    const { hook } = setup(contextOf([name, secret], RECORD), formOf([cell('xv_name'), cell('xv_secret')]));
    await ready(hook);
    // The record has no `xv_secret`: the server left it out because the user may not read it.
    expect(hook.result.current.isReadOnly('xv_secret')).toBe(true);
    expect(hook.result.current.isReadOnly('xv_name')).toBe(false);
  });

  it('secured_column_present_in_the_record_is_editable', async () => {
    const secret = attr('xv_secret', { secured: true });
    const { hook } = setup(contextOf([name, secret], { ...RECORD, xv_secret: 's' }), formOf([cell('xv_name'), cell('xv_secret')]));
    await ready(hook);
    expect(hook.result.current.isReadOnly('xv_secret')).toBe(false);
  });
});

describe('useRecordForm required rule', () => {
  const required = attr('xv_name', { isPrimaryName: true, requiredLevel: 'ApplicationRequired', displayName: 'Name' });

  it('required_blocks_save_with_message', async () => {
    const { hook, sent } = setup(contextOf([required, note], RECORD), formOf([cell('xv_name'), cell('xv_note')]));
    await ready(hook);
    expect(hook.result.current.isRequired('xv_name')).toBe(true);
    act(() => hook.result.current.setValue('xv_name', ''));
    let saved: unknown = 'not called';
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toBeUndefined();
    expect(hook.result.current.errors).toEqual({ xv_name: 'xv_name label: a value is required.' });
    expect(hook.result.current.notification).toContain('xv_name label: a value is required.');
    expect(sent).toEqual([]);
  });

  it('system_required_is_required', async () => {
    const sys = attr('xv_name', { requiredLevel: 'SystemRequired' });
    const { hook } = setup(contextOf([sys], RECORD), formOf([cell('xv_name')]));
    await ready(hook);
    expect(hook.result.current.isRequired('xv_name')).toBe(true);
  });

  it('recommended_does_not_block', async () => {
    const recommended = attr('xv_note', { requiredLevel: 'Recommended' });
    const { hook, sent } = setup(contextOf([name, recommended], undefined), formOf([cell('xv_name'), cell('xv_note')]));
    await ready(hook);
    expect(hook.result.current.isRequired('xv_note')).toBe(false);
    act(() => hook.result.current.setValue('xv_name', 'Beta'));
    await act(async () => {
      await hook.result.current.save();
    });
    expect(hook.result.current.errors).toEqual({});
    expect(sent).toHaveLength(1);
  });
});

describe('useRecordForm checkRequired (a wizard step)', () => {
  const req = (n: string) => attr(n, { requiredLevel: 'ApplicationRequired' });

  it('checks only the given attributes: an empty one is an error, the others are untouched, no notification', async () => {
    const { hook, sent } = setup(contextOf([req('xv_name'), req('xv_code')], undefined), formOf([cell('xv_name'), cell('xv_code')]));
    await ready(hook);
    let ok: unknown;
    act(() => {
      ok = hook.result.current.checkRequired(['xv_name']);
    });
    expect(ok).toBe(false);
    expect(hook.result.current.errors).toEqual({ xv_name: 'xv_name label: a value is required.' });
    expect(hook.result.current.notification).toBeUndefined();
    expect(sent).toEqual([]);
  });

  it('filled: true and its error cleared, an error of another field kept', async () => {
    const { hook } = setup(contextOf([req('xv_name'), req('xv_code')], undefined), formOf([cell('xv_name'), cell('xv_code')]));
    await ready(hook);
    act(() => {
      hook.result.current.checkRequired(['xv_name', 'xv_code']);
    });
    expect(Object.keys(hook.result.current.errors)).toEqual(['xv_name', 'xv_code']);
    // A create default arriving late fills the field without clearing its error: the check clears it.
    act(() => hook.result.current.setDefault('xv_name', 'Beta'));
    expect(hook.result.current.errors).toHaveProperty('xv_name');
    let ok: unknown;
    act(() => {
      ok = hook.result.current.checkRequired(['xv_name']);
    });
    expect(ok).toBe(true);
    expect(hook.result.current.errors).toEqual({ xv_code: 'xv_code label: a value is required.' });
  });

  it('a read-only, not required or unknown field never blocks', async () => {
    const { hook } = setup(contextOf([req('xv_name'), attr('xv_note')], undefined), formOf([cell('xv_name', true), cell('xv_note')]));
    await ready(hook);
    let ok: unknown;
    act(() => {
      ok = hook.result.current.checkRequired(['xv_name', 'xv_note', 'xv_unknown']);
    });
    expect(ok).toBe(true);
    expect(hook.result.current.errors).toEqual({});
  });
});

describe('useRecordForm save', () => {
  it('save_create_posts_and_returns_id', async () => {
    const { hook, sent } = setup(contextOf([name, note], undefined), formOf([cell('xv_name'), cell('xv_note')]));
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Beta'));
    expect(hook.result.current.dirty).toBe(true);
    let saved: { id: string } | undefined;
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toEqual({ id: 'new-id-1' });
    expect(sent).toEqual([{ method: 'POST', path: 'xv_projects', body: { xv_name: 'Beta' } }]);
  });

  it('create_defaults_do_not_make_the_form_dirty', async () => {
    const { hook, sent } = setup(contextOf([name, note], undefined), formOf([cell('xv_name'), cell('xv_note')]));
    await ready(hook);
    act(() => hook.result.current.setDefault('xv_note', 'default note'));
    expect(hook.result.current.values.xv_note).toBe('default note');
    expect(hook.result.current.dirty).toBe(false);
    act(() => hook.result.current.setValue('xv_name', 'Beta'));
    expect(hook.result.current.dirty).toBe(true);
    await act(async () => {
      await hook.result.current.save();
    });
    // The default is still posted.
    expect(sent).toEqual([{ method: 'POST', path: 'xv_projects', body: { xv_name: 'Beta', xv_note: 'default note' } }]);
  });

  it('a_late_create_default_does_not_overwrite_a_user_edit', async () => {
    const { hook, sent } = setup(contextOf([name, note], undefined), formOf([cell('xv_name'), cell('xv_note')]));
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Beta'));
    act(() => hook.result.current.setValue('xv_note', 'typed by the user'));
    // The default read finishes after the user's edit.
    act(() => hook.result.current.setDefault('xv_note', 'default note'));
    expect(hook.result.current.values.xv_note).toBe('typed by the user');
    await act(async () => {
      await hook.result.current.save();
    });
    expect(sent).toEqual([{ method: 'POST', path: 'xv_projects', body: { xv_name: 'Beta', xv_note: 'typed by the user' } }]);
  });

  it('save_update_patches_only_changed', async () => {
    const { hook, sent } = setup(contextOf([name, note], RECORD), formOf([cell('xv_name'), cell('xv_note')]), {
      after: { ...RECORD, xv_name: 'Gamma' },
    });
    await ready(hook);
    expect(hook.result.current.dirty).toBe(false);
    act(() => hook.result.current.setValue('xv_name', 'Gamma'));
    expect(hook.result.current.dirty).toBe(true);
    let saved: { id: string } | undefined;
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toEqual({ id: ID });
    expect(sent).toEqual([{ method: 'PATCH', path: `xv_projects(${ID})`, body: { xv_name: 'Gamma' } }]);
    // The record was read again: the form is clean.
    expect(hook.result.current.dirty).toBe(false);
    expect(hook.result.current.values.xv_name).toBe('Gamma');
  });

  it('save_error_keeps_values_and_message', async () => {
    const { hook } = setup(contextOf([name, note], RECORD), formOf([cell('xv_name'), cell('xv_note')]), { patchStatus: 400 });
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Gamma'));
    let saved: unknown = 'not called';
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toBeUndefined();
    expect(hook.result.current.notification).toContain('Duplicate record found');
    expect(hook.result.current.values.xv_name).toBe('Gamma');
    expect(hook.result.current.dirty).toBe(true);
  });
});

describe('useRecordForm guards and reloads', () => {
  it('two_concurrent_saves_in_create_mode_make_one_post', async () => {
    const { hook, sent } = setup(contextOf([name], undefined), formOf([cell('xv_name')]));
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Beta'));
    await act(async () => {
      await Promise.all([hook.result.current.save(), hook.result.current.save()]);
    });
    expect(sent.filter((s) => s.method === 'POST')).toHaveLength(1);
  });

  it('failed_reload_after_a_successful_save_still_returns_the_id', async () => {
    const { hook, sent } = setup(contextOf([name], RECORD), formOf([cell('xv_name')]), { readStatus: 500 });
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Gamma'));
    let saved: { id: string } | undefined;
    await act(async () => {
      saved = await hook.result.current.save();
    });
    expect(saved).toEqual({ id: ID });
    expect(sent).toHaveLength(1);
    expect(hook.result.current.notification).toContain('The record was saved, but it could not be reloaded');
  });

  it('refresh_replaces_the_values_with_the_record', async () => {
    const { hook } = setup(contextOf([name], RECORD), formOf([cell('xv_name')]), { after: { ...RECORD, xv_name: 'Fresh' } });
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Edited'));
    await act(async () => {
      await hook.result.current.refresh();
    });
    expect(hook.result.current.values.xv_name).toBe('Fresh');
    expect(hook.result.current.dirty).toBe(false);
  });

  it('remove_deletes_the_record', async () => {
    const { hook, sent } = setup(contextOf([name], RECORD), formOf([cell('xv_name')]));
    await ready(hook);
    let removed = false;
    await act(async () => {
      removed = await hook.result.current.remove();
    });
    expect(removed).toBe(true);
    expect(sent).toEqual([{ method: 'DELETE', path: `xv_projects(${ID})`, body: undefined }]);
  });

  it('state_resets_when_the_form_changes', async () => {
    const ctx = contextOf([name, note], RECORD);
    const first = formOf([cell('xv_name')]);
    const second = formOf([cell('xv_name'), cell('xv_note')]);
    setup(ctx, first);
    const hook = renderHook(({ form }) => useRecordForm(ctx, form), { wrapper, initialProps: { form: first } });
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Edited'));
    expect(hook.result.current.dirty).toBe(true);
    hook.rerender({ form: second });
    await waitFor(() => expect(hook.result.current.values.xv_name).toBe('Alpha'));
    expect(hook.result.current.values.xv_note).toBe('n1');
    expect(hook.result.current.dirty).toBe(false);
  });
});

describe('useRecordForm assign', () => {
  it('assign_patches_ownerid_with_bind', async () => {
    const { hook, sent } = setup(contextOf([name], RECORD), formOf([cell('xv_name')]));
    await ready(hook);
    await act(async () => {
      await hook.result.current.assign({ id: 'dddddddd-0000-0000-0000-000000000004', target: 'team', name: 'Sales' });
    });
    expect(sent).toEqual([{ method: 'PATCH', path: `xv_projects(${ID})`, body: { 'ownerid@odata.bind': '/teams(dddddddd-0000-0000-0000-000000000004)' } }]);
  });
});

describe('useRecordForm state', () => {
  const statecode = attr('statecode', {
    kind: 'choice',
    typeName: 'State',
    validForCreate: false,
    options: [
      { value: 0, label: 'Active', defaultStatus: 1 },
      { value: 1, label: 'Inactive', defaultStatus: 2 },
    ],
  });

  it('setState_patches_statecode_and_statuscode', async () => {
    const { hook, sent } = setup(contextOf([name, statecode], { ...RECORD, statecode: 0 }), formOf([cell('xv_name')]));
    await ready(hook);
    let changed = false;
    await act(async () => {
      changed = await hook.result.current.setState(1, 2);
    });
    expect(changed).toBe(true);
    expect(sent).toEqual([{ method: 'PATCH', path: `xv_projects(${ID})`, body: { statecode: 1, statuscode: 2 } }]);
  });

  it('setState_failure_is_the_notification', async () => {
    const { hook } = setup(contextOf([name, statecode], { ...RECORD, statecode: 0 }), formOf([cell('xv_name')]), { patchStatus: 400 });
    await ready(hook);
    let changed = true;
    await act(async () => {
      changed = await hook.result.current.setState(1, 2);
    });
    expect(changed).toBe(false);
    expect(hook.result.current.notification).toContain('Duplicate record found');
  });

  it('inactive_record_is_read_only', async () => {
    const { hook } = setup(contextOf([name, statecode], { ...RECORD, statecode: 1 }), formOf([cell('xv_name')]));
    await ready(hook);
    expect(hook.result.current.readOnly).toBe(true);
    expect(hook.result.current.inactiveLabel).toBe('Inactive');
    expect(hook.result.current.isReadOnly('xv_name')).toBe(true);

    const active = setup(contextOf([name, statecode], { ...RECORD, statecode: 0 }), formOf([cell('xv_name')]));
    await ready(active.hook);
    expect(active.hook.result.current.readOnly).toBe(false);
    expect(active.hook.result.current.inactiveLabel).toBeUndefined();
  });
});

describe('useRecordForm party lists (plan 4.1b, task 9)', () => {
  const to = attr('to', { kind: 'partylist', typeName: 'PartyList', targets: ['contact', 'queue'] });
  const cc = attr('cc', { kind: 'partylist', typeName: 'PartyList', targets: ['contact', 'queue'] });
  const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
  const LOGICAL = '@Microsoft.Dynamics.CRM.lookuplogicalname';
  const WITH_PARTIES = {
    ...RECORD,
    xv_project_activity_parties: [
      { participationtypemask: 2, _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', [`_partyid_value${FORMATTED}`]: 'Ada', addressused: 'ada@example.com' },
      { participationtypemask: 3, _partyid_value: null, addressused: 'cc@example.com' },
      // Not on the form: kept as it is on update. Regarding (8) is the server's and never sent.
      { participationtypemask: 4, _partyid_value: null, addressused: 'bcc@example.com' },
      { participationtypemask: 8, _partyid_value: 'p1', [`_partyid_value${LOGICAL}`]: 'account', [`_partyid_value${FORMATTED}`]: 'Contoso' },
    ],
  };
  const bcc = attr('bcc', { kind: 'partylist', typeName: 'PartyList', targets: ['contact', 'queue'] });
  const withNav = (ctx: RecordContext): RecordContext => ({ ...ctx, fieldContext: { ...ctx.fieldContext, partyNavigation: 'xv_project_activity_parties' } });
  const sets = () =>
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='contact')", () => HttpResponse.json({ EntitySetName: 'contacts' })),
      onPath('get', "EntityDefinitions(LogicalName='queue')", () => HttpResponse.json({ EntitySetName: 'queues' })),
    );

  it('loads the parties of each column and stays clean', async () => {
    const { hook } = setup(withNav(contextOf([name, to, cc], WITH_PARTIES)), formOf([cell('xv_name'), cell('to'), cell('cc')]));
    await ready(hook);
    expect(hook.result.current.values.to).toEqual([{ id: 'c1', logicalName: 'contact', name: 'Ada', address: 'ada@example.com' }]);
    expect(hook.result.current.values.cc).toEqual([{ address: 'cc@example.com' }]);
    expect(hook.result.current.isReadOnly('to')).toBe(false);
    expect(hook.result.current.dirty).toBe(false);
  });

  it('is read-only without the party navigation', async () => {
    const { hook } = setup(contextOf([name, to], WITH_PARTIES), formOf([cell('xv_name'), cell('to')]));
    await ready(hook);
    expect(hook.result.current.isReadOnly('to')).toBe(true);
  });

  it('create sends the parties through the party navigation', async () => {
    sets();
    const { hook, sent } = setup(withNav(contextOf([name, to, cc], undefined)), formOf([cell('xv_name'), cell('to'), cell('cc')]));
    await ready(hook);
    act(() => hook.result.current.setValue('xv_name', 'Hi'));
    act(() => hook.result.current.setValue('to', [{ id: 'q1', logicalName: 'queue', name: 'Support' }, { address: 'x@example.com' }]));
    await act(async () => {
      await hook.result.current.save();
    });
    expect(sent[0]).toEqual({
      method: 'POST',
      path: 'xv_projects',
      body: {
        xv_name: 'Hi',
        xv_project_activity_parties: [
          { participationtypemask: 2, 'partyid_queue@odata.bind': '/queues(q1)' },
          { participationtypemask: 2, addressused: 'x@example.com' },
        ],
      },
    });
  });

  it('update sends every party list whole: the server replaces the whole party set', async () => {
    sets();
    const { hook, sent } = setup(withNav(contextOf([name, to, cc, bcc], WITH_PARTIES)), formOf([cell('xv_name'), cell('to'), cell('cc')]));
    await ready(hook);
    act(() => hook.result.current.setValue('to', [...(hook.result.current.values.to as object[]), { address: 'new@example.com' }] as never));
    expect(hook.result.current.dirty).toBe(true);
    await act(async () => {
      await hook.result.current.save();
    });
    expect(sent[0]).toEqual({
      method: 'PATCH',
      path: `xv_projects(${ID})`,
      body: {
        xv_project_activity_parties: [
          { participationtypemask: 2, 'partyid_contact@odata.bind': '/contacts(c1)' },
          { participationtypemask: 2, addressused: 'new@example.com' },
          { participationtypemask: 3, addressused: 'cc@example.com' },
          { participationtypemask: 4, addressused: 'bcc@example.com' },
        ],
      },
    });
  });

  it('a party list emptied after it was saved is cleared: its type is left out of the whole set', async () => {
    sets();
    const { hook, sent } = setup(withNav(contextOf([name, to, cc, bcc], WITH_PARTIES)), formOf([cell('xv_name'), cell('to'), cell('cc')]));
    await ready(hook);
    act(() => hook.result.current.setValue('cc', []));
    await act(async () => {
      await hook.result.current.save();
    });
    expect(hook.result.current.errors.cc).toBeUndefined();
    expect(sent[0]).toEqual({
      method: 'PATCH',
      path: `xv_projects(${ID})`,
      body: {
        xv_project_activity_parties: [
          { participationtypemask: 2, 'partyid_contact@odata.bind': '/contacts(c1)' },
          { participationtypemask: 4, addressused: 'bcc@example.com' },
        ],
      },
    });
  });
});
