import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { webResourceUrl } from '../../client-api/scriptHost';
import { normaliseId } from '../../parsers/formXml';
import { mergeSubgridFilters, parseSubgridFilters, type SubgridFilter, type SubgridFilterMap } from './subgridFilters';

const PREFIX = '[xrmverse subgrid filter]';
/**
 * Where the filters stand: `pending` while the resources are read, `failed` when one of them could not be read (the others
 * still apply), `ready` otherwise (also when the app names none).
 */
export type SubgridFiltersStatus = 'pending' | 'failed' | 'ready';
const SubgridFiltersContext = createContext<{ map?: SubgridFilterMap; status: SubgridFiltersStatus }>({ status: 'ready' });

/**
 * Reads one resource; a failed fetch or parse is one console warning and no filters (undefined), so the other resources
 * still apply.
 */
async function readResource(name: string, signal: AbortSignal): Promise<SubgridFilterMap | undefined> {
  try {
    const response = await fetch(webResourceUrl(name), { credentials: 'same-origin', signal });
    if (!response.ok) {
      console.warn(PREFIX, `${name}: the resource could not be read (status ${response.status}), so it is ignored.`);
      return undefined;
    }
    const { map, warnings } = parseSubgridFilters(JSON.parse(await response.text()), name);
    for (const w of warnings) console.warn(PREFIX, w);
    return map;
  } catch (e) {
    if (signal.aborted) return undefined;
    console.warn(PREFIX, `${name}: ${e instanceof Error ? e.message : String(e)}, so the resource is ignored.`);
    return undefined;
  }
}

/**
 * Loads the subgrid filter web resources `names` (X.11) once per distinct list and offers them to the forms below. It
 * never blocks rendering: until the resources are in, no form has a filter and the status is `pending`, so a subgrid that
 * could be filtered waits instead of showing the whole view.
 */
export function SubgridFiltersProvider({ names, children }: { names: string[]; children: ReactNode }) {
  const [loaded, setLoaded] = useState<{ key: string; map: SubgridFilterMap; failed: boolean }>();
  const key = JSON.stringify(names);
  useEffect(() => {
    const list = JSON.parse(key) as string[];
    if (list.length === 0) return;
    const abort = new AbortController();
    void Promise.all(list.map((name) => readResource(name, abort.signal))).then((maps) => {
      if (abort.signal.aborted) return;
      const read = maps.filter((m): m is SubgridFilterMap => !!m);
      setLoaded({ key, map: mergeSubgridFilters(read), failed: read.length < maps.length });
    });
    return () => abort.abort();
  }, [key]);
  const value = useMemo<{ map?: SubgridFilterMap; status: SubgridFiltersStatus }>(() => {
    if (key === '[]') return { status: 'ready' };
    if (loaded?.key !== key) return { status: 'pending' };
    return { map: loaded.map, status: loaded.failed ? 'failed' : 'ready' };
  }, [key, loaded]);
  return <SubgridFiltersContext.Provider value={value}>{children}</SubgridFiltersContext.Provider>;
}

/** Whether the app's subgrid filters are still being read, could not all be read, or are ready. */
export function useSubgridFiltersStatus(): SubgridFiltersStatus {
  return useContext(SubgridFiltersContext).status;
}

/** The subgrid filters of a form by control id (lowercase); undefined when none is configured for it. */
export function useSubgridFilters(formId: string | undefined): Record<string, SubgridFilter> | undefined {
  const { map } = useContext(SubgridFiltersContext);
  if (!map || !formId) return undefined;
  const id = normaliseId(formId);
  return Object.hasOwn(map, id) ? map[id] : undefined;
}
