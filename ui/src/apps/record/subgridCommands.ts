import type { LookupValue } from '../../admin/tables/formValues';
import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import type { RelationshipMeta } from './subgridQuery';
import { holds } from './privileges';

/** The parent record of a subgrid: what its New and Add existing commands relate the rows to. */
export interface SubgridParent {
  table: string;
  entitySetName: string;
  id: string;
  /** The record's primary name: the lookup's label on the new related row. */
  name: string;
}

/** The related table a subgrid lists. */
export interface SubgridChild {
  table: string;
  entitySetName: string;
}

/** Learn, "Associate and disassociate table rows": an association that already exists answers "Cannot insert duplicate key". */
const DUPLICATE_RECORD = '0x80040237';

type PrivilegeTable = { schemaName?: string; isActivity?: boolean };

/**
 * The privileges the subgrid commands need (#118, ruling R5): New, `prvCreate` on the related table; Add existing, `prvAppend`
 * on the related table and `prvAppendTo` on the parent. Each is checked by name (`RetrieveUserPrivileges`), an activity table
 * by the shared `prv{Verb}Activity` (see `holds`, #196); the server checks the depth when the row is written.
 */
export function subgridCommandsAllowed(held: ReadonlySet<string>, child: PrivilegeTable, parent: PrivilegeTable) {
  const known = (t: PrivilegeTable) => !!t.schemaName || t.isActivity === true;
  if (!known(child) || !known(parent)) return { canNew: false, canAddExisting: false };
  return {
    canNew: holds(held, 'Create', child),
    canAddExisting: holds(held, 'Append', child) && holds(held, 'AppendTo', parent),
  };
}

/**
 * What the related table's create form opens with, from a subgrid's New:
 * - one-to-many: `extraqs` set the lookup to the parent (`{lookup}`, `{lookup}name`, `{lookup}type`, Learn: set column values
 *   using parameters passed to a form);
 * - many-to-many: the association the form makes after its first save (POST `$ref` from the new row's side).
 */
export function subgridNew(relationship: RelationshipMeta, parent: SubgridParent, child: SubgridChild): { extraqs?: Record<string, string>; associate?: { nav: string; target: string } } {
  if ('referencingAttribute' in relationship) {
    const lookup = relationship.referencingAttribute;
    return { extraqs: { [lookup]: parent.id, [`${lookup}name`]: parent.name, [`${lookup}type`]: parent.table } };
  }
  const childIsFirst = relationship.entity1 === child.table && relationship.entity1 !== relationship.entity2;
  const nav = (childIsFirst ? relationship.entity1Nav : relationship.entity2Nav) ?? relationship.schemaName;
  return { associate: { nav, target: `${parent.entitySetName}(${parent.id})` } };
}

/**
 * What the subgrid's table's create form opens with, from the New of a subgrid filtered by a form lookup (X.11): `extraqs` set
 * the target lookup column to the source's value (`{target}`, `{target}name`, `{target}type`), as a one-to-many New does.
 */
export function subgridLookupNew(target: string, value: LookupValue): Record<string, string> {
  return { [target]: value.id, [`${target}name`]: value.name ?? '', [`${target}type`]: value.target };
}

/** Makes the association of a new many-to-many row (after its create). */
export async function associateNew(api: XrmApi, entitySetName: string, id: string, associate: { nav: string; target: string }): Promise<void> {
  await api.associate(`${entitySetName}(${id})`, associate.nav, associate.target);
}

/**
 * The OData filter of Add existing's search: for one-to-many, the rows not yet related to the parent (another parent or none).
 * Many-to-many rows are not filtered (an existing association is skipped when added).
 */
export function addExistingFilter(relationship: RelationshipMeta, parentId: string): string | undefined {
  if (!('referencingAttribute' in relationship)) return undefined;
  const value = `_${relationship.referencingAttribute}_value`;
  return `${value} eq null or ${value} ne ${parentId}`;
}

/**
 * Relates the chosen rows to the parent: one-to-many PATCHes the lookup of each row (`{nav}@odata.bind`); many-to-many POSTs
 * `{parent}/{nav}/$ref`, an association that already exists counting as done. Stops at the first failure, which is thrown.
 */
export async function addExisting(api: XrmApi, relationship: RelationshipMeta, parent: SubgridParent, child: SubgridChild, ids: string[]): Promise<void> {
  for (const id of ids) {
    if ('referencingAttribute' in relationship) {
      const nav = relationship.nav ?? relationship.referencingAttribute;
      await api.patch(`${child.entitySetName}(${id})`, { [`${nav}@odata.bind`]: `/${parent.entitySetName}(${parent.id})` });
      continue;
    }
    const parentIsFirst = relationship.entity1 === parent.table;
    const nav = (parentIsFirst ? relationship.entity1Nav : relationship.entity2Nav) ?? relationship.schemaName;
    try {
      await api.associate(`${parent.entitySetName}(${parent.id})`, nav, `${child.entitySetName}(${id})`);
    } catch (e) {
      if (!(e instanceof XrmError && (e.code === DUPLICATE_RECORD || e.status === 412))) throw e;
    }
  }
}
