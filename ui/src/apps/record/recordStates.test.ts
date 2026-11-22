import { describe, expect, it } from 'vitest';
import { attr, entity } from '../../admin/tables/testAttrs';
import { defaultStatusOf, stateOfRecord, statesOf } from './recordStates';

const statecode = attr('statecode', {
  kind: 'choice',
  typeName: 'State',
  options: [
    { value: 0, label: 'Active', defaultStatus: 1 },
    { value: 1, label: 'Inactive', defaultStatus: 2 },
  ],
});
const statuscode = attr('statuscode', {
  kind: 'choice',
  typeName: 'Status',
  options: [
    { value: 1, label: 'Active', state: 0 },
    { value: 2, label: 'Inactive', state: 1 },
    { value: 3, label: 'Closed', state: 1 },
  ],
});

describe('recordStates', () => {
  it('groups_status_reasons_by_state', () => {
    expect(statesOf(entity([statecode, statuscode]))).toEqual([
      { value: 0, label: 'Active', defaultStatus: 1, statuses: [{ value: 1, label: 'Active', state: 0 }] },
      {
        value: 1,
        label: 'Inactive',
        defaultStatus: 2,
        statuses: [
          { value: 2, label: 'Inactive', state: 1 },
          { value: 3, label: 'Closed', state: 1 },
        ],
      },
    ]);
  });

  it('no_states_without_statecode_or_with_a_single_state', () => {
    expect(statesOf(entity([statuscode]))).toEqual([]);
    expect(statesOf(entity([attr('statecode', { kind: 'choice', options: [{ value: 0, label: 'Active' }] })]))).toEqual([]);
  });

  it('default_status_falls_back_to_the_first_reason', () => {
    const [, inactive] = statesOf(entity([statecode, statuscode]));
    expect(defaultStatusOf(inactive)).toBe(2);
    expect(defaultStatusOf({ ...inactive, defaultStatus: 99 })).toBe(2);
    expect(defaultStatusOf({ ...inactive, defaultStatus: 3 })).toBe(3);
    expect(defaultStatusOf({ ...inactive, statuses: [] })).toBeUndefined();
  });

  it('reads_the_state_of_a_record', () => {
    expect(stateOfRecord({ statecode: 1 })).toBe(1);
    expect(stateOfRecord({})).toBeUndefined();
    expect(stateOfRecord(undefined)).toBeUndefined();
  });
});
