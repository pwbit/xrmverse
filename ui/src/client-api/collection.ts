/** An item of a Client API collection: found by `get(name)` through its `getName()` (form selector items through `getId()`). */
type Named = { getName?: () => string; getId?: () => string };

export interface Collection<T> {
  get(): T[];
  get(name: string): T | null;
  get(index: number): T | null;
  get(predicate: (item: T, index: number) => boolean): T[];
  forEach(delegate: (item: T, index: number) => void): void;
  getLength(): number;
}

/**
 * A read-only collection (Learn, Collections). `nameOf` gives the name `get(name)` matches (by default `getName()`, else `getId()`): `get()` all items, `get(name)` or `get(index)` one item or `null`, and
 * `get(predicate)` the matching items. `items` is read on every call, so a collection can follow a list that changes.
 */
export function collection<T extends Named>(items: () => T[], nameOf: (item: T) => string | undefined = (item) => item.getName?.() ?? item.getId?.()): Collection<T> {
  function get(arg?: unknown): T | T[] | null {
    const all = items();
    if (arg === undefined) return [...all];
    if (typeof arg === 'number') return all[arg] ?? null;
    if (typeof arg === 'function') return all.filter((item, i) => (arg as (item: T, index: number) => boolean)(item, i));
    if (typeof arg === 'string') return all.find((item) => nameOf(item) === arg) ?? null;
    return null;
  }
  return {
    get: get as Collection<T>['get'],
    forEach: (delegate) => items().forEach((item, i) => delegate(item, i)),
    getLength: () => items().length,
  };
}

/** `formContext.getAttribute(arg)` and `getControl(arg)`: no argument is the whole list, else the collection's `get(arg)`. */
export function lookupIn<T extends Named>(c: Collection<T>, arg?: unknown): T | T[] | null {
  return (c.get as (a?: unknown) => T | T[] | null)(arg);
}
