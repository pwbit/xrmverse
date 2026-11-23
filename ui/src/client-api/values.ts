import type { FieldValue, LookupValue } from '../admin/tables/formValues';
import type { Party } from '../admin/tables/partyLists';
import { isNumeric, isUserLocal, type AttributeMeta } from '../admin/tables/metadata';
import { editorText, editorWallClock, wallClockOf, wallClockToUtc, type WallClock } from '../apps/timeZones';

/**
 * The value shapes of the Client API (Learn, attribute `getValue`): a lookup is an array of `{ id, name, entityType }` with
 * the id braced and upper case, a choice a number, a multi-select choice an array of numbers, a boolean `true` or `false`, a
 * date a `Date`, money and numbers a `number`, text a string; an empty value is `null`.
 */
export interface ClientLookup {
  id: string;
  name?: string | null;
  entityType: string;
}
export type ClientValue = string | number | boolean | Date | number[] | ClientLookup[] | null;

/** `{GUID}` upper case, as `getId()` and lookup values show an id. */
export const bracedUpper = (id: string): string => `{${id.replace(/[{}]/g, '').toUpperCase()}}`;
/** The id as the Web API takes it: no braces, lower case. */
export const plainId = (id: string): string => id.replace(/[{}]/g, '').trim().toLowerCase();

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** UTC, the zone of a form without the user's settings. */
const UTC = 'Etc/UTC';

/** A wall clock in the browser's local fields (Learn: a time zone independent or date only value, as the user sees it). */
function browserDate(w: WallClock): Date {
  const d = new Date(2000, w.month - 1, w.day, w.hour, w.minute);
  d.setFullYear(w.year); // the constructor maps years 0-99 to 1900-1999
  return d;
}

/**
 * A date editor value as the Client API's `Date` (#131, ruling Z6; Learn, "Get values with Client API"): a User Local value is
 * the instant (its UTC fields are the stored UTC value), read from `zone`'s clock; a date only or time zone independent value
 * carries the stored clock in the browser's local fields. Text that is not a date is null.
 */
function toDate(a: AttributeMeta, text: string, zone: string): Date | null {
  const wall = editorWallClock(a.dateOnly ? text.slice(0, 10) : text.slice(0, 16));
  if (!wall) return null;
  const d = isUserLocal(a) ? wallClockToUtc(wall, zone) : browserDate(wall);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** A Client API `Date` as a date editor value, the reverse of {@link toDate}. */
function fromDate(a: AttributeMeta, d: Date, zone: string): string {
  const wall: WallClock = isUserLocal(a)
    ? wallClockOf(d, zone)
    : { year: d.getFullYear(), month: d.getMonth() + 1, day: d.getDate(), hour: d.getHours(), minute: d.getMinutes() };
  return editorText(wall, a.dateOnly === true);
}

/** UI-1b's editor value of `a` in the Client API shape; a User Local date is read in `zone` (the user's time zone, #131). */
export function toClientValue(a: AttributeMeta, v: FieldValue | undefined, zone: string = UTC): ClientValue {
  if (v === undefined || v === null) return a.kind === 'bool' ? false : null;
  switch (a.kind) {
    case 'lookup': {
      const l = v as LookupValue;
      return [{ id: bracedUpper(l.id), name: l.name ?? null, entityType: l.target }];
    }
    case 'bool':
      return v === true;
    case 'choice':
      return typeof v === 'number' ? v : null;
    case 'choices':
      return Array.isArray(v) && v.length > 0 ? [...(v as number[])] : null;
    case 'partylist': {
      // The resolved parties as lookups (plan 4.1b, task 9); an address alone has no record to name and is left out.
      const parties = Array.isArray(v) ? (v as Party[]).filter((p) => p.id && p.logicalName) : [];
      return parties.length > 0 ? parties.map((p) => ({ id: bracedUpper(p.id!), name: p.name ?? null, entityType: p.logicalName! })) : null;
    }
    case 'datetime':
      return typeof v === 'string' && v ? toDate(a, v, zone) : null;
    default:
      if (isNumeric(a.kind)) {
        const text = String(v).trim();
        const n = Number(text);
        return text === '' || !Number.isFinite(n) ? null : n;
      }
      return typeof v === 'string' && v !== '' ? v : null;
  }
}

/** Thrown when a script passes `setValue` a value of the wrong shape: the attribute keeps its value. */
const wrong = (a: AttributeMeta, value: unknown) =>
  new Error(`setValue on ${a.logicalName}: ${JSON.stringify(value) ?? String(value)} is not a valid value for a ${a.kind} column.`);

/**
 * A Client API value as UI-1b's editor value of `a`. A lookup id may come with or without braces, in any case; `nav` is the
 * relationship's navigation property for the target, when the form knows it; a User Local date is written as `zone`'s clock
 * (#131). Throws on a value of the wrong shape.
 */
export function fromClientValue(a: AttributeMeta, value: unknown, nav: (target: string) => string | undefined, zone: string = UTC): FieldValue {
  if (value === null || value === undefined) return a.kind === 'choices' ? [] : a.kind === 'bool' ? false : null;
  switch (a.kind) {
    case 'lookup': {
      if (!Array.isArray(value)) throw wrong(a, value);
      if (value.length === 0) return null;
      const first = value[0] as Partial<ClientLookup> | null;
      const id = typeof first?.id === 'string' ? plainId(first.id) : '';
      const target = typeof first?.entityType === 'string' ? first.entityType : '';
      if (!GUID.test(id) || !target) throw wrong(a, value);
      const name = typeof first?.name === 'string' ? first.name : undefined;
      const lookup: LookupValue = { id, target, name, nav: nav(target) };
      return lookup;
    }
    case 'bool':
      if (typeof value !== 'boolean') throw wrong(a, value);
      return value;
    case 'choice':
      if (typeof value !== 'number' || !Number.isInteger(value)) throw wrong(a, value);
      return value;
    case 'choices':
      if (!Array.isArray(value) || value.some((x) => typeof x !== 'number')) throw wrong(a, value);
      return [...(value as number[])];
    case 'datetime': {
      if (!(value instanceof Date) || Number.isNaN(value.getTime())) throw wrong(a, value);
      return fromDate(a, value, zone);
    }
    default:
      if (isNumeric(a.kind)) {
        if (typeof value !== 'number' || !Number.isFinite(value)) throw wrong(a, value);
        return String(value);
      }
      if (a.kind === 'string' || a.kind === 'memo') {
        if (typeof value !== 'string') throw wrong(a, value);
        return value;
      }
      // A file, an image, a party list or a column the form cannot edit takes no value from a script.
      throw new Error(`setValue on ${a.logicalName}: a ${a.kind} column cannot be set by a script in this version.`);
  }
}
