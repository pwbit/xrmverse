import { describe, expect, it } from 'vitest';
import { addExistingFilter, subgridCommandsAllowed, subgridLookupNew, subgridNew } from './subgridCommands';

const parent = { table: 'xv_project', entitySetName: 'xv_projects', id: 'p1', name: 'Alpha' };
const oneToMany = { schemaName: 'xv_project_task', referencingEntity: 'xv_task', referencingAttribute: 'xv_projectid', referencedEntity: 'xv_project' };
const manyToMany = { schemaName: 'xv_project_tag', entity1: 'xv_project', entity2: 'xv_tag', entity1Nav: 'xv_project_tags', entity2Nav: 'xv_tag_projects' };

describe('subgridCommands', () => {
  it('privileges_by_name', () => {
    const held = new Set(['prvCreatexv_Task', 'prvAppendxv_Task', 'prvAppendToxv_Project']);
    expect(subgridCommandsAllowed(held, { schemaName: 'xv_Task' }, { schemaName: 'xv_Project' })).toEqual({ canNew: true, canAddExisting: true });
    expect(subgridCommandsAllowed(new Set(['prvAppendxv_Task']), { schemaName: 'xv_Task' }, { schemaName: 'xv_Project' })).toEqual({ canNew: false, canAddExisting: false });
    expect(subgridCommandsAllowed(held, {}, { schemaName: 'xv_Project' })).toEqual({ canNew: false, canAddExisting: false });
  });

  it('an activity child or parent is met by the shared activity privileges (#196)', () => {
    const held = new Set(['prvCreateActivity', 'prvAppendActivity', 'prvAppendToActivity']);
    expect(subgridCommandsAllowed(held, { schemaName: 'Email', isActivity: true }, { schemaName: 'Account', isActivity: false })).toEqual({ canNew: true, canAddExisting: false });
    expect(subgridCommandsAllowed(held, { schemaName: 'Email', isActivity: true }, { schemaName: 'Email', isActivity: true })).toEqual({ canNew: true, canAddExisting: true });
    expect(subgridCommandsAllowed(new Set(['prvReadActivity']), { schemaName: 'Email', isActivity: true }, { schemaName: 'Account' })).toEqual({ canNew: false, canAddExisting: false });
  });

  it('new_sets_the_lookup_or_associates_from_the_new_rows_side', () => {
    expect(subgridNew(oneToMany, parent, { table: 'xv_task', entitySetName: 'xv_tasks' })).toEqual({
      extraqs: { xv_projectid: 'p1', xv_projectidname: 'Alpha', xv_projectidtype: 'xv_project' },
    });
    expect(subgridNew(manyToMany, parent, { table: 'xv_tag', entitySetName: 'xv_tags' })).toEqual({
      associate: { nav: 'xv_tag_projects', target: 'xv_projects(p1)' },
    });
    // Self-referential: the new row is entity 2's side, as the subgrid lists it; without navigation names, the schema name.
    const self = { schemaName: 'xv_project_related', entity1: 'xv_project', entity2: 'xv_project' };
    expect(subgridNew(self, parent, { table: 'xv_project', entitySetName: 'xv_projects' })).toEqual({
      associate: { nav: 'xv_project_related', target: 'xv_projects(p1)' },
    });
  });

  it('add_existing_filter_only_for_one_to_many', () => {
    expect(addExistingFilter(oneToMany, 'p1')).toBe('_xv_projectid_value eq null or _xv_projectid_value ne p1');
    expect(addExistingFilter(manyToMany, 'p1')).toBeUndefined();
  });

  it('lookup_new_fills_the_target_column_from_the_source_value', () => {
    expect(subgridLookupNew('xv_accountid', { id: 'a1', target: 'account', name: 'Contoso' })).toEqual({
      xv_accountid: 'a1',
      xv_accountidname: 'Contoso',
      xv_accountidtype: 'account',
    });
    // A value without its name (set by a script) still fills the column.
    expect(subgridLookupNew('xv_customerid', { id: 'c1', target: 'contact' })).toEqual({ xv_customerid: 'c1', xv_customeridname: '', xv_customeridtype: 'contact' });
  });
});
