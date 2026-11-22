import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { XrmError } from '../../shell/api/errors';
import { useBulkRun } from './useBulkRun';

describe('useBulkRun', () => {
  it('reports each failure', async () => {
    const { result } = renderHook(() => useBulkRun());
    const called: string[] = [];
    let outcome;
    await act(async () => {
      outcome = await result.current.run(['a', 'b', 'c'], { a: 'Alpha', b: 'Beta', c: 'Gamma' }, async (id) => {
        called.push(id);
        if (id === 'b') throw new XrmError(400, 'nope');
      });
    });
    const expected = { done: 2, total: 3, failed: [{ id: 'b', name: 'Beta', message: 'nope' }] };
    expect(outcome).toEqual(expected);
    expect(called).toEqual(['a', 'b', 'c']);
    expect(result.current.result).toEqual(expected);
    act(() => result.current.clear());
    expect(result.current.result).toBeUndefined();
  });

  it('runs one request at a time', async () => {
    const { result } = renderHook(() => useBulkRun());
    let running = 0;
    let most = 0;
    await act(async () => {
      await result.current.run(['a', 'b'], {}, async () => {
        running++;
        most = Math.max(most, running);
        await new Promise((r) => setTimeout(r, 5));
        running--;
      });
    });
    expect(most).toBe(1);
  });
});
