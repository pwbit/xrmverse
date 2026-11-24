import { normaliseId } from '../../parsers/formXml';

/** A subgrid filtered by a lookup of the form: `source` is the form column holding the lookup, `target` the subgrid table's lookup column. */
export interface SubgridFilter {
  source: string;
  target: string;
}

/** Filters by normalised form id, then by control id (lowercase). */
export type SubgridFilterMap = Record<string, Record<string, SubgridFilter>>;

const PREFIX = '[xrmverse subgrid filter]';
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
/** An empty map with no prototype, so keys that come from resource data ("__proto__", "constructor") are plain keys. */
const dict = <T>(): Record<string, T> => Object.create(null) as Record<string, T>;
const norm = (s: string) => s.trim().toLowerCase();

/** The resource names of a JSON array of non-empty strings; an empty value is none, anything else invalid warns and is none. */
export function parseSubgridFilterNames(value: string | undefined): string[] {
  if (value === undefined || value.trim() === '') return [];
  try {
    const parsed: unknown = JSON.parse(value);
    if (Array.isArray(parsed) && parsed.every((n) => typeof n === 'string' && n.trim() !== '')) {
      return (parsed as string[]).map((n) => n.trim());
    }
  } catch {
    // Reported below.
  }
  console.warn(PREFIX, 'The list of subgrid filter resources is not a JSON array of names, so it is ignored.');
  return [];
}

/** The app's names first, then the environment's, without case-insensitive duplicates. */
export function mergeSubgridFilterNames(app: string[], env: string[]): string[] {
  const seen = new Set<string>();
  return [...app, ...env].filter((name) => {
    const key = name.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Reads one resource's `{ forms: { formId: { controlId: { source, target } } } }`; what is not usable is dropped and reported in `warnings`. */
export function parseSubgridFilters(json: unknown, origin: string): { map: SubgridFilterMap; warnings: string[] } {
  const map: SubgridFilterMap = dict();
  const warnings: string[] = [];
  if (!isObject(json) || !isObject(json.forms)) {
    return { map, warnings: [`${origin}: expected an object with a "forms" object, so the resource is ignored.`] };
  }
  for (const key of Object.keys(json)) {
    if (key !== 'forms') warnings.push(`${origin}: unknown key "${key}" ignored.`);
  }
  for (const [formKey, controls] of Object.entries(json.forms)) {
    if (!isObject(controls)) {
      warnings.push(`${origin}: form "${formKey}" is not an object, so it is ignored.`);
      continue;
    }
    const formId = normaliseId(formKey);
    for (const [controlKey, entry] of Object.entries(controls)) {
      const source = isObject(entry) && typeof entry.source === 'string' ? norm(entry.source) : '';
      const target = isObject(entry) && typeof entry.target === 'string' ? norm(entry.target) : '';
      if (!source || !target) {
        warnings.push(`${origin}: control "${controlKey}" of form "${formKey}" needs a "source" and a "target" column, so it is ignored.`);
        continue;
      }
      const controlId = norm(controlKey);
      const forForm = (map[formId] ??= dict());
      if (Object.hasOwn(forForm, controlId)) {
        warnings.push(`${origin}: control "${controlKey}" of form "${formKey}" is given more than once; the last entry is used.`);
      }
      forForm[controlId] = { source, target };
    }
  }
  return { map, warnings };
}

/** Merges the maps in order: the first one that defines a form's control wins. */
export function mergeSubgridFilters(maps: SubgridFilterMap[]): SubgridFilterMap {
  const merged: SubgridFilterMap = dict();
  for (const map of maps) {
    for (const [formId, controls] of Object.entries(map)) {
      const target = (merged[formId] ??= dict());
      for (const [controlId, filter] of Object.entries(controls)) {
        if (!Object.hasOwn(target, controlId)) target[controlId] = filter;
      }
    }
  }
  return merged;
}
