import { useCallback, useEffect, useMemo, useState } from 'react';
import { withAttributes, withDateRange, withPaging } from '../../parsers/fetchXml';
import type { XrmApi } from '../../shell/api/client';
import { runView } from '../runView';
import { BOARD_LIMIT } from './viewModes';

export interface ModeRange {
  attribute: string;
  from: string;
  to: string;
  /** The column is DateOnly (see `withDateRange`). */
  dateOnly?: boolean;
  /** An end column: records spanning the range match too (see `withDateRange`). */
  end?: string;
}

export type ModeRecords =
  | { status: 'loading' }
  | { status: 'error'; error: string }
  | { status: 'ready'; rows: Record<string, unknown>[]; more: boolean; reload(): void };

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** One request of the view's query + extra attributes (+ date range), page 1, count BOARD_LIMIT. */
export function useModeRecords(args: {
  api?: XrmApi;
  entitySet?: string;
  fetchXml?: string;
  attributes: string[];
  range?: ModeRange;
}): ModeRecords {
  const { api, entitySet, fetchXml, attributes, range } = args;
  const attributeKey = attributes.join('\n');
  const rangeKey = range ? JSON.stringify(range) : '';

  /** The FetchXML of the request, or why it cannot be built. */
  const request = useMemo((): { fetchXml: string } | { error: string } | undefined => {
    if (!fetchXml) return undefined;
    try {
      let xml = withAttributes(fetchXml, attributeKey ? attributeKey.split('\n') : []);
      if (range) xml = withDateRange(xml, range.attribute, range.from, range.to, { dateOnly: range.dateOnly, end: range.end });
      return { fetchXml: withPaging(xml, 1, BOARD_LIMIT) };
    } catch (e) {
      return { error: errorText(e) };
    }
    // rangeKey stands for range.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fetchXml, attributeKey, rangeKey]);

  const key = request && 'fetchXml' in request && entitySet ? `${entitySet}\n${request.fetchXml}` : '';
  // A reload keeps showing the rows it replaces until the new ones arrive (no spinner after a move).
  const [tick, setTick] = useState(0);
  const [result, setResult] = useState<{ key: string; rows?: Record<string, unknown>[]; more?: boolean; error?: string }>({ key: '' });
  const reload = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    if (!api || !entitySet || !request || !('fetchXml' in request)) return;
    let cancelled = false;
    const abort = new AbortController();
    runView(api, entitySet, request.fetchXml, 1, undefined, abort.signal).then(
      (page) => !cancelled && setResult({ key, rows: page.rows, more: page.more }),
      (e: unknown) => !cancelled && setResult({ key, error: errorText(e) }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
    // key stands for the entity set and the request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key, tick]);

  if (request && 'error' in request) return { status: 'error', error: request.error };
  if (result.key !== key || !key) return { status: 'loading' };
  if (result.error !== undefined) return { status: 'error', error: result.error };
  return { status: 'ready', rows: result.rows ?? [], more: result.more ?? false, reload };
}
