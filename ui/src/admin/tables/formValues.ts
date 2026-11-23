import type { en } from '../../shell/i18n/en';
import { editorText, editorWallClock, utcText, wallClockOf, wallClockToUtc } from '../../apps/timeZones';
import { isFileKind, isNumeric, isUserLocal, isWholeNumber, recordKey, type AttributeMeta } from './metadata';
import { sameParties, type Party } from './partyLists';

/** A lookup's value: the record picked, with what the bind needs (`nav` is the relationship's navigation property). */
export interface LookupValue {
  id: string;
  /** Logical name of the table the record belongs to. */
  target: string;
  /** Entity set of the target; known for a record picked in this session. */
  set?: string;
  name?: string;
  nav?: string;
}

/**
 * What an editor holds: text and numbers as strings (so partial input survives), a boolean, a choice value, the
 * values of a multi-select, a date as `YYYY-MM-DD` or `YYYY-MM-DDTHH:mm` (a User Local column in the form's time zone, UTC
 * without one; the other behaviours as stored), or a lookup. A file column holds its file
 * id and an image column its thumbnail (base64), as the record carries them; both are read-only here. A party list holds its
 * parties (plan 4.1b, task 9), written by the record form through the party navigation, never by the bodies built here.
 */
export type FieldValue = string | boolean | number | number[] | Party[] | LookupValue | null;
export type FormValues = Record<string, FieldValue>;

/** A value the form cannot send; names the field and carries a catalogue key (rendered with `t()` next to the editor). */
export class FieldError extends Error {
  constructor(
    readonly field: string,
    readonly key: keyof typeof en,
    readonly params: Record<string, string> = {},
  ) {
    super(key);
    this.name = 'FieldError';
  }
}

const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
const LOOKUP_LOGICAL_NAME = '@Microsoft.Dynamics.CRM.lookuplogicalname';

export type NavFor = (attribute: string, target: string) => string | undefined;

/**
 * The editor value of a stored date (#131, ruling Z6): a User Local value as `zone`'s clock shows it (a date-only one: the day
 * there); any other value, or any value without a zone, as stored.
 */
function dateFromRecord(a: AttributeMeta, raw: string, zone: string | undefined): string {
  const instant = new Date(raw);
  if (zone && isUserLocal(a) && raw.length > 10 && !Number.isNaN(instant.getTime())) return editorText(wallClockOf(instant, zone), a.dateOnly === true);
  return a.dateOnly ? raw.slice(0, 10) : raw.slice(0, 16);
}

/** What a date editor value sends: a User Local value with a zone as the UTC instant of that clock, anything else as before. */
function dateToSend(a: AttributeMeta, text: string, zone: string | undefined): string {
  const wall = zone && isUserLocal(a) ? editorWallClock(text) : null;
  if (wall) return utcText(wallClockToUtc(wall, zone!));
  return a.dateOnly ? text : `${text}:00Z`;
}

function fromRecord(a: AttributeMeta, record: Record<string, unknown>, navFor: NavFor, zone?: string): FieldValue {
  const raw = record[recordKey(a)];
  if (raw === null || raw === undefined) return a.kind === 'choices' ? [] : null;
  switch (a.kind) {
    case 'lookup': {
      const key = recordKey(a);
      const target = String(record[key + LOOKUP_LOGICAL_NAME] ?? a.targets[0] ?? '');
      const name = record[key + FORMATTED];
      return { id: String(raw), target, name: typeof name === 'string' ? name : undefined, nav: navFor(a.logicalName, target) };
    }
    case 'choices':
      return String(raw)
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean)
        .map(Number);
    case 'datetime':
      return dateFromRecord(a, String(raw), zone);
    case 'bool':
      return raw === true;
    case 'choice':
      return Number(raw);
    default:
      return String(raw);
  }
}

/** The editor values of a record, for the given fields; User Local dates in `zone` (the app: the user's; the admin: none, UTC). */
export function valuesFromRecord(fields: AttributeMeta[], record: Record<string, unknown>, navFor: NavFor, zone?: string): FormValues {
  return Object.fromEntries(fields.map((a) => [a.logicalName, fromRecord(a, record, navFor, zone)]));
}

const isEmpty = (v: FieldValue | undefined): boolean => v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0);

function encodeNumber(a: AttributeMeta, v: FieldValue): number {
  const text = String(v).trim();
  const n = Number(text);
  if (text === '' || !Number.isFinite(n) || (isWholeNumber(a.kind) && !Number.isInteger(n))) {
    throw new FieldError(a.logicalName, isWholeNumber(a.kind) ? 'form.err.wholeNumber' : 'form.err.number', { value: text });
  }
  return n;
}

/** The JSON property and value that send `value` for `a` (never called for an empty value on create). */
function encode(a: AttributeMeta, value: FieldValue, zone?: string): [string, unknown] {
  const name = a.logicalName;
  if (a.kind === 'lookup') {
    const lookup = value as LookupValue | null;
    // A lookup can only be bound through the navigation property of its relationship (never guessed from the column name).
    if (!lookup) throw new FieldError(name, 'form.err.noRecord', { label: a.displayName });
    if (!lookup.nav) throw new FieldError(name, 'form.err.noRelationship', { label: a.displayName, target: lookup.target });
    if (!lookup.set) throw new FieldError(name, 'form.err.noSet', { target: lookup.target });
    return [`${lookup.nav}@odata.bind`, `/${lookup.set}(${lookup.id})`];
  }
  if (isEmpty(value)) return [name, null];
  if (isNumeric(a.kind)) return [name, encodeNumber(a, value)];
  switch (a.kind) {
    case 'choices':
      return [name, (value as number[]).join(',')];
    case 'datetime':
      return [name, dateToSend(a, String(value), zone)];
    default:
      return [name, value];
  }
}

/** The bind property that clears a lookup: it needs the navigation property the original value carried. */
function bindName(a: AttributeMeta, nav: string | undefined): string {
  if (!nav) throw new FieldError(a.logicalName, 'form.err.cannotClear', { label: a.displayName });
  return `${nav}@odata.bind`;
}

/** A file or image is never in the record body: its editor uploads and deletes it on its own. */
const canWrite = (a: AttributeMeta, mode: 'create' | 'update') =>
  a.kind !== 'unsupported' &&
  // A party list is read-only in the generic UI: its rows are written through the party navigation (plan 2.11).
  a.kind !== 'partylist' && !isFileKind(a.kind) && (mode === 'create' ? a.validForCreate : a.validForUpdate);

/** `POST` body: the fields the user filled in (an empty field is left out; `false` is a value); User Local dates read in `zone`. */
export function createBody(fields: AttributeMeta[], values: FormValues, zone?: string): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  for (const a of fields) {
    if (!canWrite(a, 'create') || isEmpty(values[a.logicalName])) continue;
    const [key, value] = encode(a, values[a.logicalName], zone);
    body[key] = value;
  }
  return body;
}

/** True when two editor values of `a` are equal (empty values are equal to each other). */
export function sameValue(a: AttributeMeta, x: FieldValue | undefined, y: FieldValue | undefined): boolean {
  if (isEmpty(x) && isEmpty(y)) return true;
  if (isEmpty(x) || isEmpty(y)) return false;
  if (isNumeric(a.kind)) return Number(x) === Number(y);
  switch (a.kind) {
    case 'lookup':
      return (x as LookupValue).id === (y as LookupValue).id;
    case 'choices':
      return [...(x as number[])].sort().join() === [...(y as number[])].sort().join();
    case 'partylist':
      return sameParties(x as Party[], y as Party[]);
    default:
      return x === y;
  }
}

/**
 * `PATCH` body: only the fields whose value differs from `original` (the record as loaded), and the fields named in `always`
 * whatever their value (a script's submit mode `always`); User Local dates read in `zone`.
 */
export function changedBody(
  fields: AttributeMeta[],
  original: FormValues,
  current: FormValues,
  always: ReadonlySet<string> = new Set(),
  zone?: string,
): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  for (const a of fields) {
    if (!canWrite(a, 'update')) continue;
    if (!always.has(a.logicalName) && sameValue(a, original[a.logicalName], current[a.logicalName])) continue;
    const now = current[a.logicalName];
    if (a.kind === 'lookup' && isEmpty(now)) {
      body[bindName(a, (original[a.logicalName] as LookupValue | null)?.nav)] = null;
      continue;
    }
    const [key, value] = encode(a, now, zone);
    body[key] = value;
  }
  return body;
}
