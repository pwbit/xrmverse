import { newGuid as randomGuid } from '../../shell/api/uuid';

/** A new id as FormXML writes it: `{guid}` in lower case. */
export function newGuid(): string {
  return `{${randomGuid().toLowerCase()}}`;
}
