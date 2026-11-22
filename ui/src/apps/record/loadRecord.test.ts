import { HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import { installMetadata, lbl, rawAttr, rawEntity, rawOneToMany } from '../../admin/tables/tableTestSupport';
import { XrmApi } from '../../shell/api/client';
import { server } from '../../test/server';
import type { Cell, FormModel } from '../../model/form';
import { formColumns, loadRecord } from './loadRecord';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const U = 'aaaaaaaa-0000-0000-0000-000000000001';
const R = 'bbbbbbbb-0000-0000-0000-000000000002';

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true }),
    rawAttr('primarycontactid', 'Lookup', { Targets: ['contact'] }),
    rawAttr('doc', 'File'),
    rawAttr('doc_name', 'String', { AttributeOf: 'doc' }),
    rawAttr('secret', 'String', { IsValidForRead: false }),
    rawAttr('statecode', 'Picklist', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
    rawAttr('statuscode', 'Picklist', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
    rawAttr('ownerid', 'Lookup', { Targets: ['systemuser'] }),
  ],
  { SchemaName: 'Account', PrimaryNameAttribute: 'name' },
);
const contact = rawEntity('contact', 'contacts', [], { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname' });

function mock({ privileges = ['prvCreateAccount'], rights = 'ReadAccess, WriteAccess', record }: { privileges?: string[]; rights?: string; record?: () => Response }) {
  installMetadata([account, contact], [rawOneToMany('account_primary_contact', 'account', 'primarycontactid', 'contact', 'primarycontactid')]);
  const seen: string[] = [];
  server.use(
    onPath('get', 'WhoAmI', () => HttpResponse.json({ UserId: U })),
    onPath('get', `systemusers(${U})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`, () =>
      HttpResponse.json({ RolePrivileges: privileges.map((PrivilegeName) => ({ PrivilegeName })) }),
    ),
    onPath('get', `systemusers(${U})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () => HttpResponse.json({ AccessRights: rights })),
    onPath('get', `accounts(${R})`, ({ request }) => {
      seen.push(`${new URL(request.url).search} | ${request.headers.get('Prefer')}`);
      return record ? record() : HttpResponse.json({ accountid: R, name: 'Contoso' });
    }),
  );
  return seen;
}

describe('loadRecord', () => {
  it('record_not_found_is_error_with_message', async () => {
    mock({ record: () => HttpResponse.json({ error: { code: '0x80040217', message: 'Does Not Exist' } }, { status: 404 }) });
    await expect(loadRecord(api(), 'account', R)).rejects.toThrow('This record does not exist or you do not have access to it.');
  });

  it('loads_the_record_with_annotations_and_the_rights', async () => {
    const seen = mock({});
    const ctx = await loadRecord(api(), 'account', R);
    expect(ctx.record).toEqual({ accountid: R, name: 'Contoso' });
    expect(ctx.rights).toEqual(['Read', 'Write']);
    expect(ctx.canCreate).toBe(true);
    expect(ctx.fieldContext.record).toEqual({ set: 'accounts', id: R, data: ctx.record });
    expect(ctx.fieldContext.lookups.map((l) => l.nav)).toEqual(['primarycontactid']);
    // Readable columns only: a file with its name, a lookup by `_x_value`; no companion and no unreadable column.
    expect(seen[0]).toContain('$select=accountid,name,_primarycontactid_value,doc,doc_name,statecode,statuscode,_ownerid_value');
    expect(seen[0]).toContain('odata.include-annotations="*"');
  });

  it('create_mode_has_no_record_and_create_follows_the_privilege', async () => {
    mock({ privileges: ['prvReadAccount'] });
    const ctx = await loadRecord(api(), 'account');
    expect(ctx.record).toBeUndefined();
    expect(ctx.fieldContext.record).toBeUndefined();
    expect(ctx.rights).toEqual([]);
    expect(ctx.canCreate).toBe(false);
  });

  it('create_on_an_activity_table_follows_the_shared_activity_privilege', async () => {
    // The server names the privileges of every activity table with the stem Activity (prvCreateActivity), as Dataverse does:
    // a new task opened from the timeline must offer Save.
    const task = rawEntity('task', 'tasks', [rawAttr('activityid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('subject', 'String', { IsPrimaryName: true })], {
      SchemaName: 'Task',
      PrimaryNameAttribute: 'subject',
      IsActivity: true,
    });
    mock({ privileges: ['prvCreateActivity'] });
    installMetadata([account, contact, task], [rawOneToMany('account_primary_contact', 'account', 'primarycontactid', 'contact', 'primarycontactid')]);
    expect((await loadRecord(api(), 'task')).canCreate).toBe(true);
    mock({ privileges: ['prvReadActivity'] });
    installMetadata([account, contact, task], [rawOneToMany('account_primary_contact', 'account', 'primarycontactid', 'contact', 'primarycontactid')]);
    expect((await loadRecord(api(), 'task')).canCreate).toBe(false);
  });

  it('select_adds_the_primary_name_for_the_form_title', async () => {
    const seen = mock({});
    await loadRecord(api(), 'account', R, ['primarycontactid']);
    const select = new URLSearchParams(seen[0].split(' | ')[0]).get('$select');
    expect(select).toBe('accountid,name,_primarycontactid_value,statecode,statuscode,_ownerid_value');
  });

  it('select_is_limited_to_the_given_columns', async () => {
    const seen = mock({});
    await loadRecord(api(), 'account', R, ['name', 'primarycontactid']);
    const select = new URLSearchParams(seen[0].split(' | ')[0]).get('$select');
    expect(select).toBe('accountid,name,_primarycontactid_value,statecode,statuscode,_ownerid_value');
  });

  it('403_gives_the_not_found_message_and_other_errors_are_rethrown', async () => {
    mock({ record: () => HttpResponse.json({ error: { code: '0x80040220', message: 'No privilege' } }, { status: 403 }) });
    await expect(loadRecord(api(), 'account', R)).rejects.toThrow('This record does not exist or you do not have access to it.');
    mock({ record: () => HttpResponse.json({ error: { code: '0x80040216', message: 'boom' } }, { status: 500 }) });
    await expect(loadRecord(api(), 'account', R)).rejects.toThrow('boom');
  });
});

const fieldCell = (attribute: string): Cell => ({
  id: attribute,
  labels: {},
  showLabel: true,
  visible: true,
  colSpan: 1,
  rowSpan: 1,
  control: { kind: 'field', id: attribute, attribute, disabled: false },
});

describe('formColumns', () => {
  it('collects_tab_header_footer_and_quick_view_lookups', () => {
    const quick: Cell = { ...fieldCell('x'), control: { kind: 'quickview', id: 'q', lookupAttribute: 'primarycontactid', forms: {} } };
    const form = {
      tabs: [{ name: 't', labels: {}, visible: true, columns: [{ width: '100%', sections: [{ name: 's', labels: {}, showLabel: true, visible: true, columns: 1, labelPosition: 'left', rows: [[fieldCell('name')], [quick, fieldCell('name')]] }] }] }],
      header: [fieldCell('ownerid')],
      footer: [fieldCell('createdon')],
    } as unknown as FormModel;
    expect(formColumns(form)).toEqual(['name', 'primarycontactid', 'ownerid', 'createdon']);
  });
});
