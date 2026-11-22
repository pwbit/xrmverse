import { act, renderHook } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../shell/api/client';
import { useLoadFor } from './useLoad';

const api = vi.hoisted(() => ({}) as XrmApi);
vi.mock('../../shell/auth/AuthProvider', () => ({ useAuth: () => ({ api }) }));

describe('useLoad', () => {
  it('useLoadFor returns nothing for a key until its own result arrives', async () => {
    const answers: Record<string, { resolve: (v: string) => void; reject: (e: Error) => void }> = {};
    const load = (key: string) => () => new Promise<string>((resolve, reject) => {
      answers[key] = { resolve, reject };
    });
    /** What every render returned, with the key it was asked for. */
    const seen: { key?: string; data?: string; error?: string; loading: boolean }[] = [];
    const { result, rerender } = renderHook(({ key }: { key?: string }) => {
      const r = useLoadFor(key, load(key ?? ''));
      seen.push({ key, data: r.data, error: r.error, loading: r.loading });
      return r;
    }, { initialProps: { key: 'a' } as { key?: string } });
    expect(result.current).toMatchObject({ data: undefined, loading: true, error: undefined });
    await act(async () => answers.a.resolve('A'));
    expect(result.current).toMatchObject({ data: 'A', loading: false });

    seen.length = 0;
    rerender({ key: 'b' });
    // From the first render for b, nothing of a shows.
    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((s) => s.data === undefined && s.loading)).toBe(true);
    await act(async () => answers.b.reject(new Error('No b')));
    expect(result.current).toMatchObject({ data: undefined, loading: false, error: 'No b' });

    seen.length = 0;
    rerender({ key: 'c' });
    expect(seen.every((s) => s.data === undefined && s.error === undefined && s.loading)).toBe(true);
    await act(async () => answers.c.resolve('C'));
    expect(result.current).toMatchObject({ data: 'C', loading: false, error: undefined });

    rerender({ key: undefined });
    expect(result.current).toMatchObject({ data: undefined, loading: false, error: undefined });
  });
});
