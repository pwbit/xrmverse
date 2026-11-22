import { useEffect, useMemo, useState } from 'react';
import type { ViewPresentation } from '../../parsers/presentationJson';
import type { XrmApi } from '../../shell/api/client';
import { loadColumnKinds, type ColumnKind } from './columnKinds';
import { overlayColumns, resolveModes, type ResolvedModes } from './viewModes';

/** The kinds of an overlay that names no column (stable, so the resolved modes are too). */
const NO_KINDS: Record<string, ColumnKind> = {};

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

export interface ResolvedModesArgs {
  api?: XrmApi;
  /** The logical name of the table the overlay's columns belong to. */
  table: string;
  overlay: ViewPresentation | undefined;
  lcid: number;
  /** What else the kinds depend on (the list's view): another scope reads them again. */
  scope?: string;
  /** What the warning names when the columns cannot be read (`the view` when absent), e.g. a subgrid's table. */
  subject?: string;
  /** Reads the kinds through a cache shared by several controls (a form's); absent, they are read directly. */
  read?: (key: string, load: () => Promise<Record<string, ColumnKind>>) => Promise<Record<string, ColumnKind>>;
}

/**
 * The modes an overlay offers once the kinds of its columns are read (`loadColumnKinds`, then `resolveModes`); undefined while
 * they are read. An overlay that names no column resolves at once, without a request. When the columns cannot be read, the modes
 * are dropped with one warning only.
 */
export function useResolvedModes({ api, table, overlay, lcid, scope = '', subject = 'the view', read }: ResolvedModesArgs): ResolvedModes | undefined {
  const names = useMemo(() => overlayColumns(overlay), [overlay]);
  const key = `${scope}\n${table}\n${names.join('\n')}`;
  const [kinds, setKinds] = useState<{ key: string; kinds: Record<string, ColumnKind>; failed?: boolean }>();
  useEffect(() => {
    if (!api || names.length === 0) return;
    let cancelled = false;
    const load = () => loadColumnKinds(api, table, names, lcid);
    (read ? read(`kinds|${table}|${names.join(',')}`, load) : load()).then(
      (loaded) => !cancelled && setKinds({ key, kinds: loaded }),
      (e: unknown) => {
        if (cancelled) return;
        console.warn('[xrmverse presentation]', `The columns of ${subject} could not be read: ${errorText(e)}`);
        setKinds({ key, kinds: {}, failed: true });
      },
    );
    return () => {
      cancelled = true;
    };
    // key stands for the scope, the table and the overlay's columns; `subject` only words the warning; `read` is left out on purpose: callers pass a new
    // function on every render, and only where the kinds are cached matters, not which closure reads them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, lcid, key]);
  const loaded = names.length === 0 ? NO_KINDS : kinds?.key === key ? kinds.kinds : undefined;
  // When the columns could not be read, the modes are dropped without a warning per mode (the failure was warned once).
  const failed = kinds?.key === key && kinds.failed === true;
  return useMemo(() => (loaded ? resolveModes(overlay, loaded, failed) : undefined), [overlay, loaded, failed]);
}
