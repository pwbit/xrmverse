import { describe, expect, it } from 'vitest';
import type { AttributeMeta } from '../../admin/tables/metadata';
import type { LookupRelationship } from '../../admin/tables/tableApi';
import { initializeFromPath, mappedValues } from './initializeFrom';

const PARENT = 'aaaaaaaa-0000-0000-0000-000000000001';

const attr = (logicalName: string, kind: AttributeMeta['kind'], over: Partial<AttributeMeta> = {}): AttributeMeta => ({
  logicalName,
  displayName: logicalName,
  kind,
  typeName: kind,
  requiredLevel: 'None',
  required: false,
  validForCreate: true,
  validForUpdate: true,
  validForRead: true,
  isPrimaryId: false,
  isPrimaryName: false,
  isCustom: true,
  secured: false,
  targets: [],
  options: [],
  ...over,
});

const attributes = [
  attr('xv_parentid', 'lookup', { targets: ['xv_project'] }),
  attr('xv_code', 'string'),
  attr('xv_qty', 'int'),
  attr('xv_done', 'bool'),
  attr('xv_stage', 'choice'),
  attr('xv_hidden', 'string'),
  attr('xv_given', 'string'),
];
const lookups: LookupRelationship[] = [
  { schemaName: 'xv_project_task', referencingEntity: 'xv_task', referencingAttribute: 'xv_parentid', referencedEntity: 'xv_project', nav: 'xv_ParentId' },
];

describe('initializeFrom', () => {
  it('initializeFromPath_uses_aliases', () => {
    expect(initializeFromPath({ set: 'xv_projects', id: PARENT }, 'xv_task')).toBe(
      'InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)' +
        `?@p1=${encodeURIComponent(`{'@odata.id':'xv_projects(${PARENT})'}`)}` +
        `&@p2=${encodeURIComponent("'xv_task'")}` +
        `&@p3=${encodeURIComponent("Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'")}`,
    );
  });

  it('mappedValues_reads_binds_and_plain_values_of_the_form_columns', () => {
    const response = {
      '@odata.context': 'x',
      '@odata.type': '#Microsoft.Dynamics.CRM.xv_task',
      'xv_ParentId@odata.bind': `xv_projects(${PARENT.toUpperCase()})`,
      xv_code: 'C-1',
      xv_qty: 7,
      xv_done: true,
      xv_stage: 2,
      xv_hidden: 'not on the form',
      xv_given: 'set by a form parameter',
    };
    const { values, lookups: named } = mappedValues(
      attributes,
      ['xv_parentid', 'xv_code', 'xv_qty', 'xv_done', 'xv_stage', 'xv_given'],
      response,
      lookups,
      new Set(['xv_given']),
    );
    expect(values).toEqual({
      xv_parentid: { id: PARENT, target: 'xv_project', set: 'xv_projects', nav: 'xv_ParentId' },
      xv_code: 'C-1',
      xv_qty: '7',
      xv_done: true,
      xv_stage: 2,
    });
    expect(named).toEqual([{ attribute: 'xv_parentid', set: 'xv_projects', id: PARENT }]);
  });

  it('mappedValues_leaves_out_a_bind_of_an_unknown_navigation_or_a_lookup_off_the_form', () => {
    const response = { 'other@odata.bind': `xv_projects(${PARENT})`, 'xv_ParentId@odata.bind': `xv_projects(${PARENT})` };
    expect(mappedValues(attributes, ['xv_code'], response, lookups, new Set()).values).toEqual({});
  });
});
