import { describe, expect, it } from 'vitest';
import { attr, entity } from '../../../admin/tables/testAttrs';
import type { EntityMeta } from '../../../admin/tables/metadata';
import { parseMainAspx } from '../../mainAspx';
import { completeState, creatableActivityTypes, newActivityUrl } from './activityActions';

const activity = (logicalName: string, schemaName: string, displayName: string, attributes = [] as EntityMeta['attributes']): EntityMeta =>
  entity(attributes, { logicalName, schemaName, displayName, entitySetName: `${logicalName}s`, primaryIdAttribute: 'activityid', isActivity: true });

const statecode = attr('statecode', {
  kind: 'choice',
  typeName: 'State',
  options: [
    { value: 0, label: 'Open', defaultStatus: 2 },
    { value: 1, label: 'Completed', defaultStatus: 5 },
    { value: 2, label: 'Canceled', defaultStatus: 6 },
  ],
});
const statuscode = attr('statuscode', {
  kind: 'choice',
  typeName: 'Status',
  options: [
    { value: 2, label: 'Not Started', state: 0 },
    { value: 4, label: 'Waiting', state: 1 },
    { value: 5, label: 'Done', state: 1 },
    { value: 6, label: 'Dropped', state: 2 },
  ],
});

describe('activityActions', () => {
  it('create_menu_lists_creatable_activity_types_without_fax_and_letter', () => {
    const tables = [
      activity('task', 'Task', 'Task'),
      activity('fax', 'Fax', 'Fax'),
      activity('letter', 'Letter', 'Letter'),
      activity('email', 'Email', 'Email'),
      activity('xv_visit', 'xv_Visit', 'Site visit'),
      activity('phonecall', 'PhoneCall', 'Phone Call'),
    ];
    // By SchemaName: the custom activity only; the others need their own privilege.
    expect(creatableActivityTypes(tables, new Set(['prvCreatexv_Visit', 'prvCreateFax', 'prvReadTask'])).map((e) => e.logicalName)).toEqual(['xv_visit']);
    // The shared activity privilege covers every activity table, fax and letter still left out; sorted by name.
    expect(creatableActivityTypes(tables, new Set(['prvCreateActivity'])).map((e) => e.logicalName)).toEqual(['email', 'phonecall', 'xv_visit', 'task']);
    expect(creatableActivityTypes(tables, new Set())).toEqual([]);
  });

  it('new_activity_opens_main_form_with_regarding', () => {
    const url = newActivityUrl('app-1', 'task', { table: 'xv_project', id: 'cccccccc-0000-0000-0000-000000000003', name: 'Alpha & Co' });
    const state = parseMainAspx(url.slice(url.indexOf('?')));
    expect(state).toMatchObject({ appId: 'app-1', pageType: 'entityrecord', etn: 'task' });
    expect(state.id).toBeUndefined();
    expect(state.extraqs).toEqual({
      regardingobjectid: 'cccccccc-0000-0000-0000-000000000003',
      regardingobjectidname: 'Alpha & Co',
      regardingobjectidtype: 'xv_project',
    });
  });

  it('complete_sets_state_1_default_status', () => {
    expect(completeState(activity('task', 'Task', 'Task', [statecode, statuscode]))).toEqual({ state: 1, status: 5 });
    // Without a DefaultStatus among its reasons: the first reason of state 1.
    const noDefault = attr('statecode', { kind: 'choice', typeName: 'State', options: [{ value: 0, label: 'Open' }, { value: 1, label: 'Completed' }] });
    expect(completeState(activity('task', 'Task', 'Task', [noDefault, statuscode]))).toEqual({ state: 1, status: 4 });
    // A table without state 1 has no Complete.
    const twoStates = attr('statecode', { kind: 'choice', typeName: 'State', options: [{ value: 0, label: 'Open' }, { value: 2, label: 'Canceled' }] });
    expect(completeState(activity('xv_visit', 'xv_Visit', 'Visit', [twoStates, statuscode]))).toBeUndefined();
    expect(completeState(activity('xv_visit', 'xv_Visit', 'Visit', []))).toBeUndefined();
  });

  it('email_has_no_complete', () => {
    expect(completeState(activity('email', 'Email', 'Email', [statecode, statuscode]))).toBeUndefined();
  });
});
