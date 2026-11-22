import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { XrmApi } from '../../shell/api/client';
import { useModeRecords } from './useModeRecords';

const FETCH = '<fetch><entity name="account"><attribute name="name"/></entity></fetch>';

describe('useModeRecords reload', () => {
  it('keeps the rows while reloading, then shows the new ones', async () => {
    let answer = [{ name: 'Old' }];
    let release: (() => void) | undefined;
    let held = false;
    const api = {
      get: async () => {
        if (held) await new Promise<void>((r) => (release = r));
        return { value: answer };
      },
    } as unknown as XrmApi;
    const { result } = renderHook(() => useModeRecords({ api, entitySet: 'accounts', fetchXml: FETCH, attributes: [] }));
    await waitFor(() => expect(result.current.status).toBe('ready'));
    expect((result.current as { rows: unknown[] }).rows).toEqual([{ name: 'Old' }]);

    held = true;
    answer = [{ name: 'New' }];
    act(() => (result.current as { reload(): void }).reload());
    // The old rows stay (no spinner) until the new ones arrive.
    expect(result.current.status).toBe('ready');
    expect((result.current as { rows: unknown[] }).rows).toEqual([{ name: 'Old' }]);

    await waitFor(() => expect(release).toBeDefined());
    act(() => release!());
    await waitFor(() => expect((result.current as { rows: unknown[] }).rows).toEqual([{ name: 'New' }]));
  });
});
