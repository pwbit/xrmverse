import { useCallback, useState } from 'react';
import { errorText } from '../../shell/api/errors';

/** What a bulk command did: how many records changed, of how many, and why each other one failed. */
export interface BulkResult {
  done: number;
  total: number;
  failed: { id: string; name: string; message: string }[];
}

/**
 * Runs a command on several records, one request after the other (ruling W9): a failure does not stop the others. The
 * last result stays in `result` until `clear`.
 */
export function useBulkRun() {
  const [result, setResult] = useState<BulkResult>();
  const run = useCallback(async (ids: string[], names: Record<string, string>, op: (id: string) => Promise<void>): Promise<BulkResult> => {
    const failed: BulkResult['failed'] = [];
    let done = 0;
    for (const id of ids) {
      try {
        await op(id);
        done++;
      } catch (e) {
        failed.push({ id, name: names[id] ?? id, message: errorText(e) });
      }
    }
    const outcome = { done, total: ids.length, failed };
    setResult(outcome);
    return outcome;
  }, []);
  const clear = useCallback(() => setResult(undefined), []);
  return { run, result, clear };
}
