import { isFileKind, isSystemColumn, type AttrKind, type AttributeMeta, type EntityMeta } from './metadata';

export type FieldGroup = 'Text' | 'Numbers' | 'Dates' | 'Choices' | 'Lookups' | 'Files' | 'Other';

export interface FormGroup {
  group: FieldGroup;
  fields: AttributeMeta[];
}

const ORDER: FieldGroup[] = ['Text', 'Numbers', 'Dates', 'Choices', 'Lookups', 'Files', 'Other'];

const GROUP_OF: Record<AttrKind, FieldGroup> = {
  string: 'Text',
  memo: 'Text',
  int: 'Numbers',
  bigint: 'Numbers',
  decimal: 'Numbers',
  double: 'Numbers',
  money: 'Numbers',
  datetime: 'Dates',
  bool: 'Choices',
  choice: 'Choices',
  choices: 'Choices',
  lookup: 'Lookups',
  partylist: 'Lookups',
  file: 'Files',
  image: 'Files',
  unsupported: 'Other',
};

/**
 * True when the form shows the attribute: writable on create or update, not the key, an audit column, another attribute's
 * companion or a column the generic UI never reads (`hidden`). A file or image column is shown whatever its IsValidFor flags (a file column is valid for neither: only its
 * upload writes it).
 */
export const isFormField = (a: AttributeMeta): boolean =>
  !a.hidden &&
  (a.validForCreate || a.validForUpdate || isFileKind(a.kind)) && !a.isPrimaryId && !a.attributeOf && !isSystemColumn(a.logicalName);

/** The form's fields grouped by type (empty groups left out), the primary name first in its group. */
export function buildFormModel(entity: EntityMeta): FormGroup[] {
  const groups = ORDER.map((group) => ({ group, fields: [] as AttributeMeta[] }));
  for (const a of entity.attributes.filter(isFormField)) groups.find((g) => g.group === GROUP_OF[a.kind])!.fields.push(a);
  for (const g of groups) g.fields.sort((a, b) => Number(b.isPrimaryName) - Number(a.isPrimaryName));
  return groups.filter((g) => g.fields.length > 0);
}
