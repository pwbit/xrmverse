import { isFileKind, isSystemColumn, recordKey, type AttributeMeta } from './metadata';

const COUNT = 5;

/** Non-system, readable, non-key, non-primary-name attributes the list can show (a file id or a thumbnail is no cell). */
export const isListCandidate = (a: AttributeMeta): boolean =>
  a.validForRead &&
  !a.isPrimaryId &&
  !a.isPrimaryName &&
  !a.attributeOf &&
  a.kind !== 'unsupported' &&
  // A party list is not a property of the record (plan 2.11); a note body is the whole attached file.
  a.kind !== 'partylist' &&
  !a.hidden &&
  !isFileKind(a.kind) &&
  !isSystemColumn(a.logicalName);

/**
 * Ruling R3: the 5 candidate attributes with the most non-null values in `records` (the first, unfiltered page of the
 * table), ties broken by display name. Returns logical names, most used first.
 */
export function mostUsed(records: Record<string, unknown>[], attributes: AttributeMeta[]): string[] {
  const counted = attributes.filter(isListCandidate).map((a) => ({
    a,
    count: records.reduce((n, r) => (r[recordKey(a)] === null || r[recordKey(a)] === undefined ? n : n + 1), 0),
  }));
  counted.sort(
    (x, y) =>
      y.count - x.count ||
      x.a.displayName.localeCompare(y.a.displayName, 'en') ||
      x.a.logicalName.localeCompare(y.a.logicalName, 'en'),
  );
  return counted.slice(0, COUNT).map((c) => c.a.logicalName);
}

const cache = new Map<string, Promise<string[]>>();
/** The sign-in the cached choices belong to: another user may read different columns, so a new sign-in starts over. */
let cacheSession: object | undefined;

/**
 * The choice for `table`, computed once per sign-in (`session`, the signed-in `XrmApi`) by `compute`. The promise is
 * cached, so concurrent callers share one fetch; a failed computation is dropped so the next call tries again.
 */
export function cachedMostUsed(session: object, table: string, compute: () => Promise<string[]>): Promise<string[]> {
  if (cacheSession !== session) {
    cache.clear();
    cacheSession = session;
  }
  let pending = cache.get(table);
  if (!pending) {
    pending = compute();
    cache.set(table, pending);
    pending.catch(() => {
      if (cache.get(table) === pending) cache.delete(table);
    });
  }
  return pending;
}

/** Forgets the choice (the table's columns changed). */
export const forgetMostUsed = (table: string): void => void cache.delete(table);
