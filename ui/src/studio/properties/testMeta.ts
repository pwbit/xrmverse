import { attr, entity } from '../../admin/tables/testAttrs';
import type { EntityMeta } from '../../admin/tables/metadata';
import type { MetadataSource } from './useStudioMetadata';
import type { ValueContext } from './valueModel';

/** `account`, `contact` and `task` with a few columns of each kind. */
export const TABLES: Record<string, EntityMeta> = {
  account: entity(
    [
      attr('accountid', { kind: 'unsupported', typeName: 'Uniqueidentifier', isPrimaryId: true }),
      attr('name', { displayName: 'Account Name', isPrimaryName: true }),
      attr('numberofemployees', { displayName: 'Employees', kind: 'int', typeName: 'Integer' }),
      attr('revenue', { displayName: 'Revenue', kind: 'money', typeName: 'Money' }),
      attr('donotemail', { displayName: 'Do not email', kind: 'bool', typeName: 'Boolean', options: [{ value: 0, label: 'Allow' }, { value: 1, label: 'Do Not Allow' }] }),
      attr('industrycode', { displayName: 'Industry', kind: 'choice', typeName: 'Picklist', options: [{ value: 1, label: 'Accounting' }, { value: 2, label: 'Agriculture' }] }),
      attr('parentaccountid', { displayName: 'Parent Account', kind: 'lookup', typeName: 'Lookup', targets: ['account'] }),
      attr('primarycontactid', { displayName: 'Primary Contact', kind: 'lookup', typeName: 'Lookup', targets: ['contact'] }),
      attr('statecode', { displayName: 'Status', kind: 'choice', typeName: 'State', options: [{ value: 0, label: 'Active', defaultStatus: 1 }, { value: 1, label: 'Inactive', defaultStatus: 2 }] }),
      attr('statuscode', { displayName: 'Status Reason', kind: 'choice', typeName: 'Status', options: [{ value: 1, label: 'Active', state: 0 }, { value: 2, label: 'Inactive', state: 1 }] }),
    ],
    { logicalName: 'account', entitySetName: 'accounts', displayName: 'Account', primaryIdAttribute: 'accountid', primaryNameAttribute: 'name', isCustom: false },
  ),
  contact: entity(
    [attr('lastname', { displayName: 'Last Name' }), attr('emailaddress1', { displayName: 'Email' }), attr('birthdate', { displayName: 'Birthday', kind: 'datetime', typeName: 'DateTime' })],
    { logicalName: 'contact', entitySetName: 'contacts', displayName: 'Contact', primaryIdAttribute: 'contactid', primaryNameAttribute: 'fullname', isCustom: false },
  ),
  task: entity([attr('subject', { displayName: 'Subject' }), attr('scheduledend', { displayName: 'Due', kind: 'datetime', typeName: 'DateTime' })], {
    logicalName: 'task',
    entitySetName: 'tasks',
    displayName: 'Task',
    primaryIdAttribute: 'activityid',
    primaryNameAttribute: 'subject',
    isCustom: false,
  }),
};

/** The metadata of `TABLES`, at once. */
export const META: MetadataSource = {
  entity: (name) => (name ? TABLES[name] : undefined),
  tables: () => Object.values(TABLES),
};

export function context(over: Partial<ValueContext> = {}): ValueContext {
  return { kind: 'workflow', entity: 'account', meta: META, earlier: [], inputArgs: [], outputArgs: [], ...over };
}

export const column = (table: string, name: string) => TABLES[table].attributes.find((a) => a.logicalName === name)!;
